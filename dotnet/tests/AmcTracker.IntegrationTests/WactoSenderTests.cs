using System.Net;
using AmcTracker.Infrastructure.Services;
using Microsoft.Extensions.Options;

namespace AmcTracker.IntegrationTests;

/// <summary>
/// The Wacto sender on its own, against a fake HttpMessageHandler: URL, auth header, body, and how each kind
/// of answer becomes a result. No database and no network.
/// </summary>
public class WactoSenderTests
{
    private const string Phone = "+910000000101";
    private static readonly string[] Parameters = ["Asha Traders", "CCTV", "12 Oct 2027"];

    private readonly WactoStub _stub = new();

    private WhatsAppCloudSender Sender(Action<WhatsAppOptions>? change = null, TimeSpan? timeout = null)
    {
        var options = ApiFixture.DefaultWactoOptions();
        change?.Invoke(options);
        var http = new HttpClient(_stub, disposeHandler: false) { Timeout = timeout ?? WhatsAppCloudSender.Timeout };
        return new WhatsAppCloudSender(http, Options.Create(options));
    }

    private Task<AmcTracker.Application.Abstractions.WhatsAppSendResult> Send(WhatsAppCloudSender sender) =>
        sender.SendTemplateAsync(Phone, "amc_yearly_reminder", Parameters);

    [Fact]
    public async Task Posts_to_BaseUrl_ApiVersion_PhoneNumberId_messages()
    {
        await Send(Sender());
        await Send(Sender(o => o.BaseUrl = "https://wacto.invalid/api/")); // a base path and a trailing slash

        Assert.Equal(HttpMethod.Post, _stub.Requests[0].Method);
        Assert.Equal("https://wacto.invalid/v99.0/pnid-123/messages", _stub.Requests[0].Uri.ToString());
        Assert.Equal("https://wacto.invalid/api/v99.0/pnid-123/messages", _stub.Requests[1].Uri.ToString());
    }

    [Fact]
    public async Task The_auth_header_name_and_scheme_follow_configuration()
    {
        await Send(Sender());
        await Send(Sender(o => { o.AuthHeaderName = "X-Api-Key"; o.AuthScheme = ""; }));
        await Send(Sender(o => o.AuthScheme = "Token"));

        Assert.Equal($"Bearer {ApiFixture.WactoToken}", _stub.Requests[0].Headers["Authorization"]);
        Assert.Equal(ApiFixture.WactoToken, _stub.Requests[1].Headers["X-Api-Key"]);
        Assert.False(_stub.Requests[1].Headers.ContainsKey("Authorization"));
        Assert.Equal($"Token {ApiFixture.WactoToken}", _stub.Requests[2].Headers["Authorization"]);
    }

    [Fact]
    public async Task The_body_is_the_Meta_template_format_with_parameters_in_order_and_no_plus()
    {
        await Send(Sender());

        var body = _stub.Requests.Single().Json;
        Assert.Equal("whatsapp", body.GetProperty("messaging_product").GetString());
        Assert.Equal("910000000101", body.GetProperty("to").GetString());
        Assert.Equal("template", body.GetProperty("type").GetString());
        var template = body.GetProperty("template");
        Assert.Equal("amc_yearly_reminder", template.GetProperty("name").GetString());
        Assert.Equal("en", template.GetProperty("language").GetProperty("code").GetString());
        var component = template.GetProperty("components").EnumerateArray().Single();
        Assert.Equal("body", component.GetProperty("type").GetString());
        var parameters = component.GetProperty("parameters").EnumerateArray().ToArray();
        Assert.All(parameters, p => Assert.Equal("text", p.GetProperty("type").GetString()));
        Assert.Equal(Parameters, parameters.Select(p => p.GetProperty("text").GetString()!).ToArray());
    }

    [Fact]
    public async Task A_2xx_with_a_message_id_is_a_success_with_that_id()
    {
        var result = await Send(Sender());
        Assert.Equal((true, "wamid.TEST1", 200), (result.Success, result.ProviderMessageId, result.HttpStatus));
        Assert.NotNull(result.Duration);
    }

    [Theory]
    [InlineData("""{"messaging_product":"whatsapp"}""")]
    [InlineData("""{"messages":[]}""")]
    [InlineData("accepted")]
    [InlineData("")]
    public async Task A_2xx_without_a_message_id_is_still_a_success(string body)
    {
        _stub.Respond = _ => WactoStub.Reply(200, body);
        var result = await Send(Sender());
        Assert.Equal((true, (string?)null), (result.Success, result.ProviderMessageId));
    }

    [Fact]
    public async Task A_400_is_a_failure_that_is_not_retried_and_keeps_the_provider_error()
    {
        _stub.Respond = _ => WactoStub.Reply(400,
            """{"error":{"message":"(#132001) Template name does not exist in the translation","type":"OAuthException","code":132001}}""");

        var result = await Send(Sender());

        Assert.False(result.Success);
        Assert.False(result.IsRetryable);
        Assert.Equal("132001", result.ErrorCode);
        Assert.Equal("HTTP 400: (#132001) Template name does not exist in the translation", result.ErrorMessage);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(422)]
    public async Task Other_4xx_are_not_retried(int status)
    {
        _stub.Respond = _ => WactoStub.Reply(status, """{"error":{"code":1,"message":"no"}}""");
        Assert.False((await Send(Sender())).IsRetryable);
    }

    [Theory]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    public async Task A_429_or_5xx_is_a_failure_that_is_retried(int status)
    {
        _stub.Respond = _ => WactoStub.Reply(status, """{"error":{"code":4,"message":"try later"}}""");
        var result = await Send(Sender());
        Assert.Equal((false, true), (result.Success, result.IsRetryable));
        Assert.Equal($"HTTP {status}: try later", result.ErrorMessage);
    }

    [Fact]
    public async Task A_non_JSON_error_keeps_the_status_and_the_start_of_the_body_cut_to_500_characters()
    {
        var html = "<html>" + new string('x', 2000) + "</html>";
        _stub.Respond = _ => WactoStub.Reply(502, html, "text/html");

        var result = await Send(Sender());

        Assert.Equal(500, result.ErrorMessage!.Length);
        Assert.StartsWith("HTTP 502: <html>xxx", result.ErrorMessage);
        Assert.True(result.IsRetryable);
    }

    [Fact]
    public async Task An_error_body_in_another_shape_is_stored_as_text()
    {
        _stub.Respond = _ => WactoStub.Reply(400, """{"status":"error","reason":"invalid number"}""");
        var result = await Send(Sender());
        Assert.Equal("""HTTP 400: {"status":"error","reason":"invalid number"}""", result.ErrorMessage);
        Assert.Null(result.ErrorCode);
    }

    [Fact]
    public async Task The_token_never_reaches_the_error_message()
    {
        _stub.Respond = _ => WactoStub.Reply(401, $$$"""{"error":{"code":190,"message":"token {{{ApiFixture.WactoToken}}} is invalid"}}""");
        var result = await Send(Sender());
        Assert.DoesNotContain(ApiFixture.WactoToken, result.ErrorMessage);
        Assert.Contains("***", result.ErrorMessage);
    }

    [Fact]
    public async Task A_timeout_is_a_failure_that_is_retried()
    {
        _stub.Delay = TimeSpan.FromSeconds(5);
        var result = await Send(Sender(timeout: TimeSpan.FromMilliseconds(200)));
        Assert.Equal((false, true, "timeout"), (result.Success, result.IsRetryable, result.ErrorCode));
        Assert.Equal("Request to WhatsApp timed out.", result.ErrorMessage);
    }

    [Fact]
    public async Task A_network_error_is_a_failure_that_is_retried()
    {
        _stub.Respond = _ => throw new HttpRequestException(HttpRequestError.NameResolutionError, "no such host");
        var result = await Send(Sender());
        Assert.Equal((false, true, "network"), (result.Success, result.IsRetryable, result.ErrorCode));
        Assert.Equal("Network error (NameResolutionError).", result.ErrorMessage);
    }

    [Fact]
    public void The_timeout_is_15_seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(15), WhatsAppCloudSender.Timeout);
    }

    [Fact]
    public async Task The_status_code_is_classified_from_HttpStatusCode_values()
    {
        _stub.Respond = _ => new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        Assert.True((await Send(Sender())).IsRetryable);
    }
}
