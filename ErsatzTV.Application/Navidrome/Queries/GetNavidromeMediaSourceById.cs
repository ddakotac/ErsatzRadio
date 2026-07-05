namespace ErsatzTV.Application.Navidrome;

public record GetNavidromeMediaSourceById(int NavidromeMediaSourceId) : IRequest<Option<NavidromeMediaSourceViewModel>>;
