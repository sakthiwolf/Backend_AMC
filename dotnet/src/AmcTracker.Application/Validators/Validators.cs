using AmcTracker.Application.Dtos;
using AmcTracker.Domain.Services;
using FluentValidation;

namespace AmcTracker.Application.Validators;

public class LoginRequestValidator : AbstractValidator<LoginRequest>
{
    public LoginRequestValidator()
    {
        RuleFor(x => x.Username).NotEmpty().MaximumLength(50);
        RuleFor(x => x.Password).NotEmpty().MaximumLength(200);
        RuleFor(x => x.DeviceFingerprint).NotEmpty().MinimumLength(16).MaximumLength(100);
        RuleFor(x => x.DeviceLabel).MaximumLength(100);
    }
}

public class RefreshRequestValidator : AbstractValidator<RefreshRequest>
{
    public RefreshRequestValidator()
    {
        RuleFor(x => x.DeviceFingerprint).NotEmpty().MinimumLength(16).MaximumLength(100);
        RuleFor(x => x.RefreshToken).NotEmpty().MaximumLength(200);
    }
}

public class LogoutRequestValidator : AbstractValidator<LogoutRequest>
{
    public LogoutRequestValidator()
    {
        RuleFor(x => x.DeviceFingerprint).NotEmpty().MinimumLength(16).MaximumLength(100);
    }
}

public class CustomerRequestValidator : AbstractValidator<CustomerRequest>
{
    public CustomerRequestValidator()
    {
        RuleFor(x => x.CustomerName).NotEmpty().MaximumLength(150);
        RuleFor(x => x.Phone).NotEmpty()
            .Must(PhoneNumber.IsValidE164).WithMessage("Phone must be in E.164 format, e.g. +919876543210.");
        RuleFor(x => x.Location).MaximumLength(255);
        RuleFor(x => x.InstallationDate).NotNull();
        RuleFor(x => x.ProjectName).MaximumLength(150);
        RuleFor(x => x.ProjectValue).GreaterThanOrEqualTo(0).LessThan(10_000_000_000m);
    }
}

public class CreatePlanRequestValidator : AbstractValidator<CreatePlanRequest>
{
    public CreatePlanRequestValidator()
    {
        RuleFor(x => x.PlanType).NotNull().IsInEnum();
        RuleFor(x => x.Amount).NotNull().GreaterThan(0).LessThan(10_000_000_000m);
    }
}

public class RenewPlanRequestValidator : AbstractValidator<RenewPlanRequest>
{
    public RenewPlanRequestValidator()
    {
        RuleFor(x => x.Amount).GreaterThan(0).LessThan(10_000_000_000m);
    }
}
