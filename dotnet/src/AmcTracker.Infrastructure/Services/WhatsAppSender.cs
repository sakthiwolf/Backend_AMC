using System.Globalization;
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
    /// <summary>Name of the approved template. Body parameters: customer name, project name, end date.</summary>
    public string TemplateName { get; set; } = "amc_renewal_reminder";
    public string LanguageCode { get; set; } = "en";
    /// <summary>Testing aid: when set (E.164), every message goes to this number instead of the customer.</summary>
    public string? TestRecipient { get; set; }
}

/// <summary>WhatsApp Business Cloud API client (typed HttpClient). Never logs tokens or full numbers.</summary>
public class WhatsAppSender(HttpClient http, IOptions<WhatsAppOptions> options) : IWhatsAppSender
{
    private readonly WhatsAppOptions _opt = options.Value;

    public async Task<WhatsAppSendResult> SendRenewalReminderAsync(
        WhatsAppReminderMessage m, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_opt.Token) || string.IsNullOrWhiteSpace(_opt.PhoneNumberId))
            return new WhatsAppSendResult(false, "WhatsApp is not configured (token or phone number id missing).");

        var to = (string.IsNullOrWhiteSpace(_opt.TestRecipient) ? m.Phone : _opt.TestRecipient).TrimStart('+');
        var payload = new
        {
            messaging_product = "whatsapp",
            to,
            type = "template",
            template = new
            {
                name = _opt.TemplateName,
                language = new { code = _opt.LanguageCode },
                components = new[]
                {
                    new
                    {
                        type = "body",
                        parameters = new[]
                        {
                            Text(m.CustomerName),
                            Text(string.IsNullOrWhiteSpace(m.ProjectName) ? "-" : m.ProjectName),
                            Text(m.EndDate.ToString("dd MMM yyyy", CultureInfo.InvariantCulture)),
                        },
                    },
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

    private static object Text(string value) => new { type = "text", text = value };

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
        // Defence in depth: never let a secret or number reach the database or logs.
        if (!string.IsNullOrEmpty(_opt.Token)) message = message.Replace(_opt.Token, "***");
        return message;
    }
}
