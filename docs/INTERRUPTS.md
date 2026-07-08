# Interrupt Queue & Injection API

ErsatzRadio supports injecting near-live audio into audio-only channels — top-of-hour
chimes, DJ-style announcements, and emergency notifications — through a TTL-aware
priority queue. This is the "breakaway" pattern from broadcast radio automation:
injected audio *covers* scheduled content, and when it finishes, the schedule resumes
mid-item exactly where it would have been.

Only channels with **Song Video Mode = Audio Only** accept interrupts.

## Priorities

| Priority | Behavior |
|----------|----------|
| `0` | **Emergency.** Cuts the currently transcoding item immediately and plays as soon as possible. |
| `1+` | Waits for the next item boundary (end of the current song/track/chapter). FIFO within a priority. |

On audiobook channels the boundary is the end of the current **chapter** (each
chapter file is its own playout item), except single-file books which are one
item end to end.

## Scheduled items (airAt)

Add `airAt` (ISO 8601) to either endpoint to schedule an item. Because the HLS
timeline tracks wall time, `airAt` is the on-air time. The session worker
**truncates the preceding scheduled item** at that instant, so the interrupt
starts exactly on time -- provided it is enqueued before the transcode covering
the air time begins. Rule of thumb: **enqueue at least a few minutes early**
(one full item length + the ~60s buffer). Items enqueued too late play at the
next boundary after their air time, bounded by TTL.

A **scheduled emergency** (`priority=0` + `airAt`) adds a safety net for items
enqueued too late for truncation: if the item is still queued at its air time,
the current transcode is force-cut, and the item airs within the HLS buffer
(~40-60s) of the target. Enqueued early, truncation still lands it exactly and
the force-cut never fires. Use `priority=1` scheduled items when cutting is
unacceptable.

TTL for scheduled items counts from `airAt`, not from enqueue -- so
`airAt=09:00:00, ttlSeconds=120` means "play at 9:00, drop if it can't start by
9:02".

```bash
# top-of-hour chime, enqueued by HA cron at :55
curl -X POST http://ohs:8409/api/channels/70/interrupts/path \
  -H "Content-Type: application/json" \
  -d '{"path": "/media/shared/chimes/hour.mp3",
       "airAt": "2026-07-06T09:00:00",
       "ttlSeconds": 120,
       "title": "Top of the hour"}'
```

Timestamps without an offset are interpreted in the server's local time zone.

## Styles (replace vs duck)

Every interrupt has a `style`:

| Style | Behavior |
|-------|----------|
| `replace` (default) | Interrupt audio replaces scheduled content for its duration. |
| `duck` | Interrupt audio is mixed OVER scheduled content, which continues underneath at `duckPercent` volume (default 30). Chimes, idents, announcements. |

Duck works with every timing mode (boundary, emergency, scheduled). The scheduled
content is never paused -- the bed audio in the mix is exactly what resume-by-seek
would play, so the transition out of the duck is seamless. Requirements and edges:

- Requires a transcoding audio profile (aac/ac3). With a `Copy` profile the overlay
  is skipped with a warning.
- If less of the current item remains than the overlay's duration, the overlay is
  cut at the item boundary.
- An emergency (`priority=0`) duck cuts the current transcode and resumes it
  immediately WITH the overlay mixed in -- the listener hears the item continue,
  ducked, under the announcement.

```bash
curl -X POST http://ohs:8409/api/channels/70/interrupts \
  -F "file=@chime.wav" -F "style=duck" -F "duckPercent=25" \
  -F "priority=0" -F "ttlSeconds=120" -F "title=Doorbell"
```

## Announcer (auto "now playing" TTS)

Per-channel DJ-style announcements: when a new scheduled item starts, its metadata is
rendered through a template, synthesized via a TTS endpoint, and ducked over the
item's opening (style configurable).

```bash
# register named tts endpoints (once). wyoming://host:port speaks natively to
# wyoming-piper - no bridge or second piper needed; voice picks the piper voice.
# http(s):// endpoints use POST plain text -> audio bytes.
curl -X PUT http://ohs:8409/api/announcer/tts/endpoints \
  -H "Content-Type: application/json" \
  -d '{"name": "piper-main", "url": "wyoming://opal.lan:10200"}'
curl -X PUT http://ohs:8409/api/announcer/tts/endpoints \
  -H "Content-Type: application/json" \
  -d '{"name": "piper-mkii", "url": "wyoming://opal.lan:10201"}'

curl http://ohs:8409/api/announcer/tts/endpoints           # list
curl -X DELETE http://ohs:8409/api/announcer/tts/endpoints/piper-mkii

# enable per channel; ttsEndpoint selects by name (default: first registered,
# falling back to the legacy /api/announcer/tts single url). voice overrides the
# endpoint's default voice - e.g. an italian voice for an italian channel.
curl -X PUT http://ohs:8409/api/channels/70/announcer \
  -H "Content-Type: application/json" \
  -d '{"enabled": true, "template": "Now playing: {title} by {artist}",
       "style": "duck", "duckPercent": 25,
       "ttsEndpoint": "piper-main", "voice": "en_US-lessac-medium"}'

# check config
curl http://ohs:8409/api/channels/70/announcer
```

Announcer overlays are loudness-normalized (`loudnorm` on the overlay input) so
quiet TTS output stays audible over loud program material - this applies to all
duck-style interrupts, not just the announcer.

Template variables: `{title}`, `{artist}`, `{album}` (songs); `{title}`, `{show}`/
`{author}`, `{season}`/`{book}` (episodes/chapters). Audiobook example:
`"{title}, from {book}, by {author}"`.

Behavior notes: announcements only fire at (within 5s of) an item's scheduled start,
never for filler, and each item is announced once per session. TTS failures skip the
announcement and never disturb the stream. The TTS endpoint contract is: HTTP POST,
plain-text body, audio bytes response.

## TTS interrupts (speak text directly)

Skip the file entirely: POST text and ErsatzRadio synthesizes it through the TTS
endpoint registry and enqueues the result. Style defaults to **duck** here
(spoken over the schedule); pass `"style": "replace"` to insert instead.

```bash
curl -X POST http://ohs:8409/api/channels/70/interrupts/tts \
  -H "Content-Type: application/json" \
  -d '{"text": "Dinner is ready", "priority": 1, "ttlSeconds": 300}'
```

Optional fields: `ttsEndpoint` (registry name; default first registered),
`voice`, `title`, `airAt`, `style`, `duckPercent`, `priority`, `ttlSeconds`.

## Broadcast (multiple channels at once)

`POST /api/interrupts/tts` and `POST /api/interrupts/path` take a `channels`
field: an array of channel numbers, or the string `"active"` for every
audio-only channel with a live session. TTS is synthesized once (one file copy
per channel); path broadcasts share the file (`deleteWhenDone` is ignored).
The response is a per-channel `results` array with `ok` + item or error.

```bash
# whole-house announcement over everything currently playing
curl -X POST http://ohs:8409/api/interrupts/tts \
  -H "Content-Type: application/json" \
  -d '{"text": "Dinner is ready", "channels": "active", "priority": 0}'

# doorbell chime on two specific channels
curl -X POST http://ohs:8409/api/interrupts/path \
  -H "Content-Type: application/json" \
  -d '{"path": "/media/shared/chimes/doorbell.wav", "channels": ["1", "2"],
       "priority": 0, "style": "duck", "duckPercent": 20, "ttlSeconds": 120}'
```

### Home Assistant rest_command

```yaml
rest_command:
  eradio_say:
    url: "http://ohs:8409/api/interrupts/tts"
    method: POST
    content_type: "application/json"
    payload: >-
      {"text": {{ text | tojson }},
       "channels": {{ channels | default('active') | tojson }},
       "priority": {{ priority | default(0) }},
       "style": "{{ style | default('duck') }}",
       "duckPercent": {{ duck_percent | default(25) }},
       "ttlSeconds": {{ ttl | default(120) }}}
```

Then `action: rest_command.eradio_say` with `data: {text: "Someone is at the front door"}`
is a whole-house spoken announcement, ducked over whatever each room is playing.

## TTL

Every item carries a TTL (default 300 s). An item that has not **started** playing by
`enqueuedAt + ttlSeconds` is silently dropped — a top-of-hour chime is useless at
ten past. Set short TTLs for time-sensitive content.

## Latency (read this)

HLS buffering is real. The segmenter works 30–60 s ahead of the listener, and players
buffer several segments on top of that. Interrupt audio is appended after
already-written segments, so even a priority 0 emergency airs after the current
buffer drains — expect up to roughly a minute in the worst case. This is inherent to
HLS; the queue minimizes *transcode-side* delay, not player-side buffer.

## API

### Upload a file

```bash
curl -X POST http://ohs:8409/api/channels/70/interrupts \
  -F "file=@/tmp/top-of-hour.mp3" \
  -F "priority=1" \
  -F "ttlSeconds=120" \
  -F "title=Top of the hour"
```

### Enqueue a server-local file

For automation systems that share a volume with ErsatzRadio (e.g. Home Assistant
writing Wyoming TTS output to a shared mount):

```bash
curl -X POST http://ohs:8409/api/channels/70/interrupts/path \
  -H "Content-Type: application/json" \
  -d '{
    "path": "/media/shared/tts/announcement.wav",
    "priority": 0,
    "ttlSeconds": 60,
    "title": "Emergency notification",
    "deleteWhenDone": true
  }'
```

### Inspect / manage the queue

```bash
curl http://ohs:8409/api/channels/70/interrupts              # list pending
curl -X DELETE http://ohs:8409/api/channels/70/interrupts/{id}  # remove one
curl -X DELETE http://ohs:8409/api/channels/70/interrupts       # clear all
```

Responses include `id`, `durationSeconds`, `expiresAt`, and `sessionActive` -- when
`sessionActive` is `false`, nobody is listening to that channel and the item will
expire unless a session starts before its TTL (a `warning` field says so).

## Home Assistant example

TTS pipeline → shared volume → injection:

```yaml
script:
  radio_announce:
    fields:
      message:
        description: Text to announce
      priority:
        description: 0 = emergency (cut now), 1 = next song boundary
        default: 1
    sequence:
      - service: tts.speak
        target:
          entity_id: tts.piper
        data:
          media_player_entity_id: media_player.file_out
          message: "{{ message }}"
          # configure your tts platform to write to /media/shared/tts/announce.wav
      - service: rest_command.ersatzradio_interrupt
        data:
          priority: "{{ priority }}"

rest_command:
  ersatzradio_interrupt:
    url: "http://ohs:8409/api/channels/70/interrupts/path"
    method: POST
    content_type: application/json
    payload: >
      {"path": "/media/shared/tts/announce.wav",
       "priority": {{ priority }},
       "ttlSeconds": 120,
       "title": "HA announcement",
       "deleteWhenDone": false}
```

A top-of-hour chime is just a time trigger calling the same rest_command with a
pre-rendered file and `ttlSeconds: 120`.

## Behavior notes

- If nobody is listening (no active HLS session), items sit in the queue and expire
  by TTL. Interrupts do not start sessions.
- Uploaded files are stored under the app data `interrupts/` folder and deleted after
  playing, expiring, or being removed. `path`-based items are left alone unless
  `deleteWhenDone: true`.
- Interrupt playback failures (bad file, ffmpeg error) drop the item and keep the
  channel running — no error card interrupts the stream.
- An emergency cannot cut another interrupt that is already playing, only scheduled
  content. Interrupts are expected to be short.
- With `FFmpegProfileAudioFormat.Copy` the injected file is not transcoded or padded;
  use a transcoding profile (aac/ac3) for reliable injection of arbitrary files.
