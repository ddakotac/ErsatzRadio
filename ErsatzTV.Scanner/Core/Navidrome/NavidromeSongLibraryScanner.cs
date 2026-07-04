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
        List<NavidromePathReplacement> pathReplacements =
            await _mediaSourceRepository.GetNavidromePathReplacements(library.MediaSourceId);

        string GetLocalPath(NavidromeSong song)
        {
            return _pathReplacementService.GetReplacementNavidromePath(
                pathReplacements,
                song.GetHeadVersion().MediaFiles.Head().Path,
                false);
        }

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

    protected override IAsyncEnumerable<Tuple<NavidromeSong, int>> GetSongLibraryItems(
        NavidromeConnectionParameters connectionParameters,
        NavidromeLibrary library) =>
        _navidromeApiClient.GetSongLibraryItems(connectionParameters, library);

    protected override Task<Option<MediaVersion>> GetMediaServerStatistics(
        NavidromeConnectionParameters connectionParameters,
        NavidromeLibrary library,
        MediaItemScanResult<NavidromeSong> result,
        NavidromeSong incoming) =>
        Task.FromResult<Option<MediaVersion>>(incoming.GetHeadVersion());
}
