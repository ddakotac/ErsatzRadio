using ErsatzTV.Application.Channels;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Interfaces.Repositories;
using LanguageExt;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace ErsatzTV.Controllers;

// configuration for the per-channel "now playing" tts announcer:
//   GET /api/channels/{channelNumber}/announcer
//   PUT /api/channels/{channelNumber}/announcer  { enabled, template, style, duckPercent }
//   GET /api/announcer/tts
//   PUT /api/announcer/tts  { url }
//
// the tts endpoint must accept POST with a plain-text body and respond with audio bytes
// (e.g. a piper http server). template variables: {title} {artist} {album} {show}/{author}
// {season}/{book}. style: duck (default; mixes over the item's opening at duckPercent bed
// volume) or replace.
[ApiController]
[ApiExplorerSettings(IgnoreApi = true)]
public class AnnouncerController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly IConfigElementRepository _configElementRepository;

    public AnnouncerController(IMediator mediator, IConfigElementRepository configElementRepository)
    {
        _mediator = mediator;
        _configElementRepository = configElementRepository;
    }

    [HttpGet("api/channels/{channelNumber}/announcer")]
    public async Task<IActionResult> GetChannelAnnouncer(string channelNumber, CancellationToken cancellationToken)
    {
        Option<ChannelViewModel> maybeChannel = await _mediator.Send(
            new GetChannelByNumber(channelNumber),
            cancellationToken);

        if (maybeChannel.IsNone)
        {
            return NotFound(new { error = $"channel {channelNumber} does not exist" });
        }

        bool enabled = await _configElementRepository
            .GetValue<bool>(ConfigElementKey.AnnouncerEnabled(channelNumber), cancellationToken)
            .Map(o => o.IfNone(false));

        string template = await _configElementRepository
            .GetValue<string>(ConfigElementKey.AnnouncerTemplate(channelNumber), cancellationToken)
            .Map(o => o.IfNone("Now playing: {title}"));

        string style = await _configElementRepository
            .GetValue<string>(ConfigElementKey.AnnouncerStyle(channelNumber), cancellationToken)
            .Map(o => o.IfNone("duck"));

        int duckPercent = await _configElementRepository
            .GetValue<int>(ConfigElementKey.AnnouncerDuckPercent(channelNumber), cancellationToken)
            .Map(o => o.IfNone(30));

        return Ok(new { channelNumber, enabled, template, style, duckPercent });
    }

    [HttpPut("api/channels/{channelNumber}/announcer")]
    public async Task<IActionResult> SetChannelAnnouncer(
        string channelNumber,
        [FromBody] AnnouncerConfigRequest request,
        CancellationToken cancellationToken)
    {
        Option<ChannelViewModel> maybeChannel = await _mediator.Send(
            new GetChannelByNumber(channelNumber),
            cancellationToken);

        if (maybeChannel.IsNone)
        {
            return NotFound(new { error = $"channel {channelNumber} does not exist" });
        }

        if (request.Style is not null &&
            !string.Equals(request.Style, "duck", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(request.Style, "replace", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new { error = "style must be 'duck' or 'replace'" });
        }

        if (request.DuckPercent is < 0 or > 100)
        {
            return BadRequest(new { error = "duckPercent must be between 0 and 100" });
        }

        if (request.Enabled.HasValue)
        {
            await _configElementRepository.Upsert(
                ConfigElementKey.AnnouncerEnabled(channelNumber),
                request.Enabled.Value,
                cancellationToken);
        }

        if (request.Template is not null)
        {
            await _configElementRepository.Upsert(
                ConfigElementKey.AnnouncerTemplate(channelNumber),
                request.Template,
                cancellationToken);
        }

        if (request.Style is not null)
        {
            await _configElementRepository.Upsert(
                ConfigElementKey.AnnouncerStyle(channelNumber),
                request.Style.ToLowerInvariant(),
                cancellationToken);
        }

        if (request.DuckPercent.HasValue)
        {
            await _configElementRepository.Upsert(
                ConfigElementKey.AnnouncerDuckPercent(channelNumber),
                request.DuckPercent.Value,
                cancellationToken);
        }

        return await GetChannelAnnouncer(channelNumber, cancellationToken);
    }

    [HttpGet("api/announcer/tts")]
    public async Task<IActionResult> GetTts(CancellationToken cancellationToken)
    {
        string url = await _configElementRepository
            .GetValue<string>(ConfigElementKey.AnnouncerTtsUrl, cancellationToken)
            .Map(o => o.IfNone(string.Empty));

        return Ok(new { url });
    }

    [HttpPut("api/announcer/tts")]
    public async Task<IActionResult> SetTts(
        [FromBody] AnnouncerTtsRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request?.Url) ||
            !Uri.TryCreate(request.Url, UriKind.Absolute, out _))
        {
            return BadRequest(new { error = "url must be a valid absolute url" });
        }

        await _configElementRepository.Upsert(ConfigElementKey.AnnouncerTtsUrl, request.Url, cancellationToken);

        return Ok(new { url = request.Url });
    }

    public record AnnouncerConfigRequest(bool? Enabled, string Template, string Style, int? DuckPercent);

    public record AnnouncerTtsRequest(string Url);
}
