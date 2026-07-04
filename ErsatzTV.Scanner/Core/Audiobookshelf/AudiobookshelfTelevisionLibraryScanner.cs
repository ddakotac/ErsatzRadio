using System.IO.Abstractions;
using ErsatzTV.Core;
using ErsatzTV.Core.Audiobookshelf;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Extensions;
using ErsatzTV.Core.Interfaces.Audiobookshelf;
using ErsatzTV.Core.Interfaces.Metadata;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.Core.Metadata;
using ErsatzTV.Scanner.Core.Interfaces;
using ErsatzTV.Scanner.Core.Interfaces.Metadata;
using ErsatzTV.Scanner.Core.Metadata;
using Microsoft.Extensions.Logging;

namespace ErsatzTV.Scanner.Core.Audiobookshelf;

public class AudiobookshelfTelevisionLibraryScanner :
    MediaServerTelevisionLibraryScanner<AudiobookshelfConnectionParameters, AudiobookshelfLibrary, AudiobookshelfShow,
        AudiobookshelfSeason, AudiobookshelfEpisode, AudiobookshelfItemEtag>,
    IAudiobookshelfTelevisionLibraryScanner
{
    private readonly IAudiobookshelfApiClient _audiobookshelfApiClient;
    private readonly IAudiobookshelfTelevisionRepository _televisionRepository;
    private readonly IAudiobookshelfPathReplacementService _pathReplacementService;
    private readonly IMediaSourceRepository _mediaSourceRepository;
    private List<AudiobookshelfPathReplacement> _pathReplacements = [];

    public AudiobookshelfTelevisionLibraryScanner(
        IScannerProxy scannerProxy,
        IAudiobookshelfApiClient audiobookshelfApiClient,
        IAudiobookshelfTelevisionRepository televisionRepository,
        IAudiobookshelfPathReplacementService pathReplacementService,
        IMediaSourceRepository mediaSourceRepository,
        IFileSystem fileSystem,
        ILocalChaptersProvider localChaptersProvider,
        IMetadataRepository metadataRepository,
        ILogger<AudiobookshelfTelevisionLibraryScanner> logger)
        : base(
            scannerProxy,
            fileSystem,
            localChaptersProvider,
            metadataRepository,
            logger)
    {
        _audiobookshelfApiClient = audiobookshelfApiClient;
        _televisionRepository = televisionRepository;
        _pathReplacementService = pathReplacementService;
        _mediaSourceRepository = mediaSourceRepository;
    }

    protected override bool ServerSupportsRemoteStreaming => false;

    public async Task<Either<BaseError, Unit>> ScanLibrary(
        AudiobookshelfConnectionParameters connectionParameters,
        AudiobookshelfLibrary library,
        bool deepScan,
        CancellationToken cancellationToken)
    {
        _pathReplacements =
            await _mediaSourceRepository.GetAudiobookshelfPathReplacements(library.MediaSourceId);

        // episode paths are resolved to local paths at scan time (see
        // GetEpisodeLibraryItems), so the stored path IS the local path
        static string GetLocalPath(AudiobookshelfEpisode episode) =>
            episode.GetHeadVersion().MediaFiles.Head().Path;

        return await ScanLibrary(
            _televisionRepository,
            connectionParameters,
            library,
            GetLocalPath,
            deepScan,
            cancellationToken);
    }

    protected override IAsyncEnumerable<Tuple<AudiobookshelfShow, int>> GetShowLibraryItems(
        AudiobookshelfConnectionParameters connectionParameters,
        AudiobookshelfLibrary library) =>
        _audiobookshelfApiClient.GetShowLibraryItems(connectionParameters, library);

    protected override string MediaServerItemId(AudiobookshelfShow show) => show.ItemId;
    protected override string MediaServerItemId(AudiobookshelfSeason season) => season.ItemId;
    protected override string MediaServerItemId(AudiobookshelfEpisode episode) => episode.ItemId;
    protected override string MediaServerEtag(AudiobookshelfShow show) => show.Etag;
    protected override string MediaServerEtag(AudiobookshelfSeason season) => season.Etag;
    protected override string MediaServerEtag(AudiobookshelfEpisode episode) => episode.Etag;

    protected override IAsyncEnumerable<Tuple<AudiobookshelfSeason, int>> GetSeasonLibraryItems(
        AudiobookshelfLibrary library,
        AudiobookshelfConnectionParameters connectionParameters,
        AudiobookshelfShow show) =>
        _audiobookshelfApiClient.GetSeasonLibraryItems(connectionParameters, library, show);

    protected override async IAsyncEnumerable<Tuple<AudiobookshelfEpisode, int>> GetEpisodeLibraryItems(
        AudiobookshelfLibrary library,
        AudiobookshelfConnectionParameters connectionParameters,
        AudiobookshelfShow show,
        AudiobookshelfSeason season,
        bool isNewSeason)
    {
        await foreach ((AudiobookshelfEpisode episode, int total) in
                       _audiobookshelfApiClient.GetEpisodeLibraryItems(connectionParameters, library, season))
        {
            // resolve the server path to a local path before anything downstream sees it
            MediaFile file = episode.GetHeadVersion().MediaFiles.Head();
            file.Path = _pathReplacementService.GetReplacementAbsPath(_pathReplacements, file.Path, false);
            file.PathHash = PathUtils.GetPathHash(file.Path);

            yield return Tuple(episode, total);
        }
    }

    protected override Task<Option<ShowMetadata>> GetFullMetadata(
        AudiobookshelfConnectionParameters connectionParameters,
        AudiobookshelfLibrary library,
        MediaItemScanResult<AudiobookshelfShow> result,
        AudiobookshelfShow incoming,
        bool deepScan)
    {
        // metadata is complete at enumeration time
        if (result.IsAdded || result.Item.Etag != incoming.Etag || deepScan)
        {
            return Task.FromResult(incoming.ShowMetadata.HeadOrNone());
        }

        return Task.FromResult(Option<ShowMetadata>.None);
    }

    protected override Task<Option<SeasonMetadata>> GetFullMetadata(
        AudiobookshelfConnectionParameters connectionParameters,
        AudiobookshelfLibrary library,
        MediaItemScanResult<AudiobookshelfSeason> result,
        AudiobookshelfSeason incoming,
        bool deepScan) =>
        Task.FromResult(Option<SeasonMetadata>.None);

    protected override Task<Option<EpisodeMetadata>> GetFullMetadata(
        AudiobookshelfConnectionParameters connectionParameters,
        AudiobookshelfLibrary library,
        MediaItemScanResult<AudiobookshelfEpisode> result,
        AudiobookshelfEpisode incoming,
        bool deepScan)
    {
        if (result.IsAdded || result.Item.Etag != incoming.Etag || deepScan)
        {
            return Task.FromResult(incoming.EpisodeMetadata.HeadOrNone());
        }

        return Task.FromResult(Option<EpisodeMetadata>.None);
    }

    protected override Task<Option<Tuple<EpisodeMetadata, MediaVersion>>> GetFullMetadataAndStatistics(
        AudiobookshelfConnectionParameters connectionParameters,
        AudiobookshelfLibrary library,
        MediaItemScanResult<AudiobookshelfEpisode> result,
        AudiobookshelfEpisode incoming) =>
        Task.FromResult(Option<Tuple<EpisodeMetadata, MediaVersion>>.None);

    protected override Task<Option<MediaVersion>> GetMediaServerStatistics(
        AudiobookshelfConnectionParameters connectionParameters,
        AudiobookshelfLibrary library,
        MediaItemScanResult<AudiobookshelfEpisode> result,
        AudiobookshelfEpisode incoming) =>
        Task.FromResult<Option<MediaVersion>>(incoming.GetHeadVersion());

    protected override Task<Either<BaseError, MediaItemScanResult<AudiobookshelfShow>>> UpdateMetadata(
        MediaItemScanResult<AudiobookshelfShow> result,
        ShowMetadata fullMetadata) =>
        Task.FromResult<Either<BaseError, MediaItemScanResult<AudiobookshelfShow>>>(result);

    protected override Task<Either<BaseError, MediaItemScanResult<AudiobookshelfSeason>>> UpdateMetadata(
        MediaItemScanResult<AudiobookshelfSeason> result,
        SeasonMetadata fullMetadata) =>
        Task.FromResult<Either<BaseError, MediaItemScanResult<AudiobookshelfSeason>>>(result);

    protected override Task<Either<BaseError, MediaItemScanResult<AudiobookshelfEpisode>>> UpdateMetadata(
        MediaItemScanResult<AudiobookshelfEpisode> result,
        EpisodeMetadata fullMetadata,
        CancellationToken cancellationToken) =>
        Task.FromResult<Either<BaseError, MediaItemScanResult<AudiobookshelfEpisode>>>(result);
}
