# ErsatzRadio

A fork of [ErsatzTV](https://github.com/ErsatzTV/ErsatzTV) turned into an
**audio-only internet radio platform**: schedule music, audiobooks, and podcasts
into always-on channels, with live interrupts, scheduled chimes, ducked audio
overlays, and an automatic TTS "now playing" announcer.

Everything ErsatzTV knows about scheduling (collections, smart collections,
playouts, filler) drives radio channels instead of TV channels.

## What this fork adds

- **Audio-only channels** -- `Song Video Mode: Audio Only` on a channel produces a
  pure HLS audio stream (no video pipeline, no dummy video track)
- **Navidrome media source** -- native API scan of music libraries; playlists become
  `tag:"playlist name"` on member songs (searchable + smart-collection-able)
- **Audiobookshelf media source** -- authors/books/chapters and podcasts mapped to
  shows/seasons/episodes; collections and series become tags; chapter titles from
  ABS chapters
- **Interrupt queue** -- inject audio into a live channel via API:
  - `priority 0` cuts the current item immediately (emergency)
  - `priority 1+` waits for the next item/chapter boundary
  - `airAt` schedules to the second (top-of-hour chimes) -- the preceding item is
    truncated so the boundary lands exactly on air time
  - `style=duck` mixes the audio OVER the schedule at reduced bed volume
- **Announcer** -- per-channel automatic TTS announcements ("Now playing: X by Y")
  ducked over each item's opening; template-driven; per-channel voice
- **TTS endpoints** -- named registry; speaks **Wyoming protocol natively**
  (wyoming-piper, no bridge) or generic HTTP POST-text-audio endpoints
- **Watch folders + RSS feeds** -- newly arrived audio files (e.g. fresh podcast
  episodes downloaded by Audiobookshelf) or newly published feed episodes
  automatically play on mapped channels at the next boundary; polling-based,
  NFS/SMB-safe, configurable in the UI
- **Radio-oriented UI** -- Authors/Books/Podcasts and Song Artists/Albums browse
  pages, library filters and card subtitles, remote artwork proxying for ABS and
  Navidrome, announcer + interrupt queue management pages

## Quickstart

```bash
git clone https://github.com/ddakotac/ErsatzRadio && cd ErsatzRadio/docker
docker compose build && docker compose up -d
```

1. Add media sources (Media Sources > Navidrome / Audiobookshelf), let them scan
2. Build collections or smart collections (playlist tags work: `tag:"Road Trip"`)
3. Create a channel: FFmpeg profile with a transcoding audio format (aac; NOT
   copy -- interrupts/announcer need a transcode), Song Video Mode = Audio Only
4. Schedule it like any ErsatzTV channel
5. Listen: `http://host:8409/iptv/channel/{n}.m3u8`
6. Optional: Settings > Announcer to register piper endpoints + enable per channel

## API cheatsheet

Full docs: [docs/INTERRUPTS.md](docs/INTERRUPTS.md)

```bash
# boundary interrupt (plays after the current song/chapter)
curl -X POST http://host:8409/api/channels/1/interrupts \
  -F "file=@promo.mp3" -F "priority=1" -F "ttlSeconds=900" -F "title=Promo"

# emergency (cuts current item; ~40-60s hls buffer latency)
curl -X POST http://host:8409/api/channels/1/interrupts \
  -F "file=@alert.mp3" -F "priority=0" -F "ttlSeconds=300" -F "title=Alert"

# scheduled chime, ducked over the schedule
curl -X POST http://host:8409/api/channels/1/interrupts \
  -F "file=@chime.mp3" -F "airAt=2026-07-08T09:00:00" -F "ttlSeconds=120" \
  -F "style=duck" -F "duckPercent=15" -F "title=Top of the hour"

# from a path the container can see (for HA automations)
curl -X POST http://host:8409/api/channels/1/interrupts/path \
  -H "Content-Type: application/json" \
  -d '{"path": "/media/shared/announce.wav", "priority": 1, "ttlSeconds": 300}'

# queue management
curl http://host:8409/api/channels/1/interrupts
curl -X DELETE http://host:8409/api/channels/1/interrupts/{id}

# speak text directly (synthesized via the endpoint registry; ducks by default)
curl -X POST http://host:8409/api/channels/1/interrupts/tts \
  -H "Content-Type: application/json" -d '{"text": "Dinner is ready"}'

# broadcast to every listening channel
curl -X POST http://host:8409/api/interrupts/tts \
  -H "Content-Type: application/json" \
  -d '{"text": "Dinner is ready", "channels": "active", "priority": 0}'

# tts endpoints + announcer
curl -X PUT http://host:8409/api/announcer/tts/endpoints \
  -H "Content-Type: application/json" \
  -d '{"name": "piper-main", "url": "wyoming://opal.lan:10200"}'
curl -X PUT http://host:8409/api/channels/1/announcer \
  -H "Content-Type: application/json" \
  -d '{"enabled": true, "template": "Now playing: {title} by {artist}",
       "ttsEndpoint": "piper-main"}'
```

## Endpoints: which one to use

| endpoint | cost per listener | now-playing | timing | audio |
|---|---|---|---|---|
| `/radio/{n}` | follows the channel's configured format | ICY titles + art | | |
| `/radio/{n}.aac` | ~1% of a core (stream copy) | ICY titles + art | passthrough | original, untouched |
| `/radio/{n}.mp3` | ~10% of a core (transcode) | ICY titles + art | optional gap filling | re-encoded from the source |
| `/iptv/channel/{n}.ts` | ~1% of a core (stream copy) | static service name only | passthrough | original, untouched |

**AAC is the default** and is the right choice for most channels: it copies the
audio already inside the channel's segments, so it costs a fraction of the CPU
*and* avoids a second generation of lossy encoding.

Choose **MP3** for a channel when a client can't handle AAC, or when you want
`aresample` gap filling as insurance on a channel that has proven troublesome.
MP3 quality is `-compression_level` (0 slowest/best, 9 fastest; 7 is a good
default at 192k).

Use the **MPEG-TS** url for TVHeadend ingestion and other tuner-style consumers -
it carries no per-track metadata, so prefer a radio url for anything that can
display now-playing information.

Configure per channel at Settings > Radio. Audio-only channels advertise their
configured radio url in the channel playlist (`/iptv/channels.m3u`), so clients
do not have to guess.

## Direct radio streams (ICY / now-playing metadata)

Every channel is also available as a plain internet-radio stream with live
now-playing titles (shoutcast/ICY metadata):

```
http://host:8409/radio/{channelNumber}.mp3
```

Players that support ICY (Music Assistant, VLC, squeezelite, most radio apps)
show the current media as it plays - songs as "{artist} - {title}", audiobook
chapters as "{author} - {book}: {chapter}" - and when a delivery breaks in, the
title flips to the interrupt ("S2 Underground: The Wire - ..."), then back.
Cover art rides along as StreamUrl (the item's thumbnail, or the book cover)
for players that honor it. If your player's UI is https, the art url must be
https too (browser mixed-content blocking) - set the base explicitly:

```bash
curl -X PUT http://host:8409/api/radio/settings \
  -H "Content-Type: application/json" \
  -d '{"artworkBaseUrl": "https://eradio.example.com"}'
``` Titles come from the playout at wall-clock time;
the airing interrupt overrides. One transcode per listener (192k mp3); the underlying HLS session
starts on the first listener, so interrupts, announcer, and ducks all function.
Use this url in Music Assistant directly for rich metadata; keep the MPEG-TS
url for TVHeadend.

### Multi-room sync notes

The direct radio endpoint is tuned for synchronized multi-room groups (Music
Assistant + squeezelite and similar): fixed sample rate / channel layout / CBR
for the life of the stream, gaps at internal boundaries filled with silence
rather than passed through as jumps (`aresample=async`), an icecast-style
burst-on-connect so player buffers start full, and unbuffered chunked delivery.
Prefer this endpoint over the MPEG-TS URL for grouped players - TS/HLS
discontinuities are the usual cause of a group drifting apart at item
boundaries.

## Scheduling audiobooks and albums

`Shuffle In Order` treats **books** (seasons with real titles) and **albums** as
the schedulable unit: books/albums shuffle, chapters/tracks play in order within
each. Point a schedule item at a collection of books (or a playlist tag) with
Shuffle In Order and it just works - no per-book playlists needed. Untitled TV
seasons still group by show, exactly like upstream.

## Notes and limits

- Interrupt latency is bounded below by the HLS buffer (~40-60s), even for
  priority 0 -- that's the protocol, not a bug
- Scheduled (`airAt`) items must be enqueued before the transcode covering their
  air time starts; rule of thumb, a few minutes early
- Duck requires a transcoding audio profile; `Copy` skips the overlay
- Enqueue responses include `sessionActive` -- if false, nobody is listening and
  the item will expire unless a session starts
- Scheduling gaps and errors on audio-only channels play **silence**, never a
  video error card (fallback filler is still nicer for listeners; configure it)

## Development

Session-by-session build log: [docs/ERSATZRADIO-PLAN.md](docs/ERSATZRADIO-PLAN.md).
Files changed vs upstream and merge guidance: [docs/UPSTREAM-SYNC.md](docs/UPSTREAM-SYNC.md).
