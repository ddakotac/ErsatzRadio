using ErsatzTV.Core.Audiobookshelf;
using ErsatzTV.Core.Domain;

namespace ErsatzTV.Core.Interfaces.Repositories;

public record AudiobookshelfShowTitleItemIdResult(string Title, string ItemId);

public interface IAudiobookshelfTelevisionRepository : IMediaServerTelevisionRepository<AudiobookshelfLibrary,
    AudiobookshelfShow, AudiobookshelfSeason, AudiobookshelfEpisode, AudiobookshelfItemEtag>
{
}
