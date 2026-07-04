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
1. Navidrome scanner (CODE COMPLETE - needs migration + testing)
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

### Session 2 (2026-07-04): Navidrome full vertical
DONE (branch feature/navidrome-scanner):
- TvContext DbSets + 5 EF configurations (NavidromeMediaSource/Connection/PathReplacement/Library/Song tables)
- IMediaSourceRepository + MediaSourceRepository: full Navidrome method set (Upsert, GetAll, Get, libraries, path replacements, enable/disable sync, UpdateLibraries overload, DeleteAll)
- NavidromeSongRepository (GetOrAdd/etag diff/flag states, modeled on JellyfinMovieRepository)
- MediaServerSongLibraryScanner generic base (new; no remote-streaming path, no subtitles/chapters)
- NavidromeSongLibraryScanner (statistics from subsonic data incl. audio MediaStream built in API client projection)
- Scanner: SynchronizeNavidromeLibraryById cmd+handler, Worker "scan-navidrome" command, Program DI
- Main app: ISynchronizeNavidromeLibraryById cmds, CallNavidromeLibraryScannerHandler, SaveNavidromeSecrets, SynchronizeNavidromeLibraries, UpdateNavidromeLibraryPreferences, queries + view models, Startup DI
- NavidromeController: temporary REST api (secrets/sources/libraries/preferences/scan) for curl testing until Blazor UI exists
- Search index: no changes needed (case Song matches NavidromeSong)

USER MUST RUN: ./scripts/add-migration.sh Add_Navidrome  (generates Sqlite + MySql migrations; requires dotnet-ef tool: dotnet tool install -g dotnet-ef)

NEXT SESSION (session 3):
- Fix any build errors from session 2 patch
- Verify end-to-end: secrets -> library sync -> song scan -> songs visible in UI search/collections
- Blazor UI pages for Navidrome (mirror Pages/MediaSources jellyfin pages) OR proceed to audio-only channel mode first (user priority call)
- Scheduled periodic scans (ScannerService wiring for SynchronizeNavidromeLibraryByIdIfNeeded)
- Artwork sync from subsonic getCoverArt

### Session 3 (2026-07-04): native api pivot + libraries page
CRITICAL DISCOVERY: navidrome subsonic api reports TAG-DERIVED VIRTUAL paths (e.g. "[Unknown Album]"), not filesystem paths. Path replacement against subsonic paths can never work. Pivoted song enumeration to navidrome NATIVE rest api (/auth/login + /api/song paging with x-total-count), which exposes real absolute container paths (/music/...). Also much faster: pages all songs directly instead of one getAlbum call per album. Subsonic api still used for ping + getMusicFolders.
- Path replacement guidance is now classic prefix mapping: NavidromePath "/music" -> LocalPath "/media/shared/Music" (user must update their existing empty-prefix replacement)
- Etag includes path, so all songs auto-update on next scan (virtual->real path)
- Libraries page: NavidromeLibrary now included (filter+mapper+viewmodel inherits LibraryViewModel), scan button wired via ScannerWorkerChannel
- SynchronizeNavidromeLibraries now IScannerBackgroundServiceRequest
NOTE: fresh-install Libraries page shows nothing until local libraries get paths - stock behavior, not a bug.
STILL TODO session 4: media sources UI page for navidrome (add/edit/secrets/path replacements in browser), scheduled periodic scans, artwork via getCoverArt, THEN AUDIO-ONLY CHANNEL MODE.

### Session 3b/3c addendum
- ScannerService channel consumer: navidrome cases added (was throwing NotSupportedException)
- Navidrome native api returns paths RELATIVE TO LIBRARY ROOT (no /music prefix); correct replacement is empty NavidromePath prefix + LocalPath=/media/shared/Music (prepend)
- ARCHITECTURE DECISION: navidrome paths resolved to LOCAL paths at scan time and stored locally (like local libraries), NOT at playout time. Playout/media cards/troubleshooting need zero navidrome-specific logic. CONSEQUENCE: after changing a path replacement, run a DEEP scan to rewrite stored paths (non-deep scans skip unchanged etags and leave stale paths).

### Session 4 (2026-07-04): audio-only channel mode (branch feature/audio-channels)
- ChannelSongVideoMode.AudioOnly = 2 (no migration needed; existing int column + existing channel editor dropdown)
- IFFmpegProcessService.ForAudioOnlyPlayoutItem + impl in FFmpegLibraryProcessService: bypasses the video pipeline builder (which asserts exactly one video stream); assembles ffmpeg args directly mirroring OutputFormatHls arg shapes (hls_time 4, segment template live%06d.ts, append_list/discont_start/independent_segments/program_date_time/omit_endlist, mpegts_flags initial_discontinuity on first transcode, latm for AacLatm), -map 0:a -vn, aac/ac3/copy from profile, apad to slot duration, -t limit, -output_ts_offset, -readrate 1.0 when realtime
- Playout handler: audio-only branch skips SongVideoGenerator + watermarks/subtitles machinery entirely
- HLS session lifecycle untouched (worker just watches live.m3u8 + segments)
KNOWN RISKS: (1) untested ffmpeg arg fidelity - expect log-driven fixes; (2) if IptvController emits a master playlist with video codec attrs, audio-only variants may need a codecs fix there; (3) error/offline path still generates VIDEO error cards - audio-only silence (anullsrc) error process is TODO; (4) direct MP3/icecast endpoint TODO

### Session 5 (2026-07-04): audiobookshelf part 1 (branch feature/audiobookshelf)
Freebie included: audio-only playout now guards live inputs (no -ss / -readrate for RemoteStream IsLive) so internet radio restreams work on radio channels.
ABS design decisions:
- Auth: Bearer token in Authorization header (RemoteMediaSourceSecrets.ApiKey stores the token; no username needed)
- Book libraries: /api/libraries/{id}/authors -> Shows (ItemId "author:{id}", tag "audiobook-author"); books filtered via filter=authors.{base64 id} ordered by (publishedYear,title) -> Seasons (ItemId = book libraryItem id, SeasonNumber = order index); /api/items/{bookId}?expanded=1 audioFiles -> Episodes (ItemId "{bookId}:t{index}", EpisodeNumber = track order)
- Podcast libraries: podcasts -> Shows (ItemId "podcast:{id}", tags "podcast" + "podcast-episodic"/"podcast-serial" for smart collections), synthetic Season 1, media.episodes ordered by publishedAt -> Episodes (ItemId "pe:{episodeId}")
- Ebook libraries skipped (mediaType filter)
- Etags: sha256 over ids + updatedAt (+ counts/paths); library items use ABS updatedAt ms timestamps
- Paths: ABS returns absolute container paths in audioFile metadata; resolve to local at scan time like navidrome (part 2 scanner will apply replacements before storage)
DONE part 1: domain (MediaSource/Connection/PathReplacement/Library w AbsMediaType, Show/Season/Episode), core (ConnectionParameters/Secrets/ServerInformation/ItemEtag/PathReplacementService), interfaces (api client/secret store/path service/tv scanner/IAudiobookshelfTelevisionRepository over existing generic IMediaServerTelevisionRepository), infrastructure (models incl flexible converter, api client, secret store), FileSystemLayout secrets path.
PART 2 TODO (mirror navidrome parts 2-3 + jellyfin television): TvContext DbSets + EF configs + migrations (user: ./scripts/add-migration.sh Add_Audiobookshelf), AudiobookshelfTelevisionRepository (model on JellyfinTelevisionRepository), scanner class over MediaServerTelevisionLibraryScanner base w/ scan-time path resolution, scanner worker command "scan-audiobookshelf" + handler + DI both processes, app-layer commands (secrets/sync/preferences/path replacements) + controller /api/audiobookshelf/*, ScannerService channel cases, Libraries page filter+mapper+viewmodel+scan case.
