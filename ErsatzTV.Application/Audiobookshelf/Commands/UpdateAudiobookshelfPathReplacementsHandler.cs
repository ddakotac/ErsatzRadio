using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Interfaces.Repositories;

namespace ErsatzTV.Application.Audiobookshelf;

public class UpdateAudiobookshelfPathReplacementsHandler : IRequestHandler<UpdateAudiobookshelfPathReplacements,
    Either<BaseError, Unit>>
{
    private readonly IMediaSourceRepository _mediaSourceRepository;

    public UpdateAudiobookshelfPathReplacementsHandler(IMediaSourceRepository mediaSourceRepository) =>
        _mediaSourceRepository = mediaSourceRepository;

    public Task<Either<BaseError, Unit>> Handle(
        UpdateAudiobookshelfPathReplacements request,
        CancellationToken cancellationToken) =>
        Validate(request)
            .MapT(pms => MergePathReplacements(request, pms))
            .Bind(v => v.ToEitherAsync());

    private Task<Unit> MergePathReplacements(
        UpdateAudiobookshelfPathReplacements request,
        AudiobookshelfMediaSource audiobookshelfMediaSource)
    {
        audiobookshelfMediaSource.PathReplacements ??= new List<AudiobookshelfPathReplacement>();

        var incoming = request.PathReplacements.Map(Project).ToList();

        var toAdd = incoming.Filter(r => r.Id < 1).ToList();
        var toRemove = audiobookshelfMediaSource.PathReplacements.Filter(r => incoming.All(pr => pr.Id != r.Id)).ToList();
        var toUpdate = incoming.Except(toAdd).ToList();

        return _mediaSourceRepository.UpdatePathReplacements(audiobookshelfMediaSource.Id, toAdd, toUpdate, toRemove);
    }

    private static AudiobookshelfPathReplacement Project(AudiobookshelfPathReplacementItem vm) =>
        new() { Id = vm.Id, AudiobookshelfPath = vm.AudiobookshelfPath, LocalPath = vm.LocalPath };

    private Task<Validation<BaseError, AudiobookshelfMediaSource>> Validate(UpdateAudiobookshelfPathReplacements request) =>
        AudiobookshelfMediaSourceMustExist(request);

    private Task<Validation<BaseError, AudiobookshelfMediaSource>> AudiobookshelfMediaSourceMustExist(
        UpdateAudiobookshelfPathReplacements request) =>
        _mediaSourceRepository.GetAudiobookshelf(request.AudiobookshelfMediaSourceId)
            .Map(v => v.ToValidation<BaseError>(
                $"Audiobookshelf media source {request.AudiobookshelfMediaSourceId} does not exist."));
}
