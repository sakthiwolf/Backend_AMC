using AmcTracker.Application.Common;
using FluentValidation;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace AmcTracker.Api.Infrastructure;

/// <summary>Helpers for the standard error shape: { "error": { "code", "message" } }.</summary>
public static class ApiErrors
{
    public static object Body(string code, string message) => new { error = new { code, message } };

    public static Task Write(HttpContext ctx, int status, string code, string message)
    {
        ctx.Response.StatusCode = status;
        return ctx.Response.WriteAsJsonAsync(Body(code, message));
    }

    /// <summary>Field names only; submitted values are never echoed (they may be passwords).</summary>
    public static string ValidationMessage(IEnumerable<string> fields)
    {
        var names = fields.Select(CamelCase).Where(n => n.Length > 0).Distinct().Order().ToList();
        return $"Invalid or missing fields: {(names.Count == 0 ? "body" : string.Join(", ", names))}.";
    }

    public static string CamelCase(string path)
    {
        var name = path.StartsWith("$.") ? path[2..] : path;
        return name.Length == 0 ? name : char.ToLowerInvariant(name[0]) + name[1..];
    }

    /// <summary>Model-binding failures (bad JSON, bad enum or date) become 422 validation_error.</summary>
    public static IActionResult FromModelState(ActionContext ctx) =>
        new ObjectResult(Body("validation_error", ValidationMessage(ctx.ModelState
            .Where(kv => kv.Value?.ValidationState == ModelValidationState.Invalid)
            .Select(kv => kv.Key.Replace("request.", "", StringComparison.OrdinalIgnoreCase)))))
        { StatusCode = StatusCodes.Status422UnprocessableEntity };
}

public class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext ctx, Exception ex, CancellationToken ct)
    {
        switch (ex)
        {
            case AppException app:
                if (app.StatusCode == 401) ctx.Response.Headers.WWWAuthenticate = "Bearer";
                await Send(ctx, app.StatusCode, app.Code, app.Message, ct);
                return true;
            case ValidationException v:
                await Send(ctx, 422, "validation_error",
                    ApiErrors.ValidationMessage(v.Errors.Select(e => e.PropertyName)), ct);
                return true;
            case BadHttpRequestException bad:
                await Send(ctx, bad.StatusCode, "bad_request", "The request could not be read.", ct);
                return true;
            case OperationCanceledException when ctx.RequestAborted.IsCancellationRequested:
                return true;
            default:
                logger.LogError(ex, "Unhandled exception for {Method} {Path}", ctx.Request.Method, ctx.Request.Path);
                await Send(ctx, 500, "server_error", "Something went wrong on the server.", ct);
                return true;
        }
    }

    private static async Task Send(HttpContext ctx, int status, string code, string message, CancellationToken ct)
    {
        ctx.Response.StatusCode = status;
        await ctx.Response.WriteAsJsonAsync(ApiErrors.Body(code, message), ct);
    }
}
