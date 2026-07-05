using ErsatzTV.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ErsatzTV.Application.Songs;

public class GetSongArtistsHandler : IRequestHandler<GetSongArtists, List<SongArtistViewModel>>
{
    private readonly IDbContextFactory<TvContext> _dbContextFactory;

    public GetSongArtistsHandler(IDbContextFactory<TvContext> dbContextFactory) =>
        _dbContextFactory = dbContextFactory;

    public async Task<List<SongArtistViewModel>> Handle(
        GetSongArtists request,
        CancellationToken cancellationToken)
    {
        await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var songs = await dbContext.SongMetadata
            .AsNoTracking()
            .Select(sm => new { sm.Album, sm.Artists, sm.AlbumArtists })
            .ToListAsync(cancellationToken);

        return songs
            .Select(s => new
            {
                Artist = SongGrouping.EffectiveArtist(s.AlbumArtists, s.Artists),
                s.Album
            })
            .Where(s => !string.IsNullOrWhiteSpace(s.Artist))
            .GroupBy(s => s.Artist, StringComparer.OrdinalIgnoreCase)
            .Select(g => new SongArtistViewModel(
                g.Key,
                g.Select(s => s.Album ?? string.Empty)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count(a => !string.IsNullOrWhiteSpace(a)),
                g.Count()))
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
