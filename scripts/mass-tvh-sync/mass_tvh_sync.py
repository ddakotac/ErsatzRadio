#!/usr/bin/env python3
"""Sync TVHeadend radio channels into the Music Assistant library.

Reads the TVHeadend channel grid, filters to radio channels (by channel tag),
and upserts each one into Music Assistant as a radio item via the builtin
provider (the same mechanism as the UI's "add item from url").

Idempotent: channels already in the MA library (matched by name) are skipped
unless --force is given. Safe to run from cron / an HA automation.

Requires:  pip install music-assistant-client aiohttp

Usage:
  ./mass_tvh_sync.py                 # sync
  ./mass_tvh_sync.py --dry-run      # show what would happen
  ./mass_tvh_sync.py --introspect   # dump the MA client's music methods
                                     # (use if the library-add call fails
                                     #  after an MA update)
"""

import argparse
import asyncio
import sys

import aiohttp

# --------------------------------------------------------------------------
# CONFIG - edit these for your environment
# --------------------------------------------------------------------------

# tvheadend
TVH_URL = "http://calliope.lan:9981"
TVH_USER = "your-tvh-user"           # needs api access (web interface rights)
TVH_PASS = "your-tvh-pass"
TVH_RADIO_TAG = "Radio"              # channel tag that marks radio channels

# the user embedded in stream urls handed to MA; needs STREAMING rights in tvh.
# tip: create a dedicated low-privilege "streamer" user for this.
STREAM_USER = "your-stream-user"
STREAM_PASS = "your-stream-pass"
STREAM_PROFILE = ""                  # e.g. "audio-only" profile name, or "" for default

# music assistant
MASS_URL = "http://calliope.lan:8095"
MASS_TOKEN = "your-long-lived-mass-token"
# get a token: MA web ui > settings > profile > long lived tokens
# (or via the client: music_assistant_client.login_with_token(url, user, pass))

# prefix for item names in MA (visual grouping); "" for none
NAME_PREFIX = ""

# --------------------------------------------------------------------------


def stream_url(channel_uuid: str) -> str:
    """Build the tvh stream url MA will play."""
    auth = f"{STREAM_USER}:{STREAM_PASS}@" if STREAM_USER else ""
    base = TVH_URL.split("://", 1)
    url = f"{base[0]}://{auth}{base[1]}/stream/channel/{channel_uuid}"
    if STREAM_PROFILE:
        url += f"?profile={STREAM_PROFILE}"
    return url


async def fetch_tvh_radio_channels() -> list[dict]:
    """Return [{name, uuid, icon}] for channels carrying the radio tag."""
    auth = aiohttp.BasicAuth(TVH_USER, TVH_PASS)
    async with aiohttp.ClientSession(auth=auth) as session:
        # resolve the radio tag uuid
        async with session.get(
            f"{TVH_URL}/api/channeltag/grid", params={"limit": 500}
        ) as resp:
            resp.raise_for_status()
            tags = (await resp.json())["entries"]

        radio_tags = {
            t["uuid"]
            for t in tags
            if t.get("name", "").strip().lower() == TVH_RADIO_TAG.strip().lower()
        }
        if not radio_tags:
            print(
                f"warning: no tvh channel tag named {TVH_RADIO_TAG!r} found; "
                "check TVH_RADIO_TAG",
                file=sys.stderr,
            )
            return []

        async with session.get(
            f"{TVH_URL}/api/channel/grid", params={"limit": 1000}
        ) as resp:
            resp.raise_for_status()
            channels = (await resp.json())["entries"]

    result = []
    for ch in channels:
        if not ch.get("enabled", True):
            continue
        if not radio_tags.intersection(ch.get("tags", [])):
            continue

        icon = ch.get("icon_public_url") or ""
        if icon.startswith("imagecache"):
            icon = f"{TVH_URL}/{icon}"

        result.append(
            {
                "name": ch.get("name", ch["uuid"]),
                "uuid": ch["uuid"],
                "icon": icon,
                "number": ch.get("number", 0),
            }
        )

    result.sort(key=lambda c: (c["number"], c["name"].lower()))
    return result


async def get_library_radio_names(client) -> set[str]:
    """Names of radio items already in the MA library."""
    # method name has drifted across MA versions; try known spellings
    for attr in ("get_library_radios", "library_radios", "get_radios"):
        fn = getattr(client.music, attr, None)
        if fn is None:
            continue
        try:
            items = await fn(limit=500)
            return {item.name for item in items}
        except TypeError:
            items = await fn()
            return {item.name for item in items}
    print(
        "warning: could not list library radios (client api drift); "
        "duplicate detection disabled - run --introspect and report",
        file=sys.stderr,
    )
    return set()


async def add_radio(client, name: str, url: str) -> bool:
    """Add a radio-by-url to the MA library. Returns True on success."""
    # primary + fallbacks for api drift across MA versions
    attempts = [
        lambda: client.music.add_item_to_library(url),
        lambda: client.send_command("music/library/add_item", item=url),
        lambda: client.send_command("music/add_item_to_library", item=url),
    ]
    last_error = None
    for attempt in attempts:
        try:
            await attempt()
            return True
        except Exception as ex:  # noqa: BLE001 - surface the last error
            last_error = ex

    print(f"  FAILED to add {name}: {last_error}", file=sys.stderr)
    print(
        "  (MA client api may have changed - run with --introspect and check "
        "for the current add-to-library method)",
        file=sys.stderr,
    )
    return False


async def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dry-run", action="store_true")
    parser.add_argument("--force", action="store_true", help="re-add even if name exists")
    parser.add_argument("--introspect", action="store_true")
    args = parser.parse_args()

    try:
        from music_assistant_client import MusicAssistantClient
    except ImportError:
        print("pip install music-assistant-client aiohttp", file=sys.stderr)
        return 1

    channels = await fetch_tvh_radio_channels()
    print(f"tvheadend: {len(channels)} radio channel(s)")

    if args.dry_run:
        for ch in channels:
            print(f"  would sync: {NAME_PREFIX}{ch['name']} -> {stream_url(ch['uuid'])}")
        return 0

    async with aiohttp.ClientSession() as session:
        async with MusicAssistantClient(MASS_URL, session, token=MASS_TOKEN) as client:
            if args.introspect:
                methods = [m for m in dir(client.music) if not m.startswith("_")]
                print("client.music methods:")
                for m in sorted(methods):
                    print(f"  {m}")
                return 0

            existing = await get_library_radio_names(client)
            print(f"music assistant: {len(existing)} radio item(s) in library")

            added = skipped = failed = 0
            for ch in channels:
                name = f"{NAME_PREFIX}{ch['name']}"
                if not args.force and name in existing:
                    skipped += 1
                    continue

                ok = await add_radio(client, name, stream_url(ch["uuid"]))
                if ok:
                    added += 1
                    print(f"  added: {name}")
                else:
                    failed += 1

            print(f"done: {added} added, {skipped} already present, {failed} failed")
            if added:
                print(
                    "note: names/icons can be edited per item in the MA ui "
                    "(item page > edit radio station); refresh the Radio view to see new items"
                )

            return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(asyncio.run(main()))
