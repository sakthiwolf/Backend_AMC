using AmcTracker.Application.Abstractions;
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

/// <summary>Rules shared by create and update. Phone is checked after spaces and dashes are removed.</summary>
public abstract class CustomerFieldsValidator<T> : AbstractValidator<T> where T : ICustomerFields
{
    /// <summary>How far ahead an installation date may be.</summary>
    public const int MaxFutureYears = 1;

    protected CustomerFieldsValidator(IClock clock)
    {
        RuleFor(x => x.CustomerName).Cascade(CascadeMode.Stop)
            .Must(n => !string.IsNullOrWhiteSpace(n)).WithMessage("Enter the customer name.")
            .Must(n => n!.Trim().Length <= 150).WithMessage("Customer name can be at most 150 characters.");

        RuleFor(x => x.Phone).Cascade(CascadeMode.Stop)
            .Must(p => !string.IsNullOrWhiteSpace(p))
                .WithMessage("Enter a phone number.").WithErrorCode("invalid_phone")
            .Must(p => PhoneNumber.IsValidE164(PhoneNumber.Normalize(p)))
                .WithMessage("Use the country code, like +919876543210.").WithErrorCode("invalid_phone");

        RuleFor(x => x.Location).Must(l => l is null || l.Trim().Length <= 255)
            .WithMessage("Location can be at most 255 characters.");

        RuleFor(x => x.InstallationDate).Cascade(CascadeMode.Stop)
            .NotNull().WithMessage("Choose the installation date.")
            .Must(d => d!.Value <= clock.Today.AddYears(MaxFutureYears))
                .WithMessage("Installation date cannot be more than a year in the future.");

        RuleFor(x => x.ProjectName).Must(p => p is null || p.Trim().Length <= 150)
            .WithMessage("Project name can be at most 150 characters.");

        RuleFor(x => x.PlanType).Must(t => t is null || Enum.IsDefined(t.Value))
            .WithMessage("Choose Monthly or Yearly.");

        RuleFor(x => x.ProjectValue).Cascade(CascadeMode.Stop)
            .Must(v => v is null || v >= 0).WithMessage("Amount cannot be negative.")
            .Must(v => v is null || v < 10_000_000_000m).WithMessage("Amount is too large.")
            .Must(v => v is null || decimal.Round(v.Value, 2) == v).WithMessage("Amount can have at most 2 decimals.");
    }
}

public class CreateCustomerRequestValidator(IClock clock) : CustomerFieldsValidator<CreateCustomerRequest>(clock);

public class UpdateCustomerRequestValidator(IClock clock) : CustomerFieldsValidator<UpdateCustomerRequest>(clock);

/// <summary>Plan amount and start-date rules shared by create and renew.</summary>
internal static class PlanRules
{
    /// <summary>How far ahead a plan may start.</summary>
    public const int MaxFutureYears = 1;

    /// <summary>Zero or more, at most two decimals, fits decimal(12,2). Null is allowed here.</summary>
    public static void Amount<T>(IRuleBuilderInitial<T, decimal?> rule) => rule.Cascade(CascadeMode.Stop)
        .Must(v => v is null || v >= 0).WithMessage("Amount cannot be negative.")
        .Must(v => v is null || v < 10_000_000_000m).WithMessage("Amount is too large.")
        .Must(v => v is null || decimal.Round(v.Value, 2) == v).WithMessage("Amount can have at most 2 decimals.");

    public static void StartDate<T>(IRuleBuilder<T, DateOnly?> rule, IClock clock) => rule
        .Must(d => d is null || d.Value <= clock.Today.AddYears(MaxFutureYears))
        .WithMessage("Start date cannot be more than a year in the future.");
}

public class CreatePlanRequestValidator : AbstractValidator<CreatePlanRequest>
{
    public CreatePlanRequestValidator(IClock clock)
    {
        RuleFor(x => x.PlanType).Cascade(CascadeMode.Stop)
            .NotNull().WithMessage("Choose AMC or Monthly.")
            .IsInEnum().WithMessage("Choose AMC or Monthly.");
        RuleFor(x => x.Amount).NotNull().WithMessage("Enter the amount.");
        PlanRules.Amount(RuleFor(x => x.Amount));
        PlanRules.StartDate(RuleFor(x => x.StartDate), clock);
    }
}

/// <summary>A renewal carries its payment: when, how much, and how it was paid.</summary>
public class RenewPlanRequestValidator : AbstractValidator<RenewPlanRequest>
{
    public RenewPlanRequestValidator(IClock clock)
    {
        RuleFor(x => x.PaidOn).Cascade(CascadeMode.Stop)
            .NotNull().WithMessage("Enter the date the payment was received.")
            .Must(d => d!.Value <= clock.Today).WithMessage("The payment date cannot be in the future.");

        RuleFor(x => x.Amount).Cascade(CascadeMode.Stop)
            .NotNull().WithMessage("Enter the amount received.")
            .Must(v => v!.Value > 0).WithMessage("The amount received must be more than 0.")
            .Must(v => v!.Value < 10_000_000_000m).WithMessage("Amount is too large.")
            .Must(v => decimal.Round(v!.Value, 2) == v).WithMessage("Amount can have at most 2 decimals.");

        RuleFor(x => x.Mode).Cascade(CascadeMode.Stop)
            .NotNull().WithMessage("Choose Cash, UPI or Bank.")
            .IsInEnum().WithMessage("Choose Cash, UPI or Bank.");

        RuleFor(x => x.Note).Must(n => n is null || n.Trim().Length <= 200)
            .WithMessage("Note can be at most 200 characters.");

        PlanRules.Amount(RuleFor(x => x.PlanAmount)); // the new plan's rate: 0 or more
        PlanRules.StartDate(RuleFor(x => x.StartDate), clock);
    }
}
