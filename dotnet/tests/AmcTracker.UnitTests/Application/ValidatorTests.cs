using AmcTracker.Application.Dtos;
using AmcTracker.Application.Validators;
using AmcTracker.Domain.Enums;

namespace AmcTracker.UnitTests.Application;

public class ValidatorTests
{
    private static CustomerRequest Customer(string? phone = "+919876543210", string? name = "Asha", DateOnly? date = null) =>
        new(name, phone, "Chennai", date ?? new DateOnly(2026, 10, 12), "CCTV", 50000m, false, null);

    private readonly CustomerRequestValidator _customer = new();

    [Fact]
    public void Valid_customer_passes() => Assert.True(_customer.Validate(Customer()).IsValid);

    [Theory]
    [InlineData("9876543210")]
    [InlineData("+91 9876543210")]
    [InlineData("")]
    [InlineData(null)]
    public void Phone_must_be_E164(string? phone)
    {
        var result = _customer.Validate(Customer(phone: phone));
        Assert.Contains(result.Errors, e => e.PropertyName == "Phone");
    }

    [Fact]
    public void Name_and_installation_date_are_required()
    {
        var result = _customer.Validate(new CustomerRequest(null, "+919876543210", null, null, null, null, null, null));
        Assert.Contains(result.Errors, e => e.PropertyName == "CustomerName");
        Assert.Contains(result.Errors, e => e.PropertyName == "InstallationDate");
    }

    [Fact]
    public void Negative_project_value_is_rejected() =>
        Assert.False(_customer.Validate(Customer() with { ProjectValue = -1 }).IsValid);

    [Fact]
    public void Plan_request_needs_type_and_positive_amount()
    {
        var v = new CreatePlanRequestValidator();
        Assert.True(v.Validate(new CreatePlanRequest(PlanType.AMC, 12000m, null)).IsValid);
        Assert.False(v.Validate(new CreatePlanRequest(null, 12000m, null)).IsValid);
        Assert.False(v.Validate(new CreatePlanRequest(PlanType.Monthly, 0m, null)).IsValid);
        Assert.False(v.Validate(new CreatePlanRequest((PlanType)99, 100m, null)).IsValid);
    }
}
