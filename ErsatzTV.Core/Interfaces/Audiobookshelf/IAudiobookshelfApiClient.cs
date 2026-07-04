using ErsatzTV.Core.Audiobookshelf;
using ErsatzTV.Core.Domain;

namespace ErsatzTV.Core.Interfaces.Audiobookshelf;

public interface IAudiobookshelfApiClient
{
    Task<Either<BaseError, AudiobookshelfServerInformation>> GetServerInformation(
        AudiobookshelfConnectionParameters connectionParameters);

    Task<Either<BaseError, List<AudiobookshelfLibrary>>> GetLibraries(
        AudiobookshelfConnectionParameters connectionParameters);

    IAsyncEnumerable<Tuple<AudiobookshelfShow, int>> GetShowLibraryItems(
        AudiobookshelfConnectionParameters connectionParameters,
        AudiobookshelfLibrary library,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<Tuple<AudiobookshelfSeason, int>> GetSeasonLibraryItems(
        AudiobookshelfConnectionParameters connectionParameters,
        AudiobookshelfLibrary library,
        AudiobookshelfShow show,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<Tuple<AudiobookshelfEpisode, int>> GetEpisodeLibraryItems(
        AudiobookshelfConnectionParameters connectionParameters,
        AudiobookshelfLibrary library,
        AudiobookshelfSeason season,
        CancellationToken cancellationToken = default);
}
