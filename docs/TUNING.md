# Tuning the jitter buffer

Reference for the two settings that decide whether audio arrives in time,
and how to read the counters that say whether it did.

How these values were arrived at is run 34 in `docs/LINK-MEASUREMENTS.md`
and decision 17 in `docs/DECISIONS.md`. This page is only what to set and
what it does.

## The short version

**Use the Buffer… button on a device row and pick a profile.** Everything
below is what the profiles are made of, and is worth reading only if one of
them needs changing.

| profile | ring | target | rests at | behind the room | survives a gap of |
| --- | --- | --- | --- | --- | --- |
| Short | 200 | 100 | 112 ms | ~132 ms | 112 ms |
| **Standard** | **400** | **200** | **225 ms** | **~245 ms** | **225 ms** |
| Long | 1000 | 550 | 618 ms | ~638 ms | 618 ms |

Standard is what every measurement in `LINK-MEASUREMENTS.md` from run 34
onward describes, and on a clean channel it is enough. **Long** is for a
link that cannot be fixed: it is sized so its cushion exceeds the worst
arrival gap this project has recorded (419 ms, run 39), and run 40
measured an audible interruption every six minutes on Standard with almost
all of the gaps causing them under 200 ms. Run 42 then removed most of
that population by changing channel, and Standard now runs at about one
interruption every half hour — so **fix the air before reaching for
Long**.

**Short** is the firmware's floor and is realistic only on a wired
backhaul; it is for video, where lip-sync beats robustness.

Set every speaker in one room to the same profile. Any per-node alignment
offset is kept and added on top, which is the reason to use the profile
button rather than the two knobs underneath it.

**A profile change usually needs a reboot**, because the ring is an
allocation. The Hub stores the choice, sets the ring, and applies the
matching delay by itself once the node comes back with it — the button
reads `Buffer Standard → Long…` while that is outstanding.

### The two knobs underneath

| setting | default | Standard | what it buys |
| --- | --- | --- | --- |
| `ringMs` | 200 | **400** | room to absorb a burst |
| `delayMs` | 0 | **100** | depth, and per-node alignment |

Still settable individually from the **Ring…** and **Delay…** buttons, which
is how the profiles above were arrived at. Setting either by hand takes the
node off its profile, so the Hub stops correcting it.

## The two knobs are not the same knob

This is the distinction everything else follows from.

**`ringMs` is capacity.** How much audio the buffer can hold at all. It is a
PSRAM allocation made once at boot, 50–1000 ms. It costs memory and nothing
else: 400 ms is 154 kB of the 8 MB on a XIAO ESP32S3.

**`delayMs` is depth.** How full the buffer normally runs, added to a 100 ms
compiled default. It is what makes the speaker lag the room, and it is the
only thing that does.

A useful way to hold it: capacity decides whether a *burst* fits; depth
decides whether a *gap* is survivable. They fail differently and the
counters tell them apart.

- A gap longer than the depth starves the speaker → `underruns`,
  `silenceFrames`, `latePackets`.
- A burst larger than the free capacity overflows → `droppedFrames`, and the
  *oldest* audio is discarded, which is a jump rather than a stretch.

Raising `ringMs` does nothing for underruns. Raising `delayMs` does nothing
for overflow. Both were measured.

## What actual latency you get

**Not the target.** The fill rests at `1.125 × target`.

> **This section said `1.5 ×` for several releases, and was wrong.** That
> was true before firmware 0.38.0: nothing pulled the buffer back down, so
> it rode the trim line at 1.5 × target. Steering replaced that with a
> servo that parks the fill at the middle of the quiet band, and this page
> was never revisited — so every latency figure here was **a third too
> high**, and the project believed it was running 320 ms when it was
> running 245. Corrected against run 40, where a 200 ms target reported
> `steerMs` 225 and both nodes measured a median fill of 225 ms.

Padding pushes up from below at `0.75 × target`, trimming caps it from
above at `1.5 × target`, and the steering loop holds it at the midpoint of
the two — which belongs to neither a fast crystal nor a slow one, and is
the same depth on any two nodes sharing a ring and a delay.

```
target       = 100 ms + delayMs
pad_below    = 0.75  × target
trim_above   = 1.5   × target      (capped at 7/8 of the ring)
actual depth ≈ 1.125 × target      ← the midpoint, and what decides latency
air to ear   ≈ actual depth + the output stage
```

The depth is also **how long a silent gap the node can ride out** before
the ring runs dry, which is the single number a buffer profile is chosen
on. Run 40 measured 1 329 and 1 583 arrival gaps in the 100–200 ms band
over five hours; a 225 ms cushion sits right on top of that population,
which is why Standard produced an audible interruption every six minutes
and why Long exists.

**The output stage is not the same on both**, and the difference is larger
than it looks:

| output | after the ring | why |
| --- | --- | --- |
| I²S DAC | ~20 ms | four DMA descriptors |
| USB dongle | **~100 ms** | the UAC host driver's own buffer (`buffer_size = 0`, auto), plus whatever the dongle keeps |

That gap is what `delayMs` is correcting when two speakers with different
output stages play in one room — and it is why the trim goes on the *I²S*
node, which is the early one.

To set a latency deliberately, **make the target eight ninths of the depth
you want**, then subtract the 100 ms default. Or pick a profile, which is
this table with the arithmetic already done:

| depth wanted | target | `delayMs` | needs a ring of |
| --- | --- | --- | --- |
| **112 ms** (Short) | **100** | **0** | **200** |
| 168 ms | 150 | 50 | 300 |
| **225 ms** (Standard) | **200** | **100** | **400** |
| 337 ms | 300 | 200 | 600 |
| **618 ms** (Long) | **550** | **450** | **1000** |

The ring column is not advisory. The trim line is `1.5 × target` and may
not exceed `7/8` of capacity, so **the ring must be at least 1.72 × the
target** — a stricter rule than the three-quarters one below, and the one
that actually binds. A target that breaks it is not refused; it is silently
clamped, and you run a buffer you did not choose with nothing to say so.
The profiles are checked against this before they are offered.

The delay ceiling depends on the ring — the target may use three quarters of
capacity — so a node publishes its own limit as `maxDelayMs` in `/status`
and the Hub reads it rather than assuming. With a 400 ms ring the ceiling is
200; with the 200 ms default it is 50.

## `delayMs` is also the alignment control

A USB dongle plays tens of milliseconds later than a soldered I²S DAC given
the same packet, and the difference is obvious with both in one room. The
fix is to hold the early one back — delay is only ever *added*, since
nothing can play a sample before it arrives.

So `delayMs` does two jobs at once: a **base** shared by every node in a
group, and an **offset** for whichever node plays early. Set the base
equally everywhere and add the offset on top:

```
dongle node:  delayMs = 100          (base only)
DAC node:     delayMs = 100 + 50     (base plus its measured offset)
```

Getting this wrong is quiet — the audio is fine on each node alone and
smeared when they play together. If you change the base, change it on every
node, or the offsets between them move too.

**Split, as of the latency profiles — but only in the Hub.** The node still
has one `delayMs` and knows nothing about the two jobs. The Hub keeps the
per-node offset in `node-audio.json` and sends `profile.delayMs + alignMs`,
so raising a room's depth no longer disturbs anyone's alignment.

Latency profiles are what forced it. A profile writes `delayMs`, so without
somewhere to keep the offset, choosing one would silently discard it — and
that failure is exactly the quiet kind described above, with no counter
anywhere able to report it, because alignment is not something a node can
measure about itself.

The consequence worth knowing: **set the offset through the profile
button** (or `POST /api/devices/{id}/profile` with `alignMs`). Setting
`delayMs` by hand still works and still means what it always did, but it
takes the node off its profile, and the Hub then leaves it alone.

## Two speakers that will not stay together

**Read the "N ms apart" figure in the Speaker sync heading. Under 20 ms is
together; past 40 is an echo.** That number is the real offset, measured
rather than inferred, and it is the only figure on the page you have to
read to answer "are my speakers in sync".

Each node reports the RTP timestamp it is currently playing. Those come
from the one sender, so two nodes can be compared directly without their
clocks agreeing about anything, and the Hub carries both readings to a
common instant before subtracting. Unlike buffer depth it does not move
when a burst arrives, because a burst changes what has been *received*,
not what is being *played*.

**Judge it on the typical figure, not the instant.** The heading also shows
what the offset has typically been over the last minute, and that is the
one to read. The live number occasionally spikes — a reply delayed on the
network carries a stale position, so the node it came from reads as that
far behind. The round trip is measured and slow replies are thrown away,
but an occasional one gets through. **That is the page being late, not the
speakers moving.** Playback itself only shifts on a pad, a trim, a re-prime
or an overflow, none of which move 60 ms in a moment.

**The check that settles it without any argument: did a counter move?**
Playback phase can shift only four ways — a padded frame, a trimmed frame,
a re-prime after an underrun, or an overflow discard — and every one of
them increments something. So an offset that jumps while `underruns`,
`droppedFrames` and the late count all stay put **did not happen**. The
audio cannot move without leaving a trace, and if nothing moved, the number
is wrong rather than the speakers.

Confirmed in exactly that way: a run showing jumps to 60 ms and once 200
had zero lost and zero late packets throughout. Nothing in the audio path
had touched the phase, so the reading was the only thing that could be at
fault — and it was.

Everything below is why the other numbers on that panel are not that, and
can be skipped unless one of them looks alarming.

**Buffer depth is not the offset.** An earlier version of this section said
it was, and that was wrong in a way worth spelling out, because the Speaker
sync panel is easy to misread.

Depth is *(newest sample received)* minus *(sample now playing)*. A burst of
packets raises the first without touching the second, so on a busy channel
the number swings tens of milliseconds in seconds while nothing audible
changes at all. Two speakers reading 90 ms apart may be perfectly together.

**What actually moves a speaker's playback phase** is short: a padded frame
(plays one sample slower), a trimmed frame (one faster), a re-prime after an
underrun, or an overflow discard. Everything else is arrival, not playback.

So read the panel this way:

| Column | What it tells you |
| --- | --- |
| Buffered | how much audio is waiting. Swings with the network; not the offset |
| Settles at | the trim line, where the fill comes to rest. Identical on nodes sharing a ring and delay |
| Primed at | where each node started. **These should match** — if they do not, priming is not landing them together |
| Burst dropped | overshoot discarded at prime, so a burst cannot become a permanent offset |
| Trims / pads | **the column that matters.** Each one is a phase shift. Thousands per hour is a loop fighting the network, and it walks speakers apart |

**A speaker sitting at the top of its ring is the real fault to look for.**
The ring then discards its oldest audio to make room, and a discard is a
phase jump. Raise `ringMs` on that node: the trim line does not move, so
this buys burst headroom without adding any latency. With a 400 ms ring the
trim line sits at 300 and leaves only 100 ms of headroom, and stalls of
287 ms have been measured on this network.

**Two speakers resting at opposite ends of the band.** Measured on hardware:
one node with **0 trims** sinking to the pad line, the other with **58 970**
pinned at the trim line, 137 ms apart and stable. That is what a pair of
crystals straddling the sender's rate does — a fast one drains until padding
holds it up, a slow one fills until trimming holds it down — and the gap
between them is the quiet band itself.

Firmware 0.38.0 closes it with a slow correction toward the middle of the
band, driven by a **forty-second average** of the fill rather than its
instantaneous value. A burst moves the average almost not at all; a clock
error moves it steadily, so the correction sees the one and is blind to the
other. It spends about 13 frames a second, 0.027 %, and closes a 137 ms
disagreement in a little over two minutes.

If two speakers are more than about 20 ms apart and staying there, **give it
three minutes before doing anything else.**

**What was tried and reverted, so it is not tried again.** Firmware 0.34.0
padded the fill up toward a line 13 ms below the trim, reasoning that a
shared setpoint would hold two speakers together. It made two speakers
markedly worse and they never settled. The gap between the pad line and the
trim line is not a missing correction — it is how much jitter the buffer
absorbs without the servo touching the audio, and the fill swings about
35 ms either side of its mark on working hardware. A 13 ms band put every
ordinary burst across a threshold, and since every crossing spends a frame,
and every spent frame is a phase shift, the cure moved speakers apart faster
than anything moved them back. One node logged 100 792 trims against 34 287
pads in three hours. Reverted in 0.35.0; keep the band wide.

## Two speakers that fall out of step, and how they get back

**One speaker is a different problem from two, and the second one is the
hard one.** A single speaker riding high or low is inaudible — nothing to
compare it against — and a rare hiccup passes. Two speakers a tenth of a
second apart is a slap echo in the room, and it lasts until the loop walks
them back together.

Fill difference *is* phase difference. Both speakers receive the same
packets, so `newest received` is the same for both, and
`playing = newest − buffered`. Two nodes at 198 ms and 319 ms of fill are
121 ms apart in what you hear, exactly.

**What used to make that last minutes.** The steering creep moves phase at
one frame in four chunks — about **1 ms per second** — and the rate was the
same whether a speaker was 5 ms out or 90. Measured against the runs:

| fills | apart | before | from 0.40.0 |
| --- | --- | --- | --- |
| 198 / 319 ms | 121 ms | 85 s | ~55 s |
| 26 / 394 ms | 368 ms | 126 s | **~1 s** |

If disturbances arrive more often than the recovery takes, the speakers
never converge at all — which is what "with frequency we end up 100 ms
apart" means.

**Two changes, and the second one has a hard limit worth understanding.**

The creep is now proportional: about 4 ms/s past a quarter of the target,
2 ms/s past an eighth, and the original 1 ms/s near home. **The rate near
the setpoint is untouched**, because that is what stops the loop dithering
and 0.34.0 is the record of what happens when it is disturbed.

Past `RESYNC_MS` the speaker stops walking and steps — one discontinuity,
counted as a **step-back** in the sync panel. A click on one speaker beats
three minutes of slap echo, and only one of those is a choice anybody
would make twice.

**Why the step-back threshold cannot simply be lowered.** It is 120 ms,
and it must stay above the sender's 100 ms catch-up burst. A burst reaches
every speaker in the same instant, lifts both fills equally, and leaves
the difference between them untouched — so it is absorbed with nothing
audible happening. Stepping back on it would put a click in *both*
speakers to fix a disagreement that never existed. That is why the 121 ms
case above still takes ~55 seconds: at 94 ms from the setpoint it is under
the threshold by design, and the proportional creep is all that can help
it. The host test asserts `resync_above > 100 ms` so nobody lowers it
without meeting that argument.

The threshold is a floor rather than a constant: the quiet band grows with
the target, so at 650 ms of delay it takes whichever is larger.

**If step-backs keep appearing**, the fault is not the one this fixes. A
node collecting them is a node whose link keeps knocking it out of step,
and the loss shape and Wi-Fi drop counters are where that shows.

## The sample log: reading a run instead of photographing it

From Hub 0.70.0 the Hub writes one CSV row per node every 30 seconds to
`samples/oal-YYYY-MM-DD.csv` in its data directory. **Get it from the
admin page: the "Diagnostics log" section lists a file per day with a
Download link**, which is the whole point — a log nobody can find is a log
nobody sends. Fourteen days are kept; a night is about 1 400 rows per node
and well under a megabyte. Behind it, `/api/samples` lists and
`/samples/<name>` serves.

**It asks no node for anything.** Every value comes from readings the Hub
already had — `NodeClockService` fetches the whole `/stream` document and
used to discard most of it, and `DeviceStatusService` polls `/status`.
Adding a second poller to a device with seven sockets is what produced the
HTTP 502s, and a log file is not worth repeating it for.

**Why it matters more than convenience.** Every diagnostic mistake
recorded on this page was a *saturated lifetime counter*: `WorstStallMs`
reading 145 for ever after one bad moment, `maxArrivalGapTicks` carrying
4 445 ms across stream restarts so a clean night still showed a
four-second gap, `framesPlayed` divided by an uptime that included hours
of idling. Each was fixed by adding a per-window twin, and each time the
next lifetime counter caught somebody out.

A series makes every one of them per-interval **by subtraction**, so the
class of bug stops existing rather than being fixed one counter at a time.

It also makes this page's own rule — *did a counter move?* — answerable
across time. From a screenshot it is not: the offset jumped thirty seconds
ago and the panel shows now. With rows you can line a step-back up against
a Wi-Fi drop, a garbage collection and a send stall, and say which came
first.

Each row is self-contained, carrying the Hub's own counters alongside the
node's, so filtering to one speaker still shows what the sender was doing
at that moment. `playingTimestamp` and `rttMs` are both logged, so the
offset between two speakers can be computed afterwards — with the same
round-trip correction the live panel makes — rather than taken on trust
from a number that has already scrolled away.

## Reading a node's Wi-Fi drops, and what to do on the router

**Where the number is.** The **Devices** table, the **Wi-Fi** column, on
that node's row. It shows the SSID and channel, the BSSID underneath, and
then — only when the count is non-zero — a line like
`3 drops (access point asked it to leave) · 2 roams`. From 0.68.0 the
same reading is repeated beside a long loss burst in the link table, since
that is where somebody is looking when the question occurs to them.

**A count of zero is a result, not a blank.** A node that went silent for
seconds while its association held throughout did not leave the network,
so the gap is in the path to it: the air, or a mesh backhaul it never
sees. That points somewhere different from a disconnect and the panel now
says so rather than repeating the advice.

**The code is the node's own account and is not guessed. Read the split
at 200 before reading the code itself.**

| range | who ended it | where to look |
| --- | --- | --- |
| **1–99** | the **access point** sent a frame ending it | the router |
| **200+** | the **node** stopped hearing the access point | signal, position, backhaul |

Below 200 these are 802.11 reason codes, which arrive *in a frame from the
access point* — something decided to end the association and told the node
so. From 200 up they are Espressif's own, raised by the node's own stack
when nothing was said to it and it simply stopped hearing. Those are
opposite faults with opposite fixes, and the range says which without
any guessing.

| code | means |
| --- | --- |
| 3 | the access point deauthenticated it |
| 4 | idle too long |
| 8 | the access point disassociated it |
| 15 | four-way handshake timeout — wrong password |
| 200 | beacon timeout — it stopped hearing the access point |
| 201 | no access point found |

### On an Asus ZenWiFi AX (AiMesh)

**Roaming assistant is the one to look at, and it ships enabled.**
*Advanced Settings → Wireless → Professional → Band: 2.4 GHz →
Roaming assistant*, where it reads **"Disconnect clients with RSSI lower
than −70 dBm"**. That does exactly what it says: the router deliberately
ends the association, and the node is off the air for as long as it takes
to find and rejoin an access point — seconds, not milliseconds.

It also buys a speaker nothing. Roaming assist exists to move a client
toward a better access point; a speaker is screwed to a wall, and on
reconnect it frequently lands on the very access point it was just thrown
off. **Set it to Disable**, or if a house genuinely needs it, put the
threshold somewhere no working node ever reaches — −85 or lower.

Check the node's RSSI in the **Signal** column first. A node sitting near
−70 is one this setting will pick off repeatedly; a node at −55 is
untouched by it, and that asymmetry is why one speaker can drop all night
while the other loses nothing.

**Smart Connect** — one SSID across both bands with steering — causes the
same class of fault, since an ESP32-S3 is 2.4 GHz only and can gain
nothing from being steered. Separate SSIDs per band avoid it entirely.

For reason 200, the node stopped hearing beacons, which the router is not
doing to it. Check its RSSI and its BSSID against the other node's — if
they are on different access points, and one of those is an AiMesh
satellite on a **wireless** backhaul, that node's audio crosses the air
twice. This series has measured what that costs (runs 23 and 35), and it
is the first thing to change: wire the satellite, or move the node onto
the root.

**One change per night, and it is not pedantry.** A measurement here costs
a night, so two changes at once buys a result that cannot be attributed to
either. Photograph the Professional page before touching it.

**What not to change, and why the usual advice misses.** Disabling both
beamforming settings is commonly suggested and neither can produce a
multi-second dropout: beamforming shapes signal quality, so it shows as
retries, marginal RSSI and throughput — never as a deauthentication.
They are also not equivalent to each other:

- **Explicit Beamforming** is 802.11n/ac sounding, and the client must
  send channel feedback for it to work. An ESP32 does not, so it never
  engages for these nodes. Turning it off changes nothing here and costs
  the phones and laptops throughput.
- **Universal Beamforming** is Asus's name for *implicit* beamforming,
  inferred from received frames with no client participation. This one
  does reach the nodes, so it is the one worth a night — but only after
  Roaming assistant, and only if that did not settle it.

Then, regardless of the code: fix the 2.4 GHz channel to 1, 6 or 11
rather than Auto, so a nightly channel scan cannot move it — and check
the two access points are not on the same one, which has quietly turned a
channel comparison into a topology comparison here twice.

**This one is now measured, not advised.** Run 39 read the channel off the
nodes themselves: both mesh points on **channel 3**, which overlaps 1 and
6 and is therefore the worst of the three settings this paragraph warns
about. The node on the busier of the two took 12.7× the trims, 19× the
underruns and every step-back in the run, at −49 dBm with no disconnects —
so signal strength cannot explain it and the air can.

Run 40 moved the band to **channel 6** and the speaker-to-speaker offset
went from a median of 7 ms to **4 ms**, p90 29 → 15. Do this before
touching the buffer.

**Run 41 found that neither 3 nor 6 fits a whole house**, and run 42 found
the one that does. Measured per node, per access point:

| node | ch 3 | ch 6 | **ch 11** |
| --- | --- | --- | --- |
| Speakers, on `…0b:d0` | 28.3 underruns/h | 1.3/h | **0.6/h** |
| Stereo, on `…13:b1` | 1.6/h | 25.4/h | **1.2/h** |

Channels 3 and 6 were each excellent for one mesh point and bad for the
other; both times the house was tuned to one number and one speaker paid
for it. **Channel 11 is good for both at once**, with roughly seven times
fewer arrival gaps on each node, and it took the speaker-to-speaker offset
to a median of **2 ms with 98.8 % of readings inside 20 ms** — the best
this project has measured.

So the advice is now specific rather than general: **try 11 first.** If it
is occupied where you are, the per-node table above is how to tell which
of the three a house can live with, and which speaker is buying the
compromise.

Two things survive from those runs regardless of channel. **Signal
strength predicts nothing**: run 42 had Stereo at −56 dBm turning in 1.2
underruns an hour, where on channel 6 the same node at −46 turned in 25.4
— ten decibels better signal, twenty times worse audio. And **a matched
pair synchronises better than a good node and a poor one**, sync being a
differential quantity; channel 11 is the first configuration to make both
nodes good at the same time, which is why its offset beats every earlier
run rather than merely one of them.

The router's own view is under *System Log → Wireless Log* (clients and
their RSSI) and *System Log → General Log* (association events). Read it
against the node's count: the node knows why it left, the router knows
whether it asked.

## Before tuning anything, rule out the hardware and the air

Two faults have masqueraded as buffer problems, and both wasted more time
than the tuning did.

**A power supply can pull the playback clock by hundreds of ppm.** The
sync panel's Clock column reports each node's playback rate against the
sender. Two identical boards on identical firmware measured **+5 ppm and
−178 ppm** — and swapping only the power supplies between them moved the
error to the other board within minutes. A crystal should sit inside ±50;
beyond a couple of hundred, suspect the supply before the board, and the
swap is the test that settles it.

The loop corrects it either way — that −178 ppm node held 4 ms of offset
all night — so this is about knowing which hardware to distrust rather
than about rescuing the audio.

### Measuring the clocks, and why pads and trims cannot do it

Until 0.55.0 the Clock column was computed from pads and trims:
`(pads − trims) / framesPlayed`. The algebra is sound — the sender
delivered R frames, the DAC consumed P, and `R = P + trims − pads` — and
the figure was still useless, for a reason worth keeping.

**A pad or a trim is a phase correction, and corrections have two causes
that the counters cannot separate.** One is a crystal running at the wrong
rate. The other is the buffer recovering from a disturbance. The second is
enormously larger: a single 100 ms catch-up burst forces about 4,800 frames
of trim, which over a one-minute window reads as **1,600 ppm** — while a
real crystal error is tens. There is no window both short enough to be
responsive and long enough to bury a disturbance that big. Corrections
also arrive *after* their cause, so a window catches the recovery without
the event, and the column swung by thousands of ppm while the hardware sat
there doing nothing wrong. Two speakers reading −936 and −998 at the same
moment came from exactly this, and it prompted a hunt for two failing
crystals that did not exist.

Common-mode subtraction (0.53.0) helped and did not fix it. Subtracting
the median removes a disturbance both nodes felt equally, but the quantity
being measured was still corrections, not clocks.

**From 0.55.0 the clocks are measured directly, and the ring is not in it
at all.** A node's `framesPlayed` is its I²S sample counter; the Hub's
`packetsSent` follows the send loop's `Stopwatch`, including across a
capped catch-up. Both are sampled against one common observer — the
browser — and fitted by least squares over up to half an hour of polls.
The browser's own clock error lands in both fits and **cancels exactly in
the subtraction**, leaving the node's crystal against the Hub's.

Nothing in that path touches the buffer, so a burst, a stall, a trim or a
re-prime does not move it.

Two details it needs to be usable:

- **Least squares, not the two ends.** Each reading is stamped a round
  trip away from when the counter was really read, so an endpoint pair
  inherits the timing error of two particular samples. Simulated with
  ±40 ms of jitter, endpoint differencing over half an hour is worth about
  ±440 ppm — worthless for a figure whose interesting range is ±50. The
  fit over ~360 samples recovered a true +70 ppm as **+71**.
- **Measured by the Hub, not by the page.** Until 0.59.0 the fit lived in
  the browser, and the browser is the wrong house for it: closing the tab
  threw every sample away, so a measurement needing tens of minutes
  restarted each time somebody opened the panel — and opening it is the
  only reason it exists. On a phone it never got that far, since a
  backgrounded tab has its timers throttled and the surviving samples were
  too sparse to fit at all. `NodeClockService` now polls each consumer's
  `/stream` every 10 s, fits against the Hub's own `Stopwatch` — the same
  counter `RtpStreamer` paces from, so the node is measured against the
  very clock its buffer must keep up with — and serves the result on
  `/api/clocks`. No second fit, no subtraction, no browser in the path, and
  it keeps running with every page closed.
- **The window and the poll interval are one decision, not two.** The
  gate compares the fit's own uncertainty against 25 ppm, and that
  uncertainty depends on both. Cutting the poll from 10 s to 30 s — a
  change made to spare the nodes, and right on its own terms — tripled the
  sample spacing and pushed a 30-minute window to **24.8 ppm** against
  that gate. Sitting on the threshold, the column showed a figure, crossed
  back, and read "settling" again. The window is an hour from 0.67.0,
  which brings the same conditions to about 8.8 ppm and costs no extra
  request to any node. **Change either one and re-check the arithmetic.**
- **Gate on the fit's uncertainty, not on the clock.** 0.55.0 printed the
  figure once it had three minutes of samples, and three minutes is worth
  roughly ±130 ppm of noise in the slope alone — so it jumped by a hundred
  between refreshes while looking perfectly confident. 0.56.0 computes the
  standard error of the slope from the residuals and stays on "settling"
  until it is inside **±25 ppm**, half the ±50 band a healthy crystal sits
  in. Measured from the data, so a quiet network earns the figure sooner
  and a noisy one waits; hovering shows the current ± and how long it has
  been fitting.

### A counter that undercounts reads exactly like a slow crystal

The first thing the new column found was a bug in itself. Two speakers
reported about **−4054 and −3836 ppm** against the Hub, steadily, for an
hour — and both were fine.

The refutation was on the same screen. A node running 4,000 ppm slow
accumulates that much surplus audio and must discard it: **691,200 trims
an hour**. The nodes showed 144,548 and 190,474 *lifetime*, with buffers
sitting on the 225 ms setpoint, neither draining nor filling. The trims
are a physical consequence and cannot be argued with, so the ppm figure
was wrong by roughly eight times.

The cause was in the playout task. Each write to the sink counted
`written / 8` frames and threw the remainder away — every call, not once
per chunk. A frame is eight bytes and the driver is under no obligation to
stop on one, so a chunk split across two writes lost most of a frame, and
a trailing write of four bytes counted as nothing at all. Silent,
permanent, and one-directional: `framesPlayed` could only run slow.

Fixed in firmware 0.39.0 by counting once, from the bytes the chunk
actually moved. On the ordinary path it is now exact however the driver
splits the write.

**The lesson worth keeping is the second one.** Nothing was watching the
measurement, so the column now refuses a reading it cannot believe: past
**±2000 ppm** it says **counter faulty** rather than printing a crystal
error that does not exist. Quartz does not drift by a fifth of a percent,
and a genuine error that size would be audible as pitch rather than only
visible as a number.

The first version of that guard was wrong and shipped, which is worth
recording too. It compared `framesPlayed` against the node's *total
uptime*, on the reasoning that a node playing since boot should have
played `uptime × 48000` frames — but a node that sat idle before the
stream started has legitimately played less than its uptime. Thirty
seconds of idle in an hour is a ratio of 0.9917, inside the suspect band,
so the check meant to catch a leaking counter fired on **every ordinary
node**. A rate cannot be fooled that way: the Hub's fit only accumulates
while a node is playing, so idle time is absent from it rather than mixed
into it. The judgement lives in the Hub with the measurement, not in the
page that draws it.

The noise falls as the square root of the sample count. Simulated against
five-second polls with ±40 ms of timing jitter, and checked against a
known +70 ppm difference:

| Window | Fit | Its own ± | |
|---|---|---|---|
| 3 min | +92 | ±66 | settling |
| 5 min | +53 | ±32 | settling |
| 10 min | +70 | ±11 | shown |
| 15 min | +81 | ±7 | shown |
| 30 min | +69 | ±2 | shown |

So expect roughly ten minutes with the page open before the column says
anything, and treat it as a measurement that is made once and then trusted
— not a live readout.

When the Hub is not the producer there is no local reference, and the
column falls back to comparing the speakers with each other. That can say
which one disagrees; it cannot say which one is right.

### What a shared drift is, and what it is not

It is **not the source's clock**, and that guess cost an evening. The
source cannot pull the send rate at all: `RtpStreamer` paces from a
`Stopwatch` — the sending PC's own clock — at the nominal rate, and
`RadioSource` decodes, resamples to the Hub's rate and writes into a ring
its producer blocks on at a 1.5 s high-water mark. A fast station therefore
backs up against that mark and a slow one makes the Hub send silence and
count `UnderrunSamples`. Neither moves the RTP timestamp rate by one part
in a million. Internet radio, a soundcard, a capture device — none of them
can do this, so do not go looking there.

What can, and did: **the send loop catching up.** When the send thread has
been away, `due` runs ahead of `packetsSent` and the loop sends every owed
packet back to back, capped at `MaxCatchUpPackets` — 100 ms of audio in one
burst, to every endpoint, inside the same loop. Each node's buffer jumps by
the whole burst at once and any node near `trim_above` emergency-trims.
4800 frames inside a 30 s measurement window is 3300 ppm, so a *partial*
trim on both nodes is more than enough to read as −1000 ppm on both. This
is common mode by construction: identical packets, one loop.

So when the column goes deep negative on every speaker together, read
`lateWakes` and the recent stall and send figures on `/api/stream`. They
separate the two faults that share the symptom: **stall high with send time
low** means the thread was not scheduled — GC, a busy machine, power
management — and **send time high** means it was inside `SendTo`, which on
a Wi-Fi host is an adapter blocked by a background scan.

### Buffering at the Hub, and what it cannot fix

Radio keeps **five seconds** of decoded audio ahead of the sender from
0.54.0 (`RadioSource.Charge`, ring one second larger so a decoded block
cannot lap it). The cost is latency, paid in full: five seconds between
the station and the speakers, and the same again before a change of
station is heard. Free for radio, which is already well behind live.

It stays a `RadioSource` constant deliberately. The same five seconds on
line-in or librespot would put five seconds between the needle and the
sound.

**It protects against one failure only: the source running dry.** It does
nothing for a sender whose loop is not running, and the two are easy to
confuse because both sound like a dropout. They are distinguishable, and
the counters already do it:

| | Hub sends | Node sees | Counter |
|---|---|---|---|
| Source dry | packets on time, carrying silence | nothing wrong — clean counters, no gaps | `UnderrunSamples`, "Source ran dry" in the log |
| Send loop away | nothing at all | an **arrival gap** | `lateWakes`, recent stall / send ms |

So before adding buffer, read which one is happening. Measured here: 13,299
and 15,835 arrival gaps with zero loss — the second row, where no amount of
buffering helps.

The Play button counts the cushion down, −5 s to −1 s, from the fill the
Hub reports rather than from a timer started on the click. A fixed
countdown reaches zero just the same while a station fails to open, and
announces a readiness that never happened.

### Does the source change how well the speakers agree?

It can, but only through the sender, and it is worth being clear why.
Every speaker gets **byte-identical packets at the same moment** — same
SSRC, same sequence, same timestamps, written in one loop. A source cannot
reach one speaker and not another. What it can do is change *the sender's
timing*, and that becomes a differential offset one step later: a
catch-up burst lifts both buffers by the same ~100 ms, whichever speaker
was already nearer its trim line crosses it and trims, and a trim moves
that speaker's playback phase while the other's stays put. Same burst,
one speaker moves.

So the question "is Spotify worse than radio for sync" reduces to "does
the Hub stall more under Spotify", and these differ in ways that make it
plausible:

| | Internet radio | Spotify (librespot) |
| --- | --- | --- |
| Source cushion | **5 s** (`RadioSource.Charge`) | **500 ms** (`LibrespotInstance.HighWater`) |
| Work on this PC | HTTP fetch, decode, resample | all of that **plus** a separate librespot process doing network, decryption and Vorbis decode |
| Reader thread | dedicated | dedicated |

The 500 ms was chosen as "about double the worst stall measured over that
hour", and the worst stall measured since is **265 ms** — so it is no
longer double anything. Its own comment says the number is too small if
underruns survive it, and `UnderrunSamples` on `/api/stream` is what
answers that.

**Do not settle this by reasoning.** From 0.62.0 the sender reports
`recentGcPauseMs` per window alongside the stall and send figures, and a
collection is the one pause no thread priority escapes — the runtime
suspends every managed thread, the send loop included. Play radio, read
the Hub line in the sync note; play Spotify, read it again. If the stall
and the collection figures rise together under Spotify, the cause is
allocation in that pipeline and the fix is there. If they do not, the
source is not the difference and something else is.

### The catch-up cap no longer fits the ring it was sized against

Measured in the house, two nodes, one stream: **zero packet loss** —
146,369 of 146,369 on one, 146,370 of 146,370 on the other — jitter of
1.27 and 1.54 ms, and 13 and 14 late packets in twelve minutes. Nothing
wrong with the air at all. And yet 13,299 and 15,835 *delivery stalls*,
worst case 265 ms and 259 ms.

Two independent radios in different rooms agreeing on a worst case within
six milliseconds of each other, with no loss and no jitter to speak of, is
not a Wi-Fi fault. Packets that were never sent arrive nowhere, together.
**The sender was not sending for a quarter of a second at a time.**

Then the arithmetic that turns that into trims. `MaxCatchUpPackets` caps a
catch-up burst at 100 ms, and its rule is that the cap must fit under the
receiver's headroom above where the ring rests. When it was written, the
ring rested at the target — on a 400 ms ring with 100 ms delay that is
trim_above (300) − target (200) = 100 ms, and the cap fitted exactly.

Firmware 0.38.0 then added steering, which parks the ring at the middle of
the quiet band instead: `(pad_below + trim_above) / 2` = **225 ms**. The
headroom became 75 ms. The cap stayed at 100. Nobody revisited it, because
the two constants live in different repositories and neither mentions the
other.

So every full-size burst now lands the node at about 325 ms — above its
300 ms trim line — and it trims one frame per chunk until it is back down.
The panel showed exactly this: both buffers jumping **+102 and +106 ms in
the same instant**, both above the trim line, followed by ~2,900 and
~1,400 trims in thirty seconds. That is a Clock reading of a couple of
thousand ppm, on two boards with nothing whatever wrong with them.

The invariant is `cap ≤ trim_above − steer_to`. Do not fix it by dropping
the cap to 15 packets: the warning on that constant still stands, and a
cap below the length of a real stall turns a burst the ring could have
absorbed into a gap that no ring can. **Fix the sender's stalls and the
cap never fires.** The other lever, if the stalls prove unfixable, is to
bias `s_steer_to` below the midpoint — that buys burst headroom at the
cost of underrun margin, and this system is measurably short of the first
and not the second.

Stacked on top of any of it is a measurement artifact worth knowing. The
window starts empty when the page is opened, so the first figure ever shown
is exactly 30 s wide, and a single catch-up burst inside those 30 s
dominates it completely. Bad figures that "improve after a while" are the
window filling, not the speakers recovering.

**A failing power adapter on one node.** It presented as that node's buffer
swinging wildly while its partner sat still, and as a lopsided trim count —
one node working many times harder than an identical one beside it. The
supply feeds the Wi-Fi radio in transmit bursts, so a sagging rail becomes
a radio problem, and a radio problem becomes a buffer problem two layers
later. **Swap the adapter before touching `ringMs` or `delayMs`.**

**Two access points on one channel.** Different access points are not
different air. 802.11 is CSMA/CA, so two access points on the same channel
defer to each other's frames and share the medium whoever is transmitting;
a mesh backhaul on that channel crosses it twice. A node behind an extender
therefore stalls far worse than one on the root, with identical hardware
and firmware.

**The tell for both is asymmetry.** Two identical nodes running the same
image on the same network should show similar counters. When one shows four
times the stalls, or twenty times the trims, that difference is not a
property of the design — it is telling you the two nodes are not in the same
situation. Look for what differs physically before changing a setting that
applies to both.

## Reading the counters

`curl http://<node>:41001/stream`, or the switchboard's stream table.

Normalise **per packet**, not per run — the counters keep different epochs.
`/stream/stop` clears the consumer's statistics but not the playout's, and
`framesPlayed` runs from boot including idle silence. Divide playout figures
by `packetsSubmitted` and nothing else.

### The ones that matter

| field | means |
| --- | --- |
| `latePackets` | arrived to an *empty* ring. The speaker was already playing silence. **The headline number.** |
| `tightPackets` | arrived with under a quarter of the intended cushion. The warning ahead of the fault. |
| `droppedFrames` | discarded because the ring was full. Should be zero. |
| `silenceFrames` / `underruns` | inserted because it was empty, and how many separate times. |
| `marginBuckets` | the distribution of cushion at arrival, in five bands. |
| `arrivalGaps` / `maxArrivalGapTicks` | delivery stalls, and the worst one. Ticks ÷ 48 = ms. |
| `trimmedFrames` / `paddedFrames` | the servo walking the fill down or up. |

`latePackets` is the one to judge by, and the only one comparable across
configurations. **`marginBuckets` percentages are not comparable between
different targets** — the bands are fractions of the target, so a bigger
target makes the top band a harder test. 98.7 % full cushion against a
200 ms target beats 99.4 % against a 100 ms one.

### Two counters that lie

**`payloadErrors` is meaningless unless the source is the synthetic test
pattern.** It compares every sample against that pattern, so real music
reads as tens of millions of errors on a perfectly healthy node.

**`jitter` is blind to stalls.** It is an RFC 3550 exponentially smoothed
average, each sample moving it by a sixteenth, so a 1.12-second hole once
read as 3.21 ms of jitter. Use `arrivalGaps` for stalls; jitter only
describes the ordinary spread.

## If it is still not clean

1. **Late packets, drops near zero** → not enough depth. Raise `delayMs`,
   equally across the group.
2. **Drops climbing** → not enough capacity for the bursts. Raise `ringMs`,
   then reboot the node.
3. **Both, with a huge `maxArrivalGapTicks`** → a network event, not a
   buffer problem. Nothing reasonable buffers a 2.5-second stall; look at
   roaming, interference and access-point placement instead. Check whether
   the stall coincided with a track change first — `arrivalGaps` cannot tell
   a stalled network from a stopped sender.
4. **`lost` above zero** → genuinely dropped by the radio, which no buffer
   setting fixes. Through the whole of run 34 this stayed at zero; every
   fault was a packet that arrived intact and too late.

## Where the limits come from

The socket queue holds 64 packets — **320 ms** — so that a stall's worth of
packets can be held rather than dropped. A ring smaller than that cannot
accept what the queue saved, and the burst at the end of a stall overflows.
That is why 400 ms is the first sensible size above the 200 ms default, and
why 200 ms was the wrong size for this network.

Related settings, all in `firmware/testnode/sdkconfig.defaults` with their
reasoning: `CONFIG_LWIP_UDP_RECVMBOX_SIZE`, `CONFIG_LWIP_SO_RCVBUF` (which
must be set, or `setsockopt` fails silently),
`CONFIG_ESP_WIFI_DYNAMIC_RX_BUFFER_NUM`.
