using ErsatzTV.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ErsatzTV.Application.Audiobookshelf;

public class GetPodcastsHandler : IRequestHandler<GetPodcasts, List<PodcastViewModel>>
{
    private readonly IDbContextFactory<TvContext> _dbContextFactory;

    public GetPodcastsHandler(IDbContextFactory<TvContext> dbContextFactory) =>
        _dbContextFactory = dbContextFactory;

    public async Task<List<PodcastViewModel>> Handle(
        GetPodcasts request,
        CancellationToken cancellationToken)
    {
        await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        List<PodcastViewModel> podcasts = await dbContext.AudiobookshelfShows
            .AsNoTracking()
            .Where(s => s.ItemId.StartsWith("podcast:"))
            .Select(s => new PodcastViewModel(
                s.Id,
                s.ShowMetadata.Select(sm => sm.Title).FirstOrDefault() ?? string.Empty,
                s.Seasons.SelectMany(season => season.Episodes).Count(),
                s.LibraryPath.Library.Name))
            .ToListAsync(cancellationToken);

        return podcasts
            .OrderBy(p => p.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.LibraryName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
