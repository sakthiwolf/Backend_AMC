using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace AmcTracker.IntegrationTests;

/// <summary>Customer Master endpoints against a real SQL Server test database.</summary>
[Collection("Api")]
public class CustomerEndpointTests : IAsyncLifetime
{
    private readonly ApiFixture _api;
    public CustomerEndpointTests(ApiFixture api) => _api = api;
    public Task InitializeAsync() => _api.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static object Body(string name, string phone = "+919876543210", string? project = "CCTV",
        string date = "2026-10-12", decimal? value = 50000m, string? location = "Chennai") => new
    {
        customerName = name, phone, location, installationDate = date, projectName = project, projectValue = value,
    };

    private static async Task<int> Create(HttpClient c, object body)
    {
        var res = await c.PostAsJsonAsync("/api/customers", body);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await res.Json()).GetProperty("customerId").GetInt32();
    }

    private static async Task<JsonElement> Page(HttpClient c, string query = "") =>
        await (await c.GetAsync("/api/customers" + query)).Json();

    private static string[] Names(JsonElement page) =>
        page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("customerName").GetString()!).ToArray();

    [Fact]
    public async Task Every_customer_route_needs_a_token()
    {
        var anon = _api.Anonymous();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/customers")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/customers/1")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync("/api/customers", Body("X"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.DeleteAsync("/api/customers/1")).StatusCode);
    }

    [Fact]
    public async Task Post_then_get_returns_the_same_customer_with_a_location_header()
    {
        var c = await _api.SignedInAsync();
        var created = await c.PostAsJsonAsync("/api/customers", Body("Asha Traders", value: 12345.6m));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Json()).GetProperty("customerId").GetInt32();
        Assert.EndsWith($"/api/customers/{id}", created.Headers.Location!.ToString());

        var text = await (await c.GetAsync($"/api/customers/{id}")).Content.ReadAsStringAsync();
        var got = JsonDocument.Parse(text).RootElement;
        Assert.Equal("Asha Traders", got.GetProperty("customerName").GetString());
        Assert.Equal("+919876543210", got.GetProperty("phone").GetString());
        Assert.Equal("2026-10-12", got.GetProperty("installationDate").GetString());
        Assert.True(got.GetProperty("isActive").GetBoolean());
        Assert.False(got.GetProperty("whatsAppOptOut").GetBoolean());
        Assert.Contains("\"projectValue\":12345.60", text); // money keeps two decimals
        Assert.False(got.TryGetProperty("plans", out _)); // customer records only
    }

    [Fact]
    public async Task Invalid_input_returns_422_with_details_per_field()
    {
        var c = await _api.SignedInAsync();
        var res = await c.PostAsJsonAsync("/api/customers", Body("", phone: "9123456780", value: -1m, date: "2099-01-01"));

        Assert.Equal((HttpStatusCode)422, res.StatusCode);
        var error = (await res.Json()).GetProperty("error");
        Assert.Equal("validation_failed", error.GetProperty("code").GetString());
        var details = error.GetProperty("details").EnumerateArray().ToList();
        var fields = details.Select(d => d.GetProperty("field").GetString()!).ToArray();
        Assert.Equal(["customerName", "installationDate", "phone", "projectValue"], fields.Order().ToArray());
        Assert.All(details, d => Assert.False(string.IsNullOrEmpty(d.GetProperty("message").GetString())));
        Assert.Equal("invalid_phone",
            details.Single(d => d.GetProperty("field").GetString() == "phone").GetProperty("code").GetString());
        Assert.DoesNotContain("9123456780", await res.Content.ReadAsStringAsync()); // no echoed phone numbers
    }

    [Fact]
    public async Task Phone_with_spaces_and_dashes_is_stored_normalised_and_duplicates_are_allowed()
    {
        var c = await _api.SignedInAsync();
        await Create(c, Body("First", phone: "+91 98765-43210"));
        await Create(c, Body("Second", phone: "+919876543210", project: "Alarm"));

        var rows = await _api.WithDbAsync(db => db.Customers.OrderBy(x => x.CustomerId).ToListAsync());
        Assert.Equal(["+919876543210", "+919876543210"], rows.Select(r => r.Phone).ToArray());
    }

    [Fact]
    public async Task Search_matches_name_phone_and_project_ignoring_case()
    {
        var c = await _api.SignedInAsync();
        await Create(c, Body("Asha Traders", "+919876543210", "CCTV install"));
        await Create(c, Body("Zed Stores", "+919000000001", "Network"));
        await Create(c, Body("Mohan", "+14155550123", "Billing"));

        Assert.Equal(["Zed Stores"], Names(await Page(c, "?search=ZED")));
        Assert.Equal(["Asha Traders"], Names(await Page(c, "?search=cctv")));
        Assert.Equal(["Mohan"], Names(await Page(c, "?search=4155550")));
        Assert.Equal(["Asha Traders"], Names(await Page(c, "?search=%2B91%209876%20543210"))); // "+91 9876 543210"
        Assert.Equal(["Asha Traders", "Mohan", "Zed Stores"], Names(await Page(c)));
        Assert.Empty(Names(await Page(c, "?search=nothing-like-this")));
    }

    [Fact]
    public async Task Active_filter_and_soft_delete()
    {
        var c = await _api.SignedInAsync();
        var keep = await Create(c, Body("Keep Co", "+919000000001"));
        var gone = await Create(c, Body("Gone Co", "+919000000002"));

        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync($"/api/customers/{gone}")).StatusCode);

        // The row stays, flagged inactive, with UpdatedAt set.
        var row = await _api.WithDbAsync(db => db.Customers.SingleAsync(x => x.CustomerId == gone));
        Assert.False(row.IsActive);
        Assert.NotNull(row.UpdatedAt);

        Assert.Equal(["Keep Co"], Names(await Page(c, "?active=true")));
        Assert.Equal(["Gone Co"], Names(await Page(c, "?active=false")));
        Assert.Equal(["Gone Co", "Keep Co"], Names(await Page(c)));

        // A second delete, and an unknown id, are 404 "not_found".
        var again = await c.DeleteAsync($"/api/customers/{gone}");
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
        Assert.Equal("not_found", await again.ErrorCode());
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/api/customers/99999")).StatusCode);
        Assert.Equal(keep, (await (await c.GetAsync($"/api/customers/{keep}")).Json()).GetProperty("customerId").GetInt32());
    }

    [Fact]
    public async Task Put_updates_the_customer_and_stamps_UpdatedAt()
    {
        var c = await _api.SignedInAsync();
        var id = await Create(c, Body("Asha Traders"));

        var put = await c.PutAsJsonAsync($"/api/customers/{id}", new
        {
            customerName = "Asha Traders Ltd", phone = "+919876543210", installationDate = "2026-10-12", whatsAppOptOut = true,
        });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var json = await put.Json();
        Assert.Equal("Asha Traders Ltd", json.GetProperty("customerName").GetString());
        Assert.True(json.GetProperty("whatsAppOptOut").GetBoolean());
        Assert.NotEqual(JsonValueKind.Null, json.GetProperty("updatedAt").ValueKind);

        var bad = await c.PutAsJsonAsync($"/api/customers/{id}", Body("Asha", phone: "12345"));
        Assert.Equal((HttpStatusCode)422, bad.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.PutAsJsonAsync("/api/customers/99999", Body("X"))).StatusCode);
    }

    [Fact]
    public async Task Paging_returns_items_totals_and_caps_page_size_at_100()
    {
        var c = await _api.SignedInAsync();
        for (var i = 1; i <= 5; i++) await Create(c, Body($"Customer {i:00}", $"+9190000000{i:00}"));

        var first = await Page(c, "?pageSize=2&page=1");
        Assert.Equal(["Customer 01", "Customer 02"], Names(first));
        Assert.Equal(5, first.GetProperty("totalCount").GetInt32());
        Assert.Equal(1, first.GetProperty("page").GetInt32());
        Assert.Equal(2, first.GetProperty("pageSize").GetInt32());

        Assert.Equal(["Customer 05"], Names(await Page(c, "?pageSize=2&page=3")));
        Assert.Empty(Names(await Page(c, "?pageSize=2&page=9")));

        Assert.Equal(20, (await Page(c)).GetProperty("pageSize").GetInt32());
        Assert.Equal(100, (await Page(c, "?pageSize=1000")).GetProperty("pageSize").GetInt32());
    }

    [Fact]
    public async Task The_database_itself_rejects_a_bad_phone_or_negative_value()
    {
        await _api.SignedInAsync();
        await Assert.ThrowsAsync<DbUpdateException>(() => _api.WithDbAsync(async db =>
        {
            db.Customers.Add(new AmcTracker.Domain.Entities.Customer
            {
                CustomerName = "Bad", Phone = "9876543210", InstallationDate = new DateOnly(2026, 1, 1),
            });
            return await db.SaveChangesAsync();
        }));
        await Assert.ThrowsAsync<DbUpdateException>(() => _api.WithDbAsync(async db =>
        {
            db.Customers.Add(new AmcTracker.Domain.Entities.Customer
            {
                CustomerName = "Bad", Phone = "+919876543210", InstallationDate = new DateOnly(2026, 1, 1), ProjectValue = -1,
            });
            return await db.SaveChangesAsync();
        }));
    }
}
