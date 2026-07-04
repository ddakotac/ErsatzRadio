using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Interfaces.Repositories;

namespace ErsatzTV.Application.Navidrome;

public class UpdateNavidromePathReplacementsHandler : IRequestHandler<UpdateNavidromePathReplacements,
    Either<BaseError, Unit>>
{
    private readonly IMediaSourceRepository _mediaSourceRepository;

    public UpdateNavidromePathReplacementsHandler(IMediaSourceRepository mediaSourceRepository) =>
        _mediaSourceRepository = mediaSourceRepository;

    public Task<Either<BaseError, Unit>> Handle(
        UpdateNavidromePathReplacements request,
        CancellationToken cancellationToken) =>
        Validate(request)
            .MapT(pms => MergePathReplacements(request, pms))
            .Bind(v => v.ToEitherAsync());

    private Task<Unit> MergePathReplacements(
        UpdateNavidromePathReplacements request,
        NavidromeMediaSource navidromeMediaSource)
    {
        navidromeMediaSource.PathReplacements ??= new List<NavidromePathReplacement>();

        var incoming = request.PathReplacements.Map(Project).ToList();

        var toAdd = incoming.Filter(r => r.Id < 1).ToList();
        var toRemove = navidromeMediaSource.PathReplacements.Filter(r => incoming.All(pr => pr.Id != r.Id)).ToList();
        var toUpdate = incoming.Except(toAdd).ToList();

        return _mediaSourceRepository.UpdatePathReplacements(navidromeMediaSource.Id, toAdd, toUpdate, toRemove);
    }

    private static NavidromePathReplacement Project(NavidromePathReplacementItem vm) =>
        new() { Id = vm.Id, NavidromePath = vm.NavidromePath, LocalPath = vm.LocalPath };

    private Task<Validation<BaseError, NavidromeMediaSource>> Validate(UpdateNavidromePathReplacements request) =>
        NavidromeMediaSourceMustExist(request);

    private Task<Validation<BaseError, NavidromeMediaSource>> NavidromeMediaSourceMustExist(
        UpdateNavidromePathReplacements request) =>
        _mediaSourceRepository.GetNavidrome(request.NavidromeMediaSourceId)
            .Map(v => v.ToValidation<BaseError>(
                $"Navidrome media source {request.NavidromeMediaSourceId} does not exist."));
}
