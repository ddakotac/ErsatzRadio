using ErsatzTV.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ErsatzTV.Application.Audiobookshelf;

public class GetAudiobookAuthorsHandler : IRequestHandler<GetAudiobookAuthors, List<AudiobookAuthorViewModel>>
{
    private readonly IDbContextFactory<TvContext> _dbContextFactory;

    public GetAudiobookAuthorsHandler(IDbContextFactory<TvContext> dbContextFactory) =>
        _dbContextFactory = dbContextFactory;

    public async Task<List<AudiobookAuthorViewModel>> Handle(
        GetAudiobookAuthors request,
        CancellationToken cancellationToken)
    {
        await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        List<AudiobookAuthorViewModel> authors = await dbContext.AudiobookshelfShows
            .AsNoTracking()
            .Where(s => s.ItemId.StartsWith("author:"))
            .Select(s => new AudiobookAuthorViewModel(
                s.Id,
                s.ShowMetadata.Select(sm => sm.Title).FirstOrDefault() ?? string.Empty,
                s.Seasons.Count,
                s.LibraryPath.Library.Name))
            .ToListAsync(cancellationToken);

        return authors
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(a => a.LibraryName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
