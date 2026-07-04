using ErsatzTV.Core;
using ErsatzTV.Core.Interfaces.Audiobookshelf;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.Core.Audiobookshelf;

namespace ErsatzTV.Application.Audiobookshelf;

public class SaveAudiobookshelfSecretsHandler : IRequestHandler<SaveAudiobookshelfSecrets, Either<BaseError, Unit>>
{
    private readonly IMediator _mediator;
    private readonly IMediaSourceRepository _mediaSourceRepository;
    private readonly IAudiobookshelfApiClient _audiobookshelfApiClient;
    private readonly IAudiobookshelfSecretStore _audiobookshelfSecretStore;

    public SaveAudiobookshelfSecretsHandler(
        IAudiobookshelfSecretStore audiobookshelfSecretStore,
        IAudiobookshelfApiClient audiobookshelfApiClient,
        IMediaSourceRepository mediaSourceRepository,
        IMediator mediator)
    {
        _audiobookshelfSecretStore = audiobookshelfSecretStore;
        _audiobookshelfApiClient = audiobookshelfApiClient;
        _mediaSourceRepository = mediaSourceRepository;
        _mediator = mediator;
    }

    public Task<Either<BaseError, Unit>> Handle(SaveAudiobookshelfSecrets request, CancellationToken cancellationToken) =>
        Validate(request)
            .MapT(parameters => PerformSave(parameters, cancellationToken))
            .Bind(v => v.ToEitherAsync());

    private async Task<Validation<BaseError, Parameters>> Validate(SaveAudiobookshelfSecrets request)
    {
        var connectionParameters = new AudiobookshelfConnectionParameters(
            request.Secrets.Address,
            request.Secrets.ApiKey,
            0);

        Either<BaseError, AudiobookshelfServerInformation> maybeServerInformation = await _audiobookshelfApiClient
            .GetServerInformation(connectionParameters);

        return maybeServerInformation.Match(
            info => Validation<BaseError, Parameters>.Success(new Parameters(request.Secrets, info)),
            error => error);
    }

    private async Task<Unit> PerformSave(Parameters parameters, CancellationToken cancellationToken)
    {
        await _audiobookshelfSecretStore.SaveSecrets(parameters.Secrets);
        await _mediaSourceRepository.UpsertAudiobookshelf(
            parameters.Secrets.Address,
            parameters.ServerInformation.ServerName);

        foreach (AudiobookshelfMediaSourceViewModel mediaSource in await _mediator.Send(
                     new GetAllAudiobookshelfMediaSources(),
                     cancellationToken))
        {
            await _mediator.Send(new SynchronizeAudiobookshelfLibraries(mediaSource.Id), cancellationToken);
        }

        return Unit.Default;
    }

    private sealed record Parameters(AudiobookshelfSecrets Secrets, AudiobookshelfServerInformation ServerInformation);
}
