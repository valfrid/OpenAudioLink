# The phone app

An Android **Producer**: it originates the RTP stream itself and carries
just enough control to get one running. Decision 19 sets the scope,
decision 20 the network rules and decision 21 the Spotify build; this is
how the thing is built and how to run it.

Version 0.6.0, built by CI as one APK — see *One build* below.

## What it does, and what it deliberately does not

| Does | Does not |
| --- | --- |
| Send L24/48 kHz stereo RTP to any OpenAudioLink consumer | OTA |
| Publish a Spotify cast point | Sample logging |
| Find speakers by multicast discovery | Room measurement |
| Choose which speakers play, mid-song | Provisioning a node's Wi-Fi |
| Volume, per speaker | Anything else a Hub does |
| Room correction on and off, per speaker | |
| Drive a vinyl node as its Controller | |

Four sources, and the first is the reason the app exists:

1. **Spotify Connect** — librespot publishes a cast point named after the
   phone. It signs in once, and then appears in the picker of the account
   that signed it in.
2. **A vinyl node** already on the network, told where to send.
3. **A music file** from this phone.
4. **A test tone**, which needs no permission, file or account.

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
          control requests, 44.1-to-48 resampling. 64 tests. No Android; runs
          anywhere with a JDK.
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

`core` is written and tested: 64 tests covering the header field by field,
byte order, the sequence and timestamp wraps, the pacing cases above, the
ring, discovery parsing, the peer table's liveness, every control request
body, the device-versus-Hub rule, and the resampler.

`app` **compiles, and CI produces the APK**, beside the node firmware and
the Hub built from the same commit — there is no store listing and there
will not be one, so those artefacts are how this app reaches a phone.

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

**Still unproven.** No packet has yet been shown to reach a speaker. The
first things to check on a real device, in order:

1. Test tone to one speaker. Proves discovery, binding, the packet format
   and the port in one step.
2. `GET /stream` on that node — `lastSsrc` changing and no
   `foreignPackets` says the packets are being accepted as a real stream
   rather than tolerated.
3. A track from the library, to check the decoder and the resampler.
4. Spotify: does the cast point appear in the picker, on this phone and on
   another one?
5. Two speakers, and listen for the offset the whole synchronisation
   design exists to remove.

Untested beyond that, and worth knowing before relying on either: the
vinyl node's *Play this* button has never driven `POST /stream/start` on a
producer node, and the Spotify source has never been run at all — whether
librespot's mDNS survives Android's network stack is exactly the kind of
thing that only a phone can answer.
