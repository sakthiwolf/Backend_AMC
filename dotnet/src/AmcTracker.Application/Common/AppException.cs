namespace AmcTracker.Application.Common;

/// <summary>An error that maps straight onto { "error": { "code", "message" } } with an HTTP status.</summary>
/// <summary>One failed field in a 422 response. <see cref="Code"/> is a machine-readable reason, e.g. "invalid_phone".</summary>
public record FieldError(string Field, string Message, string? Code = null);

public class AppException(int statusCode, string code, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;

    /// <summary>Per-field problems, shown in the 422 body as "details". Null for other errors.</summary>
    public IReadOnlyList<FieldError>? Details { get; init; }

    public static AppException ValidationFailed(IReadOnlyList<FieldError> details) =>
        new(422, "validation_failed", "Some fields are invalid.") { Details = details };

    public static AppException NotFound(string what) =>
        new(404, "not_found", $"{what} was not found.");

    public static AppException Conflict(string code, string message) => new(409, code, message);
}
