# The phone app

An Android **Producer**: it originates the RTP stream itself and carries
just enough control to get one running. Decision 19 sets the scope and
decision 20 the network rules; this is how the thing is built and how to
run it.

Version 0.1.0. The core is tested; the Android half has never been
compiled — see *Status* at the bottom before expecting it to work.

## What it does, and what it deliberately does not

| Does | Does not |
| --- | --- |
| Send L24/48 kHz stereo RTP to any OpenAudioLink consumer | OTA |
| Find speakers by multicast discovery | Sample logging |
| Choose which speakers play, mid-song | Room measurement |
| Volume, per speaker | Provisioning a node's Wi-Fi |
| Room correction on and off, per speaker | Anything a Hub does |
| Drive a vinyl node as its Controller | |

The right-hand column stays the Hub's. A speaker holds its own room
correction in NVS, so it travels to a party already corrected and the
phone never needs to know a measurement was made.

**The phone cannot provision.** A speaker reaches a party network only
because a Hub pushed that pair into its NVS beforehand, so "bring your
hub" is really "bring your hub, having once met mine". Letting a phone
write Wi-Fi credentials into other people's speakers is a much larger
security question than this app is worth.

## How it is built

Two Gradle modules, and the split is the same discipline the firmware uses
for `oal_phase` and `oal_netpick`:

```text
phone/
  core/   plain Kotlin on the JVM — the wire format, pacing, discovery,
          control requests. 50 tests. No Android; runs anywhere with a JDK.
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
gradle :app:assembleDebug  # needs ANDROID_HOME
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
- **An empty buffer sends silence, not nothing.** A gap is a break in the
  sender's timeline and a consumer answers a break by re-seating itself. A
  decoder stumbling for 20 ms costs 20 ms of quiet instead of a second of
  re-priming on four speakers.

## Sources

One interface, `AudioSource`, producing 48 kHz interleaved stereo float
PCM into a ring. It knows nothing about RTP and the sender knows nothing
about decoders.

**Test tone.** No permission, no file, no account. It separates "is the
network right" from "is the decoder right" when a speaker is silent, which
is the same reason the firmware carries one.

**The phone's library.** Media3 decodes and `SonicAudioProcessor` resamples
to 48 kHz — which matters more than it sounds, because decision 13 fixes
one wire rate and most music is 44.1 kHz, so *every* ordinary track is
resampled. A `TeeAudioProcessor` after the resampler is the tap. The
player still runs a real audio sink because that is what paces the
decoder, so the phone's own volume is set to zero: the speakers play, the
phone does not.

**Internet radio** arrives at this interface whenever it is built: a
station is a different `MediaItem`, same decoder, same resampler, same tap.

**Spotify, via librespot, is not in this version** and stays source-only
when it is — decision 19 and decision 18's position, unchanged: public
repository, no Spotify branding, no built APK containing it. Sources being
modules is what makes that possible; the app is still a radio, a library
player and a tone generator without it.

### About "24-bit"

The wire is 24-bit. A 16-bit 44.1 kHz file resampled to 48 kHz is still
that file — the format adds no information. What it avoids is throwing any
away between the phone and the speaker.

## Installing it

Sideloading, not a store. `gradle :app:assembleDebug` produces an APK;
enable "install unknown apps" for whatever transfers it, and install.

**The signing keystore is never committed.** Android requires a signed
APK, and a self-signed key is enough — but that key is the same class of
secret as the Wi-Fi credentials this project already keeps out of the tree.
Anyone holding it can publish an update that a phone installs over this one
without warning. Release builds fall back to the debug key so a build works
out of the box; supply a real one through `~/.gradle/gradle.properties` if
you want reproducible updates on your own device.

## Status

`core` is written and tested: 50 tests covering the header field by field,
byte order, the sequence and timestamp wraps, the pacing cases above, the
ring, discovery parsing, the peer table's liveness, and every control
request body.

`app` is **built by CI, not by hand.** The container it was written in
cannot reach `dl.google.com` — the egress policy denies it — so neither the
Android SDK nor AndroidX and Media3 (which are published only to Google's
Maven, not Maven Central) could be fetched there. The `phone-app` job on a
GitHub runner has all of them, and the APK it produces is downloadable
from the run: there is no store listing and there will not be one, so that
artefact is how this app reaches a phone.

Every Media3 API used was checked against the 1.4.1 sources rather than
recalled — `TeeAudioProcessor(AudioBufferSink)` and its two callbacks,
`DefaultRenderersFactory.buildAudioSink(Context, boolean, boolean)`,
`DefaultAudioSink.Builder` with `setEnableFloatOutput` and
`setAudioProcessorChain`, and `SonicAudioProcessor.setOutputSampleRateHz`.
One detail that matters and is easy to get backwards: a
`DefaultAudioProcessorChain` applies the processors it is given **before**
its own silence-skipping and speed adjustment, so the tap sees audio that
has already been resampled to 48 kHz.

**Nothing in `app` has run against real hardware.**

The first things to check on a real device, in order:

1. Test tone to one speaker. Proves discovery, binding, the packet format
   and the port in one step.
2. `GET /stream` on that node — `lastSsrc` changing and no
   `foreignPackets` says the packets are being accepted as a real stream
   rather than tolerated.
3. A track from the library, to check the decoder and the resampler.
4. Two speakers, and listen for the offset the whole synchronisation design
   exists to remove.
