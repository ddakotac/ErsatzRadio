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

# tts endpoints + announcer
curl -X PUT http://host:8409/api/announcer/tts/endpoints \
  -H "Content-Type: application/json" \
  -d '{"name": "piper-main", "url": "wyoming://opal.lan:10200"}'
curl -X PUT http://host:8409/api/channels/1/announcer \
  -H "Content-Type: application/json" \
  -d '{"enabled": true, "template": "Now playing: {title} by {artist}",
       "ttsEndpoint": "piper-main"}'
```

## Notes and limits

- Interrupt latency is bounded below by the HLS buffer (~40-60s), even for
  priority 0 -- that's the protocol, not a bug
- Scheduled (`airAt`) items must be enqueued before the transcode covering their
  air time starts; rule of thumb, a few minutes early
- Duck requires a transcoding audio profile; `Copy` skips the overlay
- Enqueue responses include `sessionActive` -- if false, nobody is listening and
  the item will expire unless a session starts
- Recommended: configure fallback filler on audio channels so scheduling gaps
  play audio rather than the video error card

## Development

Session-by-session build log: [docs/ERSATZRADIO-PLAN.md](docs/ERSATZRADIO-PLAN.md).
Files changed vs upstream and merge guidance: [docs/UPSTREAM-SYNC.md](docs/UPSTREAM-SYNC.md).
