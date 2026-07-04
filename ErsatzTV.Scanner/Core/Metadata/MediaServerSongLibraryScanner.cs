using System.Collections.Immutable;
using System.IO.Abstractions;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Domain.MediaServer;
using ErsatzTV.Core.Errors;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.Core.Metadata;
using ErsatzTV.Scanner.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace ErsatzTV.Scanner.Core.Metadata;

public abstract class MediaServerSongLibraryScanner<TConnectionParameters, TLibrary, TSong, TEtag>
    where TConnectionParameters : MediaServerConnectionParameters
    where TLibrary : Library
    where TSong : Song
    where TEtag : MediaServerItemEtag
{
    private readonly IFileSystem _fileSystem;
    private readonly ILogger _logger;
    private readonly IMetadataRepository _metadataRepository;
    private readonly IScannerProxy _scannerProxy;

    protected MediaServerSongLibraryScanner(
        IScannerProxy scannerProxy,
        IFileSystem fileSystem,
        IMetadataRepository metadataRepository,
        ILogger logger)
    {
        _scannerProxy = scannerProxy;
        _fileSystem = fileSystem;
        _metadataRepository = metadataRepository;
        _logger = logger;
    }

    protected async Task<Either<BaseError, Unit>> ScanLibrary(
        IMediaServerSongRepository<TLibrary, TSong, TEtag> songRepository,
        TConnectionParameters connectionParameters,
        TLibrary library,
        Func<TSong, string> getLocalPath,
        bool deepScan,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ScanLibrary(
                songRepository,
                connectionParameters,
                library,
                getLocalPath,
                GetSongLibraryItems(connectionParameters, library),
                deepScan,
                cancellationToken);
        }
        catch (Exception ex) when (ex is TaskCanceledException or OperationCanceledException)
        {
            return new ScanCanceled();
        }
    }

    private async Task<Either<BaseError, Unit>> ScanLibrary(
        IMediaServerSongRepository<TLibrary, TSong, TEtag> songRepository,
        TConnectionParameters connectionParameters,
        TLibrary library,
        Func<TSong, string> getLocalPath,
        IAsyncEnumerable<Tuple<TSong, int>> songEntries,
        bool deepScan,
        CancellationToken cancellationToken)
    {
        var incomingItemIds = new List<string>();
        var existingSongs = (await songRepository.GetExistingSongs(library))
            .ToImmutableDictionary(e => e.MediaServerItemId, e => e);

        await foreach ((TSong incoming, int totalSongCount) in songEntries.WithCancellation(cancellationToken))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return new ScanCanceled();
            }

            incomingItemIds.Add(MediaServerItemId(incoming));

            decimal percentCompletion = Math.Clamp((decimal)incomingItemIds.Count / totalSongCount, 0, 1);
            if (!await _scannerProxy.UpdateProgress(percentCompletion, cancellationToken))
            {
                return new ScanCanceled();
            }

            string localPath = getLocalPath(incoming);

            if (!await ShouldScanItem(songRepository, library, existingSongs, incoming, localPath, deepScan))
            {
                continue;
            }

            Either<BaseError, MediaItemScanResult<TSong>> maybeSong = await songRepository
                .GetOrAdd(library, incoming, deepScan, cancellationToken)
                .MapT(result =>
                {
                    result.LocalPath = localPath;
                    return result;
                })
                .BindT(existing => UpdateStatistics(connectionParameters, library, existing, incoming, deepScan));

            if (maybeSong.IsLeft)
            {
                foreach (BaseError error in maybeSong.LeftToSeq())
                {
                    _logger.LogWarning(
                        "Error processing song {Title}: {Error}",
                        incoming.SongMetadata.Head().Title,
                        error.Value);
                }

                continue;
            }

            foreach (MediaItemScanResult<TSong> result in maybeSong.RightToSeq())
            {
                await songRepository.SetEtag(result.Item, MediaServerEtag(incoming));

                if (_fileSystem.File.Exists(result.LocalPath))
                {
                    Option<int> flagResult = await songRepository.FlagNormal(library, result.Item);
                    if (flagResult.IsSome)
                    {
                        result.IsUpdated = true;
                    }
                }
                else
                {
                    Option<int> flagResult = await songRepository.FlagUnavailable(library, result.Item);
                    if (flagResult.IsSome)
                    {
                        result.IsUpdated = true;
                    }
                }

                if (result.IsAdded || result.IsUpdated)
                {
                    if (!await _scannerProxy.ReindexMediaItems([result.Item.Id], cancellationToken))
                    {
                        _logger.LogWarning("Failed to reindex media items from scanner process");
                    }
                }
            }
        }

        // trash songs that are no longer present on the media server
        var fileNotFoundItemIds = existingSongs.Keys.Except(incomingItemIds).ToList();
        List<int> ids = await songRepository.FlagFileNotFound(library, fileNotFoundItemIds);
        if (!await _scannerProxy.ReindexMediaItems(ids.ToArray(), cancellationToken))
        {
            _logger.LogWarning("Failed to reindex media items from scanner process");
        }

        return Unit.Default;
    }

    protected abstract string MediaServerItemId(TSong song);
    protected abstract string MediaServerEtag(TSong song);

    protected abstract IAsyncEnumerable<Tuple<TSong, int>> GetSongLibraryItems(
        TConnectionParameters connectionParameters,
        TLibrary library);

    protected virtual Task<Option<MediaVersion>> GetMediaServerStatistics(
        TConnectionParameters connectionParameters,
        TLibrary library,
        MediaItemScanResult<TSong> result,
        TSong incoming) => Task.FromResult(Option<MediaVersion>.None);

    private async Task<bool> ShouldScanItem(
        IMediaServerSongRepository<TLibrary, TSong, TEtag> songRepository,
        TLibrary library,
        ImmutableDictionary<string, TEtag> existingSongs,
        TSong incoming,
        string localPath,
        bool deepScan)
    {
        // deep scan will always pull every song
        if (deepScan)
        {
            return true;
        }

        string existingEtag = string.Empty;
        MediaItemState existingState = MediaItemState.Normal;
        if (existingSongs.TryGetValue(MediaServerItemId(incoming), out TEtag? existingEntry))
        {
            existingEtag = existingEntry.Etag;
            existingState = existingEntry.State;
        }

        if (existingState is MediaItemState.Unavailable or MediaItemState.FileNotFound &&
            existingEtag == MediaServerEtag(incoming))
        {
            // skip scanning unavailable/file not found items that are unchanged and still don't exist locally
            if (!_fileSystem.File.Exists(localPath))
            {
                return false;
            }
        }
        else if (existingEtag == MediaServerEtag(incoming))
        {
            // item is unchanged, but file does not exist
            // don't scan, but mark as unavailable
            if (!_fileSystem.File.Exists(localPath) && existingState is not MediaItemState.Unavailable)
            {
                foreach (int id in await songRepository.FlagUnavailable(library, incoming))
                {
                    if (!await _scannerProxy.ReindexMediaItems([id], CancellationToken.None))
                    {
                        _logger.LogWarning("Failed to reindex media items from scanner process");
                    }
                }
            }

            return false;
        }

        if (existingEntry is null)
        {
            _logger.LogDebug("INSERT: new song {Song}", incoming.SongMetadata.Head().Title);
        }
        else
        {
            _logger.LogDebug("UPDATE: Etag has changed for song {Song}", incoming.SongMetadata.Head().Title);
        }

        return true;
    }

    private async Task<Either<BaseError, MediaItemScanResult<TSong>>> UpdateStatistics(
        TConnectionParameters connectionParameters,
        TLibrary library,
        MediaItemScanResult<TSong> result,
        TSong incoming,
        bool deepScan)
    {
        TSong existing = result.Item;

        if (deepScan || result.IsAdded || MediaServerEtag(existing) != MediaServerEtag(incoming) ||
            existing.MediaVersions.Head().Streams.Count == 0)
        {
            Option<MediaVersion> maybeMediaVersion = await GetMediaServerStatistics(
                connectionParameters,
                library,
                result,
                incoming);

            foreach (MediaVersion mediaVersion in maybeMediaVersion)
            {
                if (await _metadataRepository.UpdateStatistics(result.Item, mediaVersion))
                {
                    result.IsUpdated = true;
                }
            }
        }

        return result;
    }
}
