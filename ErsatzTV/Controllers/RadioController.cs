using System.Diagnostics;
using System.Globalization;
using System.Text;
using ErsatzTV.Core.Interfaces.FFmpeg;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.Core.Interfaces.Streaming;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Interrupts;
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
    public Task GetRadioStreamMp3(string channelNumber, CancellationToken cancellationToken) =>
        StreamChannel(channelNumber, RadioFormat.Mp3, cancellationToken);

    [HttpGet("radio/{channelNumber}.aac")]
    public Task GetRadioStreamAac(string channelNumber, CancellationToken cancellationToken) =>
        StreamChannel(channelNumber, RadioFormat.Aac, cancellationToken);

    // format-agnostic url: serves whatever the channel is configured to use, so
    // clients and playlists never have to guess
    [HttpGet("radio/{channelNumber}")]
    public async Task GetRadioStream(string channelNumber, CancellationToken cancellationToken)
    {
        RadioFormat format = await ResolveFormat(channelNumber, cancellationToken);
        await StreamChannel(channelNumber, format, cancellationToken);
    }

    private async Task StreamChannel(
        string channelNumber,
        RadioFormat format,
        CancellationToken cancellationToken)
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

        // kestrel response buffering adds jitter to a paced stream
        HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>()
            ?.DisableBuffering();

        Response.StatusCode = 200;
        Response.ContentType = format == RadioFormat.Aac ? "audio/aac" : "audio/mpeg";
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

        // sync-group friendliness (multi-room squeezelite groups are sensitive to
        // gaps, format changes, and thin client buffers):
        //   aresample=async=1  - the internal hls session emits a DISCONTINUITY at
        //                        every item boundary and interrupt; this fills gaps
        //                        with silence and absorbs small overlaps instead of
        //                        letting them become jumps the client must chase
        //   NO readrate cap    - an icecast-style server is paced by TCP
        //                        backpressure, not an artificial realtime cap.
        //                        With a cap, a client whose buffer drains can
        //                        NEVER refill (we never send faster than it
        //                        consumes), so it sits in a permanent underrun
        //                        state - and squeezelite players report BUFFERING,
        //                        which silently disables Music Assistant's sync
        //                        correction for the entire group. Reading is
        //                        naturally limited to 1x by the live hls source
        //                        once the reader reaches the playlist edge.
        //   live_start_index   - start ~40s behind the playlist edge so producer
        //                        hiccups never starve the reader
        //   threads 1          - one audio stream needs no thread pool (the ts
        //                        wrapper does the same); saves ~10% of a core
        //                        per listener
        //   ar 48000           - matches the segmenter output, so no per-listener
        //                        sample rate conversion
        //   fixed -ar/-ac/CBR  - never change sample rate, layout, or bitrate
        //                        mid-stream (format changes desync groups)
        //   write_xing/id3v2 off - bare mp3 frames, like an icecast mount
        var arguments = new List<string>
        {
            "-nostdin", "-hide_banner", "-loglevel", "error", "-nostats",
            "-threads", "1",
            "-fflags", "+genpts+discardcorrupt+igndts",
            "-live_start_index", "-10",
            "-i", internalUrl,
            "-map", "0:a"
        };

        if (format == RadioFormat.Aac)
        {
            // copy the aac already inside the segments: no decode, no encode,
            // original quality, ~1% of a core instead of ~10%
            arguments.AddRange(["-c:a", "copy", "-f", "adts"]);
        }
        else
        {
            bool gapFill = await GetGapFill(channelNumber, cancellationToken);
            if (gapFill)
            {
                arguments.AddRange(["-af", "aresample=async=1:first_pts=0"]);
            }

            int quality = await GetMp3Quality(channelNumber, cancellationToken);

            arguments.AddRange(
            [
                "-c:a", "libmp3lame",
                "-b:a", "192k",
                "-compression_level", quality.ToString(CultureInfo.InvariantCulture),
                "-ar", "48000", "-ac", "2",
                "-write_xing", "0", "-id3v2_version", "0",
                "-f", "mp3"
            ]);
        }

        arguments.AddRange(["-flush_packets", "1", "pipe:1"]);

        foreach (string arg in arguments)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = new Process();
        process.StartInfo = startInfo;

        _logger.LogInformation(
            "Starting icy radio stream for channel {Channel} ({Name}); format={Format} metadata={Metadata}",
            channelNumber,
            channelName,
            format,
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
                    await output.FlushAsync(cancellationToken);
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

                await output.FlushAsync(cancellationToken);
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

    private async Task<RadioFormat> ResolveFormat(string channelNumber, CancellationToken cancellationToken)
    {
        Option<string> maybeFormat = await _configElementRepository.GetValue<string>(
            ConfigElementKey.RadioFormat(channelNumber),
            cancellationToken);

        foreach (string value in maybeFormat)
        {
            if (string.Equals(value, "mp3", StringComparison.OrdinalIgnoreCase))
            {
                return RadioFormat.Mp3;
            }
        }

        // aac (stream copy) is the default: cheapest, and preserves the original
        // audio rather than transcoding it a second time
        return RadioFormat.Aac;
    }

    private async Task<int> GetMp3Quality(string channelNumber, CancellationToken cancellationToken)
    {
        Option<int> maybeQuality = await _configElementRepository.GetValue<int>(
            ConfigElementKey.RadioMp3Quality(channelNumber),
            cancellationToken);

        foreach (int quality in maybeQuality)
        {
            return Math.Clamp(quality, 0, 9);
        }

        return 7;
    }

    private async Task<bool> GetGapFill(string channelNumber, CancellationToken cancellationToken)
    {
        Option<bool> maybeGapFill = await _configElementRepository.GetValue<bool>(
            ConfigElementKey.RadioGapFill(channelNumber),
            cancellationToken);

        foreach (bool gapFill in maybeGapFill)
        {
            return gapFill;
        }

        return true;
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
