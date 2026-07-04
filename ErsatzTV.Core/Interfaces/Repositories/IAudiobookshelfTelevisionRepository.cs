using ErsatzTV.Core.Audiobookshelf;
using ErsatzTV.Core.Domain;

namespace ErsatzTV.Core.Interfaces.Repositories;

public interface IAudiobookshelfTelevisionRepository : IMediaServerTelevisionRepository<AudiobookshelfLibrary,
    AudiobookshelfShow, AudiobookshelfSeason, AudiobookshelfEpisode, AudiobookshelfItemEtag>
{
}
