# Upstream sync notes

ErsatzTV moves fast. This file lists what this fork touches so upstream merges
are deliberate instead of archaeological. Rule of thumb: **new files merge
clean; the danger is the four modified hot-path files below.**

## New files (merge-safe, ours entirely)

- `ErsatzTV.Core/Interrupts/*` -- queue item, service, styles, DuckOverlay
- `ErsatzTV.Core/Tts/*` -- WyomingTtsClient, TtsEndpoint
- `ErsatzTV.Core/Audiobookshelf/*`, `ErsatzTV.Core/Navidrome/*` -- url helpers, secrets
- `ErsatzTV.Infrastructure/Audiobookshelf/*`, `ErsatzTV.Infrastructure/Navidrome/*`
- `ErsatzTV.Infrastructure/Data/Repositories/{Audiobookshelf,Navidrome}*Repository.cs`
- `ErsatzTV.Application/Audiobookshelf/*`, `ErsatzTV.Application/Songs/*` (browse queries)
- `ErsatzTV.Application/Streaming/ChannelAnnouncerService.cs`
- `ErsatzTV.Scanner/Core/{Audiobookshelf,Navidrome}/*`
- `ErsatzTV/Controllers/{Interrupts,Announcer,Artwork(abs/navidrome routes)}Controller.cs`
- Pages: AuthorList, BookList, PodcastList, SongArtistList, AlbumList,
  InterruptQueue, Settings/AnnouncerSettings

## Modified upstream files (CONFLICT ZONE)

### 1. `ErsatzTV.Application/Streaming/HlsSessionWorker.cs` (heaviest)
- Interrupt dequeue at the top of the session loop (`TryDequeue` with
  `_transcodedUntil` as stream time)
- `_pendingDuck` stash: duck-style items ride the NEXT scheduled transcode as
  `MaybeDuckOverlay`; `_duckToCleanUp` file lifecycle (loop top + Run finally)
- `TruncateAt = PeekNextAirTime(...)` on the playout item request
- Announcer hook (`AnnounceUpcomingItem`) before dequeue
- `TranscodeInterrupt` method + per-transcode `_interruptCts` force-cut +
  pts-offset recovery (`GetPtsOffset`)
- **INVARIANT (do not lose in merge): `NextState` must map
  `Seek/ZeroAndRealtime + !isComplete => SeekAndRealtime`.** Upstream assumes
  realtime transcodes always complete; duck overlays and airAt truncation
  produce incomplete realtime transcodes. Losing this reverts to "item restarts
  after every duck/truncation."

### 2. `.../Queries/GetPlayoutItemProcessByChannelNumberHandler.cs`
- Audio-only branch (SongVideoMode.AudioOnly bypasses the video pipeline)
- Duck clamp: finish bounded to overlay duration; `isComplete=false`
- TruncateAt clamp (playout-offset adjusted, audio-only only)
- Duck transcodes force `effectiveRealtime = true` (the 44s work-ahead chunk
  cap must never cut an overlay)

### 3. `ErsatzTV.Core/FFmpeg/FFmpegLibraryProcessService.cs`
- `ForAudioOnlyPlayoutItem` (manual arg assembly) + `Option<DuckOverlay>` param:
  second input + filter_complex
  `[0:a]volume=bed[bed];[1:a]loudnorm[ov];[bed][ov]amix=duration=first:normalize=0[mix];[mix]apad[aout]`
- Copy-codec profiles skip the overlay with a warning

### 4. `ErsatzTV.Application/Streaming/Queries/GetPlayoutItemProcessByChannelNumber.cs`
- Non-positional init props `TruncateAt` + `MaybeDuckOverlay` (added as init
  props specifically so upstream constructor-signature changes don't conflict)

## Lighter touches

- `MediaCards/Mapper.cs` -- abs://, navidrome:// artwork branches; season-title
  preference; library-name card subtitles (null-safe: only when handlers include
  `LibraryPath.Library`)
- `Search/Queries/QuerySearchIndex{Shows,Songs}Handler.cs` -- library includes
- `TelevisionShowList/SongList.razor` -- library filter dropdowns (query-builder)
- `MainLayout.razor` + resx -- nav entries
- `ConfigElementKey.cs` -- announcer.* keys
- `Startup.cs` -- DI registrations (interrupt service, announcer, secret stores)
- `site.css` -- 2-line media-card title clamp
- `TvContext.cs` + migrations -- Audiobookshelf/Navidrome tables

## Merge procedure

1. Merge upstream; expect conflicts only in the four files above
2. In `HlsSessionWorker`, re-verify the `NextState` invariant FIRST
3. Rebuild; run the smoke tests: boundary interrupt, emergency + resume position,
   airAt exact-time, duck + seamless resume, announcer end-to-end
4. Scanner etags: if upstream changes metadata shapes, bump `EtagVersion` in both
   ABS and Navidrome clients to force a one-time refresh
