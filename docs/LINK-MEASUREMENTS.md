# Link measurements

What the network actually does, measured node-to-node with the real RTP
profile (L24 stereo, 48 kHz, 5 ms packets, 200 packets/s, ~2.3 Mbit/s) and a
synthetic source. This exists so the transport can be designed against
numbers rather than assumptions, before any ADC or DAC is in hand.

Run these from the Hub's **Node-to-node link test** panel. The producer
streams straight to the consumers; the Hub only says who sends to whom.

## Reading the numbers

**Loss reported by the consumer is not the same as loss over the air.** The
producer's radio refuses a send when its transmit buffers are momentarily
empty, and the sequence number advances anyway — so a refused packet arrives
at the consumer's statistics indistinguishable from one the air ate. The
producer's `send errors` column is the correction term:

```
air loss = consumer lost - producer send errors
```

Every early measurement here was wrong until both ends were instrumented.
The first clean run "lost" fifteen packets that were never transmitted.

**Which access points the nodes are on decides the hop count.** Wi-Fi
infrastructure mode never lets two stations talk directly, so a packet is
always node → AP → node. When the two nodes have attached to different mesh
points it crosses the backhaul as well, which is a third hop. Two separate
comparisons in this series were quietly invalidated that way. The panel now
prints the access points each run crossed; a run that does not say
"two hops" cannot be compared with one that does.

## Results

Hardware: two Seeed XIAO ESP32-S3, ~20 cm apart. Network: ASUS ZenWiFi,
two mesh points, 2.4 GHz. Firmware 0.6.x.

| # | Channel | Width | Path | Consumer loss | Send errors | Air loss | Jitter |
|---|---|---|---|---|---|---|---|
| 1 | 9 | 40 MHz | unknown | 0.240% | not yet counted | — | 2.04 ms |
| 2 | 9 | 40 MHz | unknown | 0.111% | not yet counted | — | 1.19 ms |
| 3 | 9 | 40 MHz | unknown | 0.355% | not yet counted | — | — |
| 4 | 3 | 20 MHz | 2 hops | 0.035% | 15 | **0%** | 1.81 ms |
| 5 | 11 | 20 MHz | 3 hops | 0.707% | 0 | 0.707% | 3.10 ms |
| 6 | 7 | 20 MHz | 3 hops | 0.172% | 0 | 0.172% | 2.31 ms |
| 7 | 7 | 20 MHz | **2 hops** | 0.044% | 5 | **0.029%** | **1.54 ms** |

Runs 1–3 predate both the send-error counter and the access-point line, so
their loss figures conflate three causes and their topology is unrecorded.
They are kept only to show that power save mattered (run 1 → run 2 changed
nothing but `esp_wifi_set_ps(WIFI_PS_NONE)`).

## What the series established

**Wi-Fi power save costs packets.** Run 1 → 2: loss halved and jitter fell
by 40% from disabling it alone. A node that sleeps between beacons is not a
node that can receive a packet every 5 ms.

**Channel choice is first-order, and the usual 1/6/11 advice did not
apply.** Runs 5 and 6 share a path and an access-point assignment and differ
only in channel: 11 → 7 cut loss by 4x and jitter by 25%. A scan showed why —
the neighbouring networks sat on ~2 and ~13, leaving channel 7 as the only
20 MHz slot with clear air on both sides. Channel 11 partially overlapped a
neighbour, which is the case CSMA/CA handles worst: too much interference to
ignore, too little to hear and defer to.

**40 MHz on 2.4 GHz is not worth it.** There is no room for it here, and
the stream needs 2.3 Mbit/s out of a channel that already offers tens.

**The third hop is the dominant remaining cost.** Runs 6 and 7 differ only in
whether the nodes shared an access point: 0.172% → 0.029%, jitter 2.31 ms →
1.54 ms. Crossing the mesh backhaul costs roughly 6x the loss. This is not an
artefact to engineer away — in a real installation the speakers are where
they are, and some consumers will be on a different mesh point. It is a
budget to design the buffer against.

**Losses are scattered, not bursty.** Run 7: 7 gaps in 170 seconds, 2.14
packets per gap, longest 5. At 5 ms per packet that is ~10 ms of audio at a
time, worst case 25 ms, roughly once every 24 seconds. Run 6, over a worse
path, was gentler still per event: 1.60 packets per gap, longest 3. Gaps of
this shape are the kind loss concealment can hide. A burst outage would not
be, and none has been seen.

**Nothing has ever been corrupted.** Zero payload errors across every run,
with the pattern source recomputing all 480 samples of every packet. Zero
duplicates and zero reordering throughout. The failure mode of this network
is a missing packet, never a wrong one — so the transport needs concealment
and a jitter buffer, not integrity checking.

## Method

1. Put both nodes on the same access point. They pick by signal strength,
   so when they sit equidistant between two mesh points they will split.
   Power down the far point, or move them within a metre of the near one,
   then reboot both so they re-scan.
2. Select the producer and consumers, source **Pattern** (the tone is
   audible but unverifiable), and press **Start link**.
3. Let it run several minutes. Loss at these rates is a few events per
   minute; a 30-second run measures noise.
4. Read the access-point line under the results before trusting anything
   above it.
5. Subtract the producer's send errors from the consumer's loss.
