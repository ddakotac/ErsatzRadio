using ErsatzTV.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ErsatzTV.Application.Songs;

public class GetSongAlbumsHandler : IRequestHandler<GetSongAlbums, List<SongAlbumViewModel>>
{
    private readonly IDbContextFactory<TvContext> _dbContextFactory;

    public GetSongAlbumsHandler(IDbContextFactory<TvContext> dbContextFactory) =>
        _dbContextFactory = dbContextFactory;

    public async Task<List<SongAlbumViewModel>> Handle(
        GetSongAlbums request,
        CancellationToken cancellationToken)
    {
        await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var songs = await dbContext.SongMetadata
            .AsNoTracking()
            .Select(sm => new { sm.Album, sm.Artists, sm.AlbumArtists })
            .ToListAsync(cancellationToken);

        var grouped = songs
            .Select(s => new
            {
                Artist = SongGrouping.EffectiveArtist(s.AlbumArtists, s.Artists),
                Album = s.Album ?? string.Empty
            })
            .Where(s => !string.IsNullOrWhiteSpace(s.Album));

        foreach (string artist in request.Artist.Filter(a => !string.IsNullOrWhiteSpace(a)))
        {
            grouped = grouped.Where(s => string.Equals(s.Artist, artist, StringComparison.OrdinalIgnoreCase));
        }

        return grouped
            .GroupBy(s => (Album: s.Album.ToUpperInvariant(), Artist: s.Artist.ToUpperInvariant()))
            .Select(g => new SongAlbumViewModel(g.First().Album, g.First().Artist, g.Count()))
            .OrderBy(a => a.Artist, StringComparer.OrdinalIgnoreCase)
            .ThenBy(a => a.Album, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
