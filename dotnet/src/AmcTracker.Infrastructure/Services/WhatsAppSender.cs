using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AmcTracker.Application.Abstractions;
using Microsoft.Extensions.Options;

namespace AmcTracker.Infrastructure.Services;

public class WhatsAppOptions
{
    public const string Section = "WhatsApp";

    public string BaseUrl { get; set; } = "https://graph.facebook.com/";
    public string ApiVersion { get; set; } = "v21.0";
    public string Token { get; set; } = "";
    public string PhoneNumberId { get; set; } = "";
    public string LanguageCode { get; set; } = "en";
    /// <summary>
    /// Use <see cref="FakeWhatsAppSender"/> (records, never sends). Defaults to true in Development and false
    /// elsewhere. The template name and test phone live under "Reminders".
    /// </summary>
    public bool? UseFake { get; set; }
}

/// <summary>WhatsApp Business Cloud API client (typed HttpClient). Never logs tokens, numbers or message text.</summary>
public class WhatsAppCloudSender(HttpClient http, IOptions<WhatsAppOptions> options) : IWhatsAppSender
{
    private readonly WhatsAppOptions _opt = options.Value;

    public async Task<WhatsAppSendResult> SendTemplateAsync(
        string phone, string templateName, IReadOnlyList<string> parameters, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_opt.Token) || string.IsNullOrWhiteSpace(_opt.PhoneNumberId))
            return new WhatsAppSendResult(false, "WhatsApp is not configured (token or phone number id missing).");

        var payload = new
        {
            messaging_product = "whatsapp",
            to = phone.TrimStart('+'), // the Cloud API wants digits only
            type = "template",
            template = new
            {
                name = templateName,
                language = new { code = _opt.LanguageCode },
                components = new[]
                {
                    new { type = "body", parameters = parameters.Select(p => new { type = "text", text = p }).ToArray() },
                },
            },
        };

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_opt.ApiVersion}/{_opt.PhoneNumberId}/messages")
            {
                Content = JsonContent.Create(payload),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _opt.Token);
            using var response = await http.SendAsync(request, ct);
            if (response.IsSuccessStatusCode) return new WhatsAppSendResult(true);

            var body = await response.Content.ReadAsStringAsync(ct);
            return new WhatsAppSendResult(false, $"HTTP {(int)response.StatusCode}: {ExtractMessage(body)}");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return new WhatsAppSendResult(false, "Request to WhatsApp timed out.");
        }
        catch (HttpRequestException ex)
        {
            return new WhatsAppSendResult(false, $"Network error ({ex.GetType().Name}).");
        }
    }

    private string ExtractMessage(string body)
    {
        string message;
        try
        {
            using var doc = JsonDocument.Parse(body);
            message = doc.RootElement.TryGetProperty("error", out var err)
                && err.TryGetProperty("message", out var msg) ? msg.GetString() ?? "error" : "error";
        }
        catch (JsonException) { message = "unexpected response"; }
        // Defence in depth: never let a secret reach the database or logs.
        if (!string.IsNullOrEmpty(_opt.Token)) message = message.Replace(_opt.Token, "***");
        return message;
    }
}

/// <summary>One call recorded by <see cref="FakeWhatsAppSender"/>.</summary>
public record WhatsAppCall(string Phone, string TemplateName, IReadOnlyList<string> Parameters);

/// <summary>
/// For tests and Development: records each call in memory and never contacts WhatsApp.
/// Set <see cref="FailWith"/> to make every call fail with that error.
/// </summary>
public class FakeWhatsAppSender : IWhatsAppSender
{
    private readonly Lock _gate = new();
    private readonly List<WhatsAppCall> _calls = [];

    public string? FailWith { get; set; }

    public IReadOnlyList<WhatsAppCall> Calls
    {
        get { lock (_gate) return [.. _calls]; }
    }

    public void Clear()
    {
        lock (_gate) _calls.Clear();
        FailWith = null;
    }

    public Task<WhatsAppSendResult> SendTemplateAsync(
        string phone, string templateName, IReadOnlyList<string> parameters, CancellationToken ct = default)
    {
        lock (_gate) _calls.Add(new WhatsAppCall(phone, templateName, [.. parameters]));
        return Task.FromResult(FailWith is { } error
            ? new WhatsAppSendResult(false, error)
            : new WhatsAppSendResult(true));
    }
}
