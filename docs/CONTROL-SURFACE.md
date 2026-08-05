# A control surface for the house

Status: recorded, nothing scheduled. Written down so the shape is not
re-derived later, and because one half of it is much cheaper than it
looks.

## The problem it solves

`CAST-POINTS.md` argues that in normal use nobody opens the Hub's page:
you choose a room in Spotify and that picks what plays *and* where. That
holds while a provider offers one receiver per room.

It stops holding the moment a provider is a single source — a turntable,
a Chromecast dongle feeding a Producer, a line input. Then something has
to say *which speakers*, and today the only thing that can is a browser
pointed at the Hub. That is fine for setup and wrong for daily use.

So: **a way to connect one provider to some consumers, in a few seconds,
without finding a computer.**

## Two forms, and the cheap one may be better

### A. A tag on the wall

A QR code or an NFC sticker in each room, opening the Hub's page already
scoped to that room.

Cost: a printed square, or an NFC sticker for a few kronor. **No hardware
and no new protocol.** Android and current iPhones both read NFC tags
without an app.

What it needs from the Hub is small: a URL that means "this room", and a
page that honours it — something like `/#room=kitchen` landing on a view
where the kitchen is already chosen and the only question left is which
source.

This is worth noticing: a tag by the kitchen door that means **"play
here"** is very close to the one-act selection the whole cast point design
is built around, and it costs almost nothing.

### B. A screen on the wall

A "Cheap Yellow Display" — the ESP32-2432S028R, an ESP32 with a 2.8-inch
320×240 touchscreen for around 150 SEK — showing the house: which
providers exist, which consumers are alive, and what is currently
connected to what. Touch a source, touch some rooms, done.

The attraction is not the screen. It is that **this is the same chip and
the same firmware family as the speakers**, so it inherits everything
already built: discovery, identity, the control protocol, OTA. A panel is
another device that announces itself and asks the Controller what exists.

It also answers a case the browser cannot. Decision 9's party deployment
has no Hub at all — a turntable claims the Controller role and speakers
join it. A panel that finds *whoever* is Controller works in the house and
at the party, using the peer table and the election that already exist.

## What either one actually does

Very little, which is the point. Both are clients of endpoints the Hub
already serves:

| | |
| --- | --- |
| What can play | `GET /api/devices`, filtered by the producer role |
| Where it can go | `GET /api/castpoints` |
| Connect them | `POST /api/castpoints/{id}/play` with a producer |
| Stop | `POST /api/castpoints/{id}/stop` |

Nothing new in the protocol. A control surface is a different way of
pressing buttons that exist.

## Order, if this is ever built

1. **The room URL.** A page that accepts a room and starts scoped to it.
   That alone makes tags useful, and it is an afternoon of UI work.
2. **Tags.** Print or stick. No code.
3. **The panel**, if the tags prove the idea and the screen still appeals.
   By then the API it needs will have been exercised by a real user rather
   than designed in the abstract.

Doing it in that order means the expensive half is only built if the cheap
half turns out to be used.
