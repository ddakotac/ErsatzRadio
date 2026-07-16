using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace ErsatzTV.Core.Interrupts;

/// <summary>
///     Fire-and-forget webhook notifications for interrupt lifecycle events:
///     "enqueued" (delivery dispatch), "airing" (transcode starting), "completed"
///     (transcode finished), "expired" (ttl passed without airing). External systems
///     (Home Assistant) key automations off the event field - e.g. boost volume on
///     airing, restore on completed, cancel on expired - with no buffer guesswork.
/// </summary>
public static class InterruptWebhook
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(10) };

    public static void Fire(string webhookUrl, string eventName, InterruptQueueItem item, ILogger logger) =>
        _ = FireAsync(webhookUrl, eventName, item, logger);

    private static async Task FireAsync(
        string webhookUrl,
        string eventName,
        InterruptQueueItem item,
        ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(webhookUrl))
        {
            return;
        }

        try
        {
            var payload = new
            {
                @event = eventName,
                channel = item.ChannelNumber,
                title = item.Title,
                priority = item.Priority,
                style = item.Style.ToString().ToLowerInvariant(),
                durationSeconds = Math.Round(item.Duration.TotalSeconds, 2),
                streamUrl = $"/iptv/channel/{item.ChannelNumber}.m3u8"
            };

            using var content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await Client.PostAsync(webhookUrl, content);

            logger.LogDebug(
                "Interrupt webhook {Event} for {Title} on channel {Channel} responded {StatusCode}",
                eventName,
                item.Title,
                item.ChannelNumber,
                (int)response.StatusCode);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Interrupt webhook {Event} for {Title} failed",
                eventName,
                item.Title);
        }
    }
}
