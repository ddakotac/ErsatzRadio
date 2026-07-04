using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Interfaces.Audiobookshelf;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.Core.Audiobookshelf;
using ErsatzTV.Scanner.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace ErsatzTV.Scanner.Application.Audiobookshelf;

public class
    SynchronizeAudiobookshelfLibraryByIdHandler : IRequestHandler<SynchronizeAudiobookshelfLibraryById,
    Either<BaseError, string>>
{
    private readonly IConfigElementRepository _configElementRepository;
    private readonly ILibraryRepository _libraryRepository;
    private readonly ILogger<SynchronizeAudiobookshelfLibraryByIdHandler> _logger;
    private readonly IMediaSourceRepository _mediaSourceRepository;
    private readonly IAudiobookshelfSecretStore _audiobookshelfSecretStore;
    private readonly IAudiobookshelfTelevisionLibraryScanner _audiobookshelfTelevisionLibraryScanner;
    private readonly IScannerProxy _scannerProxy;

    public SynchronizeAudiobookshelfLibraryByIdHandler(
        IScannerProxy scannerProxy,
        IMediaSourceRepository mediaSourceRepository,
        IAudiobookshelfSecretStore audiobookshelfSecretStore,
        IAudiobookshelfTelevisionLibraryScanner audiobookshelfTelevisionLibraryScanner,
        ILibraryRepository libraryRepository,
        IConfigElementRepository configElementRepository,
        ILogger<SynchronizeAudiobookshelfLibraryByIdHandler> logger)
    {
        _scannerProxy = scannerProxy;
        _mediaSourceRepository = mediaSourceRepository;
        _audiobookshelfSecretStore = audiobookshelfSecretStore;
        _audiobookshelfTelevisionLibraryScanner = audiobookshelfTelevisionLibraryScanner;
        _libraryRepository = libraryRepository;
        _configElementRepository = configElementRepository;
        _logger = logger;
    }

    public async Task<Either<BaseError, string>>
        Handle(SynchronizeAudiobookshelfLibraryById request, CancellationToken cancellationToken)
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
                LibraryMediaKind.Shows =>
                    await _audiobookshelfTelevisionLibraryScanner.ScanLibrary(
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
                _logger.LogError("Error synchronizing audiobookshelf library: {Error}", error);
            }

            return result.Map(_ => parameters.Library.Name);
        }

        _logger.LogDebug("Skipping unforced scan of audiobookshelf media library {Name}", parameters.Library.Name);

        return parameters.Library.Name;
    }

    private async Task<Validation<BaseError, RequestParameters>> Validate(
        SynchronizeAudiobookshelfLibraryById request,
        CancellationToken cancellationToken) =>
        (await ValidateConnection(request), await AudiobookshelfLibraryMustExist(request),
            await ValidateLibraryRefreshInterval(cancellationToken))
        .Apply((connectionParameters, audiobookshelfLibrary, libraryRefreshInterval) =>
            new RequestParameters(
                connectionParameters,
                audiobookshelfLibrary,
                request.ForceScan,
                libraryRefreshInterval,
                request.DeepScan,
                request.BaseUrl
            ));

    private Task<Validation<BaseError, AudiobookshelfConnectionParameters>> ValidateConnection(
        SynchronizeAudiobookshelfLibraryById request) =>
        AudiobookshelfMediaSourceMustExist(request)
            .BindT(MediaSourceMustHaveActiveConnection)
            .BindT(MediaSourceMustHaveCredentials);

    private Task<Validation<BaseError, AudiobookshelfMediaSource>> AudiobookshelfMediaSourceMustExist(
        SynchronizeAudiobookshelfLibraryById request) =>
        _mediaSourceRepository.GetAudiobookshelfByLibraryId(request.AudiobookshelfLibraryId)
            .Map(v => v.ToValidation<BaseError>(
                $"Audiobookshelf media source for library {request.AudiobookshelfLibraryId} does not exist."));

    private Validation<BaseError, AudiobookshelfConnectionParameters> MediaSourceMustHaveActiveConnection(
        AudiobookshelfMediaSource audiobookshelfMediaSource)
    {
        Option<AudiobookshelfConnection> maybeConnection = audiobookshelfMediaSource.Connections.HeadOrNone();
        return maybeConnection
            .Map(connection => new AudiobookshelfConnectionParameters(
                connection.Address,
                string.Empty,
                connection.AudiobookshelfMediaSourceId))
            .ToValidation<BaseError>("Audiobookshelf media source requires an active connection");
    }

    private async Task<Validation<BaseError, AudiobookshelfConnectionParameters>> MediaSourceMustHaveCredentials(
        AudiobookshelfConnectionParameters connectionParameters)
    {
        AudiobookshelfSecrets secrets = await _audiobookshelfSecretStore.ReadSecrets();
        return Optional(secrets.Address == connectionParameters.Address)
            .Where(match => match)
            .Filter(_ => !string.IsNullOrWhiteSpace(secrets.ApiKey))
            .Map(_ => connectionParameters with { ApiKey = secrets.ApiKey })
            .ToValidation<BaseError>("Audiobookshelf media source requires an api token");
    }

    private Task<Validation<BaseError, AudiobookshelfLibrary>> AudiobookshelfLibraryMustExist(
        SynchronizeAudiobookshelfLibraryById request) =>
        _mediaSourceRepository.GetAudiobookshelfLibrary(request.AudiobookshelfLibraryId)
            .Map(v => v.ToValidation<BaseError>($"Audiobookshelf library {request.AudiobookshelfLibraryId} does not exist."));

    private Task<Validation<BaseError, int>> ValidateLibraryRefreshInterval(CancellationToken cancellationToken) =>
        _configElementRepository.GetValue<int>(ConfigElementKey.LibraryRefreshInterval, cancellationToken)
            .FilterT(lri => lri is >= 0 and < 1_000_000)
            .Map(lri => lri.ToValidation<BaseError>("Library refresh interval is invalid"));

    private record RequestParameters(
        AudiobookshelfConnectionParameters ConnectionParameters,
        AudiobookshelfLibrary Library,
        bool ForceScan,
        int LibraryRefreshInterval,
        bool DeepScan,
        string BaseUrl);
}
