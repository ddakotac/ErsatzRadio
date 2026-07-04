using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Interfaces.Navidrome;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.Core.Interfaces.Search;
using ErsatzTV.Core.Navidrome;
using Microsoft.Extensions.Logging;

namespace ErsatzTV.Application.Navidrome;

public class
    SynchronizeNavidromeLibrariesHandler : IRequestHandler<SynchronizeNavidromeLibraries, Either<BaseError, Unit>>
{
    private readonly ILogger<SynchronizeNavidromeLibrariesHandler> _logger;
    private readonly IMediaSourceRepository _mediaSourceRepository;
    private readonly INavidromeApiClient _navidromeApiClient;
    private readonly INavidromeSecretStore _navidromeSecretStore;
    private readonly ISearchIndex _searchIndex;

    public SynchronizeNavidromeLibrariesHandler(
        IMediaSourceRepository mediaSourceRepository,
        INavidromeSecretStore navidromeSecretStore,
        INavidromeApiClient navidromeApiClient,
        ILogger<SynchronizeNavidromeLibrariesHandler> logger,
        ISearchIndex searchIndex)
    {
        _mediaSourceRepository = mediaSourceRepository;
        _navidromeSecretStore = navidromeSecretStore;
        _navidromeApiClient = navidromeApiClient;
        _logger = logger;
        _searchIndex = searchIndex;
    }

    public Task<Either<BaseError, Unit>> Handle(
        SynchronizeNavidromeLibraries request,
        CancellationToken cancellationToken) =>
        Validate(request)
            .MapT(p => SynchronizeLibraries(p, cancellationToken))
            .Bind(v => v.ToEitherAsync());

    private Task<Validation<BaseError, ConnectionAndSource>> Validate(SynchronizeNavidromeLibraries request) =>
        MediaSourceMustExist(request)
            .BindT(MediaSourceMustHaveActiveConnection)
            .BindT(MediaSourceMustHaveCredentials);

    private Task<Validation<BaseError, NavidromeMediaSource>> MediaSourceMustExist(
        SynchronizeNavidromeLibraries request) =>
        _mediaSourceRepository.GetNavidrome(request.NavidromeMediaSourceId)
            .Map(o => o.ToValidation<BaseError>("Navidrome media source does not exist."));

    private Validation<BaseError, ConnectionAndSource> MediaSourceMustHaveActiveConnection(
        NavidromeMediaSource navidromeMediaSource)
    {
        Option<NavidromeConnection> maybeConnection = navidromeMediaSource.Connections.HeadOrNone();
        return maybeConnection.Map(connection => new ConnectionAndSource(
                new NavidromeConnectionParameters(
                    connection.Address,
                    string.Empty,
                    string.Empty,
                    connection.NavidromeMediaSourceId),
                navidromeMediaSource))
            .ToValidation<BaseError>("Navidrome media source requires an active connection");
    }

    private async Task<Validation<BaseError, ConnectionAndSource>> MediaSourceMustHaveCredentials(
        ConnectionAndSource connectionAndSource)
    {
        NavidromeSecrets secrets = await _navidromeSecretStore.ReadSecrets();
        return Optional(secrets.Address == connectionAndSource.ConnectionParameters.Address)
            .Where(match => match)
            .Filter(_ => !string.IsNullOrWhiteSpace(secrets.Username) && !string.IsNullOrWhiteSpace(secrets.ApiKey))
            .Map(_ => connectionAndSource with
            {
                ConnectionParameters = connectionAndSource.ConnectionParameters with
                {
                    Username = secrets.Username,
                    Password = secrets.ApiKey
                }
            })
            .ToValidation<BaseError>("Navidrome media source requires a username and password");
    }

    private async Task<Unit> SynchronizeLibraries(
        ConnectionAndSource connectionAndSource,
        CancellationToken cancellationToken)
    {
        Either<BaseError, List<NavidromeLibrary>> maybeLibraries = await _navidromeApiClient.GetLibraries(
            connectionAndSource.ConnectionParameters);

        foreach (BaseError error in maybeLibraries.LeftToSeq())
        {
            _logger.LogWarning(
                "Unable to synchronize libraries from navidrome server {NavidromeServer}: {Error}",
                connectionAndSource.MediaSource.ServerName,
                error.Value);
        }

        foreach (List<NavidromeLibrary> libraries in maybeLibraries.RightToSeq())
        {
            var existing = connectionAndSource.MediaSource.Libraries
                .OfType<NavidromeLibrary>()
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
        NavidromeConnectionParameters ConnectionParameters,
        NavidromeMediaSource MediaSource);
}
