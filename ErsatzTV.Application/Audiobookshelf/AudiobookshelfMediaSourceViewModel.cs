using ErsatzTV.Application.MediaSources;

namespace ErsatzTV.Application.Audiobookshelf;

public record AudiobookshelfMediaSourceViewModel(int Id, string Name, string Address) : RemoteMediaSourceViewModel(
    Id,
    Name,
    Address);
