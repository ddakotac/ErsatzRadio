using System.IO.Abstractions;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Extensions;
using ErsatzTV.Core.Interfaces.Navidrome;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.Core.Metadata;
using ErsatzTV.Core.Navidrome;
using ErsatzTV.Scanner.Core.Interfaces;
using ErsatzTV.Scanner.Core.Metadata;
using Microsoft.Extensions.Logging;

namespace ErsatzTV.Scanner.Core.Navidrome;

public class NavidromeSongLibraryScanner :
    MediaServerSongLibraryScanner<NavidromeConnectionParameters, NavidromeLibrary, NavidromeSong, NavidromeItemEtag>,
    INavidromeSongLibraryScanner
{
    private readonly INavidromeApiClient _navidromeApiClient;
    private List<NavidromePathReplacement> _pathReplacements = [];
    private readonly INavidromeSongRepository _navidromeSongRepository;
    private readonly IMediaSourceRepository _mediaSourceRepository;
    private readonly INavidromePathReplacementService _pathReplacementService;

    public NavidromeSongLibraryScanner(
        IScannerProxy scannerProxy,
        INavidromeApiClient navidromeApiClient,
        INavidromeSongRepository navidromeSongRepository,
        INavidromePathReplacementService pathReplacementService,
        IMediaSourceRepository mediaSourceRepository,
        IFileSystem fileSystem,
        IMetadataRepository metadataRepository,
        ILogger<NavidromeSongLibraryScanner> logger)
        : base(
            scannerProxy,
            fileSystem,
            metadataRepository,
            logger)
    {
        _navidromeApiClient = navidromeApiClient;
        _navidromeSongRepository = navidromeSongRepository;
        _pathReplacementService = pathReplacementService;
        _mediaSourceRepository = mediaSourceRepository;
    }

    public async Task<Either<BaseError, Unit>> ScanLibrary(
        NavidromeConnectionParameters connectionParameters,
        NavidromeLibrary library,
        bool deepScan,
        CancellationToken cancellationToken)
    {
        _pathReplacements =
            await _mediaSourceRepository.GetNavidromePathReplacements(library.MediaSourceId);

        // paths are resolved to local paths at scan time (see GetSongLibraryItems),
        // so the local path IS the stored path; playout and availability need no
        // navidrome-specific logic downstream
        static string GetLocalPath(NavidromeSong song) =>
            song.GetHeadVersion().MediaFiles.Head().Path;

        return await ScanLibrary(
            _navidromeSongRepository,
            connectionParameters,
            library,
            GetLocalPath,
            deepScan,
            cancellationToken);
    }

    protected override string MediaServerItemId(NavidromeSong song) => song.ItemId;

    protected override string MediaServerEtag(NavidromeSong song) => song.Etag;

    protected override async IAsyncEnumerable<Tuple<NavidromeSong, int>> GetSongLibraryItems(
        NavidromeConnectionParameters connectionParameters,
        NavidromeLibrary library)
    {
        await foreach ((NavidromeSong song, int total) in
                       _navidromeApiClient.GetSongLibraryItems(connectionParameters, library))
        {
            // resolve the server-relative path to a local path before anything
            // downstream sees the item
            MediaFile file = song.GetHeadVersion().MediaFiles.Head();
            file.Path = _pathReplacementService.GetReplacementNavidromePath(
                _pathReplacements,
                file.Path,
                false);
            file.PathHash = PathUtils.GetPathHash(file.Path);

            yield return Tuple(song, total);
        }
    }

    protected override Task<Option<MediaVersion>> GetMediaServerStatistics(
        NavidromeConnectionParameters connectionParameters,
        NavidromeLibrary library,
        MediaItemScanResult<NavidromeSong> result,
        NavidromeSong incoming) =>
        Task.FromResult<Option<MediaVersion>>(incoming.GetHeadVersion());
}
