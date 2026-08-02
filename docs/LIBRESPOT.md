# Spotify Connect at the Hub

Status: implemented, not yet verified end to end on hardware

This is the first provider source for cast points (`CAST-POINTS.md`). It
is what makes the feature the feature: choosing "Kitchen" in Spotify on a
phone picks what plays *and* where it plays, in one act, without opening
the Hub's page.

## The shape of it

```
Spotify phone ──mDNS/Connect──▶ librespot "Kitchen" ──stdout PCM──▶ Hub
                                                                     │
                                                       44.1 → 48 kHz  │
                                                        L24 RTP       ▼
                                            kitchen speaker, and any other
                                            consumer in the Kitchen cast point
```

**One librespot per cast point.** The process runs whether or not anything
is playing, because that is what puts the room in the phone's device list.
Create a cast point and it appears; rename it and it is renamed; delete it
and it is gone. The Hub owns the whole lifecycle.

**The receiver drives the stream.** Nothing in the Hub's interface starts
playback. Audio starting to come out of a receiver is what starts the RTP
stream to that cast point's speakers, and audio stopping for
`IdleSeconds` is what stops it.

## Installing librespot

The Hub does not ship the binary. Whether a particular reimplementation is
licensed for a particular service is the operator's decision, so the Hub
manages a binary you supplied rather than one it chose for you.

### Getting a Windows binary

**There is no official one.** The librespot project declines to publish
precompiled binaries — the request is
[issue #727](https://github.com/librespot-org/librespot/issues/727),
closed `wontfix` — and the release assets are source archives only.

So this repository builds it, the same way it builds firmware images and
the Hub package: **Actions → librespot → Run workflow**, then download the
artifact. Nothing installed locally, and the first run took five and a
half minutes. The workflow is `.github/workflows/librespot.yml`; it pins
the version, runs the binary once before uploading it, and prints the
SHA256 in the run summary.

Building it on your own machine instead needs a Rust toolchain and the
Microsoft C++ build tools.
**[BUILDING-LIBRESPOT-WINDOWS.md](BUILDING-LIBRESPOT-WINDOWS.md) covers
both routes from nothing**, plus the firewall rule and a standalone test
that isolates librespot from the Hub. Not WSL: it builds Linux binaries,
and Windows cannot start one.

`--no-default-features` drops `rodio-backend`, which exists to talk to a
sound card this Hub does not want it talking to. **`with-libmdns` must
stay**: it is what advertises each receiver on the network, and without it
nothing appears in the phone's picker. `native-tls` on Windows is
SChannel, so no OpenSSL. The pipe backend is not feature-gated and is
always present.

Checked against **librespot 0.8.0** (crates.io, November 2025): every
argument the Hub passes exists under those exact names, and `--format`
accepts `F32`.

Other projects do ship Windows binaries — `go-librespot` and various forks
— but they are separate implementations with their own command lines, and
the Hub drives librespot's. If you use one, expect to adjust the
arguments.

### Telling the Hub where it is

Either put `librespot` (or `librespot.exe`) beside the Hub executable, or
put it on `PATH`, or name it in `appsettings.json`:

```json
"Librespot": {
  "Enabled": true,
  "ExecutablePath": "",
  "DeviceType": "speaker",
  "Format": "F32",
  "Bitrate": 320,
  "InitialVolume": 50,
  "IdleSeconds": 5,
  "ExtraArguments": "",
  "CacheDirectory": ""
}
```

A Hub with no librespot installed logs one line at startup and carries on
being a Hub. Nothing else in the project depends on it.

Spotify Premium is required — that is a property of the service, not of
this design.

One process runs per cast point, all the time. Each costs a few tens of
megabytes of memory and almost no CPU while idle, so a house with six
rooms runs six of them. If that ever becomes the wrong trade the answer is
fewer cast points, not a shared receiver: a shared receiver is one entry in
the phone's picker, which is the thing this whole design exists to avoid.

## What the Hub asks librespot for

```
librespot --name "<cast point name>" --backend pipe --format F32
          --device-type speaker --bitrate 320 --initial-volume 50
          --cache <data>/librespot/<cast point id> --disable-audio-cache
          --zeroconf-interface <this host's address on the speakers' subnet>
```

**`--backend pipe`** writes raw PCM to stdout. librespot's own log goes to
stderr, where the Hub reads it at debug level.

**`--format F32`** is what librespot decodes to internally, so asking for
it skips a quantisation step the Hub would only have to undo — everything
from the pipe to the packetiser is float. `S16`, `S24_3` and `S32` also
work if a build will not do F32. `F64` and `S24` are valid librespot
formats this Hub does not read, and it says so at startup rather than
producing noise.

**`--cache`** is where the credentials live, per cast point, because each
cast point is a separate Spotify device that the phone logs into
separately. Without it every Hub restart means logging in again from the
phone, which is exactly the kind of small daily friction that decides
whether a thing gets used. The *audio* cache is disabled: it is large, it
is per cast point, and the audio is going straight out to the air anyway.

**Volume comes for free.** librespot applies the volume set on the phone to
the samples before they reach the pipe, so it reaches the speakers without
the Hub doing anything. That is one of the two things `CAST-POINTS.md`
asks for beyond mere function.

Confirmed on the real binary, which announces
`Mixing with softvol and volume control: Log(60.0)` at startup: with no
hardware mixer to hand off to — the build has no sound-card backend at all
— it has nowhere to apply volume except the samples.

**`--zeroconf-interface` is not optional on a machine with a VPN.** Without
it librespot binds every interface and lets the operating system choose
where multicast goes, which it does by route metric. On the machine this
was found on:

```
InterfaceAlias   InterfaceMetric   ConnectionState
Tailscale                      5         Connected
Wi-Fi                         50         Connected
```

Tailscale wins by a factor of ten, so every announcement left over the
overlay to nobody. The symptom is precise and misleading: the receiver is
reachable at its address — a phone's browser gets a full answer from
`http://<host>:<port>/?action=getInfo` — and completely absent from
Spotify's device list, because being *found* and being *reached* travel by
different paths.

The Hub picks the address itself rather than asking the routing table,
because it knows something the routing table does not: where the speakers
are. An address sharing a subnet with a known speaker is reachable from
that subnet by definition, and the phone is on it too. That is
`LocalAddressSelector`'s reasoning — written for firmware downloads after
the same VPN bit this project once before — applied to announcements.
`Librespot:ZeroconfInterface` overrides it. Until the first speaker is
discovered there is nothing to match against, so the choice is left to
librespot and revisited the moment one appears.

## Two things worth understanding

**Flow control is the pipe.** A pipe backend writes as fast as it can
decode — there is no sound card slowing it down. A reader that kept up
would pull an entire track into memory in seconds. So the Hub stops
reading once 100 ms of audio is waiting, the pipe fills, and librespot
blocks. That is the same back-pressure a sound card would have applied,
and what is held becomes the stream's cushion against scheduling hiccups.
It is also the latency this adds: about 100 ms, against roughly 2 s for
AirPlay 2 and Chromecast.

**Playing is decided by data flow**, not by an event hook. librespot's
`--onevent` would need a helper executable on both platforms to report
what the bytes already say. Bytes arriving means playing; five seconds
without them means stopped.

## Resampling

Spotify is 44.1 kHz and the RTP profile is 48 kHz, so something has to
resample, and `CAST-POINTS.md` puts it at the Hub: one clock domain across
the whole house is worth more than the CPU it costs.

The ratio is exact — 44100:48000 reduces to 147:160 — so this is a
polyphase FIR, not an interpolator chasing a drifting phase.
`RationalResampler` upsamples by 160, low-pass filters and decimates by
147, multiplying only the 64 taps per output sample that land on a real
input sample.

Measured against an ideal sine: worst error about **-110 dB**, response
flat to **20 kHz**, images below **-90 dB**, DC gain within 1e-5 of unity.
That is far better than the lossy source it carries, which is the point —
the resampler should not be the thing anyone can hear. Cost is about 6
million multiplies a second for 48 kHz stereo.

## What this does not do yet

**One Hub stream at a time.** The Hub sends one RTP stream, so two cast
points cannot play different music from two Spotify accounts at once. If
two receivers are playing, the one that started most recently wins —
pressing play is a statement about what should be heard now. A single
account already plays to one device at a time, so this only shows up with
two accounts.

**Stop from the Hub does not pause Spotify.** Stopping a Spotify-fed cast
point from the Hub's page stops the sending; the receiver is still
playing, so the stream comes back within a tick. Pausing belongs on the
phone. This is a consequence of the receiver driving the stream, which is
the right way round.

The same follows for starting a *node*-producer cast point that shares a
speaker with a playing Spotify one: it stops the Hub's stream, and the
receiver — still playing — takes it back. A receiver that is actually
playing outranks a button, and the loop terminates rather than
oscillating. Pause on the phone first.

**Clock drift is unhandled.** The sender is paced by the PC's clock and
librespot is paced by the pipe, so over hours the two can walk apart by a
sample or two. The ring buffer absorbs it by dropping or padding, both
counted. Synchronised playout across several speakers is the real fix and
is blocked on the DAC regardless (`CAST-POINTS.md`).

**Track changes are seamless, gaps are not marked.** A stream that goes
idle discards its buffered tail, so the next track does not open with the
end of the last one.

## Trying it

1. Put the binary where the Hub can find it and restart the Hub. The log
   says `Spotify Connect receivers will run from <path>`.
2. Make a cast point in the Hub's page with a speaker in it.
3. On a phone on the same network, open Spotify and look in the device
   picker. The cast point's name is there.
4. Choose it and press play. `GET /api/librespot` shows the receiver
   playing; the cast point's row says so too.

If the name does not appear, the receiver did not start — the reason is in
the `On Spotify` column and in `GET /api/librespot`.
