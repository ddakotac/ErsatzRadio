using System.Text;
using System.Text.Json;
using ErsatzTV.Core;
using ErsatzTV.Core.Interfaces.FFmpeg;
using ErsatzTV.Core.Interfaces.Streaming;
using ErsatzTV.Core.Interfaces.Tts;
using ErsatzTV.Core.Interrupts;

namespace ErsatzTV.Services;

/// <summary>
///     Shared dispatch for delivery sources (watch folders, rss feeds): enqueues the
///     content on each mapped channel with optional intro/outro TTS announcements
///     chained around it (the worker plays consecutive queue items back-to-back), and
///     fires an optional webhook so external systems (Home Assistant) can react -
///     tune a player, preset volume, send a notification.
///     Intro/outro always play replace-style; the content uses the configured style.
/// </summary>
public static class DeliveryDispatch
{
    public static async Task Dispatch(
        string source,
        string name,
        string title,
        string contentPath,
        TimeSpan contentDuration,
        List<string> channels,
        int priority,
        InterruptStyle style,
        double duckBedVolume,
        int ttlSeconds,
        bool deleteContentWhenDone,
        string introText,
        string outroText,
        string ttsEndpoint,
        string voice,
        string webhookUrl,
        IChannelInterruptService interruptService,
        ITtsSynthesisService ttsSynthesisService,
        IFFmpegSegmenterService segmenterService,
        IHttpClientFactory httpClientFactory,
        Func<string, CancellationToken, Task<Option<TimeSpan>>> probeDuration,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = DateTimeOffset.Now;
        DateTimeOffset expiresAt = now.AddSeconds(ttlSeconds);

        // synthesize intro/outro once; per-channel copies below
        Option<(string Path, TimeSpan Duration)> intro =
            await Synthesize(RenderTemplate(introText, name, title), ttsEndpoint, voice,
                ttsSynthesisService, probeDuration, logger, cancellationToken);

        Option<(string Path, TimeSpan Duration)> outro =
            await Synthesize(RenderTemplate(outroText, name, title), ttsEndpoint, voice,
                ttsSynthesisService, probeDuration, logger, cancellationToken);

        List<string> distinctChannels = channels.Distinct().ToList();
        var sessionStates = new Dictionary<string, bool>();
        var sequence = 0;

        var firstChannel = true;
        foreach (string channelNumber in distinctChannels)
        {
            // consecutive queue items with ordered timestamps play back-to-back
            foreach ((string introPath, TimeSpan introDuration) in intro)
            {
                Enqueue(
                    interruptService,
                    channelNumber,
                    firstChannel ? introPath : CopyFile(introPath),
                    $"{name}: intro",
                    priority,
                    now.AddMilliseconds(sequence++),
                    expiresAt,
                    introDuration,
                    deleteFileWhenDone: true,
                    InterruptStyle.Replace,
                    duckBedVolume);
            }

            Enqueue(
                interruptService,
                channelNumber,
                firstChannel || !deleteContentWhenDone ? contentPath : CopyFile(contentPath),
                $"{name}: {title}",
                priority,
                now.AddMilliseconds(sequence++),
                expiresAt,
                contentDuration,
                deleteContentWhenDone,
                style,
                duckBedVolume);

            foreach ((string outroPath, TimeSpan outroDuration) in outro)
            {
                Enqueue(
                    interruptService,
                    channelNumber,
                    firstChannel ? outroPath : CopyFile(outroPath),
                    $"{name}: outro",
                    priority,
                    now.AddMilliseconds(sequence++),
                    expiresAt,
                    outroDuration,
                    deleteFileWhenDone: true,
                    InterruptStyle.Replace,
                    duckBedVolume);
            }

            firstChannel = false;

            bool sessionActive = segmenterService.IsActive(channelNumber);
            sessionStates[channelNumber] = sessionActive;

            logger.LogInformation(
                "{Source} {Name}: enqueued {Title} on channel {Channel} (priority {Priority}, {Style}{Extras}); sessionActive={SessionActive}{Warning}",
                source,
                name,
                title,
                channelNumber,
                priority,
                style,
                (intro.IsSome ? ", intro" : string.Empty) + (outro.IsSome ? ", outro" : string.Empty),
                sessionActive,
                sessionActive
                    ? string.Empty
                    : " - NOBODY IS LISTENING; the item will expire unless a session starts before its ttl");
        }

        await FireWebhook(
            webhookUrl,
            source,
            name,
            title,
            contentDuration,
            priority,
            style,
            expiresAt,
            sessionStates,
            httpClientFactory,
            logger,
            cancellationToken);
    }

    private static void Enqueue(
        IChannelInterruptService interruptService,
        string channelNumber,
        string path,
        string title,
        int priority,
        DateTimeOffset enqueuedAt,
        DateTimeOffset expiresAt,
        TimeSpan duration,
        bool deleteFileWhenDone,
        InterruptStyle style,
        double duckBedVolume) =>
        interruptService.Enqueue(
            new InterruptQueueItem
            {
                Id = Guid.NewGuid(),
                ChannelNumber = channelNumber,
                Path = path,
                Title = title,
                Priority = priority,
                EnqueuedAt = enqueuedAt,
                ExpiresAt = expiresAt,
                Duration = duration,
                DeleteFileWhenDone = deleteFileWhenDone,
                Style = style,
                DuckBedVolume = duckBedVolume
            });

    private static async Task<Option<(string Path, TimeSpan Duration)>> Synthesize(
        string text,
        string ttsEndpoint,
        string voice,
        ITtsSynthesisService ttsSynthesisService,
        Func<string, CancellationToken, Task<Option<TimeSpan>>> probeDuration,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Option<(string, TimeSpan)>.None;
        }

        Option<string> maybePath = await ttsSynthesisService.SynthesizeToFile(
            text,
            ttsEndpoint,
            voice,
            cancellationToken);

        foreach (string path in maybePath)
        {
            Option<TimeSpan> maybeDuration = await probeDuration(path, cancellationToken);
            foreach (TimeSpan duration in maybeDuration)
            {
                return (path, duration);
            }

            logger.LogWarning("Failed to probe synthesized announcement; skipping it");
            TryDelete(path, logger);
        }

        return Option<(string, TimeSpan)>.None;
    }

    private static string RenderTemplate(string template, string name, string title) =>
        string.IsNullOrWhiteSpace(template)
            ? string.Empty
            : template
                .Replace("{name}", name, StringComparison.OrdinalIgnoreCase)
                .Replace("{title}", title, StringComparison.OrdinalIgnoreCase);

    private static string CopyFile(string sourcePath)
    {
        string copyPath = Path.Combine(
            FileSystemLayout.InterruptsFolder,
            $"delivery-{Guid.NewGuid()}{Path.GetExtension(sourcePath)}");

        File.Copy(sourcePath, copyPath);
        return copyPath;
    }

    private static async Task FireWebhook(
        string webhookUrl,
        string source,
        string name,
        string title,
        TimeSpan duration,
        int priority,
        InterruptStyle style,
        DateTimeOffset expiresAt,
        Dictionary<string, bool> sessionStates,
        IHttpClientFactory httpClientFactory,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(webhookUrl))
        {
            return;
        }

        try
        {
            var payload = new
            {
                source,
                name,
                title,
                priority,
                style = style.ToString().ToLowerInvariant(),
                durationSeconds = Math.Round(duration.TotalSeconds, 2),
                expiresAt,
                channels = sessionStates.Map(kvp => new
                {
                    channel = kvp.Key,
                    sessionActive = kvp.Value,
                    streamUrl = $"/iptv/channel/{kvp.Key}.m3u8"
                }).ToList()
            };

            HttpClient client = httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(10);

            using var content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await client.PostAsync(webhookUrl, content, cancellationToken);

            logger.LogInformation(
                "{Source} {Name}: webhook {Url} responded {StatusCode}",
                source,
                name,
                webhookUrl,
                (int)response.StatusCode);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "{Source} {Name}: webhook {Url} failed", source, name, webhookUrl);
        }
    }

    private static void TryDelete(string path, ILogger logger)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to delete delivery temp file {Path}", path);
        }
    }
}
