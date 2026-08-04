# Spotify Connect at the Hub

Status: implemented. Sign-in verified on hardware 2026-08-03; the
audio path end to end is not yet.

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

## A receiver has to sign in. Discovery is not enough.

**Established 2026-08-03, after two evenings of measuring the wrong
things.** Current Spotify clients do not offer *unclaimed* zeroconf
devices in their picker. A librespot that announces itself perfectly, on a
network that provably carries the announcement, is invisible to every
Spotify client until it has authenticated.

The proof was a client on the same machine as the receiver, over loopback,
with no network involved at all — and still nothing in the list. Running
the same receiver with `--enable-oauth` and approving it in a browser put
it in the picker immediately, beside the Google Cast devices and the
account's other Connect endpoints.

So the setup flow is: **each cast point signs in once, and is then simply
there.** Which is what setting up any Connect speaker feels like, and what
`CAST-POINTS.md` wants of the Hub's page — setup, and nothing else.

```
librespot --name "Kitchen" --backend pipe --format F32 --device-type speaker
          --cache <hub data>/librespot/<cast point id> --enable-oauth
```

A browser opens on that machine, you approve, and a credential blob lands
in the cache directory. The Hub already passes exactly that `--cache`
path, so from then on its own instance starts signed in.

### What it costs, and it is not nothing

**Guests cannot claim a speaker.** The zeroconf hand-over — a stranger's
phone finding an unclaimed receiver and giving it credentials — is the
mechanism that makes a real Connect speaker usable by whoever is in the
room, and current clients do not offer it. A cast point belongs to the
account that signed it in.

That is a genuine reduction against a Chromecast Audio, and it is
Spotify's decision rather than this design's. It is also an argument for a
second adapter: AirPlay does not work this way, and a house that wants
guests playing in the kitchen may need one.

**A one-time browser step per room.** A Hub running as a Windows service
has no browser, and librespot's OAuth redirect lands on `127.0.0.1`, so
the sign-in has to happen at the Hub machine. Once per cast point, ever.

## Being found is not one problem, it is two

A receiver is in one of two states, and they are reached by different
routes. Conflating them cost an evening, so they are set out here.

**Unclaimed.** Nobody has signed it in. The only way anyone learns it
exists is the mDNS announcement, and the only way it becomes usable is a
Spotify client on the same network finding it and handing over
credentials (`addUser` on the zeroconf HTTP server). This is the state a
new receiver starts in, and it is entirely dependent on multicast
surviving the local network.

**Claimed.** It has authenticated — through that handover, or through
`--enable-oauth` — and registered itself with Spotify as a device on that
account. It now appears in the picker wherever that account is used,
including away from home, with no multicast involved at all.

The consequence is the important part: **a receiver has to be claimed
once, and after that discovery stops mattering.** The community reports
the same thing from the other side — a device only appears in Spotify's
device list once it has been connected to at least once, and the Web API
lists only recently-connected devices.

So a cast point needs a one-time sign-in, and is then stable. That is
precisely what setting up a Chromecast feels like, and it fits what
`CAST-POINTS.md` asks for: the Hub's page is for setup and nothing else.

### A real fault found on the way: the host could not hear

Worth keeping even though it was not the answer, because it was a genuine
fault and the Hub would have hit it too.

Found 2026-08-03, after an evening of measuring the wrong direction.

Windows had classified a **wired** home LAN as a **Public** network, which
switches off its built-in inbound mDNS rules. The receiver could send and
could not receive, and every test we had measured sending.

What that looks like, all of which was true at once:

- the receiver answers `?action=getInfo` from a phone's browser, fully
- its log shows announcements going out on the right interface
- a direct query from another machine gets an answer back
- and no phone can discover it

Because discovery is a *question*, and the question never arrived. The
receiver answered every request that reached it and was never asked.

**Check what the host receives, not what it sends.** With
`RUST_LOG=libmdns=trace`, a healthy host on a normal home network shows a
constant stream of neighbours asking about `_googlecast`, `_shelly`,
`_home-assistant`, `_workstation`. The broken one showed packets from its
own VPN address and nothing else. That silence was the whole diagnosis.

```powershell
Set-NetConnectionProfile -InterfaceAlias "Ethernet" -NetworkCategory Private
netsh advfirewall firewall add rule name="mDNS in" dir=in action=allow protocol=UDP localport=5353 profile=any
```

`hub/scripts/install-service.ps1` now opens 5353 and warns when Windows
has classified a network Public — the Hub's own discovery listens on
multicast `239.255.41.10:41000` and would have failed identically.

**A second mDNS responder on the same machine muddies every reading.** On
this host a Home Assistant VM shared the hardware and had its own address
on the LAN. Answers to a query for the receiver came back with *its*
source address rather than the host's, which reads as a relay or a NAT and
sent this investigation chasing a VPN that was not involved. Shutting the
VM down made the answers arrive straight from the host.

Nothing was broken by it, but it cost time: when a reply comes from an
address that is not the one you are testing, find out what that address is
before theorising about it. Virtual machines, containers and any
reflecting responder can all do this.

One instrument to distrust: **an mDNS browser app is not proof of
absence.** Those apps build their list from the
`_services._dns-sd._udp.local` meta-query, and libmdns does not answer it,
so `_spotify-connect._tcp` never appears there whether or not it is
reachable. A direct PTR query for `_spotify-connect._tcp.local` — what
`Test-Librespot.ps1` sends, and what Spotify sends — is the honest test.

### Where else it goes wrong, and it is usually the network

If a phone cannot see an unclaimed receiver, the announcement is not
reaching it. The cause found repeatedly by others, and matching what was
measured here, is **mesh Wi-Fi not forwarding mDNS between its access
points** — librespot discussion #1314 records it plainly: *"everything
works only if the client is in the same cell as the librespot server"*,
fixed by putting the mesh in access point mode rather than router mode.
Issue #1672 is the same symptom, closed without a root cause.

That produces a very specific and misleading picture, all of which was
observed here:

- the phone reaches the receiver's address perfectly, because unicast is
  bridged between cells as normal
- the receiver logs mDNS questions from half the house, because those
  devices share its cell
- the phone never sees it, because it is on a different one
- Chromecasts still appear in the phone's list, because Google keeps a
  cloud-backed device list and does not depend on local discovery

Two checks separate it from everything else. Put the phone physically
beside the host and toggle its Wi-Fi so it re-associates to the nearest
access point — if the receiver appears and then vanishes when you walk
away, it is the mesh. And run `hub/scripts/Test-Librespot.ps1` from
another machine with `-SkipLocalChecks`: discoverable from the host but
not from elsewhere is the same verdict.

### What this means for guests

**Claiming is per account.** A guest with their own Spotify account has to
discover the receiver locally and claim it themselves, exactly as they
would a real Connect speaker. So a house that wants guests to be able to
play in the kitchen needs working mDNS; signing the receivers in solves
the owner's case and not theirs.

That makes the mesh's behaviour a real constraint on the product rather
than a local annoyance, and worth fixing at the router rather than
working around here.

## "error audio key": stale credentials, and the fix

Symptom: every track loads and is skipped about a second later, and the
log carries

```
ERROR librespot_core::audio_key] error audio key 0 2
WARN  Unable to load key, continuing without decryption: Service unavailable
ERROR Unable to read audio file: Symphonia Decoder Error: channel closed
ERROR Skipping to next track, unable to load track
```

librespot cannot fetch the decryption keys, so nothing can be decoded.

This is [librespot issue #1649](https://github.com/librespot-org/librespot/issues/1649),
**open with no documented fix**, reported against 0.7.0 and 0.8.0 and
triggered by OAuth authentication with Spotify Connect playback. It is
hitting Music Assistant, Home Assistant and spotify-player as well.

**What worked here, 2026-08-03: delete the credential cache and sign in
again.**

```
rmdir /s /q <hub data>\librespot\<cast point id>
```

then start once with `--enable-oauth`. The keys began working immediately
and every track loaded cleanly. A stale token appears to be at least one
cause, which is worth knowing given the upstream issue documents none.

If a cast point starts skipping every track, that is the first thing to
try. It costs one browser sign-in.

## Do not judge the pipe by a file

A file never blocks, so redirecting the pipe to one paces nothing:
librespot decodes flat out, finishes a three-minute track in about a
second, and races through the playlist. That is the flow-control
mechanism visible by its absence, and it looks alarmingly like a fault.

Measured here: **933 MB in a couple of minutes**, which at 352,800 bytes
per second of music is about 45 minutes of audio. That is what success
looks like on this test — not a file that grows in real time.

The Hub is the consumer that paces it, by stopping at 100 ms of buffered
audio and letting the pipe fill. Nothing else in this project should ever
read that pipe as fast as it can.

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
