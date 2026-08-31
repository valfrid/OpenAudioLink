# Tuning the jitter buffer

Reference for the two settings that decide whether audio arrives in time,
and how to read the counters that say whether it did.

How these values were arrived at is run 34 in `docs/LINK-MEASUREMENTS.md`
and decision 17 in `docs/DECISIONS.md`. This page is only what to set and
what it does.

## The short version

| setting | default | in use | what it buys |
| --- | --- | --- | --- |
| `ringMs` | 200 | **400** | room to absorb a burst |
| `delayMs` | 0 | **100** | depth, and per-node alignment |

At those values a node runs about **300 ms of buffer**, roughly **320 ms
from air to ear**, and measured 10 ppm of late audio with zero loss over two
hours.

Set both from the Hub: the **Ring…** and **Delay…** buttons on a device row,
each showing its current value. `ringMs` applies at the node's next reboot
because it is an allocation; `delayMs` applies immediately and slides in
over about half a minute.

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

**Not the target.** The fill sits at `1.5 × target`, not at the target.

Nothing pulls the buffer back down toward its aim point: padding pushes up
from below, trimming caps it from above at 1.5 × target, and bursts only
ever push upward. So the ring rides its ceiling. The target behaves as a
floor, and 1.5 × target is what decides latency.

```
target       = 100 ms + delayMs
actual depth ≈ 1.5 × target
air to ear   ≈ actual depth + the output stage
```

**The output stage is not the same on both**, and the difference is larger
than it looks:

| output | after the ring | why |
| --- | --- | --- |
| I²S DAC | ~20 ms | four DMA descriptors |
| USB dongle | **~100 ms** | the UAC host driver's own buffer (`buffer_size = 0`, auto), plus whatever the dongle keeps |

That gap is what `delayMs` is correcting when two speakers with different
output stages play in one room — and it is why the trim goes on the *I²S*
node, which is the early one.

To set a latency deliberately, **make the target two thirds of what you
want**, then subtract the 100 ms default:

| wanted | target | `delayMs` |
| --- | --- | --- |
| 150 ms | 100 | 0 |
| 225 ms | 150 | 50 |
| **300 ms** | **200** | **100** |
| 450 ms | 300 | 200 |

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

*(These being one setting is a wart. Splitting them into a shared `targetMs`
and a per-node `alignMs` would let a group's depth be raised without
disturbing anyone's alignment. Not done.)*

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

The Clock figure is a **rate over the last minute**, not an average since
boot, and the difference matters when testing a change. The cumulative
counters carry the climb from priming at 200 ms to the steering band at
220 — 960 frames of net padding that is not a crystal error and never
leaves the totals. On a node nine minutes old that alone is 37 ppm, and it
reads highest on a freshly booted node, which is exactly the node somebody
studies after swapping its supply. Hovering shows the since-boot figure
beside it; the two disagreeing is itself informative.

**Read the column across the speakers before reading any one row.** Clock
measures a node against *the sender*, so it moves when either end does. In
the house both speakers once showed about **−1000 ppm at the same moment**,
which reads as two dying crystals and is nothing of the sort: two
independent crystals do not drift together, and a real 1000 ppm error —
a tenth of a percent — would be audible as pitch, not as a number on a
page. What it says is that the source was running fast and both nodes were
trimming to keep up with it.

So the figure decomposes. What every node shares is **common mode** and
belongs to the sender; what is left after subtracting it is that node's own
error, and only that part can be blamed on hardware on the shelf. The panel
does this split from 0.53.0: it colours each row on the node's own error,
still shows the raw figure, names the common drift underneath the table
when it exceeds 100 ppm, and puts the leftover on hover. Before that split
a fast source painted two healthy boards red.

A shared drift is not itself a fault. Every node follows the sender, so
they stay with each other and the audio is fine; it is worth knowing
because it points at the source — a soundcard, a resampler, a capture
device — rather than at the speakers.

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
