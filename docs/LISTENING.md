# Listening: the microphone role, room calibration and voice

**Status: proposal.** Nothing here is built. The microphone is on order for
`ROOM-CALIBRATION.md`'s Phase 1, and this describes what else it makes
possible and what shape the system needs to take to allow it.

## The short version

A microphone earns a **role of its own** rather than joining Producer, and
the reason is physical before it is architectural: a microphone has to be
where the *listener* is, and every other node has to be where the
*equipment* is. Those are opposite ends of a room.

Two features share the hardware:

- **Room calibration** — measure what the room does to the sound and correct
  for it (`ROOM-CALIBRATION.md`).
- **Voice** — say something and have the house do it.

They share more than the microphone, which is the interesting part: the
measurement one feature makes is exactly the measurement the other needs.

## Why a new role, and not Producer

A microphone node captures audio and sends it over RTP, which is what a
Producer does. On the wire they are indistinguishable. Everywhere else they
are not:

| | Producer | Listener |
| --- | --- | --- |
| Where it must sit | at the source equipment — turntable, PA, dongle | where a person listens, acoustically open |
| Where its audio goes | to Consumers, to be heard | to the Controller, to be analysed |
| When it runs | while something is playing | on demand, or waiting for a word |
| What it feeds | the music path | nothing anybody hears |
| If it is wrong | the music stops | a measurement lies, or a microphone listens when it should not |

The last row is the one that settles it. **A microphone is not a line in.**
A device that can hear the room deserves to be listed as such, switched off
as a class, and visible in an inventory without anybody reading a hardware
profile to work out what it is. Roles are already how this system says what
a box is for; this is a thing worth saying.

Roles are a list, so nothing stops one device holding Consumer and Listener
at once — but in practice the placement rules above forbid the combination
that would be most convenient. A microphone in the speaker is in the wrong
place for calibration by definition: it measures the speaker, not the room.

## Where the compute lives: still the Controller

Unchanged from `ROOM-CALIBRATION.md`. The Listener captures; the Controller
orchestrates and computes. A node runs a sweep and streams what it heard; the
Hub decides what to play, when, correlates the result and produces the
filters. The same split holds for voice: the node hears its wake word and
opens a stream, and everything after that is the Hub's.

This keeps the ESP32 doing what it is good at — capture, timing, one job —
and keeps the interesting arithmetic somewhere it can be debugged.

## The correction this document exists to record

An earlier note in conversation claimed a node doing echo cancellation has
the reference signal for free, because it is the device playing the audio.
**That is only true when the microphone is inside the speaker**, and the
placement rule above says it must not be.

A Listener at the other end of the room has no local reference at all. The
options are:

1. **The Hub cancels.** It knows exactly what it sent and receives what the
   microphone heard. Both are RTP with timestamps.
2. **Push to talk**, or a wake word robust enough to work over music, with no
   cancellation at all.
3. **Send the reference over the network** to the Listener. Plausible and
   unappealing: it doubles the node's traffic and asks it to align two
   streams whose relative delay it cannot measure.

Option 1 is the right one, and it is right for a reason that only appears
when the two features are considered together.

## Where the two features meet

Echo cancellation needs to know the delay and the impulse response between a
speaker and a microphone. **That is precisely what room calibration
measures.**

So the calibration run is not merely a neighbour of the voice feature — it is
its calibration step. Measure once, when the microphone is installed, and:

- the DSP gets the room correction it was measured for;
- the canceller gets the delay and response it needs to subtract music from
  what the microphone hears;
- `ROOM-CALIBRATION.md`'s acoustic delay section gets its numbers.

One sweep, three uses. If any part of this is built, that measurement is the
part to build first.

## What voice would actually involve

Two tiers, and the first is worth having on its own.

**Local commands.** "Play P3 in the kitchen." "Louder." "Stop." A fixed
grammar over the endpoints that already exist, resolved on the Hub. No
internet, no account, nothing leaving the house, and an answer in the time it
takes to run a string match. This is the version somebody would use daily.

**Open-ended conversation.** Speech to text can be local — Whisper on the Hub
is well within a desktop machine. The reasoning step is where audio or text
would have to leave the network, and that is a decision about the house
rather than about the software.

Worth stating plainly so nobody is surprised later: a conversational
assistant here would be the Hub calling a model API with its own key. It
would not be a continuation of any conversation held while building this, and
it would have no memory of the project unless deliberately given one.

## The obstacle that is ours rather than the technology's

**The streamer sends one source at a time.** A spoken reply while music plays
needs the music ducked and the reply mixed over it, and nothing in this
system can do that. `RtpStreamer` takes one `IAudioSource`, decision 2's
receiver arithmetic assumes one stream, and the cast point model says a room
plays one thing.

This is a bigger change than any of the speech handling, and it should be
decided deliberately rather than discovered:

- **A mixing source** that takes music and speech and ducks one under the
  other. Contained, and it fits the existing `IAudioSource` shape.
- **Reply without interrupting** — a chime, a GUI response, a light. Cheapest,
  and enough for local commands where the *action* is the answer.
- **Stop, speak, resume**, which is intrusive but honest and needs no mixing.

For local commands the second is likely enough: if you say "louder" and it
gets louder, the confirmation is the room getting louder.

## Placement, since it is the thing that started this

A Listener wants:

- the **listening position**, or near it — where people actually sit, because
  that is the response worth correcting;
- **acoustically open** — not in a shelf, not against a wall, not behind the
  sofa;
- **away from the speakers**, which is the opposite of every Consumer;
- **away from the equipment**, which is the opposite of every Producer.

It also wants mains power and no fan. This is a small box on a shelf at ear
height in the middle of the room, and it is worth saying out loud because it
is the one node whose position is a measurement input rather than a
convenience.

## If any of it gets built, in this order

1. **The measurement**, `ROOM-CALIBRATION.md` Phase 1. It is needed by
   everything else here and is useful on its own.
2. **The Listener role**, so the microphone is a first-class thing in the
   registry, the setup page and the docs — including a way to turn it off.
3. **Local commands**, with a wake word on the node and no cancellation:
   usable when the room is quiet, which is when people talk to their houses
   anyway.
4. **Cancellation**, using the calibration measurement, so it also works
   while music plays.
5. **Anything conversational**, last, and only after deciding what leaves the
   house.
