namespace AmcTracker.Application.Common;

/// <summary>An error that maps straight onto { "error": { "code", "message" } } with an HTTP status.</summary>
public class AppException(int statusCode, string code, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;

    public static AppException NotFound(string what) =>
        new(404, "not_found", $"{what} was not found.");

    public static AppException Conflict(string code, string message) => new(409, code, message);
}
