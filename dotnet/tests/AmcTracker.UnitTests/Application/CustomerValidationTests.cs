using AmcTracker.Application.Abstractions;
using AmcTracker.Application.Dtos;
using AmcTracker.Application.Validators;
using NSubstitute;

namespace AmcTracker.UnitTests.Application;

public class CustomerValidationTests
{
    private static readonly DateOnly Today = new(2026, 10, 16);
    private readonly CreateCustomerRequestValidator _create;
    private readonly UpdateCustomerRequestValidator _update;

    public CustomerValidationTests()
    {
        var clock = Substitute.For<IClock>();
        clock.Today.Returns(Today);
        _create = new CreateCustomerRequestValidator(clock);
        _update = new UpdateCustomerRequestValidator(clock);
    }

    private static CreateCustomerRequest Req(
        string? name = "Asha", string? phone = "+919876543210", DateOnly? date = null, decimal? value = 50000m) =>
        new(name, phone, "Chennai", date ?? Today, "CCTV", value, false);

    [Fact]
    public void A_valid_customer_passes() => Assert.True(_create.Validate(Req()).IsValid);

    [Theory]
    [InlineData("+919876543210")]
    [InlineData("+14155550123")]
    [InlineData("+91 98765-43210")] // spaces and dashes are normalised first
    public void Phone_accepts_E164(string phone) => Assert.True(_create.Validate(Req(phone: phone)).IsValid);

    [Theory]
    [InlineData("9876543210")]
    [InlineData("+0123456789")]
    [InlineData("+91 98765 abc")]
    [InlineData("")]
    [InlineData(null)]
    public void Phone_rejects_everything_else_with_invalid_phone(string? phone)
    {
        var error = Assert.Single(_create.Validate(Req(phone: phone)).Errors, e => e.PropertyName == "Phone");
        Assert.Equal("invalid_phone", error.ErrorCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Name_is_required(string? name) =>
        Assert.Contains(_create.Validate(Req(name: name)).Errors, e => e.PropertyName == "CustomerName");

    [Fact]
    public void Name_is_at_most_150_characters_after_trimming()
    {
        Assert.True(_create.Validate(Req(name: "  " + new string('a', 150) + "  ")).IsValid);
        Assert.False(_create.Validate(Req(name: new string('a', 151))).IsValid);
    }

    [Fact]
    public void Installation_date_is_required_and_not_more_than_a_year_ahead()
    {
        Assert.Contains(_create.Validate(Req() with { InstallationDate = null }).Errors,
            e => e.PropertyName == "InstallationDate");
        Assert.True(_create.Validate(Req(date: Today.AddYears(1))).IsValid);
        Assert.False(_create.Validate(Req(date: Today.AddYears(1).AddDays(1))).IsValid);
    }

    [Theory]
    [InlineData("-1", false)]
    [InlineData("0", true)]
    [InlineData("12345.67", true)]
    [InlineData("12345.678", false)]
    public void Project_value_must_be_zero_or_more_with_at_most_two_decimals(string value, bool ok) =>
        Assert.Equal(ok, _create.Validate(Req(value: decimal.Parse(value))).IsValid);

    [Fact]
    public void Optional_fields_may_be_missing() =>
        Assert.True(_create.Validate(
            new CreateCustomerRequest("Asha", "+919876543210", null, Today, null, null, null)).IsValid);

    [Fact]
    public void Update_uses_the_same_rules() =>
        Assert.False(_update.Validate(
            new UpdateCustomerRequest("Asha", "9876543210", null, Today, null, null, null)).IsValid);
}
