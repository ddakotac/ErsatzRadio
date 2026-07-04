# ErsatzRadio — Project Plan & Session Log

Fork of ErsatzTV building a schedule-aware, on-demand internet radio platform.
Sources: Navidrome (music) + Audiobookshelf (audiobooks/podcasts) + local + live restreams.

## Key architecture discoveries
- ErsatzTV already has Song/Artist domain models, Songs LibraryMediaKind, SongFolderScanner (local music works today)
- Songs currently play as generated video (SongVideoGenerator + SongProgressFilter) -> we add an audio-only channel mode instead of ripping out video
- On-demand serving already exists: StartFFmpegSessionHandler/HlsSessionWorker spin up ffmpeg per listener, idle-timeout teardown
- Remote source pattern: Core (domain+interfaces) / Infrastructure (API client+models) / Scanner (sync commands+scanners) / EF configs+migrations (Sqlite AND MySql) / Application layer / Blazor UI pages / DI in both ErsatzTV and ErsatzTV.Scanner
- Subsonic song paths are RELATIVE to music folder; NavidromePathReplacement with empty NavidromePath prefix prepends LocalPath
- Navidrome auth: subsonic token (u, t=md5(pw+salt), s, v=1.16.1, c=ErsatzRadio, f=json)

## Build order
1. Navidrome scanner (IN PROGRESS)
2. Audio-only channel mode (HLS audio; skip SongVideoGenerator)
3. Audiobookshelf scanner (Author=Show, Book=Season, Chapter=Episode; serial/episodic flag)
4. Icecast/direct MP3 endpoint
5. TTL-aware priority interrupt queue + injection API (Home Assistant/Wyoming TTS)
6. Docker packaging

## Session log
### Session 1 (2026-07-03): Navidrome engine core
DONE (branch feature/navidrome-scanner):
- Domain: NavidromeMediaSource, NavidromeConnection, NavidromePathReplacement, NavidromeLibrary, NavidromeSong
- Core: NavidromeConnectionParameters, NavidromeSecrets, NavidromeServerInformation, NavidromeItemEtag, NavidromePathReplacementService
- Interfaces: INavidromeApiClient, INavidromeSecretStore, INavidromePathReplacementService, INavidromeSongLibraryScanner, INavidromeSongRepository, IMediaServerSongRepository (new generic base - first remote-song support in codebase)
- Infrastructure: NavidromeApiClient (raw HttpClient, not Refit; subsonic ping/getMusicFolders/getAlbumList2 paged/getAlbum), Subsonic JSON models, NavidromeSecretStore
- FileSystemLayout: added NavidromeSecretsPath

NEXT SESSION (Navidrome part 2 — DB + scanner):
- TvContext DbSets + EF entity configurations (mirror Jellyfin configs in ErsatzTV.Infrastructure/Data/Configurations)
- EF migrations for BOTH ErsatzTV.Infrastructure.Sqlite and ErsatzTV.Infrastructure.MySql (use dotnet ef migrations add)
- NavidromeSongRepository impl (model on JellyfinMovieRepository)
- MediaServerSongLibraryScanner base class (model on MediaServerMovieLibraryScanner in ErsatzTV.Scanner/Core/Metadata)
- NavidromeSongLibraryScanner + Scanner Application commands (SynchronizeNavidromeLibraryById etc.)
- ErsatzTV.Application/Navidrome (connection params query, save secrets, sync commands - mirror Application/Jellyfin)
- Blazor UI: MediaSources pages for Navidrome (mirror Jellyfin pages)
- DI registration in ErsatzTV Startup + ErsatzTV.Scanner Program
- Search index integration for NavidromeSong

NOTE: sandbox NuGet blocked (403) — ask user to allowlist api.nuget.org, nuget.org, globalcdn.nuget.org in Claude network settings for compile verification
