namespace ErsatzTV.Application.Songs;

public record GetSongAlbums(Option<string> Artist) : IRequest<List<SongAlbumViewModel>>;
