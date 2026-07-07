using ErsatzTV.Core.Audiobookshelf;
using Flurl;
using ErsatzTV.Core.Domain;
using ErsatzTV.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ErsatzTV.Application.Audiobookshelf;

public class GetAudiobookBooksHandler : IRequestHandler<GetAudiobookBooks, List<AudiobookBookViewModel>>
{
    private readonly IDbContextFactory<TvContext> _dbContextFactory;

    public GetAudiobookBooksHandler(IDbContextFactory<TvContext> dbContextFactory) =>
        _dbContextFactory = dbContextFactory;

    public async Task<List<AudiobookBookViewModel>> Handle(
        GetAudiobookBooks request,
        CancellationToken cancellationToken)
    {
        await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        IQueryable<AudiobookBookViewModel> query =
            from season in dbContext.AudiobookshelfSeasons.AsNoTracking()
            join show in dbContext.AudiobookshelfShows.AsNoTracking() on season.ShowId equals show.Id
            where show.ItemId.StartsWith("author:")
            select new AudiobookBookViewModel(
                season.Id,
                show.Id,
                season.SeasonMetadata.Select(sm => sm.Title).FirstOrDefault() ?? string.Empty,
                show.ShowMetadata.Select(sm => sm.Title).FirstOrDefault() ?? string.Empty,
                season.Episodes.Count,
                show.LibraryPath.Library.Name,
                season.SeasonMetadata
                    .SelectMany(sm => sm.Artwork)
                    .Where(a => a.ArtworkKind == ArtworkKind.Poster)
                    .Select(a => a.Path)
                    .FirstOrDefault() ?? string.Empty);

        foreach (int showId in request.ShowId)
        {
            query =
                from season in dbContext.AudiobookshelfSeasons.AsNoTracking()
                join show in dbContext.AudiobookshelfShows.AsNoTracking() on season.ShowId equals show.Id
                where show.ItemId.StartsWith("author:") && show.Id == showId
                select new AudiobookBookViewModel(
                    season.Id,
                    show.Id,
                    season.SeasonMetadata.Select(sm => sm.Title).FirstOrDefault() ?? string.Empty,
                    show.ShowMetadata.Select(sm => sm.Title).FirstOrDefault() ?? string.Empty,
                    season.Episodes.Count,
                    show.LibraryPath.Library.Name,
                    season.SeasonMetadata
                        .SelectMany(sm => sm.Artwork)
                        .Where(a => a.ArtworkKind == ArtworkKind.Poster)
                        .Select(a => a.Path)
                        .FirstOrDefault() ?? string.Empty);
        }

        List<AudiobookBookViewModel> books = await query.ToListAsync(cancellationToken);

        return books
            .Map(b => b with { Poster = RewritePoster(b.Poster) })
            .OrderBy(b => b.Author, StringComparer.OrdinalIgnoreCase)
            .ThenBy(b => b.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        static string RewritePoster(string poster) =>
            poster.StartsWith("abs://", StringComparison.OrdinalIgnoreCase)
                ? AudiobookshelfUrl.RelativeProxyForArtwork(poster).SetQueryParam("width", 440)
                : poster;
    }
}
