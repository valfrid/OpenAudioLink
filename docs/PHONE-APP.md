# The phone app

An Android **Producer**: it originates the RTP stream itself and carries
just enough control to get one running. Decision 19 sets the scope,
decision 20 the network rules and decision 21 the Spotify build; this is
how the thing is built and how to run it.

Version 0.9.1, built by CI as one APK — see *One build* below.

## What it does, and what it deliberately does not

| Does | Does not |
| --- | --- |
| Send L24/48 kHz stereo RTP to any OpenAudioLink consumer | OTA |
| Publish a Spotify cast point | Sample logging |
| Find speakers by multicast discovery | Room measurement |
| Choose which speakers play, mid-song | Pushing party-network credentials |
| Volume, per speaker | Anything else a Hub does |
| Room correction on and off, per speaker | |
| Drive a vinyl node as its Controller | |

Five sources, and the first is the reason the app exists:

1. **Spotify Connect** — librespot publishes a cast point named after the
   phone. It signs in once, and then appears in the picker of the account
   that signed it in.
2. **A vinyl node** already on the network, told where to send.
3. **A music file** from this phone.
4. **Internet radio** — MP3, AAC and FLAC, from a saved station list.
5. **A test tone**, which needs no permission, file or account.

The right-hand column stays the Hub's. A speaker holds its own room
correction in NVS, so it travels to a party already corrected and the
phone never needs to know a measurement was made.

**Provisioning needs no Hub, and never did.** An unprovisioned node opens
its own setup access point — `OpenAudioLink-XXXXXX` — and serves a form
for SSID, password, name, roles, channel and output, then reboots onto
the network. It is the same pattern as any other smart device in a
house: connect, open a browser, fill it in. `oal_wifi.c` has the portal,
and it re-opens itself if the network later disappears, refusing to retry
while somebody is still connected to it.

This entry used to read "the phone cannot provision", which overstated a
much narrower thing. **What the phone does not do is push the
party-network credentials**: the pre-shared pair a node needs in NVS
*beforehand* to follow a phone hotspot without being re-provisioned, which
the Hub generates and sends with `POST /config {"party":{…}}`. So "bring
your hub" is really "bring your hub, having once met mine" — and that is
about standalone party mode, not about getting a speaker onto a network
in the first place.

Even that is a scope decision rather than a limit. It is one HTTP POST to
port 41001, which this app already talks to for volume and room
correction; it is left out because letting a phone write Wi-Fi
credentials into other people's speakers is a much larger security
question than this app is worth.

## How it is built

Two Gradle modules, and the split is the same discipline the firmware uses
for `oal_phase` and `oal_netpick`:

```text
phone/
  core/   plain Kotlin on the JVM — the wire format, pacing, discovery,
          control requests, 44.1-to-48 resampling, silence suppression,
          send-interval measurement, the station model and playlist
          resolver. 104 tests. No Android; runs anywhere with a JDK.
  app/    the Android half — UI, decoder, foreground service, radio locks.
          Needs the Android SDK.
```

The wire format is the part that cannot be debugged by looking at the
screen: a byte-order mistake is "loud static" on a speaker in another room,
and pointing it at a speaker and listening cannot tell a wrong payload type
from a wrong byte order from a wrong port. So it is built in `core` and
checked byte by byte against `protocol/AUDIO-RTP.md`.

`settings.gradle.kts` includes `:app` only when an Android SDK is present,
so CI runs `:core:test` on a plain JDK image.

```bash
cd phone
gradle :core:test          # anywhere
gradle :app:assembleDebug  # needs ANDROID_HOME; fetches librespot
```

## The three Android things that fail silently

Each of these produces no error anywhere, and each looks like a broken
speaker or a broken network.

**A multicast lock.** The Wi-Fi chip filters multicast frames before any
socket sees them unless `WifiManager.MulticastLock` is held. Without it
discovery finds nothing at all.

**A high-performance Wi-Fi lock.** Station power save batches frames into
beacon intervals. The consumer's servo absorbs it, but it turns a quiet
link into a bursty one for no benefit.

**Naming the multicast interface, three ways.** `LinkProperties` gives an
interface name, and on a handset at home that name resolved to nothing
`NetworkInterface` could see — so discovery joined the group on the
system's choice and outbound announces began failing with `ENETUNREACH`.
Receiving still worked, which is what made it invisible: the Hub appeared
in the list so discovery looked healthy, while this phone was announcing
to nobody. `WifiBinding.multicastInterface()` now tries the name, then the
interface that actually holds the phone's Wi-Fi address, then the first
up, non-loopback, multicast-capable IPv4 interface with `wlan` preferred —
because the alternative on a phone is `rmnet`, the one place a speaker's
audio is guaranteed not to be.

**Explicit socket binding.** A phone with mobile data up is multi-homed,
and a socket left to choose for itself will send a speaker's audio to the
cellular interface, where it vanishes. Every socket here is bound to the
Wi-Fi `Network` object. This is the Android form of the lesson decision 15
records for the Hub — and it is also what makes a hotspot party work: the
speakers are served over Wi-Fi while the music arrives over mobile data.

## The hotspot must be 2.4 GHz

Android often defaults to auto or prefers 5 GHz. The XIAO ESP32-S3's radio
is 2.4 GHz only, so a 5 GHz hotspot is **invisible to every speaker**, with
nothing anywhere to say why. This is the most likely way for a party to
fail to start, and it looks exactly like a broken speaker.

The hotspot must also carry the **group passphrase** — the pair the Hub
generated and pushed to the nodes with `POST /config {"party":{…}}`. It is
not in this repository and never will be; it lives in the Hub's data
directory and is typed into the phone once.

## The home network may be dual-band, but it must be one subnet

Putting the speakers on 2.4 GHz and the phone on 5 GHz is a good idea: the
phone is the producer and the producer's uplink is the hop that showed up
in the measurements against the Hub, while the ESP32-S3's radio has no
choice in the matter. Most home routers publish both bands as one bridged
LAN, and then this is simply a better arrangement of the same network.

The condition is that the two bands are **bridged, not routed**. OAL's
discovery is a multicast announce to `239.255.41.10`, and multicast does
not cross a subnet boundary. A router that puts each band on its own
subnet — a guest SSID, a separate "IoT" network, some mesh systems in
their default configuration — leaves a phone that finds no speakers at
all, which looks identical to a quiet network, a wrong interface, or
nothing switched on.

So the details line prints this phone's own address next to the interface
it joined on. Same `/24` as the speakers reported by the Hub, and the
bands are bridged; a different one, and they are separate networks and no
amount of waiting will help.

### One subnet is necessary and not sufficient: mesh nodes drop multicast

A second failure sits behind the first and looks nothing like it. On a
mesh — two or more access points bridging one LAN — announcements can
travel in one direction only, so which speakers this app finds depends on
**which node the phone happens to be associated with**.

Seen on an ASUS ZenWiFi pair, everything on `192.168.0.0/24`: by the main
router both speakers appeared; by the secondary node only the speaker
associated with that same node did. The cause was **IGMP snooping**, and
disabling it on the router fixed it. `protocol/DISCOVERY.md` has the
mechanism, the two settings usually confused with it, and how to tell
this apart from a quiet network using the `heard` counter.

Worth knowing for reading a fault here: only *discovery* is affected.
Audio is unicast to the address inside the announcement, so a speaker
already found keeps playing across the mesh perfectly well. It is being
found the first time that fails.

### The band does not decide who can see the cast point

A worry worth answering directly: publishing Spotify Connect from a phone
on 5 GHz does **not** hide the cast point from a second phone elsewhere in
the house.

Once the device has been claimed by an account, it is visible through
Spotify's own servers — the picker on a second phone signed into that
**same account** lists it whether that phone is on 5 GHz, on 2.4 GHz, or
on mobile data in another country. Zeroconf on the LAN is only how the
device is claimed the first time.

A phone signed into a **different** account cannot use it, and that is not
a network problem either: current Spotify clients no longer show unclaimed
zeroconf devices at all (`docs/LIBRESPOT.md`), so a guest could not have
picked it from the same room on the same band. Spotify Jam is the route
for guests.

What the second phone *does* need is the audio to arrive, and that is the
first half of this section: whoever is producing must reach the speakers'
subnet.

## Pacing, and what a phone does to it

A producer's whole obligation, now that consumers place themselves on the
sender's RTP timeline, is to stamp consistently and keep sending. The
sender counts packets from a **fixed anchor**, not from the last packet:
sleeping "5 ms" two hundred times a second drifts by seconds an hour, and a
consumer measuring against the sender's timeline would read that drift as
the sender walking away from it.

Three behaviours follow, all tested:

- **A late wake-up catches up.** Up to a quarter of a second of owed
  packets go out back to back — more than a scheduler hiccup, less than a
  consumer's jitter buffer, so the burst is absorbed rather than heard.
- **A long freeze resynchronises.** A process frozen by Doze for a minute
  owes 12 000 packets; sending them would be five seconds of airtime
  delivering audio nobody can use. It re-anchors, sets the RTP marker bit,
  and starts from now.
- **An empty buffer sends silence, not nothing — for 200 ms.** A gap is a
  break in the sender's timeline and a consumer answers a break by
  re-seating itself, so a decoder stumbling for 20 ms costs 20 ms of quiet
  instead of a second of re-priming on four speakers. Past 200 ms it is
  not a stumble, it is a stop, and there is no timeline left to keep
  continuous: `SilenceGate` stops sending until audio comes back, and
  marks the packet that resumes.

## The screen

Shaped like the Hub's `play.html`, because it is the same job for the same
person and two products that do one thing should not have to be learned
twice: a brand line with a health dot, a banner for what is playing, the
rooms, and a row of tiles answering *what would you like to hear*.

**The version is on the brand line, always.** Not behind the details
switch and not only in the announce, because this app is installed by
hand from a CI artefact and two builds can differ by a feature while
looking identical. That cost a round: a fault reported "in 0.8.4" was
0.8.3 still installed, and the only way to tell was noticing that a
field 0.8.4 adds was absent from the screenshot. The Hub's footer prints
name, version and protocol for the same reason.

**The send counters outlive the sending.** They are shown whenever the
stream has sent anything at all, not only while audio is flowing. Gating
them on "is audio flowing right now" threw away the record of a run at
the exact moment it became worth reading: a Spotify queue that empties
overnight leaves the silence gate holding, so the screen next morning
said `waiting 4h 45m · nothing sent` and nothing else — no packet count
and no gap histogram, for a five-hour stream that had just ended. The
Hub's log had recorded seven producer-side stalls that night, and the one
line that could confirm them from the sending end had erased itself.

**The instrumentation is behind a switch.** The packet counters, the
discovery heartbeat and librespot's own lines were each added because a
screenshot could not be read without them, and each one earned its place —
so none of them was deleted. They live under *Settings → Show details*,
off by default. A screen that opens on packet counts and another
program's stderr is an instrument panel; this is meant to be a thing
somebody plays music with.

The discovery line under that switch reads `discovery: on wlan0
(192.168.0.34) · heard … · announced …`. The address is this phone's own,
and it is there for one question the rest of the screen cannot answer: see
*The home network may be dual-band* above.

**The cast point's name is editable**, and defaults to `OAL ` plus the
phone's own name. A Spotify device list is a flat alphabetical pile of
everything in the house, so the prefix keeps a Hub's rooms and a phone
together in it rather than scattered between a television and somebody's
laptop. It is a default, not a rule. Renaming needs the stream stopped: a
running librespot advertises the name it was started with and there is no
way to tell it otherwise, so renaming mid-stream would leave the screen
and the Spotify picker disagreeing.

The same name is used for the OpenAudioLink announce and for the Spotify
cast point, deliberately — a device that appears as one thing in the
picker and another in the speaker list is two devices as far as anybody
looking at it is concerned.

**The palette is the Hub's**, taken from `oal.css` rather than chosen
again: `--bg #111317`, `--panel #1B1F25`, `--accent #62D1A6`, and the
18dp/12dp rounding those cards use. `Theme.kt` carries them in Compose and
`themes.xml` paints the window and the system bars before the first frame,
so there is no white flash on the way in.

**Dark in every configuration, not "dark first".** 0.8.0 followed the
system theme, which matches the Hub only on a handset that happens to be
set to dark — and on one set to light it produced the accent colour and
none of the rest, a visual change that looked like no visual change. The
choice is made in `Theme.kt` rather than deferred to a setting somebody
made for other reasons: one product, one look, usually read in a dim room
with music playing.

**The typeface is bundled, and until 0.8.7 the app never chose one.**
Leaving `fontFamily` unset means `FontFamily.Default`, which on Android
is the *system* font — and on a Samsung handset that is whatever the
owner picked under Display → Font size and style. So the app rendered in
a face neither this project nor the Hub had any say in, and "matches the
Hub" was true of the palette and the rounding but never the letters.

**Roboto** is bundled in `res/font` and every text style is pinned to it,
for reasons rather than taste:
`oal.css` asks for `Inter, system-ui, -apple-system, "Segoe UI", Roboto,
Arial, sans-serif` and ships no `@font-face`, so the Hub itself renders
in Inter only where Inter is installed — on Windows it is Segoe UI.
Roboto is the entry in the Hub's own stack an Android device would
reach, it is Android's native UI face, and it is Apache-2.0, which a
public repository needs. The licence sits in `phone/app/licenses/`.

**Three real weights, and no fourth.** 400, 500 and 700 exist as files.
Asking for anything else has the platform pick the nearest and
*synthesise* the difference by stroking the glyph, so the two styles that
asked for SemiBold now ask for Bold, which is nearer the Hub's 700–900
headings anyway.

Counters ask that bundled face for its **tabular figures**
(`fontFeatureSettings = "tnum"`), which is what `oal.css` does with
`font-variant-numeric: tabular-nums`. They are read by comparing one
reading against another, and proportional digits make a changing count
appear to jitter sideways.

### The hollow glyphs were the phone, and four builds went looking in the app

Worth recording in full, because the mistake was in the method rather
than in any line of code.

For several versions this screen drew some text as dark, double-walled
letters with a light edge, mixed in among lines that drew correctly — a
text field's label hollow while the value in the same field was solid.
The cause was **Samsung's *High contrast fonts*** (Accessibility →
Visibility enhancements), a system feature that strokes an outline around
text it judges too low-contrast to read. The device was drawing over the
app.

Three changes were shipped chasing it, and none of them could have
worked: a monospace family (0.8.2), `fontFeatureSettings = "tnum"`
(0.8.3, removed in 0.8.6, now back), and the bundled family above
(0.8.7). Each cost a build, an install and a screenshot to learn one bit,
which is the wrong way round.

What settled it was a **test card** (0.8.8, since removed) that rendered
one string twenty times, holding everything constant and varying one
property per line. Its answer was not ambiguous:

| varied | result |
| --- | --- |
| family — bundled, default, sans, serif, monospace | no difference, all hollow |
| size — 12, 16, 22 sp | no difference, all hollow |
| weight — 400, 500, 700 | no difference, all hollow |
| background — ground, panel, panel2 | no difference, all solid |
| **colour** | near-white and pure white **solid**; muted, accent, 50% grey **hollow** |

Ordered by contrast against the `#111317` ground it is monotone —
`#FFFFFF` 18.6:1 and `#F4F6F8` 17.2:1 solid, `#62D1A6` 9.9:1, `#9EA8B3`
7.7:1 and `#808080` 4.7:1 hollow — with no exceptions anywhere in the
card. Nothing in Compose behaves that way. A text colour is a fill, and a
fill does not become an outline because the colour got darker.

Two lessons, both cheap next time:

- **A fault that tracks contrast rather than any property you set is the
  system.** Ask what the device is doing before changing what the app
  asks for.
- **A control has to be a control.** Another app on the same phone
  rendering normally was taken as evidence against a system cause; it was
  black text on white, which that feature leaves alone, so it proved
  nothing. The comparison needed low-contrast text on a dark background.

Anyone keeping High contrast fonts switched on will still see outlines
here — that is the feature working, on an app whose muted grey and mint
sit below its threshold by design.

**Underruns are shown as time, and counted per stream.** `PcmRing` counts
frames and the ring outlives any one sender, so the screen once read
`10451 packets · 17856 underruns` — a per-stream count beside a
since-launch one, which looks like more silence than audio and is really
372 ms of padding inside a minute of music. `resetCounters()` is now
called when a stream starts, separately from `clear()`, which throws away
audio mid-stream and must not erase the evidence of the stumble that
caused it.

**The marks are placeholders, drawn in `Glyphs.kt`** as vector paths
rather than fetched: they tint with the theme, add no dependency to a
build that already fetches one binary over the network, and can be
replaced wholesale when there is a real visual language. The launcher icon
is an adaptive icon in the Hub's palette — `--bg #111317`, `--accent
#62d1a6` from `oal.css` — so the two halves of the project look related.

One thing the Spotify tile deliberately does **not** carry is anything
resembling Spotify's logo. That mark is a trademark and this project has
no licence to draw it; the tile shows a generic broadcast glyph, and the
word beside it is a factual statement of what the feature talks to.

## Reading the node's numbers about this phone

An hour of the Hub's sample log, with the phone producing to two speakers,
splits cleanly into two regimes. The discriminator is `fillMinMs` — how
empty the consumer's buffer got between samples.

| | good stretch | poor stretch |
| --- | --- | --- |
| `fillMinMs` | 98–197 ms | 0–13 ms |
| gaps over 200 ms | 9 in 600 s | **1 262 in 930 s** |
| underruns | 2 | 264 |
| phase error | 0.6–2.1 ms | 10–68 ms |

**The number that says where to look is that both speakers reported the
same thing.** 1 262 gaps on one, 1 264 on the other, over the same
fifteen minutes, on radios eight decibels apart. Two receivers do not
agree to within a fifth of a percent by coincidence: whatever caused those
gaps happened once, upstream of both of them.

Upstream of both is this phone or the air between it and them, and
**nothing at either end could tell those apart** — the node knows when a
packet arrived, and only the producer knows when it was sent. So the
producer measures it too now, in the firmware's own buckets, and
*Show details* prints both. Read together they answer it in one line:
gaps at both ends means this app stalled; gaps only at the node means it
paced correctly and the network clumped the packets on the way, which
nothing here can fix.

One thing the packet counts already rule out: the phone kept sending
**6 000 packets per 30-second sample throughout**, which is the full
200 per second. It was not going quiet. Whatever produced those gaps
delayed packets rather than skipping them.

**The measurement came back, and it cleared the producer.** Over 38
minutes — 455 919 packets — the phone's own sending was late by more than
three packet intervals just **33 times**, worst 34 ms, and **never once by
more than 50 ms**:

```text
sending:       455919 packets · 0 underruns · 0 resyncs
sent unevenly: 33 gaps · worst 34 ms
               <20 22 · 20-50 11 · 50-100 0 · 100-200 0 · >200 0
```

The speakers, over the same kind of interval, report 41 gaps **over
200 ms every thirty seconds**. So the packets leave this app on time and
arrive in clumps, and the nodes are not asleep either — the firmware sets
`WIFI_PS_NONE`. Whatever bunches them is between the socket and the
speaker.

That is what `expedite()` in `WifiBinding` addresses: the audio socket is
marked **DSCP 46, expedited forwarding**, which Wi-Fi's WMM maps to the
*voice* access category. Voice contends for the medium with a much shorter
window than best effort and is not held back to be aggregated into a
larger frame — and aggregation is precisely the mechanism that turns an
evenly paced stream into quarter-second bursts. Unmarked, this audio had
been competing as ordinary background traffic all along.

It may not be the whole answer. An operating system is free to ignore the
marking and a network without WMM certainly will, so this is the cheapest
strong candidate rather than a proven cure — but the measurement has at
least moved the search off the producer, where three rounds of work had
been aimed.

**And the overnight run does not settle it.** Both runs were clean, but a
sleeping house is an uncontended channel, which is exactly the condition
predicted to look like this whether or not the marking does anything. The
figures are *suggestive* — 0.04 gaps per 30 s against 0.45 in the best
quiet stretch measured before the change — and suggestive across
different hours on different traffic is not proof. The test that would
settle it is deliberate: play to both speakers, then start a large upload
from the phone.

Two changes went in with the measurement. The sending thread now asks for
`THREAD_PRIORITY_URGENT_AUDIO` — `Thread.MAX_PRIORITY` is very nearly a
no-op on Android, where the Java priorities are squeezed into a narrow
band of nice values, and −19 is the band the platform's own audio threads
run in. And the firmware stops counting deliberate silence as a stall: see
below.

## Deliberate silence is not a stall

`SilenceGate` means a paused track puts a real hole in the arrival stream,
and the node was counting those as stalls — which is how a healthy link
came to report 66 517 ppm with a worst gap of sixteen seconds. Sixteen
seconds is somebody pausing the music.

**Firmware 0.55.0** carries this. A node still on 0.54.0 keeps counting
pauses as stalls, so its `arrivalGaps` and a 0.55.0 node's are not the
same measurement and must not be compared.

RFC 3550 already has the word for it: the marker bit on an audio profile
marks the first packet after a silent period. The consumer now passes it
to `oal_rtp_stats_on_marked_packet`, and a gap that ends in a marked
packet is counted as `deliberateGaps` instead — kept, because "the
producer went quiet 2 535 times" is worth knowing, just not under the
heading *stalls*, and kept out of `maxArrivalGapTicks` so the lifetime
maximum stops reporting the longest anybody left the music paused.

## Staying in touch

Two speakers and a turntable played, and then kept dropping off the list —
reappearing on *Look again*, **unticked and silent**. Two separate faults
wearing one symptom.

**The app forgot the choice.** `refreshSpeakers` read the ticks off the
speakers it already had and re-applied them to the ones it could currently
see. A device that fell out of the liveness window vanished from that
list, and with it the only record that anybody had chosen it. A choice is
a fact about a device, not a property of whichever devices happen to be
visible in the last thirty seconds, so it now lives in its own set — and
is written to preferences, so an app the system reclaims mid-party comes
back pointed at the same speakers.

**And the window is easy to miss.** Multicast on Wi-Fi goes out at a low
basic rate, unacknowledged and unretried, and a phone's power save batches
what does arrive. Six lost announces in a row is thirty seconds, which is
the entire liveness window — so a speaker sitting there perfectly healthy
can disappear because the air was busy. Three things now push back:

- **A ticked speaker that goes quiet is not removed.** It stays on the
  list marked "not answering", and **it stays in the destination set**, so
  the audio does not stop for a device that is very often still there.
  Sending to an address that has gone costs one unicast stream that nobody
  receives.
- **A unicast status request counts as being heard from.**
  `PeerTable.answered()` refreshes a peer's timestamp without an announce.
  A `GET /status` that succeeds over TCP is much better evidence than a
  multicast datagram that happened to survive, and it works precisely in
  the case that caused this: a healthy device whose announces are being
  eaten by the air.
- **A probe goes out early**, at ten seconds of silence rather than after
  the device has gone. The protocol has one for exactly this — every
  device replies at once instead of waiting up to five seconds for its next
  scheduled announce — and it is rate-limited to one every five seconds,
  because a probe makes the whole network answer.

## Publishing is not playing

Pressing *Publish to Spotify* used to start the packet counter
immediately, before Spotify had been opened. Nothing was broken — the ring
pads an empty read with silence so the pacer always has something to send,
so the phone was streaming digital silence at 200 packets a second to
speakers nobody had asked to play anything. It also destroyed the only
signal a person had that anything was working, because the counter ran
whether or not Spotify ever connected.

`SilenceGate` splits the two, and the app shows them as **Published** and
**Playing**:

1. *Publish* starts librespot and the sender. The cast point appears in
   Spotify's device list; the wire stays empty and the screen says
   "waiting 13m · nothing sent". (It says *how long*, not how many packets
   were held: "155895 packets held" reads like a fault, and it is thirteen
   minutes of nothing at 5 ms a tick.)
2. Somebody picks it in Spotify and presses play. Audio reaches the ring.
3. The gate opens, the resuming packet carries the RTP marker bit, and the
   packet count starts moving.

Two details that are easy to get wrong and impossible to hear until two
speakers disagree:

- **The media clock keeps running while the sender is quiet.**
  `RtpStream.skip()` advances the timestamp by the frames the silence
  covered. A producer that resumed with the timestamp it left off with
  would be claiming the pause never happened, and a consumer placing
  itself on the sender's timeline would seat the new audio exactly as far
  in the past as the pause was long.
- **The sequence number does not move.** Sequence counts packets on the
  wire and a receiver reads a gap in it as loss, so burning numbers on
  packets deliberately not sent would report the producer's own silence as
  a broken network.

## Sources

One interface, `AudioSource`, producing 48 kHz interleaved stereo float
PCM into a ring. It knows nothing about RTP and the sender knows nothing
about decoders.

**Test tone.** No permission, no file, no account. It separates "is the
network right" from "is the decoder right" when a speaker is silent, which
is the same reason the firmware carries one.

**The phone's library.** The picker filters on `audio/*` and Media3 does
the decoding, so the supported set is ExoPlayer's own extractors plus the
phone's own decoders — no decoder extensions are bundled. In practice:
**MP3, AAC** (M4A, MP4, ADTS), **FLAC, WAV, Ogg Vorbis, Opus** (in
Matroska everywhere, in `.ogg` from Android 10), **AMR** and **Matroska**.
Not ALAC, which needs a device decoder few phones have; not WMA; not
AIFF; not DSD.

Two things every file goes through, both from decision 13's one wire rate:
it is **resampled to 48 kHz**, so a 96 kHz file becomes a 48 kHz stream,
and it is **folded to 16-bit** on the way. The wire is still L24 and an
ordinary 16/44.1 recording is untouched by the second of those, but this
is not a high-resolution path and should not be described as one.

That 16-bit fold is forced, and the reason is a bug worth recording.
`DefaultAudioSink.configure()` builds two different pipelines and the
audio-processor chain you supply is **only in one of them**:

```java
if (shouldUseFloatOutput(pcmEncoding)) {
    pipeline.addAll(toFloatPcmAvailableAudioProcessors);
} else {
    pipeline.addAll(toIntPcmAvailableAudioProcessors);
    pipeline.add(audioProcessorChain.getAudioProcessors());
}
```

`shouldUseFloatOutput` is `enableFloatOutput && isEncodingHighResolutionPcm(...)`
— 24-bit, 32-bit and float. This app asked for float output, so a 24-bit
FLAC took a path containing neither the resampler nor the tap: nothing was
resampled and nothing ever reached the ring. From outside that is a track
that plays silently forever with the screen saying "published, waiting"
and no packet ever sent — a decoder fault wearing a network fault's
clothes. Float output is now off, every file takes the int path, and
`SonicAudioProcessor` would have refused float anyway: it accepts
`ENCODING_PCM_16BIT` and throws on everything else.

Keeping 24 bits would mean resampling here instead of in Media3 — a
general rate converter rather than the fixed 147:160 the Spotify source
uses — which is real work and not something to do by accident.

Media3 decodes and `SonicAudioProcessor` resamples
to 48 kHz — which matters more than it sounds, because decision 13 fixes
one wire rate and most music is 44.1 kHz, so *every* ordinary track is
resampled. A `TeeAudioProcessor` after the resampler is the tap. The
player still runs a real audio sink because that is what paces the
decoder, so the phone's own volume is set to zero: the speakers play, the
phone does not.

**Internet radio** arrives at exactly that interface, and did: `RadioSource`
is mostly paperwork around a `LibrarySource` pointed at a URL. **MP3, AAC
and FLAC**, which Media3 decodes over HTTP with its own network stack, plus
a real HLS client for the segment playlists the Hub deliberately refuses.

The Hub's `RadioSource` is a week of work by comparison and the difference
is not skill: Windows decodes FLAC only in its own container, ships no Ogg
demuxer, and its MP3 reader wants a seekable stream — which a station is
the opposite of. Android's decoders were written for streaming.

**One thread owns the player**, and finding that out cost a shipped crash.
ExoPlayer binds to the Looper of whichever thread built it and then
asserts that every later call arrives from the same one; break it and you
get `IllegalStateException: Player is accessed on the wrong thread`.
`RadioSource` resolves a playlist on a background thread and started the
player from there, so the first `setMediaItem` was rejected and an
uncaught exception on a bare thread closed the app the moment anybody
pressed Play on a station.

The local-file path escaped only because the file picker's callback runs
on the main thread — luck, not design — and the same rule would have been
broken from the other end eventually, since `Producer` stops a finished
source from a coroutine on `Dispatchers.IO` and `release()` from there is
the identical fault. The confinement now lives in `LibrarySource`, where
the player does, so callers may start and stop a source from wherever
they like.

What is not free is resolving what somebody pasted, so `StationPlaylist`
is ported from the Hub with its reasoning intact: half of what people call
a stream URL is a few lines of text naming the real one, the content type
decides rather than the extension, and **a live stream is never read as
text**, because reading one line of it consumes audio and blocks until
more arrives. Stations are stored on the phone rather than the Hub — this
app exists to work at a party where there may be no Hub at all — in the
Hub's own shape, so exchanging the lists later is a transfer rather than a
translation.

**Spotify Connect.** librespot runs as a process with `--backend pipe` and the app reads raw PCM from its stdout —
the same arrangement the Windows Hub uses, because librespot is a Rust
program rather than a library with a Java binding. The pipe is also the
flow control: stop reading and the kernel buffer fills, which blocks
librespot, so nothing needs a rate limiter.

Spotify is 44.1 kHz, so this is the one source that goes through
`RationalResampler` — a port of the Hub's, the same 147:160 polyphase FIR,
so the phone produces the same 48 kHz the Hub does rather than a second
one nobody could hear the difference in and everybody would have to reason
about.

**librespot needs somewhere it is allowed to write, and Android's default
is not it.** With `--disable-audio-cache` librespot streams each track
into a temporary file from the `tempfile` crate, which asks Rust for
`std::env::temp_dir()`:

```rust
env::var_os("TMPDIR").map(PathBuf::from).unwrap_or_else(|| {
    if cfg!(target_os = "android") { "/data/local/tmp".into() }
    else { "/tmp".into() }
})
```

`/data/local/tmp` belongs to the shell, not to apps. So on a real phone
librespot authenticated, accepted the play command, resolved the track,
and then could not create the file to download it into:

```text
! Unable to load encrypted file: PermissionDenied,
    PathError { path: "/data/local/tmp/.tmpIyyh2o", code: 13 }
! Skipping to next track, unable to load track
  Loading <Father Figure - Remastered> …
! Unable to load encrypted file: PermissionDenied …
  Not playing next track because there are no more tracks left in queue.
```

Every layer above it worked and nothing was ever going to come out. The
app now sets `TMPDIR` to its own cache directory in librespot's
environment. Note the `cfg!` is compile-time: patching `env::consts::OS`
to `"linux"` for the OAuth client ID does not move this, and the fix had
to be an environment variable.

This is also the clearest case yet for putting librespot's own lines on
the phone. Nothing about the packet counters, the discovery heartbeat or
any state this app can see would ever have named `/data/local/tmp`.

**librespot's mDNS name is the cast point.** It is named after the phone,
because at a party "Anna's phone" tells four people in a room which device
is which.

**It must sign in once before it appears at all.** Spotify clients do not
offer unclaimed zeroconf devices — see `LIBRESPOT.md`, proven over
loopback — so the app has a "Sign in to Spotify" button that runs
librespot with `--enable-oauth`, opens Spotify's own page in the phone's
browser, and catches the redirect on `127.0.0.1:5588` on the same handset.
No password is typed into this app and none is stored; what lands is a
credential in app-private storage, which is reusable playback access to a
real account and never leaves the phone.

That sign-in is a **separate run of librespot**, because it prints the
authorisation URL to stdout — the stream the pipe backend fills with PCM.
The sign-in run sends audio to `/dev/null` so stdout carries the URL.

**Signed in is not published, and the two get confused because the words
sound the same.** Signing in claims the cast point against an account; it
does not put a receiver on the network. The receiver exists only while
librespot is running, and the sign-in run is killed the moment it has
earned the credential — so between "Signed in" and pressing *Publish to
Spotify* there is nothing to find, and looking in Spotify's device list at
that moment correctly finds nothing. This cost an evening on a real phone:
sign-in succeeded, the app said so, and the device list stayed empty
because nothing was publishing. 0.6.3 says it on the screen — "Signed in —
but nothing is published yet" — and, while publishing, shows librespot's
own last line, because "Authenticated as …" is the one sentence that
separates a receiver Spotify has not listed yet from one that never
connected.

**Connect playback needs Spotify Premium.** librespot cannot stream on a
free account, but it *will* sign in on one, claim the cast point, and
appear in the device list looking exactly like an account that can — and
then play nothing. Said on screen before the first attempt, because
otherwise it is indistinguishable from a fault in this app.

**librespot's own lines are on screen while publishing**, the last six of
them, with warnings and errors marked. Six rather than one: the first
version showed only the most recent line and what arrived was `failed
filling up next_track during stopping: Invalid state { context is not
available }`. That is real — it comes from librespot's `handle_stop`, and
it means the stop handler found no context to fall back to — but it is
what happens *after* a session fails, not why. Whether librespot ever
printed "Authenticated as …" is called out separately, above the lines,
because everything else hangs off it.

**The cast point belongs to the account that signed it in**, so a guest on
a different account will not see it. A shared household account covers a
home; **Spotify Jam** covers a party, with guests joining the host's
session and adding to the queue. "Forget account" clears the credential
before handing the phone on.

## One build, and what that costs

Decision 21: **one APK**, with the producer, the control surface, the
resampler and librespot in it. A two-flavour split was tried and dropped —
two artefacts and two names, so that a person had to know which was the
real app, is ceremony rather than safety for a project with one operator.

The honest consequence: decision 19's protection is **gone**, not
relocated. Shipping librespot in the artefact everybody installs takes the
operator's licensing decision for them. What is left in its place is that
the app is on no store, carries no Spotify branding, and says plainly what
is inside — which is weaker than a build that cannot do it, and was traded
deliberately, because a feature nobody can reach protects nobody.

The binary is not in this repository. `librespot-android.yml` builds it on
demand and publishes it on its own `librespot-android-v*` tag; Gradle
fetches it and checks it against the SHA256 that release publishes,
refusing one that publishes no hash — `get-librespot.ps1`, in Gradle.

Three things Android forces, and none is obvious:

- **It ships as `liblibrespot.so` and is not a library.** Android refuses
  to execute a file from an app's data directory (W^X, since Android 10)
  but will execute one from the APK's native library directory. Anything
  matching `lib*.so` in `jniLibs` lands there.
- **`useLegacyPackaging = true`** goes with it. The modern default keeps
  native libraries compressed inside the APK, which is fine for something
  you `dlopen` and useless for something you `exec` — there is no path to
  hand `ProcessBuilder`.
- **`--no-default-features`**, or it will not cross-compile: the default
  audio backend wants ALSA and the default TLS wants OpenSSL. The `pipe`
  backend is compiled unconditionally and `rustls-tls-webpki-roots`
  carries its own certificates, so nothing needs a C library.

### About "24-bit"

The wire is 24-bit. A 16-bit 44.1 kHz file resampled to 48 kHz is still
that file — the format adds no information. What it avoids is throwing any
away between the phone and the speaker.

## Installing it

**The artefacts carry their versions in their names** — `openaudiolink-phone-0.7.5-debug`, `testnode-esp32s3-0.55.0`,
`OpenAudioLink-Hub-win-x64-0.104.0` — and the APK inside is named the same
way. They did not, and a build list where every entry reads
`openaudiolink-phone-debug` cannot say which is which: an APK already on a
phone looks exactly like a new one, and somebody told a version had been
built quite reasonably concluded it had not.

Sideloading, not a store. Download `openaudiolink-phone-debug` from a CI
run's artifacts, enable "install unknown apps" for whatever transfers it,
and install. Successive builds share the applicationId and the debug key,
so each replaces the last in place.

**The signing keystore is never committed.** Android requires a signed
APK, and a self-signed key is enough — but that key is the same class of
secret as the Wi-Fi credentials this project already keeps out of the tree.
Anyone holding it can publish an update that a phone installs over this one
without warning. Release builds fall back to the debug key so a build works
out of the box; supply a real one through `~/.gradle/gradle.properties` if
you want reproducible updates on your own device.

## Status

`core` is written and tested: 104 tests covering the header field by field,
byte order, the sequence and timestamp wraps, the pacing cases above, the
ring, the silence gate and the skipped-time accounting, discovery parsing,
the peer table's liveness and its second liveness channel, every control request body, the
device-versus-Hub rule, and the resampler.

`app` **works on a phone, and CI produces the APK**, beside the node firmware and
the Hub built from the same commit — there is no store listing yet, so
those artefacts are how this app reaches a phone.

A Play listing is now planned rather than ruled out; `docs/PLAY-STORE.md`
holds the plan, what has to be fixed before a first upload, and the one
decision already settled — the applicationId is `se.valfrid.openaudiolink`
and cannot change once published. **The APK stays regardless.** It is how
a build reaches a handset five minutes after a commit, and a store
release cycle is far too slow to debug a speaker with.

It is **built by CI rather than by hand** because the container it was
written in cannot reach `dl.google.com`: the egress policy denies it, so
neither the Android SDK nor AndroidX and Media3 (published only to
Google's Maven, not Maven Central) could be fetched there.

Two things the first builds caught, both worth knowing about:

- `android:Theme.Material.DayNight` is not a platform theme. The DayNight
  variants are `DeviceDefault` and arrived in API 29; this app supports
  26, so it is light in `values` and dark in `values-night`.
- Plugin versions live in `settings.gradle.kts` under `pluginManagement`,
  not in the root build file. The Kotlin JVM and Kotlin Android plugins
  ship in one artefact, so a root `apply false` put it on every project's
  classpath and `:app` asking for its own version was refused. Declaring
  versions in `pluginManagement` resolves nothing until a project applies
  the plugin, which is what lets `:core` build where Google's Maven is
  unreachable at all.

Every Media3 API used was checked against the 1.4.1 sources rather than
recalled — `TeeAudioProcessor(AudioBufferSink)` and its two callbacks,
`DefaultRenderersFactory.buildAudioSink(Context, boolean, boolean)`,
`DefaultAudioSink.Builder` with `setEnableFloatOutput` and
`setAudioProcessorChain`, and `SonicAudioProcessor.setOutputSampleRateHz`.
One detail that matters and is easy to get backwards: a
`DefaultAudioProcessorChain` applies the processors it is given **before**
its own silence-skipping and speed adjustment, so the tap sees audio that
has already been resampled to 48 kHz.

### What has actually run

**0.1.0 reached a phone**, and found the speakers: discovery worked on the
first try. Two faults came out of that one screenshot, and both are worth
recording because neither was visible from the code.

- **Every HTTP request was refused before it left the handset.** Android
  blocks cleartext HTTP from targetSdk 28, and every node endpoint is
  plain HTTP on 41001. Discovery is UDP, so it worked; volume, room
  correction and stream control were all dead, and the app reported that
  as "this speaker's firmware has no volume control" — blaming working
  firmware for its own missing manifest flag. Fixed in 0.2.0 with
  `usesCleartextTraffic`, and the two messages are now different
  sentences.
- **The Hub was listed as a speaker.** It announces
  `["controller","producer"]` — the same producer role a turntable
  announces — so a list filtered on consumer-or-producer offered to play
  music at a Windows PC. Roles cannot separate them; the port can, and
  four tests now pin it.

**0.6.x reached a phone and signed in to Spotify.** On a Galaxy A8 (2018),
Android 9, with the phone acting as the hotspot and no other device on the
network at all — a deliberate smoke test, not a party.

- **The app crash-looped before drawing anything.** `client.probe()` ran
  on the main thread inside the service's `onCreate`;
  `NetworkOnMainThreadException` killed the process, `START_STICKY`
  brought it back, and it died again. Fixed by drawing the screen first
  and deferring every socket call to a coroutine.
- **It then looked frozen, and was not.** With no devices on the network
  three buttons were legitimately disabled and "Look again" had no visible
  effect, which is indistinguishable from a hang. Fixed with an on-screen
  heartbeat — interface, datagrams heard, announces and probes sent — so a
  quiet network reads as quiet rather than broken.
- **Two of the app's own rules made the smoke test impossible.** Sources
  required a ticked speaker, and starting a stream required station Wi-Fi
  — which a phone *hosting* the hotspot does not have. Both removed:
  publishing to nobody is how a party starts.
- **The sign-in "failed" on a five-minute timeout** invented here, which
  expired while a person was reading an emailed code. The wait now has no
  deadline and a Cancel button.
- **`redirect_uri: Not matching configuration`.** librespot picks its
  Spotify client ID from `std::env::consts::OS`, so cross-compiling for
  Android selected the Android client ID, which has no loopback redirect,
  and there is no `--client-id` option. The build now patches that
  constant to `"linux"`; `librespot-android-v0.8.0-2` carries the fix and
  sign-in completes.

**It works.** 0.7.2, a Galaxy A8 (2018) on Android 9, a home network with
the Hub on it: Spotify picked the cast point, the phone produced the
stream, and **sound came out of a speaker**. The counters from that
session:

```text
discovery: on wlan0 · heard 2424 · announced 283 · probes 3
sending: 63839 packets · 0 underruns · 0 resyncs
librespot: signed in to Spotify.
<Jesus to a Child> (410746 ms) loaded
```

63 839 packets is five and a half minutes of continuous audio. **Zero
underruns** says the ring never ran dry, so librespot's bursts and the
5 ms wire never disagreed for even one packet. **Zero resyncs** says the
pacer never fell far enough behind to give up and re-anchor — on a phone,
with a foreground service holding it awake, over Wi-Fi. That is the whole
pacing argument above, measured rather than asserted.

Two warnings librespot printed in that same session, both benign and both
worth knowing, because they are the same fault twice:

```text
! couldn't load context info because: context is not available. type: Default
! Invalid start position of 613867 ms exceeds track's duration of 410746 ms,
  starting track from the beginning
```

A transfer arrives carrying a playback position but the context does not
resolve, so librespot loads a track from the session rather than the one
Spotify was actually playing — and then the position from the old track
does not fit the new one. It recovers by starting from the beginning, so
the visible cost is that **transferring playback to this cast point
restarts the track** instead of resuming where you were. It is inside
librespot's Connect state machine, not in anything here.

**Overnight, 8–9 September.** Two unbroken runs on a quiet house network,
with the Hub at 0.105.0 logging `deliberateGaps` for the first time:

| | Spotify, 65 min | Internet radio, **251 min** |
| --- | --- | --- |
| gaps over 200 ms | 0.12 per 30 s | **0.04 per 30 s** |
| underruns | 12 | 192 |
| resyncs | 3 | 33 |
| phase error, median | 1.6 ms | 1.6 ms |
| loss | 106 ppm | 1 070 ppm |
| `ssrcChanges` | 0 | 0 |

Four hours and eleven minutes of continuous radio, three million packets,
one sender throughout. **That is the crash fixed and internet radio
working on hardware**, and it is also the longest unbroken run this
project has ever measured.

`deliberateGaps` read **2 and 1** across the whole night, which settles
something left open: the 41-per-30-seconds seen the previous evening were
real stalls, not the silence gate being counted as one.

Two things worth not glossing over. The **loss in the radio run is ten
times the Spotify run's** — 1 070 ppm against 106 — and though 0.1 % of
isolated losses is inaudible, it is unexplained. And the **two speakers
are not equal**: over the same run, `Stereo` recorded 152 gaps over 200 ms
to `Speakers`' 21, and 25 re-primes to 2. That is one node's radio link,
not the producer, and it is the kind of thing a house has rather than a
bug a build can fix.

**Still unproven**, and each needs hardware rather than code:

1. `GET /stream` on the receiving node — `lastSsrc` changing and no
   `foreignPackets` would say the packets are being accepted as a real
   stream rather than tolerated. Sound coming out proves rather a lot, but
   not this.
2. Two speakers at once, and listening for the offset the whole
   synchronisation design exists to remove.
3. A track from the phone's own library, which exercises the Media3
   decoder and the 16-bit int path rather than librespot's pipe.
4. The vinyl node's *Play* button, which has still never driven
   `POST /stream/start` on a producer node.
