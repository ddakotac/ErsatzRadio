using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Interfaces.Navidrome;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.Core.Navidrome;
using ErsatzTV.Scanner.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace ErsatzTV.Scanner.Application.Navidrome;

public class
    SynchronizeNavidromeLibraryByIdHandler : IRequestHandler<SynchronizeNavidromeLibraryById,
    Either<BaseError, string>>
{
    private readonly IConfigElementRepository _configElementRepository;
    private readonly ILibraryRepository _libraryRepository;
    private readonly ILogger<SynchronizeNavidromeLibraryByIdHandler> _logger;
    private readonly IMediaSourceRepository _mediaSourceRepository;
    private readonly INavidromeSecretStore _navidromeSecretStore;
    private readonly INavidromeSongLibraryScanner _navidromeSongLibraryScanner;
    private readonly IScannerProxy _scannerProxy;

    public SynchronizeNavidromeLibraryByIdHandler(
        IScannerProxy scannerProxy,
        IMediaSourceRepository mediaSourceRepository,
        INavidromeSecretStore navidromeSecretStore,
        INavidromeSongLibraryScanner navidromeSongLibraryScanner,
        ILibraryRepository libraryRepository,
        IConfigElementRepository configElementRepository,
        ILogger<SynchronizeNavidromeLibraryByIdHandler> logger)
    {
        _scannerProxy = scannerProxy;
        _mediaSourceRepository = mediaSourceRepository;
        _navidromeSecretStore = navidromeSecretStore;
        _navidromeSongLibraryScanner = navidromeSongLibraryScanner;
        _libraryRepository = libraryRepository;
        _configElementRepository = configElementRepository;
        _logger = logger;
    }

    public async Task<Either<BaseError, string>>
        Handle(SynchronizeNavidromeLibraryById request, CancellationToken cancellationToken)
    {
        Validation<BaseError, RequestParameters> validation = await Validate(request, cancellationToken);
        return await validation.Match(
            parameters => Synchronize(parameters, cancellationToken),
            error => Task.FromResult<Either<BaseError, string>>(error.Join()));
    }

    private async Task<Either<BaseError, string>> Synchronize(
        RequestParameters parameters,
        CancellationToken cancellationToken)
    {
        _scannerProxy.SetBaseUrl(parameters.BaseUrl);

        var lastScan = new DateTimeOffset(parameters.Library.LastScan ?? SystemTime.MinValueUtc, TimeSpan.Zero);
        DateTimeOffset nextScan = lastScan + TimeSpan.FromHours(parameters.LibraryRefreshInterval);
        if (parameters.ForceScan || parameters.LibraryRefreshInterval > 0 && nextScan < DateTimeOffset.Now)
        {
            Either<BaseError, Unit> result = parameters.Library.MediaKind switch
            {
                LibraryMediaKind.Songs =>
                    await _navidromeSongLibraryScanner.ScanLibrary(
                        parameters.ConnectionParameters,
                        parameters.Library,
                        parameters.DeepScan,
                        cancellationToken),
                _ => Unit.Default
            };

            if (result.IsRight)
            {
                parameters.Library.LastScan = DateTime.UtcNow;
                await _libraryRepository.UpdateLastScan(parameters.Library);
            }

            foreach (BaseError error in result.LeftToSeq())
            {
                _logger.LogError("Error synchronizing navidrome library: {Error}", error);
            }

            return result.Map(_ => parameters.Library.Name);
        }

        _logger.LogDebug("Skipping unforced scan of navidrome media library {Name}", parameters.Library.Name);

        return parameters.Library.Name;
    }

    private async Task<Validation<BaseError, RequestParameters>> Validate(
        SynchronizeNavidromeLibraryById request,
        CancellationToken cancellationToken) =>
        (await ValidateConnection(request), await NavidromeLibraryMustExist(request),
            await ValidateLibraryRefreshInterval(cancellationToken))
        .Apply((connectionParameters, navidromeLibrary, libraryRefreshInterval) =>
            new RequestParameters(
                connectionParameters,
                navidromeLibrary,
                request.ForceScan,
                libraryRefreshInterval,
                request.DeepScan,
                request.BaseUrl
            ));

    private Task<Validation<BaseError, NavidromeConnectionParameters>> ValidateConnection(
        SynchronizeNavidromeLibraryById request) =>
        NavidromeMediaSourceMustExist(request)
            .BindT(MediaSourceMustHaveActiveConnection)
            .BindT(MediaSourceMustHaveCredentials);

    private Task<Validation<BaseError, NavidromeMediaSource>> NavidromeMediaSourceMustExist(
        SynchronizeNavidromeLibraryById request) =>
        _mediaSourceRepository.GetNavidromeByLibraryId(request.NavidromeLibraryId)
            .Map(v => v.ToValidation<BaseError>(
                $"Navidrome media source for library {request.NavidromeLibraryId} does not exist."));

    private Validation<BaseError, NavidromeConnectionParameters> MediaSourceMustHaveActiveConnection(
        NavidromeMediaSource navidromeMediaSource)
    {
        Option<NavidromeConnection> maybeConnection = navidromeMediaSource.Connections.HeadOrNone();
        return maybeConnection
            .Map(connection => new NavidromeConnectionParameters(
                connection.Address,
                string.Empty,
                string.Empty,
                connection.NavidromeMediaSourceId))
            .ToValidation<BaseError>("Navidrome media source requires an active connection");
    }

    private async Task<Validation<BaseError, NavidromeConnectionParameters>> MediaSourceMustHaveCredentials(
        NavidromeConnectionParameters connectionParameters)
    {
        NavidromeSecrets secrets = await _navidromeSecretStore.ReadSecrets();
        return Optional(secrets.Address == connectionParameters.Address)
            .Where(match => match)
            .Filter(_ => !string.IsNullOrWhiteSpace(secrets.Username) && !string.IsNullOrWhiteSpace(secrets.ApiKey))
            .Map(_ => connectionParameters with { Username = secrets.Username, Password = secrets.ApiKey })
            .ToValidation<BaseError>("Navidrome media source requires a username and password");
    }

    private Task<Validation<BaseError, NavidromeLibrary>> NavidromeLibraryMustExist(
        SynchronizeNavidromeLibraryById request) =>
        _mediaSourceRepository.GetNavidromeLibrary(request.NavidromeLibraryId)
            .Map(v => v.ToValidation<BaseError>($"Navidrome library {request.NavidromeLibraryId} does not exist."));

    private Task<Validation<BaseError, int>> ValidateLibraryRefreshInterval(CancellationToken cancellationToken) =>
        _configElementRepository.GetValue<int>(ConfigElementKey.LibraryRefreshInterval, cancellationToken)
            .FilterT(lri => lri is >= 0 and < 1_000_000)
            .Map(lri => lri.ToValidation<BaseError>("Library refresh interval is invalid"));

    private record RequestParameters(
        NavidromeConnectionParameters ConnectionParameters,
        NavidromeLibrary Library,
        bool ForceScan,
        int LibraryRefreshInterval,
        bool DeepScan,
        string BaseUrl);
}
