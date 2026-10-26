using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AmcTracker.Application.Abstractions;
using Microsoft.Extensions.Options;

namespace AmcTracker.Infrastructure.Services;

/// <summary>Which WhatsApp sender to use (WhatsApp:Provider).</summary>
public static class WhatsAppProviders
{
    /// <summary>Wacto, using the Meta Cloud API message format.</summary>
    public const string Wacto = "Wacto";
    /// <summary>In-memory: records each call, never sends. The default in Development.</summary>
    public const string Fake = "Fake";
}

public class WhatsAppOptions
{
    public const string Section = "WhatsApp";

    /// <summary>"Wacto" or "Fake". Unset: Fake in Development, Wacto everywhere else.</summary>
    public string? Provider { get; set; }
    /// <summary>Provider host, e.g. https://api.example.com (no trailing path needed).</summary>
    public string BaseUrl { get; set; } = "";
    /// <summary>Path segment before the phone number id, e.g. v21.0.</summary>
    public string ApiVersion { get; set; } = "";
    public string PhoneNumberId { get; set; } = "";
    /// <summary>Secret. Machine variable WhatsApp__Token only; never in appsettings.json.</summary>
    public string Token { get; set; } = "";
    public string AuthHeaderName { get; set; } = "Authorization";
    /// <summary>Put before the token in the auth header ("Bearer x"). Empty sends the token as-is.</summary>
    public string AuthScheme { get; set; } = "Bearer";
    public string TemplateLanguage { get; set; } = "en";

    /// <summary>The configuration keys (not values) that must be set for a real provider and are empty.</summary>
    public IReadOnlyList<string> MissingKeys()
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(BaseUrl)) missing.Add("WhatsApp:BaseUrl");
        else if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            missing.Add("WhatsApp:BaseUrl (must be an https URL)");
        if (string.IsNullOrWhiteSpace(ApiVersion)) missing.Add("WhatsApp:ApiVersion");
        if (string.IsNullOrWhiteSpace(PhoneNumberId)) missing.Add("WhatsApp:PhoneNumberId");
        if (string.IsNullOrWhiteSpace(Token)) missing.Add("WhatsApp:Token");
        if (string.IsNullOrWhiteSpace(AuthHeaderName)) missing.Add("WhatsApp:AuthHeaderName");
        if (string.IsNullOrWhiteSpace(TemplateLanguage)) missing.Add("WhatsApp:TemplateLanguage");
        return missing;
    }

    /// <summary>{BaseUrl}/{ApiVersion}/{PhoneNumberId}/messages</summary>
    public Uri MessagesUri() =>
        new($"{BaseUrl.TrimEnd('/')}/{ApiVersion.Trim('/')}/{Uri.EscapeDataString(PhoneNumberId.Trim())}/messages");
}

/// <summary>
/// Sends an approved template through a Meta Cloud API compatible endpoint (Wacto). Typed HttpClient with a
/// 15-second timeout. Logs nothing itself: the caller logs ids, HTTP status and duration. The token, phone
/// number, request body and response body are never logged, and the token never reaches an error message.
/// </summary>
public class WhatsAppCloudSender(HttpClient http, IOptions<WhatsAppOptions> options) : IWhatsAppSender
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private readonly WhatsAppOptions _opt = options.Value;

    public async Task<WhatsAppSendResult> SendTemplateAsync(
        string phone, string templateName, IReadOnlyList<string> parameters, CancellationToken ct = default)
    {
        var payload = new
        {
            messaging_product = "whatsapp",
            to = phone.Trim().TrimStart('+'), // the API wants digits only
            type = "template",
            template = new
            {
                name = templateName,
                language = new { code = _opt.TemplateLanguage },
                components = new[]
                {
                    new { type = "body", parameters = parameters.Select(p => new { type = "text", text = p }).ToArray() },
                },
            },
        };

        var clock = Stopwatch.StartNew();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _opt.MessagesUri())
            {
                Content = JsonContent.Create(payload),
            };
            var token = _opt.Token.Trim();
            var scheme = _opt.AuthScheme.Trim();
            request.Headers.TryAddWithoutValidation(_opt.AuthHeaderName.Trim(),
                scheme.Length == 0 ? token : $"{scheme} {token}");

            using var response = await http.SendAsync(request, ct);
            var status = (int)response.StatusCode;
            var body = await response.Content.ReadAsStringAsync(ct);
            clock.Stop();

            if (response.IsSuccessStatusCode)
                return new WhatsAppSendResult(true, MessageId(body), HttpStatus: status, Duration: clock.Elapsed);

            var (code, message) = Error(body);
            var retryable = response.StatusCode == HttpStatusCode.TooManyRequests || status >= 500 || status < 400;
            return WhatsAppSendResult.Failed(Scrub($"HTTP {status}: {message}"), retryable,
                code, status, clock.Elapsed);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return WhatsAppSendResult.Failed("Request to WhatsApp timed out.", retryable: true, code: "timeout",
                duration: clock.Elapsed);
        }
        catch (HttpRequestException ex)
        {
            return WhatsAppSendResult.Failed($"Network error ({ex.HttpRequestError}).", retryable: true,
                code: "network", duration: clock.Elapsed);
        }
    }

    /// <summary>messages[0].id from the Meta shape; null when the body has none (still a success).</summary>
    private static string? MessageId(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("messages", out var messages)
                && messages.ValueKind == JsonValueKind.Array && messages.GetArrayLength() > 0
                && messages[0].ValueKind == JsonValueKind.Object
                && messages[0].TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
            {
                var value = id.GetString();
                return string.IsNullOrWhiteSpace(value) ? null : value.Length <= 100 ? value : value[..100];
            }
        }
        catch (JsonException) { }
        return null;
    }

    /// <summary>
    /// error.code and error.message from the Meta shape. Anything else (not JSON, another shape): the body
    /// text itself, which the result cuts to 500 characters.
    /// </summary>
    private static (string? Code, string Message) Error(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object
                && err.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.String)
            {
                string? code = err.TryGetProperty("code", out var c)
                    ? c.ValueKind == JsonValueKind.Number ? c.GetRawText() : c.GetString()
                    : null;
                return (code, msg.GetString() ?? "");
            }
        }
        catch (JsonException) { }
        return (null, string.IsNullOrWhiteSpace(body) ? "(empty response)" : body.Trim());
    }

    /// <summary>Defence in depth: the token never reaches the database.</summary>
    private string Scrub(string text) =>
        string.IsNullOrEmpty(_opt.Token) ? text : text.Replace(_opt.Token.Trim(), "***");
}

/// <summary>One call recorded by <see cref="FakeWhatsAppSender"/>.</summary>
public record WhatsAppCall(string Phone, string TemplateName, IReadOnlyList<string> Parameters);

/// <summary>
/// For tests and Development: records each call in memory and never contacts WhatsApp.
/// Set <see cref="FailWith"/> to make every call fail with that error (<see cref="FailRetryable"/> decides
/// whether it may be tried again), and <see cref="MessageId"/> for the id a success returns.
/// </summary>
public class FakeWhatsAppSender : IWhatsAppSender
{
    private readonly Lock _gate = new();
    private readonly List<WhatsAppCall> _calls = [];

    public string? FailWith { get; set; }
    public bool FailRetryable { get; set; } = true;
    public string? MessageId { get; set; } = "wamid.fake";

    public IReadOnlyList<WhatsAppCall> Calls
    {
        get { lock (_gate) return [.. _calls]; }
    }

    public void Clear()
    {
        lock (_gate) _calls.Clear();
        FailWith = null;
        FailRetryable = true;
        MessageId = "wamid.fake";
    }

    public Task<WhatsAppSendResult> SendTemplateAsync(
        string phone, string templateName, IReadOnlyList<string> parameters, CancellationToken ct = default)
    {
        lock (_gate) _calls.Add(new WhatsAppCall(phone, templateName, [.. parameters]));
        return Task.FromResult(FailWith is { } error
            ? WhatsAppSendResult.Failed(error, FailRetryable)
            : new WhatsAppSendResult(true, MessageId));
    }
}
