# Inputs

The swithpanel shoul select type of a provider with a cast-point.
One active pair at a time, when selecting new pair provider <=> cast-point.
(the cast-point could be one2one consunsumer, but also one2many. 
So all targets that should be available, for Spotify, but also Vinyle, Internet Radio, and other to come is must be defined as cast-point)

Spotify / librespot is a special case, or an alternative way to set up a cast-point <=> consumer pair in case spotify is provider, by simpli in app select cast-point.

I would like to make this very symetrical, so this i

This switchpanel should clear show active active pair.
In adition volume control on cast-point shoul be availabke in this switch-panel.

Simple attribute of active provider like Spotify song, Internet channel should be visible.

The internet radio should as provider have its own panel like a "spotify app"

The today control pannel is more of a admin set-up. This should be cleaned up. The part that was used for performance and integratio activity shoul be kept, but moved a part from needed admin pars.
Whats needed 
device connectiity setting
setting up device roles, consumer/provider settings like stereo/mono/left/right
firmware upgrade
device status

what cold be sparate tooling are
generate test signals

If no server, the swith control skould be on one availabe provider, likr the vinyle provider or the node provide a usb virtual sound card connected to a pc.

This control role could also host a stand alone wifi ap (as kind of back up) for this small node system


# A control surface for the house

Status: **step 1 built** — the switchboard is at `/play`, and it honours
`/play#room=kitchen`. Steps 2 and 3 (tags, panel) are unscheduled and
unchanged. What follows is the reasoning as it was written before any of it
existed; the section at the end records what building the first step
actually changed.

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

## The panel could be the head of a headless system

Decision 9 says the Controller is a small role and the Hub is merely a
device that hosts it — verified on hardware in both the house case and the
party case, where a turntable claimed the role and speakers joined it.

So a panel holding the Controller role is not a new architecture. It is a
third host for a role that already moves, and it makes a house with **no
PC at all** coherent: speakers, a source, and a screen by the door.

**What a panel can host:** discovery, the peer table, the election, the
cast points, and telling a Producer where to send. All of it is small.
Cast points are a name and a handful of device ids, which fits NVS
comfortably.

**What it cannot host: a streaming service receiver.** librespot needs a
real operating system — a filesystem, TLS, an OGG decoder, hundreds of
megabytes of dependencies. No ESP32 is going to run it.

That boundary is exactly decision 8's and 9's separation doing its job:
**Controller is not Provider.** A panel-headed house plays a turntable, a
line input, or anything the ADC can reach. It does not play Spotify.
Adding Spotify means adding a machine, and that machine may as well be the
Hub.

Worth being blunt about, because "just put the Controller on the wall
panel" sounds like it removes the PC entirely, and it only removes the PC
from the deployments that never needed streaming.

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

1. **A web app served by the Hub**, with a room URL so a page can start
   scoped to one room. That alone makes tags useful, and it needs no
   hardware.
2. **Tags.** Print or stick. No code.
3. **The panel**, if the tags prove the idea and the screen still appeals.
   By then the API it needs will have been exercised by a real user rather
   than designed in the abstract.

Doing it in that order means the expensive half is only built if the cheap
half turns out to be used.

### One constraint on how the web app is written

**Talk only to the documented endpoints, and render entirely in the
browser.** No server-side templating, no Hub-specific glue.

That is not architectural purity for its own sake. It is what lets the
same page be served later by something that is not the Hub — a wall panel
hosting the Controller role, serving the same endpoints from
`oal_control`, which already runs an HTTP server on every node.

Written that way, "break it out onto the wall" is a matter of hosting the
files somewhere else. Written the other way, it is a rewrite.

## What building step 1 changed

The page is `hub/src/OpenAudioLink.Hub/wwwroot/play.html`: one file, no
build step, no dependencies, served as a static file. It kept the
constraint above — every call it makes is one of the endpoints below, and
nothing in it knows which machine served it.

Three things did not survive contact.

### The room URL is `/play#room=`, not `/#room=`

`/` is the setup page and has been since the beginning: firmware, roles,
diagnostics, and the instructions in `LIBRESPOT.md` that point at it.
Taking that address for the switchboard would have moved every one of
those. `/play` redirects to `/play.html` so a printed tag carries the
short form, and browsers preserve the fragment across the redirect.

The room is matched against the cast point id **and** against its
slugified name, because whoever writes a sticker is reading the name on
the setup page rather than the slug underneath it. `#room=Kök` and
`#room=kok` reach the same room.

### One endpoint had to learn that the Hub is also a producer

The table below says "connect them: `POST /api/castpoints/{id}/play` with
a producer". That was true only for *node* producers. The Hub holds the
producer role like any other device, and asking it to start a stream used
to mean POSTing to a node control endpoint the Hub does not have — so
radio, a test tone and system audio each had their own endpoint that took
a list of IP addresses and knew nothing about rooms.

That is the wrong shape for a switchboard, which asks exactly one
question: *play this in that room*. So `play` now branches on whether the
named producer is this Hub, and starts the stream locally when it is —
the same path `LibrespotService` already took when Spotify drove a cast
point. The per-source endpoints stay, because they are what a script or a
`curl` line uses, and because they can aim at an address that is not a
cast point at all.

Spotify is deliberately absent from the sources this accepts. A cast point
plays Spotify because somebody pressed play on a phone; the Hub cannot
make that happen, and offering a button that appears to would be a lie.

### Stations had to live on the Hub

The obvious place to keep a list of radio stations is the browser, and it
is wrong for exactly the reason this document exists: the control surface
is a wall tag and a phone that has never seen this house before. Stations
in one browser's local storage are invisible to every other way of
reaching the Hub, which is most of them. They are a JSON file beside the
cast points, seeded once with four stations so a fresh Hub has something
on the page, and never re-seeded — a station somebody deleted stays
deleted.

### Volume was the one thing that had to be built from nothing

Everything else on the page was a different way of pressing a button that
already existed. Volume was not a button anywhere — decision 14 has the
argument, but the short version is that Spotify had been hiding the gap:
librespot applies the phone's volume before the Hub ever sees the samples,
so every test so far had a working volume control belonging to somebody
else's software.

It lands on the switchboard as one slider per **room**, not per speaker. A
person standing in the kitchen thinks the kitchen has a volume; that its
two speakers each hold their own level is an implementation detail they
should never have to hold in their head. The per-speaker control exists —
it is what balancing a stereo pair needs — and it lives on the setup page,
where implementation details belong.

Two details that make it feel like a control rather than a form:

- **It sends while dragging**, throttled to 250 ms, rather than only on
  release. Dragging in silence and discovering the result afterwards is
  how a slider feels broken. The node applies a change on its next 5 ms
  chunk, so the room really does follow the thumb.
- **The poll leaves it alone** while it is being touched, and for two and
  a half seconds after. Speakers report their level on the Hub's own poll
  cycle, so for a moment after a change the freshest reading is still the
  old one; without the grace period the thumb springs back and then
  forward again.

### The endpoints as they now stand

| | |
| --- | --- |
| What rooms exist | `GET /api/castpoints` |
| What can play | `GET /api/devices` filtered by the producer role, `GET /api/stations` |
| Connect them | `POST /api/castpoints/{id}/play` — `{producer, source, stationId}` |
| Stop | `POST /api/castpoints/{id}/stop` |
| How loud | `POST /api/castpoints/{id}/volume` — `{percent}` |
| How loud, one speaker | `POST /api/devices/{id}/volume` — the setup page's |
| What is playing | `GET /api/stream` |
| Remember a station | `POST /api/stations`, `DELETE /api/stations/{id}` |

One new thing in the *protocol* — `POST /volume` on a node, which had to
exist because nothing could change the level. Everything else is still a
different way of pressing buttons that exist.
