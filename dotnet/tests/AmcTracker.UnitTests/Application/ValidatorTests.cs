using AmcTracker.Application.Abstractions;
using AmcTracker.Application.Dtos;
using AmcTracker.Application.Validators;
using AmcTracker.Domain.Enums;
using NSubstitute;

namespace AmcTracker.UnitTests.Application;

// Customer rules are covered in CustomerValidationTests.
public class ValidatorTests
{
    private static readonly DateOnly Today = new(2026, 10, 21);
    private readonly IClock _clock = Substitute.For<IClock>();

    public ValidatorTests() => _clock.Today.Returns(Today);

    [Fact]
    public void Plan_request_needs_AMC_or_Monthly_and_an_amount_of_zero_or_more_with_two_decimals()
    {
        var v = new CreatePlanRequestValidator(_clock);
        Assert.True(v.Validate(new CreatePlanRequest(PlanType.AMC, 12000m, null)).IsValid);
        Assert.True(v.Validate(new CreatePlanRequest(PlanType.Monthly, 0m, null)).IsValid);
        Assert.True(v.Validate(new CreatePlanRequest(PlanType.Monthly, 499.99m, null)).IsValid);
        Assert.False(v.Validate(new CreatePlanRequest(null, 12000m, null)).IsValid);
        Assert.False(v.Validate(new CreatePlanRequest((PlanType)99, 100m, null)).IsValid);
        Assert.False(v.Validate(new CreatePlanRequest(PlanType.AMC, null, null)).IsValid);
        Assert.False(v.Validate(new CreatePlanRequest(PlanType.AMC, -1m, null)).IsValid);
        Assert.False(v.Validate(new CreatePlanRequest(PlanType.AMC, 10.555m, null)).IsValid);
    }

    [Fact]
    public void Start_date_may_be_up_to_one_year_ahead()
    {
        var create = new CreatePlanRequestValidator(_clock);
        Assert.True(create.Validate(new CreatePlanRequest(PlanType.AMC, 1m, Today.AddYears(1))).IsValid);
        Assert.False(create.Validate(new CreatePlanRequest(PlanType.AMC, 1m, Today.AddYears(1).AddDays(1))).IsValid);

        var renew = new RenewPlanRequestValidator(_clock);
        RenewPlanRequest Req(DateOnly? start = null) => new(Today, 100m, PaymentMode.Cash, null, start);
        Assert.True(renew.Validate(Req()).IsValid);
        Assert.True(renew.Validate(Req(Today.AddYears(1))).IsValid);
        Assert.False(renew.Validate(Req(Today.AddYears(1).AddDays(1))).IsValid);
    }

    [Fact]
    public void A_renewal_needs_a_payment_with_a_mode_and_an_amount_above_zero()
    {
        var v = new RenewPlanRequestValidator(_clock);
        Assert.True(v.Validate(new RenewPlanRequest(Today, 0.01m, PaymentMode.Bank)).IsValid);
        Assert.False(v.Validate(new RenewPlanRequest(null, null, null)).IsValid);
        Assert.False(v.Validate(new RenewPlanRequest(Today, 0m, PaymentMode.Cash)).IsValid);
        Assert.False(v.Validate(new RenewPlanRequest(Today.AddDays(1), 5m, PaymentMode.Cash)).IsValid);
        Assert.False(v.Validate(new RenewPlanRequest(Today, 5m, (PaymentMode)9)).IsValid);
    }
}
