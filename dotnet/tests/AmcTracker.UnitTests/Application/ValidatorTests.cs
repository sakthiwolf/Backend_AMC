using AmcTracker.Application.Dtos;
using AmcTracker.Application.Validators;
using AmcTracker.Domain.Enums;

namespace AmcTracker.UnitTests.Application;

// Customer rules are covered in CustomerValidationTests.
public class ValidatorTests
{
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
