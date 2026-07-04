using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Navidrome;

namespace ErsatzTV.Core.Interfaces.Repositories;

public interface INavidromeSongRepository : IMediaServerSongRepository<NavidromeLibrary, NavidromeSong, NavidromeItemEtag>
{
}
