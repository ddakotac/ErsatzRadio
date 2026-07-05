using ErsatzTV.Application.MediaSources;

namespace ErsatzTV.Application.Navidrome;

public record NavidromeMediaSourceViewModel(int Id, string Name, string Address) : RemoteMediaSourceViewModel(
    Id,
    Name,
    Address);
