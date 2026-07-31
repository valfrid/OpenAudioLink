# Cast points

Status: design, not implemented

## The goal, in the user's words

> If running Spotify on a phone one could select to cast to kitchen,
> livingroom, ..., all house.

That is the experience to preserve. It is not the same as being a
Chromecast, and conflating the two leads somewhere that cannot be built.

## Why not simply be a Chromecast

Google's Cast SDK offers two receiver types, Web Receiver and Android TV
Receiver, and both run on Google-certified hardware. The SDK licence
grants the right to build *senders* that interoperate with Cast receivers,
not to build receivers. Third-party "Chromecast built-in" speakers reach
that status through Google's certification program using Google's own
firmware, which is not available to implement against. Reverse-engineered
receivers exist, but the applications that matter require device
attestation, so they do not work with them. Chromecast Audio, the product
closest to this project's purpose, was discontinued in 2019 and never
replaced.

So being a Cast target is a non-goal. What the user actually asked for
survives that intact, because the interesting part was never the protocol.

## The model

**A cast point is a name and a set of consumers.**

```
CastPoint {
  id            "kitchen"
  name          "Kitchen"          -- what the phone shows
  destinations  [device ids]       -- one, or twelve
}
```

That is the whole concept. A zone is a cast point with one consumer; a
group is a cast point with several; "House" is a cast point containing
everything. There is no separate group type, no nesting, and no
membership hierarchy to reason about, because none of those earn their
complexity: the producer already replicates one byte-identical packet to
N unicast destinations, which is what a group *is* at the transport
layer.

Each cast point is advertised on the network under its own name, so the
phone sees "Kitchen", "Living room" and "House" as separate targets in
whatever picker it already uses. Selecting one starts a stream to that
cast point's destinations. This is the whole of the feature.

## What advertises them

Deliberately undecided. The model above is protocol-independent, and
committing to a protocol before the model is built would be the same
mistake as chasing Cast.

| Route | What the phone sees | Cost |
| ----- | ------------------- | ---- |
| Spotify Connect (librespot) | one Spotify device per cast point | unlicensed reimplementation; Premium required; one app |
| AirPlay 2 (shairport-sync) | one AirPlay speaker per cast point | every app on Apple devices; weaker on Windows |
| UPnP/DLNA renderer | one renderer per cast point | open standard, poor UX |

The pattern is the same for all three: **one receiver instance per cast
point**, each advertising its own mDNS name, each feeding raw PCM to the
Hub, which packages it as RTP and sends it to that cast point's
destinations. Adding a protocol is adding an adapter, not changing the
model.

For a public repository the right shape is a Source interface at the Hub
with these as pluggable implementations, documented rather than bundled.
Whether a particular receiver is licensed for a particular service is the
operator's decision to make, not something to embed in the project.

## What this actually costs

**Sample rate.** Spotify is 44.1 kHz; the RTP profile is 48 kHz
(`ARCHITECTURE.md`). Something has to resample. Doing it at the Hub keeps
one clock domain across the whole system, which is worth more than the
CPU it costs — two rates in one house means two drift problems instead of
one.

**One stream per account.** A Spotify account plays to one device at a
time, so a cast point per room does not give different music in different
rooms from one account. This is the same limitation a Chromecast group
has, and it is a property of the service, not of this design.

**Overlapping destinations.** Two active cast points that share a consumer
would send two streams to one speaker. The Hub must refuse that, or stop
the first. Cheapest correct rule: a consumer belongs to at most one
*playing* cast point, and starting a cast point that overlaps a playing
one stops the other.

**Latency.** AirPlay 2 buffers around two seconds and Cast is comparable;
this project's own path adds about 20 ms. Fine for music, useless for
video lip-sync. If audio-follows-video is ever wanted it is a separate
design, not a tuning exercise.

**Synchronised playout.** Two speakers in one room drifting apart is far
more audible than one speaker drifting alone, so a cast point with several
destinations is what makes the clock work matter. Until that exists,
multi-destination cast points are usable but not yet correct.

## What already exists

More than it looks. The producer replicates to multiple unicast
destinations and this has been measured at length
(`LINK-MEASUREMENTS.md`). The device registry names and tracks consumers.
The Hub already starts and stops streams with an explicit destination
list. A cast point is a saved destination list with a name on it.

What is missing is the naming and persistence, the advertisement, the
receiver adapters, and the resampling.

## Order of work

1. Cast points at the Hub: create, name, choose consumers, persist, start
   and stop a stream to one. Testable with the two nodes on the bench and
   no protocol adapter at all.
2. A Source interface, with WASAPI loopback as the first implementation —
   it is already designed in `WINDOWS-AUDIO-CAPTURE.md`.
3. One network receiver adapter, chosen then rather than now.
4. Synchronised playout, which is blocked on the DAC regardless.
