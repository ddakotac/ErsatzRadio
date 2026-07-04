using ErsatzTV.Core;
using ErsatzTV.Core.Interfaces.Navidrome;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.Core.Navidrome;

namespace ErsatzTV.Application.Navidrome;

public class SaveNavidromeSecretsHandler : IRequestHandler<SaveNavidromeSecrets, Either<BaseError, Unit>>
{
    private readonly IMediator _mediator;
    private readonly IMediaSourceRepository _mediaSourceRepository;
    private readonly INavidromeApiClient _navidromeApiClient;
    private readonly INavidromeSecretStore _navidromeSecretStore;

    public SaveNavidromeSecretsHandler(
        INavidromeSecretStore navidromeSecretStore,
        INavidromeApiClient navidromeApiClient,
        IMediaSourceRepository mediaSourceRepository,
        IMediator mediator)
    {
        _navidromeSecretStore = navidromeSecretStore;
        _navidromeApiClient = navidromeApiClient;
        _mediaSourceRepository = mediaSourceRepository;
        _mediator = mediator;
    }

    public Task<Either<BaseError, Unit>> Handle(SaveNavidromeSecrets request, CancellationToken cancellationToken) =>
        Validate(request)
            .MapT(parameters => PerformSave(parameters, cancellationToken))
            .Bind(v => v.ToEitherAsync());

    private async Task<Validation<BaseError, Parameters>> Validate(SaveNavidromeSecrets request)
    {
        var connectionParameters = new NavidromeConnectionParameters(
            request.Secrets.Address,
            request.Secrets.Username,
            request.Secrets.ApiKey,
            0);

        Either<BaseError, NavidromeServerInformation> maybeServerInformation = await _navidromeApiClient
            .GetServerInformation(connectionParameters);

        return maybeServerInformation.Match(
            info => Validation<BaseError, Parameters>.Success(new Parameters(request.Secrets, info)),
            error => error);
    }

    private async Task<Unit> PerformSave(Parameters parameters, CancellationToken cancellationToken)
    {
        await _navidromeSecretStore.SaveSecrets(parameters.Secrets);
        await _mediaSourceRepository.UpsertNavidrome(
            parameters.Secrets.Address,
            parameters.ServerInformation.ServerName);

        foreach (NavidromeMediaSourceViewModel mediaSource in await _mediator.Send(
                     new GetAllNavidromeMediaSources(),
                     cancellationToken))
        {
            await _mediator.Send(new SynchronizeNavidromeLibraries(mediaSource.Id), cancellationToken);
        }

        return Unit.Default;
    }

    private sealed record Parameters(NavidromeSecrets Secrets, NavidromeServerInformation ServerInformation);
}
