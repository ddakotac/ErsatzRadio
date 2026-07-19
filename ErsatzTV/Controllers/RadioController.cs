using System.Diagnostics;
using System.Text;
using ErsatzTV.Core.Interfaces.FFmpeg;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.Core.Interfaces.Streaming;
using ErsatzTV.Core.Domain;
using LanguageExt;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ErsatzTV.Infrastructure.Data;
using ErsatzTV.Infrastructure.Extensions;

namespace ErsatzTV.Controllers;

// direct internet-radio endpoint: plain http audio with ICY (shoutcast) metadata,
// so players like Music Assistant show live now-playing titles - scheduled items
// from the playout, overridden by the currently airing interrupt ("S2 Underground:
// The Wire..." while a delivery breaks in).
//   GET /radio/{channelNumber}.mp3
// clients that send "Icy-MetaData: 1" get StreamTitle blocks every icy-metaint
// bytes; everyone else gets a clean mp3 stream. one ffmpeg per listener, wrapping
// the same internal hls session as the mpeg-ts endpoint (interrupts, announcer,
// and ducks all function; the session starts on first listener).
[ApiController]
[ApiExplorerSettings(IgnoreApi = true)]
public class RadioController : ControllerBase
{
    private const int MetaInt = 16000;
    private static readonly TimeSpan TitleRefreshInterval = TimeSpan.FromSeconds(5);

    private readonly IDbContextFactory<TvContext> _dbContextFactory;
    private readonly IChannelAnnouncerService _announcerService;
    private readonly IChannelInterruptService _interruptService;
    private readonly IConfigElementRepository _configElementRepository;
    private readonly ILogger<RadioController> _logger;

    public RadioController(
        IDbContextFactory<TvContext> dbContextFactory,
        IChannelAnnouncerService announcerService,
        IChannelInterruptService interruptService,
        IConfigElementRepository configElementRepository,
        ILogger<RadioController> logger)
    {
        _dbContextFactory = dbContextFactory;
        _announcerService = announcerService;
        _interruptService = interruptService;
        _configElementRepository = configElementRepository;
        _logger = logger;
    }

    [HttpGet("radio/{channelNumber}.mp3")]
    public async Task GetRadioStream(string channelNumber, CancellationToken cancellationToken)
    {
        await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        Option<Channel> maybeChannel = await dbContext.Channels
            .AsNoTracking()
            .SelectOneAsync(c => c.Number, c => c.Number == channelNumber, cancellationToken);

        if (maybeChannel.IsNone)
        {
            Response.StatusCode = 404;
            return;
        }

        string channelName = maybeChannel.Map(c => c.Name).IfNone(channelNumber);

        Option<string> maybeFFmpegPath = await _configElementRepository.GetValue<string>(
            ConfigElementKey.FFmpegPath,
            cancellationToken);

        if (maybeFFmpegPath.IsNone)
        {
            Response.StatusCode = 500;
            return;
        }

        bool wantsMetadata = Request.Headers.TryGetValue("Icy-MetaData", out var icyHeader) &&
                             icyHeader.ToString().Trim() == "1";

        Response.StatusCode = 200;
        Response.ContentType = "audio/mpeg";
        Response.Headers["Cache-Control"] = "no-cache, no-store";
        Response.Headers["icy-name"] = channelName;
        Response.Headers["icy-genre"] = "Various";
        if (wantsMetadata)
        {
            Response.Headers["icy-metaint"] = MetaInt.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        int localPort = HttpContext.Connection.LocalPort;
        string internalUrl = $"http://localhost:{localPort}/iptv/channel/{channelNumber}.m3u8?mode=segmenter";

        var startInfo = new ProcessStartInfo
        {
            FileName = maybeFFmpegPath.IfNone("ffmpeg"),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        foreach (string arg in new[]
                 {
                     "-nostdin", "-hide_banner", "-loglevel", "error", "-nostats",
                     "-fflags", "+genpts+discardcorrupt+igndts",
                     "-readrate", "1.0",
                     "-i", internalUrl,
                     "-map", "0:a",
                     "-c:a", "libmp3lame", "-b:a", "192k", "-ar", "44100", "-ac", "2",
                     "-f", "mp3", "pipe:1"
                 })
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = new Process();
        process.StartInfo = startInfo;

        _logger.LogInformation(
            "Starting icy radio stream for channel {Channel} ({Name}); metadata={Metadata}",
            channelNumber,
            channelName,
            wantsMetadata);

        try
        {
            process.Start();
            _ = process.StandardError.ReadToEndAsync(cancellationToken); // drain

            Stream audio = process.StandardOutput.BaseStream;
            Stream output = Response.Body;

            var buffer = new byte[8192];
            var bytesSinceMeta = 0;
            string lastMeta = null;
            DateTimeOffset lastTitleCheck = DateTimeOffset.MinValue;
            string currentTitle = channelName;
            string currentArtUrl = null;

            // https MASS ui + http art = browser mixed-content block (blank square);
            // set radio.artwork_base_url to a proxied https host when needed
            Option<string> maybeBase = await _configElementRepository.GetValue<string>(
                ConfigElementKey.RadioArtworkBaseUrl,
                cancellationToken);

            string artBase = maybeBase
                .Filter(b => !string.IsNullOrWhiteSpace(b))
                .Map(b => b.TrimEnd('/'))
                .IfNone($"{Request.Scheme}://{Request.Host}");

            while (!cancellationToken.IsCancellationRequested)
            {
                int read = await audio.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (read <= 0)
                {
                    break;
                }

                if (!wantsMetadata)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    continue;
                }

                var offset = 0;
                while (offset < read)
                {
                    int chunk = Math.Min(read - offset, MetaInt - bytesSinceMeta);
                    await output.WriteAsync(buffer.AsMemory(offset, chunk), cancellationToken);
                    offset += chunk;
                    bytesSinceMeta += chunk;

                    if (bytesSinceMeta == MetaInt)
                    {
                        bytesSinceMeta = 0;

                        if (DateTimeOffset.Now - lastTitleCheck > TitleRefreshInterval)
                        {
                            lastTitleCheck = DateTimeOffset.Now;
                            (currentTitle, currentArtUrl) = await ResolveNowPlaying(
                                channelNumber,
                                channelName,
                                artBase,
                                cancellationToken);
                        }

                        string metaKey = $"{currentTitle}|{currentArtUrl}";
                        byte[] metaBlock = BuildMetadataBlock(
                            metaKey == lastMeta ? null : currentTitle,
                            currentArtUrl);

                        lastMeta = metaKey;
                        await output.WriteAsync(metaBlock, cancellationToken);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // client disconnected
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Icy radio stream for channel {Channel} ended", channelNumber);
        }
        finally
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // already gone
            }

            _logger.LogInformation("Icy radio stream for channel {Channel} closed", channelNumber);
        }
    }

    // GET/PUT /api/radio/settings - artworkBaseUrl override for the icy StreamUrl
    // (needed when players/browsers require https art; point at your reverse proxy)
    [HttpGet("api/radio/settings")]
    public async Task<IActionResult> GetSettings(CancellationToken cancellationToken)
    {
        Option<string> maybeBase = await _configElementRepository.GetValue<string>(
            ConfigElementKey.RadioArtworkBaseUrl,
            cancellationToken);

        return Ok(new { artworkBaseUrl = maybeBase.IfNone(string.Empty) });
    }

    [HttpPut("api/radio/settings")]
    public async Task<IActionResult> PutSettings(
        [FromBody] RadioSettingsRequest request,
        CancellationToken cancellationToken)
    {
        string url = request?.ArtworkBaseUrl?.Trim() ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(url) &&
            !(Uri.TryCreate(url, UriKind.Absolute, out Uri parsed) &&
              (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps)))
        {
            return BadRequest(new { error = "artworkBaseUrl must be an absolute http(s) url (or empty to clear)" });
        }

        await _configElementRepository.Upsert(
            ConfigElementKey.RadioArtworkBaseUrl,
            url,
            cancellationToken);

        return Ok(new { artworkBaseUrl = url });
    }

    public record RadioSettingsRequest(string ArtworkBaseUrl);

    private async Task<(string Title, string ArtUrl)> ResolveNowPlaying(
        string channelNumber,
        string channelName,
        string artBase,
        CancellationToken cancellationToken)
    {
        // an airing interrupt (delivery, tts, broadcast) overrides the schedule
        foreach (string airing in _interruptService.GetNowAiring(channelNumber))
        {
            return (airing, null);
        }

        try
        {
            Option<NowPlayingInfo> maybeInfo = await _announcerService.GetNowPlayingForCurrentItem(
                channelNumber,
                cancellationToken);

            foreach (NowPlayingInfo info in maybeInfo)
            {
                string artUrl = string.IsNullOrWhiteSpace(info.ArtworkRelativeUrl)
                    ? null
                    : $"{artBase}{info.ArtworkRelativeUrl}";

                return (info.Title, artUrl);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to resolve now-playing for channel {Channel}", channelNumber);
        }

        return (channelName, null);
    }

    // icy metadata block: 1 length byte (len/16) + "StreamTitle='...';" (plus
    // optional StreamUrl='...' - many players treat it as cover art) null-padded
    // to a multiple of 16. an empty block (single zero byte) means "no change".
    private static byte[] BuildMetadataBlock(string title, string artUrl = null)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return [0];
        }

        // icy titles are latin-ish and quote-terminated; sanitize
        string safe = title.Replace("'", "\u2019").Replace(";", ",");
        if (safe.Length > 480)
        {
            safe = safe[..480];
        }

        string meta = $"StreamTitle='{safe}';";
        if (!string.IsNullOrWhiteSpace(artUrl))
        {
            meta += $"StreamUrl='{artUrl}';";
        }

        byte[] text = Encoding.UTF8.GetBytes(meta);
        int blocks = (text.Length + 15) / 16;

        var result = new byte[1 + blocks * 16];
        result[0] = (byte)blocks;
        Buffer.BlockCopy(text, 0, result, 1, text.Length);
        return result;
    }
}
