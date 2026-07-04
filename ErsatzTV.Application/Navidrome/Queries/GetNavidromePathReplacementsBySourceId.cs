namespace ErsatzTV.Application.Navidrome;

public record GetNavidromePathReplacementsBySourceId(int NavidromeMediaSourceId)
    : IRequest<List<NavidromePathReplacementViewModel>>;
