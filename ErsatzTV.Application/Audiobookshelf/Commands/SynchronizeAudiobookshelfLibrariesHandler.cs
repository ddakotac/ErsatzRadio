using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Interfaces.Audiobookshelf;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.Core.Interfaces.Search;
using ErsatzTV.Core.Audiobookshelf;
using Microsoft.Extensions.Logging;

namespace ErsatzTV.Application.Audiobookshelf;

public class
    SynchronizeAudiobookshelfLibrariesHandler : IRequestHandler<SynchronizeAudiobookshelfLibraries, Either<BaseError, Unit>>
{
    private readonly ILogger<SynchronizeAudiobookshelfLibrariesHandler> _logger;
    private readonly IMediaSourceRepository _mediaSourceRepository;
    private readonly IAudiobookshelfApiClient _audiobookshelfApiClient;
    private readonly IAudiobookshelfSecretStore _audiobookshelfSecretStore;
    private readonly ISearchIndex _searchIndex;

    public SynchronizeAudiobookshelfLibrariesHandler(
        IMediaSourceRepository mediaSourceRepository,
        IAudiobookshelfSecretStore audiobookshelfSecretStore,
        IAudiobookshelfApiClient audiobookshelfApiClient,
        ILogger<SynchronizeAudiobookshelfLibrariesHandler> logger,
        ISearchIndex searchIndex)
    {
        _mediaSourceRepository = mediaSourceRepository;
        _audiobookshelfSecretStore = audiobookshelfSecretStore;
        _audiobookshelfApiClient = audiobookshelfApiClient;
        _logger = logger;
        _searchIndex = searchIndex;
    }

    public Task<Either<BaseError, Unit>> Handle(
        SynchronizeAudiobookshelfLibraries request,
        CancellationToken cancellationToken) =>
        Validate(request)
            .MapT(p => SynchronizeLibraries(p, cancellationToken))
            .Bind(v => v.ToEitherAsync());

    private Task<Validation<BaseError, ConnectionAndSource>> Validate(SynchronizeAudiobookshelfLibraries request) =>
        MediaSourceMustExist(request)
            .BindT(MediaSourceMustHaveActiveConnection)
            .BindT(MediaSourceMustHaveCredentials);

    private Task<Validation<BaseError, AudiobookshelfMediaSource>> MediaSourceMustExist(
        SynchronizeAudiobookshelfLibraries request) =>
        _mediaSourceRepository.GetAudiobookshelf(request.AudiobookshelfMediaSourceId)
            .Map(o => o.ToValidation<BaseError>("Audiobookshelf media source does not exist."));

    private Validation<BaseError, ConnectionAndSource> MediaSourceMustHaveActiveConnection(
        AudiobookshelfMediaSource audiobookshelfMediaSource)
    {
        Option<AudiobookshelfConnection> maybeConnection = audiobookshelfMediaSource.Connections.HeadOrNone();
        return maybeConnection.Map(connection => new ConnectionAndSource(
                new AudiobookshelfConnectionParameters(
                    connection.Address,
                    string.Empty,
                    connection.AudiobookshelfMediaSourceId),
                audiobookshelfMediaSource))
            .ToValidation<BaseError>("Audiobookshelf media source requires an active connection");
    }

    private async Task<Validation<BaseError, ConnectionAndSource>> MediaSourceMustHaveCredentials(
        ConnectionAndSource connectionAndSource)
    {
        AudiobookshelfSecrets secrets = await _audiobookshelfSecretStore.ReadSecrets();
        return Optional(secrets.Address == connectionAndSource.ConnectionParameters.Address)
            .Where(match => match)
            .Filter(_ => !string.IsNullOrWhiteSpace(secrets.ApiKey))
            .Map(_ => connectionAndSource with
            {
                ConnectionParameters = connectionAndSource.ConnectionParameters with { ApiKey = secrets.ApiKey }
            })
            .ToValidation<BaseError>("Audiobookshelf media source requires an api token");
    }

    private async Task<Unit> SynchronizeLibraries(
        ConnectionAndSource connectionAndSource,
        CancellationToken cancellationToken)
    {
        Either<BaseError, List<AudiobookshelfLibrary>> maybeLibraries = await _audiobookshelfApiClient.GetLibraries(
            connectionAndSource.ConnectionParameters);

        foreach (BaseError error in maybeLibraries.LeftToSeq())
        {
            _logger.LogWarning(
                "Unable to synchronize libraries from audiobookshelf server {AudiobookshelfServer}: {Error}",
                connectionAndSource.MediaSource.ServerName,
                error.Value);
        }

        foreach (List<AudiobookshelfLibrary> libraries in maybeLibraries.RightToSeq())
        {
            var existing = connectionAndSource.MediaSource.Libraries
                .OfType<AudiobookshelfLibrary>()
                .ToList();
            var toAdd = libraries.Filter(library => existing.All(l => l.ItemId != library.ItemId)).ToList();
            var toRemove = existing.Filter(library => libraries.All(l => l.ItemId != library.ItemId)).ToList();
            var toUpdate = libraries
                .Filter(l => toAdd.All(a => a.ItemId != l.ItemId) && toRemove.All(r => r.ItemId != l.ItemId)).ToList();
            List<int> ids = await _mediaSourceRepository.UpdateLibraries(
                connectionAndSource.MediaSource.Id,
                toAdd,
                toRemove,
                toUpdate,
                cancellationToken);
            if (ids.Count != 0)
            {
                await _searchIndex.RemoveItems(ids);
                _searchIndex.Commit();
            }
        }

        return Unit.Default;
    }

    private sealed record ConnectionAndSource(
        AudiobookshelfConnectionParameters ConnectionParameters,
        AudiobookshelfMediaSource MediaSource);
}
