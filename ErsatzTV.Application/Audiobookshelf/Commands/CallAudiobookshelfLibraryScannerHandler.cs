using System.Globalization;
using ErsatzTV.Application.Libraries;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Errors;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.FFmpeg.Runtime;
using ErsatzTV.Infrastructure.Data;
using ErsatzTV.Infrastructure.Extensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ErsatzTV.Core.Interfaces.Metadata;

namespace ErsatzTV.Application.Audiobookshelf;

public class CallAudiobookshelfLibraryScannerHandler : CallLibraryScannerHandler<ISynchronizeAudiobookshelfLibraryById>,
    IRequestHandler<ForceSynchronizeAudiobookshelfLibraryById, Either<BaseError, string>>,
    IRequestHandler<SynchronizeAudiobookshelfLibraryByIdIfNeeded, Either<BaseError, string>>
{
    private readonly IScannerProxyService _scannerProxyService;

    public CallAudiobookshelfLibraryScannerHandler(
        IDbContextFactory<TvContext> dbContextFactory,
        IConfigElementRepository configElementRepository,
        IScannerProxyService scannerProxyService,
        IRuntimeInfo runtimeInfo,
        ILogger<CallAudiobookshelfLibraryScannerHandler> logger)
        : base(dbContextFactory, configElementRepository, runtimeInfo, logger)
    {
        _scannerProxyService = scannerProxyService;
    }

    Task<Either<BaseError, string>> IRequestHandler<ForceSynchronizeAudiobookshelfLibraryById, Either<BaseError, string>>.
        Handle(
            ForceSynchronizeAudiobookshelfLibraryById request,
            CancellationToken cancellationToken) => Handle(request, cancellationToken);

    Task<Either<BaseError, string>>
        IRequestHandler<SynchronizeAudiobookshelfLibraryByIdIfNeeded, Either<BaseError, string>>.
        Handle(
            SynchronizeAudiobookshelfLibraryByIdIfNeeded request,
            CancellationToken cancellationToken) => Handle(request, cancellationToken);

    private async Task<Either<BaseError, string>> Handle(
        ISynchronizeAudiobookshelfLibraryById request,
        CancellationToken cancellationToken)
    {
        Validation<BaseError, ScanParameters> validation = await Validate(request, cancellationToken);
        return await validation.Match(
            parameters => PerformScan(parameters, request, cancellationToken),
            error =>
            {
                foreach (ScanIsNotRequired scanIsNotRequired in error.OfType<ScanIsNotRequired>())
                {
                    return Task.FromResult<Either<BaseError, string>>(scanIsNotRequired);
                }

                return Task.FromResult<Either<BaseError, string>>(error.Join());
            });
    }

    private async Task<Either<BaseError, string>> PerformScan(
        ScanParameters parameters,
        ISynchronizeAudiobookshelfLibraryById request,
        CancellationToken cancellationToken)
    {
        Option<Guid> maybeScanId = _scannerProxyService.StartScan(request.AudiobookshelfLibraryId);
        foreach (var scanId in maybeScanId)
        {
            try
            {
                var arguments = new List<string>
                {
                    "scan-audiobookshelf",
                    request.AudiobookshelfLibraryId.ToString(CultureInfo.InvariantCulture),
                    GetBaseUrl(scanId)
                };

                if (request.ForceScan)
                {
                    arguments.Add("--force");
                }

                if (request.DeepScan)
                {
                    arguments.Add("--deep");
                }

                return await base.PerformScan(parameters, arguments, cancellationToken);
            }
            finally
            {
                _scannerProxyService.EndScan(scanId);
            }
        }

        return BaseError.New($"Library {request.AudiobookshelfLibraryId} is already scanning");
    }

    protected override async Task<Tuple<string, DateTimeOffset>> GetLastScan(
        TvContext dbContext,
        ISynchronizeAudiobookshelfLibraryById request,
        CancellationToken cancellationToken)
    {
        Option<AudiobookshelfLibrary> maybeLibrary = await dbContext.AudiobookshelfLibraries
            .SelectOneAsync(l => l.Id, l => l.Id == request.AudiobookshelfLibraryId, cancellationToken);

        DateTime minDateTime = maybeLibrary.Match(
            l => l.LastScan ?? SystemTime.MinValueUtc,
            () => SystemTime.MaxValueUtc);

        string libraryName = maybeLibrary.Match(l => l.Name, string.Empty);

        return new Tuple<string, DateTimeOffset>(libraryName, new DateTimeOffset(minDateTime, TimeSpan.Zero));
    }

    protected override bool ScanIsRequired(
        DateTimeOffset lastScan,
        int libraryRefreshInterval,
        ISynchronizeAudiobookshelfLibraryById request)
    {
        if (lastScan == SystemTime.MaxValueUtc)
        {
            return false;
        }

        DateTimeOffset nextScan = lastScan + TimeSpan.FromHours(libraryRefreshInterval);
        return request.ForceScan || libraryRefreshInterval > 0 && nextScan < DateTimeOffset.Now;
    }
}
