using ErsatzTV.Application.Navidrome;
using ErsatzTV.Core;
using ErsatzTV.Core.Navidrome;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace ErsatzTV.Controllers;

// temporary api surface for navidrome until blazor ui pages exist.
// exercise the full vertical with curl:
//   POST /api/navidrome/secrets { "address": "...", "username": "...", "password": "..." }
//   GET  /api/navidrome/sources
//   GET  /api/navidrome/sources/{id}/libraries
//   POST /api/navidrome/libraries/preferences [ { "id": 1, "shouldSyncItems": true } ]
//   POST /api/navidrome/libraries/{id}/scan?deep=false
[ApiController]
[ApiExplorerSettings(IgnoreApi = true)]
[Route("api/navidrome")]
public class NavidromeController : ControllerBase
{
    private readonly IMediator _mediator;

    public NavidromeController(IMediator mediator) => _mediator = mediator;

    [HttpPost("secrets")]
    public async Task<IActionResult> SaveSecrets(
        [FromBody] NavidromeSecretsRequest request,
        CancellationToken cancellationToken)
    {
        var secrets = new NavidromeSecrets
        {
            Address = request.Address,
            Username = request.Username,
            ApiKey = request.Password
        };

        Either<BaseError, Unit> result = await _mediator.Send(new SaveNavidromeSecrets(secrets), cancellationToken);
        return result.Match<IActionResult>(
            _ => Ok(new { status = "connected" }),
            error => BadRequest(new { error = error.Value }));
    }

    [HttpGet("sources")]
    public async Task<IActionResult> GetSources(CancellationToken cancellationToken)
    {
        List<NavidromeMediaSourceViewModel> sources =
            await _mediator.Send(new GetAllNavidromeMediaSources(), cancellationToken);
        return Ok(sources);
    }

    [HttpGet("sources/{id:int}/libraries")]
    public async Task<IActionResult> GetLibraries(int id, CancellationToken cancellationToken)
    {
        List<NavidromeLibraryViewModel> libraries =
            await _mediator.Send(new GetNavidromeLibrariesBySourceId(id), cancellationToken);
        return Ok(libraries);
    }

    [HttpPost("libraries/preferences")]
    public async Task<IActionResult> UpdateLibraryPreferences(
        [FromBody] List<NavidromeLibraryPreference> preferences,
        CancellationToken cancellationToken)
    {
        Either<BaseError, Unit> result = await _mediator.Send(
            new UpdateNavidromeLibraryPreferences(preferences),
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
            new ForceSynchronizeNavidromeLibraryById(id, deep),
            cancellationToken);
        return result.Match<IActionResult>(
            name => Ok(new { status = "scanned", library = name }),
            error => BadRequest(new { error = error.Value }));
    }

    public record NavidromeSecretsRequest(string Address, string Username, string Password);
}
