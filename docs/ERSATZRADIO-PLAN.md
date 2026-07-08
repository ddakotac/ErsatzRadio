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

### Session 6 (2026-07-04): TTL-aware priority interrupt queue + injection API + media source UI (branch feature/interrupt-queue)
Note: ABS part 2 + migrations landed on main before this session (commits b01f0a7..d14ce12).
COMPILE-VERIFIED this session (NuGet now reachable in sandbox; dotnet 10 SDK via dotnet-install.sh).

Interrupt queue design:
- In-memory singleton IChannelInterruptService/ChannelInterruptService (Core/Interrupts; CA1711 forbids *Queue type names) - per-channel list, lower priority number wins, FIFO within priority, TTL purge on dequeue
- InterruptQueueItem: Id/ChannelNumber/Path/Title/Priority/EnqueuedAt/ExpiresAt/Duration/DeleteFileWhenDone
- Priority 0 = EMERGENCY: Enqueue invokes a per-channel force handler registered by HlsSessionWorker.Run; handler cancels a per-transcode CTS linked into the scheduled ffmpeg process
- Force-cut recovery: cancellation catch in Transcode() distinguishes interrupt-cut from session-cancel; recovers _transcodedUntil from GetPtsOffset (last PTS in playlist = actual audio written), sets SeekAndRealtime, returns true so the loop continues
- Priority >= 1 waits for the next item boundary: Run loop dequeues at top of each iteration (5s max latency when buffered ahead) and plays via TranscodeInterrupt
- TranscodeInterrupt: GetInterruptProcessByChannelNumber (FFmpegProcessHandler subclass) reuses ForAudioOnlyPlayoutItem with the injected file, start=_transcodedUntil, finish=start+duration, segmentKey for discontinuity map; failures DROP the item and keep the session alive (no error card)
- After any interrupt: state=SeekAndRealtime so the next scheduled request seeks into the covered playout item at _transcodedUntil - scheduled content is "covered", exactly like broadcast radio
- Audio-only channels ONLY (handler + API both enforce ChannelSongVideoMode.AudioOnly)
- KNOWN LIMITATION: HLS work-ahead buffer means even emergency interrupts air after already-written segments play out (~30-60s worst case + player buffer). True playlist truncation not attempted (players have already buffered).
- Emergency cannot cut another interrupt (force handler only wraps scheduled transcodes)

Injection API (InterruptsController, FileSystemLayout.InterruptsFolder for uploads):
- POST /api/channels/{num}/interrupts (multipart file + priority/ttlSeconds/title; 100MB limit; uploaded files deleted after play/expiry)
- POST /api/channels/{num}/interrupts/path (json path/priority/ttlSeconds/title/deleteWhenDone for server-local files, e.g. HA shared volume)
- GET / DELETE {id} / DELETE (list/remove/clear)
- Duration probed with ffprobe at enqueue (400 if unprobeable); default priority 1, ttl 300s

Media source UI (the "general cleanup"):
- RemoteMediaSourceEditor extended: ShowUsername/ApiKeyLabel/MaskApiKey params + RequireUsername flag on edit VM (validator .When) - Emby/Jellyfin behavior unchanged
- Navidrome + Audiobookshelf VMs now inherit RemoteMediaSourceViewModel (shared component generic constraint)
- New app-layer: Disconnect{Navidrome,Audiobookshelf} (+search index removal/unlock), Get{Navidrome,Audiobookshelf}Secrets, Get{Navidrome,Audiobookshelf}MediaSourceById
- 8 pages: {Navidrome,Audiobookshelf}{MediaSources,MediaSourceEditor,LibrariesEditor,PathReplacementsEditor}.razor at media/sources/{navidrome,audiobookshelf}[...] (mirror jellyfin pages; navidrome editor uses username+password, abs uses API token)
- MainLayout nav links + resx entries (en + pl)
- NavidromeController/AudiobookshelfController REST surfaces left in place (harmless; HA-friendly)

Post-session-6 live testing results:
- Podcast library scan crashed: audioFile.index null for podcast episodes -> fixed (nullable + positional fallback for book tracks, etags unchanged; patch 0004)
- ABS book/podcast libraries + navidrome songs confirmed scanning and visible in UI

NEXT SESSION (session 7): MEDIA NAVIGATION UI (dakota-confirmed priority)
- Library disambiguation: show source library name on show/season/episode/song cards and detail pages
  (dakota has 7 "JK Rowling" shows across 7 libraries). Library filter dropdown on Shows/Songs list pages.
  Search index already carries library_id so grouped/filtered queries are cheap.
- Book (season) display titles: seasons render hardcoded "Season {n}" - surface the book title
  (scanner knows it; needs SeasonMetadata title through to season cards + detail pages)
- Chapter (episode) titles: prefer ABS chapters[] titles over audio filenames when counts align;
  fallback filename -> "{book} - Part {n}" chain stays
- Navidrome artist/album browse: Music->Artists is Artist-entity backed (music video libraries only),
  so navidrome-only installs see a blank page. Build artist + album pages grouped over song
  metadata/tags (search-index-backed), drill-down artist -> albums -> songs
- Carry-over: audio-only error process (anullsrc), icecast/direct MP3 endpoint, optional interrupt
  queue management page

Interrupt queue live-testing checklist (parallel to session 7, log-driven):
- boundary interrupt (priority 1) airs at song boundary; TTL expiry drops stale items
- emergency (priority 0) force-cut: verify PTS recovery resumes scheduled item at correct offset
- HA -> tts_get_url -> multipart upload flow (docs/INTERRUPTS.md)
- fallback filler on audio-only channels confirmed supported by code review (same AudioOnly branch);
  recommend assigning fallback collections to radio channels until anullsrc error process lands


### Session 7 (2026-07-05): media navigation ui + remote artwork (branch feature/media-nav-ui)
COMPILE-VERIFIED (app + scanner, 0 errors).

Remote artwork (was: no art for abs or navidrome):
- Root cause: session 4/5 scanners set Artwork = [] everywhere; jellyfin/emby populate scheme urls
  (jellyfin://...) that MediaCards/Television/Movies mappers rewrite to relative proxy urls served by
  ArtworkController with server-side auth
- New schemes: abs://items/{id}/cover, abs://authors/{id}/image (token query auth),
  navidrome://{songId} (subsonic getCoverArt, md5 salted token)
- Core: AudiobookshelfUrl + NavidromeUrl helpers (mirror JellyfinUrl); mapper branches are
  UNCONDITIONAL StartsWith checks (no maybe-source plumbing needed - proxy resolves secrets itself)
- ArtworkController: /artwork/{posters,thumbnails,fanart}/{abs,navidrome}/{*path} (+iptv variants),
  secrets via injected secret stores
- Scanner: author image + podcast/book covers on shows/seasons; song thumbnails (navidrome://{id});
  NavidromeSongRepository.UpdateSong now syncs artwork (was missing); abs repo already synced
- ETAG VERSION BUMP (v2) in both clients: next scan refreshes ALL synced items once so artwork +
  titles land on existing rows. Expect a slow first scan after deploy; benign.

Book/chapter naming:
- Season titles: scanner already stored book titles in SeasonMetadata.Title (session 5); UI hardcoded
  "Season {n}". Mappers now prefer non-empty metadata title (cards + tv detail). Also improves
  jellyfin named seasons.
- Chapter titles: AbsChapter model + chapters[] on expanded items; when chapter count == track count,
  chapter titles name book episodes; fallback chain filename -> "{book} - Part {n}" unchanged

Library disambiguation (7x "JK Rowling" problem):
- Show + song card subtitles append " · {library name}" (null-safe; only when handler includes library)
- QuerySearchIndexShows/SongsHandler: Include LibraryPath.Library
- Library filter dropdown on TelevisionShowList + SongList: query-builder approach - injects/strips
  library_id:{n} in the search query string so pagination/letterbar/permalinks preserve the filter

Thin artist/album browse (navidrome):
- GetSongArtists / GetSongAlbums: narrow EF projection (album/artists/albumArtists), client-side
  grouping (artists/albumArtists are EF json primitive collections - no sql group by). Effective
  artist = albumArtist ?? first artist.
- Pages: media/music/song-artists (name/album count/song count) -> media/music/albums?artist=X ->
  songs?query=album:"X" AND (album_artist:"Y" OR artist:"Y"). Reuses song list + search index for the
  final hop. Quote-escaped lucene phrases.
- Nav: Song Artists + Albums links (en+pl resx). Existing Artists page (music-video Artist entities)
  untouched.

GOTCHA fixed during session: adding LanguageExt Prelude to _Imports.razor breaks razor files that
already import it locally (CS0105-as-error); import per-page instead.

Post-session-7 live testing (dakota):
- Artwork CONFIRMED WORKING for abs (book covers render on seasons page)
- Patches 0006/0007 applied clean; docs patch failed (0005 agenda was never pushed -> plan doc
  drift). RESOLUTION: plan doc now ships as a whole file, not a diff. Drift ended.

NEXT SESSION (session 8): AUTHOR/BOOK LIST VIEWS (dakota-requested) + carry-overs
- Authors list view (table like artists page): Author | book count | Library, click -> show detail
  page (an author = one Show per library; library column disambiguates the 7 Rowlings)
- Books list view (table like albums page): Book | Author | Library, click -> season detail
- Nav: dakota asked to rename "TV Shows" -> "Authors". Caveat: podcasts are also shows. Default
  plan (pending dakota confirmation): nav entry "Authors" points at the NEW list view; existing
  tiles page keeps its route and stays reachable via search
- MediaCard text wrapping: title/subtitle single-line ellipsis cuts off titles, bad when no
  artwork. Switch to 2-line clamp (-webkit-line-clamp) in MediaCard.razor
- Interrupt queue live-test findings from dakota's TTS testing (pending)
- Audio-only error process (anullsrc) - carried over, recommend fallback collections meanwhile
- Icecast/direct MP3 endpoint (build order item 4) - carried over twice now
- Possible: artwork on artist/album tables (getCoverArt by album id), chapter titles live-check,
  search facets, breadcrumbs


### Session 8 (2026-07-05): scheduled interrupts (airAt) + author/book/podcast list views (branch feature/scheduled-interrupts-and-list-views)
COMPILE-VERIFIED (app + scanner, 0 errors).

Live-test results driving this session (all three interrupt mechanisms validated by dakota):
- boundary (p1): played at song boundary, PTS-continuous, resumed mid-schedule. TTL expiry validated.
- emergency (p0): force-cut + PTS recovery exact (recovered until == enqueue + buffer), aired ~50s later
- KEY INSIGHT: the hls timeline IS wall time (program_date_time; _transcodedUntil wall-stamped).
  "air at T" == "make the item boundary land at stream position T". No timer force-cutting needed.

Scheduled interrupts (airAt):
- InterruptQueueItem.AirAt (nullable); ExpiresAt = (airAt ?? enqueuedAt) + ttl - TTL bounds LATENESS
- TryDequeue(channel, asOf) judges eligibility + expiry in STREAM time (worker passes _transcodedUntil)
- PeekNextAirTime(channel, after) -> worker sets TruncateAt on GetPlayoutItemProcessByChannelNumber
  (non-positional init prop, no caller breakage)
- handler clamps finish/duration when TruncateAt in (effectiveNow, finish), audio-only channels only,
  playout-offset-adjusted; isComplete=false -> SeekAndRealtime -> next loop dequeues the due item at
  exactly its air time
- priority 0 force handler only fires when the item is already due (scheduled p0 waits for truncation)
- api: airAt (iso 8601, AssumeLocal) on both endpoints; 400 if unparseable or airAt+ttl already past;
  LanguageExt GOTCHA: Either<L, DateTimeOffset?> throws on null Right - use Either<L, Option<T>>
- v1 limitation (documented): items enqueued after the transcode covering their air time started play
  late (next boundary after due, TTL-bounded). Enqueue a few minutes early. Timer-based late force-cut
  and duck style deferred to session 9.

Author/book/podcast list views (dakota's option c + breakout):
- Distinguished by AudiobookshelfShow.ItemId prefix (author:/podcast:); book seasons joined via
  AudiobookshelfSeasons x AudiobookshelfShows on ShowId
- GetAudiobookAuthors / GetAudiobookBooks(Option<int> ShowId) / GetPodcasts - EF constructor
  projections with counts + library name
- Pages: media/authors (Author|Books|Library; name -> books?author={showId}, count -> show tiles),
  media/books (Book|Author|Chapters|Library; book -> season detail, ?author= filter chip),
  media/podcasts (Podcast|Episodes|Library -> show detail)
- Nav: "TV Shows" entry REPLACED by Authors/Books/Podcasts (tiles page still routable at
  media/tv/shows via search links). resx en+pl.
- MediaCard titles: 2-line clamp (-webkit-line-clamp) instead of single-line nowrap ellipsis

NEXT SESSION (session 9): DUCKING + ANNOUNCER
- style: duck - force-cut then resume scheduled item WITH overlay as second input, sidechain-ducked,
  single process (cut-point recovery + seek-resume already proven by p0 test). filter_complex work:
  amix/sidechaincompress, duck level + fades, apad/duration interaction
- tier-1 announcer: per-channel toggle, TTS template ("now playing {title} by {author}") rendered at
  item boundaries via configurable TTS endpoint (wyoming/piper http or command), enqueued as boundary
  interrupt
- scheduled live-test findings (truncation accuracy) from dakota
- carried: audio-only error process (anullsrc), icecast/direct MP3 endpoint (x3 now)


### Session 9 (2026-07-06): duck-style interrupts + tts announcer (branch feature/duck-and-announcer)
COMPILE-VERIFIED (app + scanner, 0 errors). Session 8 scheduled interrupts validated live by dakota
first (truncation landed the injection at exactly airAt; work-ahead chunking composed correctly with
truncation via incomplete-item state).

Duck style (style=duck, duckPercent, both endpoints):
- KEY DESIGN: duck rides the SCHEDULED transcode path, not the interrupt path. Worker stashes the
  dequeued duck item as _pendingDuck; the next scheduled transcode carries MaybeDuckOverlay on
  GetPlayoutItemProcessByChannelNumber. Path replacement/fallback/seek/playout-offset all come free.
- Handler clamps finish to overlay duration (isComplete=false -> SeekAndRealtime -> seamless resume:
  the mixed bed audio is byte-identical to what resume-by-seek produces)
- Duck transcodes force effectiveRealtime=true (the 44s work-ahead chunk cap would cut overlays)
- ForAudioOnlyPlayoutItem grew Option<DuckOverlay>: second -i input + filter_complex
  [0:a]volume={bed}[bed];[bed][1:a]amix=duration=first:normalize=0[mix];[mix]apad[aout], -map [aout]
- Copy audio profile: overlay skipped with warning (needs transcode)
- Overlay longer than item remainder: cut at item boundary
- Temp file lifecycle: consumed duck tracked in _duckToCleanUp, deleted at next loop top + Run finally
- Slug/offline state: pending duck dropped with warning

Announcer (tier 1, api-configured, NEEDS LIVE TEST - tts glue untestable in sandbox):
- config elements (no migration): announcer.tts.url (global; POST text -> audio bytes, e.g. piper
  http server) + per-channel announcer.{num}.{enabled,template,style,duck_percent}
- ChannelAnnouncerService (scoped per session worker): called at loop top with _transcodedUntil;
  fires only when a FillerKind.None item starts within 5s of that stream time; dedups per media item
  (dedup set even on tts failure to avoid hammering); 30s config cache
- template vars: {title} {artist} {album} {show}/{author} {season}/{book}; default "Now playing: {title}"
- enqueues priority-1 duck (default) interrupt, ttl 60s from item start, DeleteFileWhenDone
- AnnouncerController: GET/PUT /api/channels/{num}/announcer, GET/PUT /api/announcer/tts

NEXT SESSION (session 10) candidates:
- LIVE TEST duck (boundary + emergency + scheduled variants) and announcer end-to-end with a piper
  http endpoint; expect filter_complex tuning (amix normalize behavior, bed fade in/out polish)
- late-scheduled force-cut refinement (enqueue-after-transcode-started case)
- audio-only error process (anullsrc) - carried x4
- icecast/direct MP3 endpoint - carried x4
- ui: interrupt queue + announcer config page; books tiles/list toggle


### Session 10 (2026-07-07): wyoming tts + endpoints registry + playlist/collection tags (branch feature/scheduled-interrupts-and-list-views cont.)
COMPILE-VERIFIED (app + scanner, 0 errors). Session 9 duck + announcer fully validated live first
(incl. hotfix 0011: NextState assumed realtime transcodes always complete - incomplete realtime
transcodes from duck/truncation restarted the item; now seek-resume).

Wyoming tts (dakota runs 2x slackr31337/wyoming-piper-gpu on opal.lan:10200/10201):
- WyomingTtsClient (Core/Tts): raw tcp, synthesize event with inline data, tolerates legacy
  data_length framing, collects audio-chunk pcm -> RIFF wav. no deps, 30s timeout, 64mb guard.
- named endpoints registry (announcer.tts.endpoints config element, json): PUT/GET/DELETE
  /api/announcer/tts/endpoints[/{name}]. urls: http(s):// (POST text -> audio) or wyoming://host:port.
- per-channel: ttsEndpoint (name) + voice (overrides endpoint default). resolution: named endpoint ->
  first registered -> legacy announcer.tts.url. UNTESTED live: wyoming protocol against real piper.
- overlay loudnorm (I=-16 TP=-1.5 LRA=11 on [1:a]) - quiet tts/overlays survive loud program material
  (dakota's serenity-vs-metal finding)

sessionActive: enqueue responses now report whether the channel has an active hls session + warning
when not (the wrong-channel footgun from duck testing)

Playlist/collection tags (tag:"name" in searches + smart collections - the schedulability feature):
- navidrome: native /api/playlist + /api/playlist/{id}/tracks (mediaFileId) fetched once per library
  scan -> song Tags; etag includes sorted playlist names so membership changes resync
- abs: /api/libraries/{id}/{collections,series} (AbsBookGroup models - CA1711 forbids *Collection
  names) -> tags on book SEASONS and chapter EPISODES; 5-min cache shares one fetch across
  season+episode scans; etags include tag fingerprint
- AudiobookshelfTelevisionRepository.UpdateSeason now syncs Tags (show/episode already did);
  NavidromeSongRepository.UpdateSong tag sync existed
- NOTE: tag changes resync on next scan via etag; no version bump this time (fingerprint change
  bumps affected items only)

GOTCHA (recurring): python edit scripts write-at-end - a failed assert mid-script silently rolls
back earlier successful replaces in the same file. Verify signatures after mixed edit rounds.

NEXT SESSION (session 11) candidates:
- LIVE TEST: wyoming tts against real piper (protocol framing is the risk), per-channel voices,
  playlist/collection tags after rescan (verify tag:"name" queries + smart collections)
- late-scheduled force-cut; audio-only error process (anullsrc, x5); icecast endpoint (x5)
- ui: announcer + tts endpoints config page, interrupt queue page, books tiles/list toggle


### Session 11 (2026-07-07): ui + cleanup (branch feature/ui-cleanup)
COMPILE-VERIFIED (app + scanner, 0 errors). Session 10 fully validated live first: wyoming tts
against real piper WORKED FIRST TRY (announcer end-to-end with voice), navidrome playlist tags
(61 playlists/6513 songs), abs collection/series tags (after hotfix 0013: season GetOrAdd missing
.ThenInclude(sm => sm.Tags) -> null Tags crash in the new sync), {artist} template confirmed.

UI:
- /settings/announcer (nav: Settings > Announcer): tts endpoints table (add/delete, wyoming:// or
  http(s):// validation) + per-channel announcer form (audio-only channel select; enabled/template/
  style/duckPercent/ttsEndpoint select/voice override). Direct IConfigElementRepository access,
  deliberately duplicating the small controller read/write logic for leanness.
- /system/interrupts (nav: Support > Interrupts): audio-only channel selector, sessionActive chip,
  queue table (title/priority/style/duration/airAt/expires) with per-row remove + clear all, 5s
  auto-refresh (System.Timers + InvokeAsync).
- books tiles/list toggle: ?view=tiles query param (linkable, author filter preserved);
  AudiobookBookViewModel gained Poster (season poster artwork projected in the EF query, abs://
  rewritten to relative proxy url via AudiobookshelfUrl.RelativeProxyForArtwork + width=440 in the
  handler - same transform the card mapper uses); tiles render shared MediaCard components,
  Href=media/tv/seasons/{id}.

Docs:
- README-ERSATZRADIO.md: fork overview, feature list, quickstart, api cheatsheet, limits
- docs/UPSTREAM-SYNC.md: new-vs-modified file inventory; the four conflict-zone files
  (HlsSessionWorker, GetPlayoutItemProcessByChannelNumberHandler, FFmpegLibraryProcessService,
  GetPlayoutItemProcessByChannelNumber) with the NextState incomplete-realtime INVARIANT called out;
  merge procedure + smoke tests + etag bump guidance.

PENDING dakota validations: per-channel voice on an italian channel (it_IT via piper-mkii or voice
override); duckPercent taste tuning (10-15 suggested for loud program material).

NEXT SESSION (session 12) candidates:
- live-test the three ui pages + books tiles
- late-scheduled force-cut refinement; audio-only error process (anullsrc, x5)
- icecast/direct MP3 endpoint (PARKED per dakota: flexibility, not priority)
- possible: announcer preview button (synthesize test phrase), interrupt upload from the ui page


### Session 12 (2026-07-07): tts interrupts + broadcast (branch feature/tts-interrupts-broadcast)
COMPILE-VERIFIED (app + scanner, 0 errors). Session 11 ui pages confirmed working by dakota first.
Driven by dakota's HA question: "how do I push from HA, and can I hit all active channels at once?"

TtsSynthesisService (ITtsSynthesisService, Core/Interfaces/Tts):
- extracted resolve-endpoint + wyoming/http synthesis + write-wav from ChannelAnnouncerService
  (announcer now delegates; behavior identical). registered scoped; controller injects it too.

POST /api/channels/{n}/interrupts/tts:
- { text, ttsEndpoint?, voice?, priority?, ttlSeconds?, title?, airAt?, style?, duckPercent? }
- synthesizes via the registry, probes, enqueues with DeleteFileWhenDone
- STYLE DEFAULTS TO DUCK for tts (spoken over the schedule) - differs from file endpoints (replace)

Broadcast:
- POST /api/interrupts/tts + POST /api/interrupts/path with channels: ["1","2"] | "active"
  ("active" = audio-only channels with live sessions via IFFmpegSegmenterService)
- tts synthesized ONCE; one file COPY per channel (DeleteFileWhenDone per-item would race on a
  shared file); path broadcasts share the file and ignore deleteWhenDone
- per-channel results array {channelNumber, ok, item|error}; invalid channels don't fail the batch
- channels field bound as JsonElement (string "active" vs array polymorphism)

docs/INTERRUPTS.md: tts + broadcast sections incl. HA rest_command for whole-house announcements
(rest_command.eradio_say -> ducked spoken announcement on every listening channel).

GOTCHA: controller ProbeDuration returns Either<IActionResult, TimeSpan> (not BaseError).

NEXT SESSION (session 13) candidates:
- LIVE TEST: single-channel tts interrupt, broadcast to "active", HA rest_command end-to-end
- late-scheduled force-cut; anullsrc audio-only error process (x6)
- icecast endpoint (parked)
- possible: announcer preview/test button on the settings page (now trivial via the tts endpoint)


### Session 13 (2026-07-07): closing hardening (branch feature/closing-hardening)
COMPILE-VERIFIED (app + scanner, 0 errors). Session 12 broadcast + HA rest_command validated live
first (after hotfixes 0016 route-combining + 0017 newtonsoft binding - PATTERN: check Startup's web
stack config (class [Route] attrs, AddNewtonsoftJson) before extending controllers).

Announcer preview (dakota-requested):
- GET /api/announcer/tts/preview?text=&endpoint=&voice= -> synthesizes and returns audio/wav
  (temp file deleted after read)
- settings page: Preview Voice button renders the channel's template with sample metadata and
  plays it in an inline <audio> element (cache-busted url)

Audio-only error/offline silence (anullsrc, carried x6 - DONE):
- ForError: audio-only + hls segmenter channels emit silence (anullsrc lavfi, profile-matched
  codec/bitrate/samplerate/channels, copy->aac fallback since lavfi can't stream-copy) instead of
  the video error card; error logged as warning
- Slug (offline card): same treatment (python replace-all GOTCHA: the ForError insert anchor also
  matched Slug - kept intentionally, adapted for its signature)
- unknown-duration errors play silence in 30s chunks so the loop keeps checking for recovery and
  pending interrupts

Scheduled force-cut for late-enqueued emergencies:
- Enqueue(priority 0 + future airAt) schedules a Timer at airAt: if the item is STILL QUEUED then
  (truncation missed it - enqueued after the covering transcode started), the force handler cuts;
  item airs within the hls buffer of the target. Early-enqueued items dequeue via truncation before
  the timer fires -> no-op.
- timers cancelled on dequeue/expiry/remove/clear (CancelAirAtTimer under _sync)
- semantics: priority 0 + airAt = "cut if necessary"; priority 1 + airAt = truncation-or-boundary only

docs: INTERRUPTS.md scheduled-emergency section; README tts/broadcast cheatsheet + silence note.

REMAINING (documented, deliberate): icecast/direct MP3 endpoint (parked - flexibility not priority).
LIVE TESTS PENDING: preview button, silence on a scheduling gap, late-enqueued scheduled p0.

The project is feature-complete for the radio appliance goal: audio-only channels, navidrome+abs
sources with playlist/collection/series tags, full interrupt matrix (replace/duck x boundary/
emergency/scheduled + late force-cut), tts announcer with wyoming piper + per-channel voices,
whole-house HA broadcast, management ui, fork docs + upstream-sync guidance.
