using System.Text.RegularExpressions;

namespace AmcTracker.Domain.Services;

/// <summary>E.164 phone numbers: "+", a non-zero first digit, 8 to 15 digits in total.</summary>
public static partial class PhoneNumber
{
    [GeneratedRegex(@"^\+[1-9]\d{7,14}$")]
    private static partial Regex E164Pattern();

    /// <summary>Removes the spaces and dashes people type, so +91 98765-43210 becomes +919876543210.</summary>
    public static string Normalize(string? value) =>
        string.IsNullOrEmpty(value) ? "" : value.Replace(" ", "").Replace("-", "");

    public static bool IsValidE164(string? value) =>
        !string.IsNullOrEmpty(value) && E164Pattern().IsMatch(value);

    /// <summary>Keeps the country prefix and last four digits for logs. Never log the full number.</summary>
    public static string Mask(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "(none)";
        if (value.Length <= 7) return new string('*', value.Length);
        return value[..3] + new string('*', value.Length - 7) + value[^4..];
    }
}
