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

**Look at the Speaker sync panel first.** It sits above the device list
whenever two or more Consumers are playing, and it does the one
subtraction that matters.

The offset between two Consumers **is** the difference in their buffer
depths. Nothing in this design says when a given sample is due — no
presentation timestamp, no shared clock — so each node plays as fast as
its own DAC asks, from whatever it holds. Same packets, same nominal rate:
whichever holds more is playing older audio, by exactly that much.

So the panel shows each speaker's depth, the line it is steered to, and
the spread between them. Under about 10 ms two speakers read as one
source; by 20 the image smears; past 40 it is an echo.

| Column | What it tells you |
| --- | --- |
| Buffered | the depth, and its distance from the steering line |
| Steering to | the same number on every node with the same ring and delay — a node far from it is the one that moved |
| Primed at | where it started. Two nodes should agree here |
| Burst dropped | overshoot discarded at prime. Before 0.33.0 this was silently kept, and whatever a burst delivered became that node's offset for the session |
| Trims / pads | how much correcting it has needed |

**A large spread that will not close** used to be the normal case and is
now the interesting one. Before firmware 0.33.0 there was no correcting
force at all between `pad_below` and `trim_above` — 150 ms on a 400 ms
ring — so any offset acquired inside that band was permanent, and the only
cure was restarting the stream. Since 0.33.0 both nodes prime at the same
line and are steered back to it at about 0.5 ms a second, so **give a
disagreement a few minutes before restarting anything.**

If the spread stays wide while both nodes report depths close to their
steering line, then depth is not the whole story on your network and the
packets are reaching the two nodes at systematically different times.
That is a different fault and worth saying so, because everything above
assumes it is not happening.

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
