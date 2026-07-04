using Dapper;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Errors;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.Core.Metadata;
using ErsatzTV.Core.Navidrome;
using ErsatzTV.Infrastructure.Extensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ErsatzTV.Infrastructure.Data.Repositories;

public class NavidromeSongRepository : INavidromeSongRepository
{
    private readonly IDbContextFactory<TvContext> _dbContextFactory;
    private readonly ILogger<NavidromeSongRepository> _logger;

    public NavidromeSongRepository(
        IDbContextFactory<TvContext> dbContextFactory,
        ILogger<NavidromeSongRepository> logger)
    {
        _dbContextFactory = dbContextFactory;
        _logger = logger;
    }

    public async Task<List<NavidromeItemEtag>> GetExistingSongs(NavidromeLibrary library)
    {
        await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync();
        return await dbContext.Connection.QueryAsync<NavidromeItemEtag>(
                @"SELECT ItemId, Etag, MI.State FROM NavidromeSong
                      INNER JOIN Song S on NavidromeSong.Id = S.Id
                      INNER JOIN MediaItem MI on S.Id = MI.Id
                      INNER JOIN LibraryPath LP on MI.LibraryPathId = LP.Id
                      WHERE LP.LibraryId = @LibraryId",
                new { LibraryId = library.Id })
            .Map(result => result.ToList());
    }

    public async Task<Option<int>> FlagNormal(NavidromeLibrary library, NavidromeSong song)
    {
        if (song.State is MediaItemState.Normal)
        {
            return Option<int>.None;
        }

        await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync();

        song.State = MediaItemState.Normal;

        Option<int> maybeId = await dbContext.Connection.ExecuteScalarAsync<int>(
            @"SELECT NavidromeSong.Id FROM NavidromeSong
              INNER JOIN MediaItem MI ON MI.Id = NavidromeSong.Id
              INNER JOIN LibraryPath LP on MI.LibraryPathId = LP.Id AND LibraryId = @LibraryId
              WHERE NavidromeSong.ItemId = @ItemId",
            new { LibraryId = library.Id, song.ItemId });

        foreach (int id in maybeId)
        {
            return await dbContext.Connection.ExecuteAsync(
                "UPDATE MediaItem SET State = 0 WHERE Id = @Id AND State != 0",
                new { Id = id }).Map(count => count > 0 ? Some(id) : None);
        }

        return None;
    }

    public async Task<Option<int>> FlagUnavailable(NavidromeLibrary library, NavidromeSong song)
    {
        if (song.State is MediaItemState.Unavailable)
        {
            return Option<int>.None;
        }

        await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync();

        song.State = MediaItemState.Unavailable;

        Option<int> maybeId = await dbContext.Connection.ExecuteScalarAsync<int>(
            @"SELECT NavidromeSong.Id FROM NavidromeSong
              INNER JOIN MediaItem MI ON MI.Id = NavidromeSong.Id
              INNER JOIN LibraryPath LP on MI.LibraryPathId = LP.Id AND LibraryId = @LibraryId
              WHERE NavidromeSong.ItemId = @ItemId",
            new { LibraryId = library.Id, song.ItemId });

        foreach (int id in maybeId)
        {
            return await dbContext.Connection.ExecuteAsync(
                "UPDATE MediaItem SET State = 2 WHERE Id = @Id AND State != 2",
                new { Id = id }).Map(count => count > 0 ? Some(id) : None);
        }

        return None;
    }

    public async Task<List<int>> FlagFileNotFound(NavidromeLibrary library, List<string> songItemIds)
    {
        if (songItemIds.Count == 0)
        {
            return [];
        }

        await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync();

        List<int> ids = await dbContext.Connection.QueryAsync<int>(
                @"SELECT M.Id
                FROM MediaItem M
                INNER JOIN NavidromeSong ON NavidromeSong.Id = M.Id
                INNER JOIN LibraryPath LP on M.LibraryPathId = LP.Id AND LP.LibraryId = @LibraryId
                WHERE NavidromeSong.ItemId IN @SongItemIds",
                new { LibraryId = library.Id, SongItemIds = songItemIds })
            .Map(result => result.ToList());

        await dbContext.Connection.ExecuteAsync(
            "UPDATE MediaItem SET State = 1 WHERE Id IN @Ids AND State != 1",
            new { Ids = ids });

        return ids;
    }

    public async Task<Either<BaseError, MediaItemScanResult<NavidromeSong>>> GetOrAdd(
        NavidromeLibrary library,
        NavidromeSong item,
        bool deepScan,
        CancellationToken cancellationToken)
    {
        await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        Option<NavidromeSong> maybeExisting = await dbContext.NavidromeSongs
            .Include(s => s.LibraryPath)
            .ThenInclude(lp => lp.Library)
            .Include(s => s.MediaVersions)
            .ThenInclude(mv => mv.MediaFiles)
            .Include(s => s.MediaVersions)
            .ThenInclude(mv => mv.Streams)
            .Include(s => s.MediaVersions)
            .ThenInclude(mv => mv.Chapters)
            .Include(s => s.SongMetadata)
            .ThenInclude(sm => sm.Genres)
            .Include(s => s.SongMetadata)
            .ThenInclude(sm => sm.Tags)
            .Include(s => s.SongMetadata)
            .ThenInclude(sm => sm.Studios)
            .Include(s => s.SongMetadata)
            .ThenInclude(sm => sm.Actors)
            .Include(s => s.SongMetadata)
            .ThenInclude(sm => sm.Artwork)
            .Include(s => s.SongMetadata)
            .ThenInclude(sm => sm.Guids)
            .Include(s => s.TraktListItems)
            .ThenInclude(tli => tli.TraktList)
            .SelectOneAsync(s => s.ItemId, s => s.ItemId == item.ItemId, cancellationToken);

        foreach (NavidromeSong navidromeSong in maybeExisting)
        {
            var result = new MediaItemScanResult<NavidromeSong>(navidromeSong) { IsAdded = false };
            if (navidromeSong.Etag != item.Etag || deepScan)
            {
                await UpdateSong(dbContext, navidromeSong, item);
                result.IsUpdated = true;
            }

            return result;
        }

        return await AddSong(dbContext, library, item, cancellationToken);
    }

    public async Task<Unit> SetEtag(NavidromeSong song, string etag)
    {
        await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync();
        return await dbContext.Connection.ExecuteAsync(
            "UPDATE NavidromeSong SET Etag = @Etag WHERE Id = @Id",
            new { Etag = etag, song.Id }).Map(_ => Unit.Default);
    }

    private static async Task UpdateSong(TvContext dbContext, NavidromeSong existing, NavidromeSong incoming)
    {
        // library path is used for search indexing later
        incoming.LibraryPath = existing.LibraryPath;
        incoming.Id = existing.Id;

        // metadata
        SongMetadata metadata = existing.SongMetadata.Head();
        SongMetadata incomingMetadata = incoming.SongMetadata.Head();
        metadata.MetadataKind = incomingMetadata.MetadataKind;
        metadata.Title = incomingMetadata.Title;
        metadata.SortTitle = incomingMetadata.SortTitle;
        metadata.Album = incomingMetadata.Album;
        metadata.Artists = incomingMetadata.Artists;
        metadata.AlbumArtists = incomingMetadata.AlbumArtists;
        metadata.Track = incomingMetadata.Track;
        metadata.Comment = incomingMetadata.Comment;
        metadata.Year = incomingMetadata.Year;
        metadata.ReleaseDate = incomingMetadata.ReleaseDate;
        metadata.DateAdded = incomingMetadata.DateAdded;
        metadata.DateUpdated = DateTime.UtcNow;

        // genres
        foreach (Genre genre in metadata.Genres
                     .Filter(g => incomingMetadata.Genres.All(g2 => g2.Name != g.Name))
                     .ToList())
        {
            metadata.Genres.Remove(genre);
        }

        foreach (Genre genre in incomingMetadata.Genres
                     .Filter(g => metadata.Genres.All(g2 => g2.Name != g.Name))
                     .ToList())
        {
            metadata.Genres.Add(genre);
        }

        // tags
        foreach (Tag tag in metadata.Tags
                     .Filter(g => incomingMetadata.Tags.All(g2 => g2.Name != g.Name))
                     .Filter(g => g.ExternalCollectionId is null)
                     .ToList())
        {
            metadata.Tags.Remove(tag);
        }

        foreach (Tag tag in incomingMetadata.Tags
                     .Filter(g => metadata.Tags.All(g2 => g2.Name != g.Name))
                     .ToList())
        {
            metadata.Tags.Add(tag);
        }

        // version
        MediaVersion version = existing.MediaVersions.Head();
        MediaVersion incomingVersion = incoming.MediaVersions.Head();
        version.Name = incomingVersion.Name;
        version.DateAdded = incomingVersion.DateAdded;
        version.Duration = incomingVersion.Duration;

        // media file
        MediaFile file = version.MediaFiles.Head();
        MediaFile incomingFile = incomingVersion.MediaFiles.Head();
        file.Path = incomingFile.Path;
        file.PathHash = PathUtils.GetPathHash(incomingFile.Path);

        await dbContext.SaveChangesAsync();
    }

    private async Task<Either<BaseError, MediaItemScanResult<NavidromeSong>>> AddSong(
        TvContext dbContext,
        NavidromeLibrary library,
        NavidromeSong song,
        CancellationToken cancellationToken)
    {
        try
        {
            if (await MediaItemRepository.MediaFileAlreadyExists(
                    song,
                    library.Paths.Head().Id,
                    dbContext,
                    _logger,
                    cancellationToken))
            {
                return new MediaFileAlreadyExists();
            }

            // blank out etag for initial save in case other updates fail
            string etag = song.Etag;
            song.Etag = string.Empty;

            song.LibraryPathId = library.Paths.Head().Id;

            await dbContext.AddAsync(song, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);

            // restore etag
            song.Etag = etag;

            await dbContext.Entry(song).Reference(s => s.LibraryPath).LoadAsync(cancellationToken);
            await dbContext.Entry(song.LibraryPath).Reference(lp => lp.Library).LoadAsync(cancellationToken);
            return new MediaItemScanResult<NavidromeSong>(song) { IsAdded = true };
        }
        catch (Exception ex)
        {
            return BaseError.New(ex.ToString());
        }
    }
}
