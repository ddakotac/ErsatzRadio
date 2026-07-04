namespace ErsatzTV.Application.Navidrome;

public record GetNavidromeLibrariesBySourceId(int NavidromeMediaSourceId)
    : IRequest<List<NavidromeLibraryViewModel>>;
