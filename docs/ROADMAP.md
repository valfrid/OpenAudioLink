# OpenAudioLink Roadmap

## Phase 1 — Architecture

Status: complete.

Deliverables:

- role-based architecture
- control-plane/audio-plane separation
- software component boundaries
- hardware baseline
- lifecycle and versioning model
- repository structure
- master prompt

## Phase 2 — Repository and Windows foundation

The Windows Hub is the first implementation focus.

### 2.1 Repository foundation

- create repository structure
- commit Phase 1 documentation
- select license
- establish coding conventions
- add CI
- create buildable Windows and firmware skeletons

### 2.2 Windows Hub skeleton

Likely baseline:

- modern .NET
- ASP.NET Core backend
- Windows background service
- optional logged-in desktop audio agent
- browser-based web UI

Initial functions:

- health endpoint
- configuration storage
- device inventory
- logging
- web UI shell

### 2.3 Device model and discovery

Implement:

- device identity
- role
- hardware profile
- firmware version
- protocol version
- capabilities
- online/offline state

First proof:

```text
ESP boots -> announces -> Hub discovers -> device appears in UI
```

ESP32-C3 boards may be used for this milestone.

### 2.4 Basic control plane

Commands:

- identify
- rename
- reboot
- read status
- write configuration
- factory-reset request

### 2.5 USB flashing and provisioning

Windows application should:

- detect ESP devices
- flash bundled firmware
- assign identity
- select role and hardware profile
- configure Wi-Fi
- verify network appearance

### 2.6 OTA foundation

- firmware manifest
- checksums
- compatibility checks
- manual update workflow
- recovery behaviour

## Phase 3 — First Windows-to-receiver audio path

```text
Windows WASAPI -> RTP/UDP -> ESP receiver -> I²S DAC
```

Goals:

- 24-bit/48 kHz stereo
- stable playback
- jitter buffer
- basic clock correction
- one and then multiple receivers

## Phase 4 — Analog Source

**Done 2026-08-07.** A record played through the whole chain for the first
time (`LINK-MEASUREMENTS.md` run 18).

```text
PCM1808 -> ESP32-S3 -> RTP/UDP -> Receiver(s)
```

Goals:

- ~~clean ADC capture~~ — 47 998 Hz, 0 read errors, −5 / −7 dBFS
- ~~direct Producer-to-Consumer stream~~ — no PC in the audio path
- ~~Hub control without Hub audio relay~~ — the Hub only says who sends to whom
- standalone limited Controller mode later — still later

Two things the bring-up cost a session each, both recorded where they
happened:

- **Nothing could say whether a turntable was connected.** Every counter
  the capture path had counted frames, and a powered ADC produces frames
  with its inputs open. Fixed by the peak level meter in firmware 0.12.0,
  and the general lesson is in run 18: an instrument that cannot
  distinguish the failure from the success is not an instrument.
- **The ADC could not be selected from the setup page at all.** Both
  source selectors offered Pattern and Tone only, so the obvious first
  test proved the network and said nothing about the turntable.

Still open on this path:

- **A same-access-point measurement.** The first run had the two nodes
  three hops apart across a mesh backhaul and lost the occasional sample.
  That is a network condition, not a result.
- **Noise floor and clipping headroom** for this turntable and preamp.
  The meter can answer both now; neither has been characterised.
- **The producer reported every packet late** on an earlier synthetic run,
  with 36.92 ms of jitter at the consumer. Real — those counters reset at
  stream start — and unexplained. The capture task runs at a higher
  priority than the sending task, which is new since the ADC became
  enabled by default, so that is the first place to look.

## Known gaps, small enough to fix when convenient

- **`SystemAudioSource` refuses a sample-rate mismatch.** It throws when
  the Windows endpoint is not at 48 kHz, with a comment saying resampling
  is not implemented. It is now: `RationalResampler` was built for
  librespot and does exactly this. Wiring it in matters more than it
  sounds, because Spotify's lossless tier is only available inside their
  own desktop app — capturing that app's output is the only route to
  lossless from Spotify (`LIBRESPOT.md`), and it arrives at 44.1 kHz.
- **Drift correction.** The playout ring absorbs it and counts both ends
  but does not trim the clock. Belongs with decision 12's multi-speaker
  synchronisation rather than on its own.
- **The audio sink drops the frame index.** `oal_stream_sink_t` is
  `(payload, frames)`, so playout knows what the samples are but not which
  frames they are. Decision 12's playout contract is "play frame N at time
  T", which needs N: the signature has to grow the packet's RTP timestamp.
  One parameter, one call site, both ends in this repo — worth doing the
  next time that file is open, well before anything depends on it.
- **A per-node latency offset, beside the channel profile.** Drift and
  latency are different faults and only one of them moves. Two powered PA
  speakers each have their own DSP, so two different models are a *fixed*
  few milliseconds apart forever — a permanently shifted stereo image that
  no drift servo will ever correct, because nothing about it is drifting.
  The fix is a signed millisecond trim per node, added to its playout
  target, set once when the speaker is installed. It belongs in NVS next
  to decision 10's channel profile and in the same part of the
  provisioning portal: both answer "what is this particular box", not
  "what is playing". Cheap to design in with decision 12's work,
  irritating to discover afterwards from a stereo image that sits left of
  centre for no visible reason.
- **Concealment.** A lost packet is 5 ms of silence, not an
  interpolation. Worth measuring before deciding it needs fixing.
- ~~**No volume control anywhere.**~~ **Built**, exactly where this said it
  belonged: a per-node gain in NVS beside the channel profile, applied in
  the playout path before the DAC, with `POST /volume` on the node,
  `POST /api/castpoints/{id}/volume` on the Hub and a slider on the
  switchboard. Decision 14 records what building it settled — the cube
  taper, why it is applied at playout rather than on arrival, and the two
  things still missing (no mute that remembers, and no balance between the
  halves of a stereo pair).
- **The ring rides above its target and never comes back down.** Nothing
  pulls the fill toward the target, so wherever a burst leaves it is where
  it stays — measured at 100 ms of a 160 ms ring against a 60 ms target,
  which is protection against gaps and almost none against bursts. It
  works, so this is not urgent, and the obvious fix is worse than it
  looks: trimming the fill back to target buys burst headroom by giving
  away exactly the depth the gaps need. Doing it properly means deciding
  which fault to prefer, and that wants a real listening test rather than
  an argument.
- **The playout state machine has no host test.** Both of the bugs behind
  the first hardware dropouts were in it — a counter that could never
  increment and a re-prime on the first empty chunk — and both would have
  been caught by one. It needs the ring and its state machine separated
  from the I²S calls first, which is why it did not happen during the
  debugging.

## Internet radio, the next source

**Built as far as MP3, plus the switchboard that makes it usable.** The
sections below are the plan as written; what actually landed, and what is
still open, is at the end under "Where radio stands".

Chosen because it is the cheapest strong source left and the only one that
can be **lossless**. Radio Paradise, Linn and Naim serve FLAC over HTTP, so
the ceiling belongs to the station rather than to the protocol — where
Spotify Connect is capped at 320 kbps Vorbis by Spotify and no client-side
work changes that (`LIBRESPOT.md`).

Everything downstream exists and is proven on hardware: `IAudioSource`, the
147:160 resampler for the 44.1 kHz most stations use, the packetiser, the
streamer, cast points. This is fetch, decode, hand over PCM — no separate
process, no account, no sign-in, no zeroconf. Far less than librespot cost.

Decision 3 preferred the Hub as host and decision 13 settled the rate
question: the source resamples, 48 kHz stays the only rate on the wire.

### The decoder is the only real decision

The same shape as decision 11's licensing question, and the same kind of
answer:

- **NAudio's Media Foundation reader.** Already a dependency, decodes MP3
  and AAC natively on Windows, and asks the operator for nothing. FLAC
  needs a separate library.
- **ffmpeg.** Decodes everything including FLAC, but is another binary the
  operator supplies — consistent with how librespot is handled, at the cost
  of another install step.

Start with Media Foundation, because a source that needs no extra download
is a source that works the first time. FLAC follows once the path is proven.

### Two things that will bite before any decoder does

**Playlist indirection.** Many "stream URLs" are `.pls` or `.m3u` files
naming the real stream. Trivial to resolve and mandatory to handle: without
it the first URL anyone pastes fails in a way that looks like a decoder
bug.

**HLS and DASH stations are a different project.** The BBC and most
commercial broadcasters left single endless streams for segment playlists
that need continuous refetching and discontinuity handling. That is a
client, not a fetch. Decide to skip it deliberately rather than discover it
halfway through.

Also: buffer against stalls. Decision 3 named this as what makes internet
radio robust, and today's playout work is the same lesson from the other
end — a source that runs dry is as audible as a network that does, and
`RtpStreamer` now logs it.

### Test stations, chosen for what each proves

A station is a URL, so adding them is free and there is no library to
maintain. These are test cases, not a collection:

| Station | Proves |
| --- | --- |
| SomaFM | Plain Icecast, MP3 and AAC. If this fails nothing works |
| Sveriges Radio | Local, has an open API for channel URLs, and P2 is a real quality test |
| Radio Paradise | FLAC — the lossless path |
| Linn Radio | FLAC again, different implementation, so the handling is not shaped around one station |

Check current URLs at the source rather than hardcoding a list; they rotate
often enough that a checked-in list ages badly. For the switchboard,
`radio-browser.info` is a free community directory searchable by country,
genre, codec and bitrate, which turns "which stations" into a query rather
than a list somebody maintains.

### It is also the case the control surface was written for

A station has no phone app choosing the room, so something must say *which
speakers* — exactly the single-source problem in `CONTROL-SURFACE.md`, and
the same shape a turntable creates. Radio makes the switchboard worth
opening, and the switchboard makes radio usable. They want doing near each
other.

### Where radio stands

Done:

- **Playlist resolution.** `StationPlaylist` reads `.pls` and `.m3u`,
  refuses relative and `file://` entries, and recognises an HLS playlist
  before parsing it — because an HLS playlist is a valid `.m3u`, and read
  as a station list it yields four-second segments that play and stop,
  which looks like a decoder fault rather than an unsupported protocol.
  20 tests.
- **MP3 decoding off a live socket**, frame by frame through Windows' own
  ACM codec, at whatever rate the station's first frame declares.
  Deliberately not a reader: the usual .NET audio readers want a seekable
  stream, and a radio station is the opposite of seekable.
- **Reconnect** on a fixed three-second retry, sending silence meanwhile
  rather than stalling the sender.
- **Saved stations**, on the Hub rather than in a browser, with four
  seeded on first run.
- **The switchboard**, `/play`, room-scoped by `#room=`. See
  `CONTROL-SURFACE.md` for what building it changed.

Open, in the order they are worth doing:

- **FLAC.** The reason radio was chosen — it is the only lossless source
  this project can have — and the one thing still missing. Needs either a
  decoder library or the ffmpeg dependency the section above weighed.
- **AAC**, which some SomaFM channels and most European broadcasters use.
  Media Foundation can do it; the awkwardness is that its readers want a
  seekable stream for the same reason the MP3 readers did.
- **ICY metadata** for track titles. Skipped on purpose so far: asking for
  it means stripping a block every `icy-metaint` bytes, and getting that
  arithmetic wrong is indistinguishable from a corrupt stream. Worth doing
  now that the switchboard has somewhere to show a title.
- **`radio-browser.info`** as a directory, so adding a station is a search
  rather than a pasted URL.
- **Nothing verifies a station before it plays.** The stream starts, the
  source's thread connects, and a station that has moved reports itself
  through the stream description. Better than silence, worse than an
  error on the button that was just pressed.

## Later candidates

Priority is intentionally not fixed:

- USB audio input
- ~~Spotify Connect~~ — done, `LIBRESPOT.md`
- AirPlay as a second provider adapter. The strongest remaining argument
  is that it needs no account at all, where Spotify Connect binds a cast
  point to whoever signed it in
- Chromecast as a *provider*: a dongle's output into an ESP32 Producer,
  keeping the Cast front end that already works and is account-free while
  OpenAudioLink distributes to speakers. Analog through the PCM1808 first,
  because it needs no new parts and no sample-rate conversion
- ~~internet radio~~ — MP3 done, FLAC and AAC open, see above
- Home Assistant integration
- Bluetooth input
- DSP
- more hardware profiles
- alternative Consumers: USB Audio Class DAC on an ESP32-S3 in host mode,
  a PC Consumer application, a Raspberry Pi Consumer (see decision 8)
- a wall control surface — the web app half is built (`/play`); tags and
  the panel are still open, see `CONTROL-SURFACE.md`
