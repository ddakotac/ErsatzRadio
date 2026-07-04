using ErsatzTV.Application.Audiobookshelf;
using ErsatzTV.Core;
using ErsatzTV.Core.Audiobookshelf;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace ErsatzTV.Controllers;

// temporary api surface for audiobookshelf until blazor ui pages exist.
// exercise the full vertical with curl:
//   POST /api/audiobookshelf/secrets { "address": "...", "username": "...", "password": "..." }
//   GET  /api/audiobookshelf/sources
//   GET  /api/audiobookshelf/sources/{id}/libraries
//   POST /api/audiobookshelf/libraries/preferences [ { "id": 1, "shouldSyncItems": true } ]
//   POST /api/audiobookshelf/libraries/{id}/scan?deep=false
[ApiController]
[ApiExplorerSettings(IgnoreApi = true)]
[Route("api/audiobookshelf")]
public class AudiobookshelfController : ControllerBase
{
    private readonly IMediator _mediator;

    public AudiobookshelfController(IMediator mediator) => _mediator = mediator;

    [HttpPost("secrets")]
    public async Task<IActionResult> SaveSecrets(
        [FromBody] AudiobookshelfSecretsRequest request,
        CancellationToken cancellationToken)
    {
        var secrets = new AudiobookshelfSecrets
        {
            Address = request.Address,
            ApiKey = request.Token
        };

        Either<BaseError, Unit> result = await _mediator.Send(new SaveAudiobookshelfSecrets(secrets), cancellationToken);
        return result.Match<IActionResult>(
            _ => Ok(new { status = "connected" }),
            error => BadRequest(new { error = error.Value }));
    }

    [HttpGet("sources")]
    public async Task<IActionResult> GetSources(CancellationToken cancellationToken)
    {
        List<AudiobookshelfMediaSourceViewModel> sources =
            await _mediator.Send(new GetAllAudiobookshelfMediaSources(), cancellationToken);
        return Ok(sources);
    }

    [HttpGet("sources/{id:int}/libraries")]
    public async Task<IActionResult> GetLibraries(int id, CancellationToken cancellationToken)
    {
        List<AudiobookshelfLibraryViewModel> libraries =
            await _mediator.Send(new GetAudiobookshelfLibrariesBySourceId(id), cancellationToken);
        return Ok(libraries);
    }

    [HttpPost("libraries/preferences")]
    public async Task<IActionResult> UpdateLibraryPreferences(
        [FromBody] List<AudiobookshelfLibraryPreference> preferences,
        CancellationToken cancellationToken)
    {
        Either<BaseError, Unit> result = await _mediator.Send(
            new UpdateAudiobookshelfLibraryPreferences(preferences),
            cancellationToken);
        return result.Match<IActionResult>(
            _ => Ok(new { status = "updated" }),
            error => BadRequest(new { error = error.Value }));
    }

    [HttpGet("sources/{id:int}/path-replacements")]
    public async Task<IActionResult> GetPathReplacements(int id, CancellationToken cancellationToken)
    {
        List<AudiobookshelfPathReplacementViewModel> replacements =
            await _mediator.Send(new GetAudiobookshelfPathReplacementsBySourceId(id), cancellationToken);
        return Ok(replacements);
    }

    [HttpPost("sources/{id:int}/path-replacements")]
    public async Task<IActionResult> UpdatePathReplacements(
        int id,
        [FromBody] List<AudiobookshelfPathReplacementItem> replacements,
        CancellationToken cancellationToken)
    {
        Either<BaseError, Unit> result = await _mediator.Send(
            new UpdateAudiobookshelfPathReplacements(id, replacements),
            cancellationToken);
        return result.Match<IActionResult>(
            _ => Ok(new { status = "updated" }),
            error => BadRequest(new { error = error.Value }));
    }

    [HttpPost("libraries/{id:int}/scan")]
    public async Task<IActionResult> ScanLibrary(
        int id,
        [FromQuery] bool deep,
        CancellationToken cancellationToken)
    {
        Either<BaseError, string> result = await _mediator.Send(
            new ForceSynchronizeAudiobookshelfLibraryById(id, deep),
            cancellationToken);
        return result.Match<IActionResult>(
            name => Ok(new { status = "scanned", library = name }),
            error => BadRequest(new { error = error.Value }));
    }

    public record AudiobookshelfSecretsRequest(string Address, string Token);
}
