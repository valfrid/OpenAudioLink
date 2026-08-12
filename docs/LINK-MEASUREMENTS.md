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
two mesh points, 2.4 GHz. Firmware 0.6.x and 0.7.0.

| # | Channel | Width | Path | Consumer loss | Send errors | Air loss | Jitter |
|---|---|---|---|---|---|---|---|
| 1 | 9 | 40 MHz | unknown | 0.240% | not yet counted | — | 2.04 ms |
| 2 | 9 | 40 MHz | unknown | 0.111% | not yet counted | — | 1.19 ms |
| 3 | 9 | 40 MHz | unknown | 0.355% | not yet counted | — | — |
| 4 | 3 | 20 MHz | 2 hops | 0.035% | 15 | **0%** | 1.81 ms |
| 5 | 11 | 20 MHz | 3 hops | 0.707% | 0 | 0.707% | 3.10 ms |
| 6 | 7 | 20 MHz | 3 hops | 0.172% | 0 | 0.172% | 2.31 ms |
| 7 | 7 | 20 MHz | **2 hops** | 0.044% | 5 | **0.029%** | **1.54 ms** |
| 8 | 7 | 20 MHz | 2 hops | 4.99% | 434 | 0% | 4.58 ms |
| 9 | 7 | 20 MHz | 2 hops | 29.0% | 1,499 | 0% | 7.60 ms |
| 10 | 7 | 20 MHz | 2 hops | 46.7% | unreadable | — | 11.10 ms |
| 11 | 7 | 20 MHz | **2 hops** | 0.045% | **0** | **0.045%** | **1.04 ms** |
| 12 | 7 | 20 MHz | 2 hops | **0%** | **0** | **0%** | 1.23 ms |
| 13 | 7 | 20 MHz | 2 hops | 3.46% | 19 | **0%** | 0.75 ms |
| 14 | 7 | 20 MHz | 2 hops | **0%** | **0** | **0%** | 1.44 ms |
| 15 | 7 | 20 MHz | 2 hops | **0%** | **0** | **0%** | **1.21 ms** |

Runs 1–3 predate both the send-error counter and the access-point line, so
their loss figures conflate three causes and their topology is unrecorded.
They are kept only to show that power save mattered (run 1 → run 2 changed
nothing but `esp_wifi_set_ps(WIFI_PS_NONE)`).

Runs 8-10 are a false trail, kept because of what they cost. See below.

Runs 12-14 are the A/B that verified firmware 0.7.0 against the 0.6.6
baseline: 12 and 14 are the two builds under the same conditions, both
losing nothing at all over roughly 25,000 packets each. Run 13 is 0.7.0
started moments after its own OTA reboot; see below.

Run 15 is the longest and cleanest: 104,013 packets — nearly nine minutes —
with nothing refused, nothing lost and not one gap recorded, *through two
changes to the destination set while it played*. A second destination was
added for about seventy seconds and then removed, which the producer's
counters show as 104,006 packets against 117,954 datagrams. The consumer
noticed nothing.

## Run 16: the Hub to a node, which is the hop that matters

Decision 11 named this the most important gap in this document, because
once the Hub became a Producer in ordinary use it carries almost all the
audio — and every run above measures node to node instead.

Hardware: Windows Hub on a **wired** server, one XIAO ESP32-S3 with a
PCM5102A, two hops (server → switch → access point → node). Firmware
0.9.5, channel 7, 20 MHz, RSSI about −50.

| # | Path | Consumer loss | Loss shape | Jitter | Dup / reorder |
|---|---|---|---|---|---|
| 16 | wired Hub, 2 hops | **0% of 71,901** | no gaps | **2.33 ms** | 0 / 0 |

Six minutes, not one packet lost and not one gap. Jitter is higher than
the best node-to-node runs (1.0–1.5 ms) and that is expected rather than
disappointing: a general-purpose operating system schedules the sender,
where an ESP32 does nothing else. It is also **less than half** what the
same Hub produced before its send loop was taken off the .NET thread pool
and given its own — 5.5 ms then, 2.33 ms now.

Two cautions about reading this run:

- **`payloadErrors` is meaningless here.** It compares every sample
  against the pattern source and the producer was sending a tone, so it
  counts nearly all of them. Only a `pattern` producer makes that column
  mean anything.
- **Zero loss does not mean the audio was clean.** Runs 1–15 could treat
  loss as the whole story because nothing was playing them. This run had a
  DAC on the end, and the same link that lost nothing still produced
  audible dropouts until the playout buffer was resized and the sender's
  scheduling fixed. Late is not lost, and only the receiver's playout
  counters show the difference (`HARDWARE.md`).

The second point is the general lesson of this document's next chapter: a
loss figure of zero was necessary and nowhere near sufficient.

## Run 17: real music, end to end, for an hour

The same path carrying what it was built for — Spotify Connect into
librespot, resampled 44.1 to 48 kHz at the Hub, out as RTP, through the
node's DAC into powered speakers. Firmware 0.9.5, wired Hub.

| # | Path | Consumer loss | Loss shape | Jitter | Dup / reorder |
|---|---|---|---|---|---|
| 17 | wired Hub, 2 hops, 46 min | **0.001%** of 504,886 | 3 gaps, longest 2 | **1.33 ms** | 0 / 0 |

Six packets in half a million, in three gaps of two. Jitter *lower* than
run 16's tone at 2.33 ms, which is the opposite of what might be expected
from a heavier source and is worth not over-reading: both runs are well
inside what the playout buffer absorbs, and the difference is more likely
the machine's mood than anything structural.

Two numbers from the Hub's side belong with it, because neither is
visible from the node:

- **`underrunSamples: 66160`** — the *source* ran dry, not the network.
  librespot's pipe had nothing when the streamer asked, about 0.7 seconds
  across 46 minutes, most plausibly at track boundaries. The Hub inserts
  silence and carries on. Worth watching rather than fixing.
- **`worstStallMs: 85` over 16,309 late wakes** — the sender was still
  descheduled for up to 85 ms at a time. Inaudible only because the node's
  playout target is 100 ms, which leaves 15 ms of margin. This run predates
  the Hub's GC change; the same field is how to tell whether that helped.

## Run 18: a turntable, node to node

**2026-08-07. The Analog Source works.** A record on a turntable, through
a PCM1808 module into a XIAO ESP32-S3 holding Producer, out as RTP, into a
second S3 holding Consumer, through a PCM5102A into powered speakers. No
PC in the audio path at all — the Hub only said who sends to whom.

Firmware 0.12.0 both ends. Levels at the ADC read **−5 / −7 dBFS**, which
is a healthy line signal with about 5 dB of headroom left.

Occasional missed samples, audible as the odd tick rather than as
dropouts. **The two nodes were on different mesh access points — three
hops across the backhaul**, and the page said so, which is the whole
reason the BSSID is on that line. Not comparable with runs 14-16, and not
a number worth quoting: the vinyl path has less slack than any other
source here, because the capture ring is 40 ms by design and every
millisecond in it is delay between the needle and the speaker.

The measurement worth taking is the same path with both nodes on one
access point. Until then this run establishes that it works, not how well.

### What made this findable at all

Nothing in the capture path could say whether a turntable was connected.
The rate, the buffer fill, the drop count and the read errors all count
*frames*, and a powered ADC clocks out frames whether or not anything is
plugged into it — so a node with the cable on the floor produced a trace
identical to one playing a record. The first bring-up attempt read as
perfectly healthy and made no sound.

The peak level meter added in 0.12.0 is what closed that, and it is the
same lesson as the whole of this document: **an instrument that cannot
distinguish the failure from the success is not an instrument.**

The second thing that cost time was not a fault at all. The setup page's
link test offered Pattern and Tone and nothing else, so there was no way
to send the ADC anywhere from it — and a vinyl test run on Pattern proves
the network and says nothing about the turntable. Both selectors now offer
Line in.

## Run 19: internet radio, six hours, unattended

**2026-08-12. The first overnight run, and the first with a wired mesh
backhaul.** SomaFM Groove Salad through the Hub — fetch, MP3 decode,
44.1→48 kHz resample, RTP — to one Consumer node. Hub 0.11.10, firmware
0.14.0. Both access points now joined by Ethernet rather than a 5 GHz
backhaul, and the Hub itself on the same switch.

| | |
| --- | --- |
| Duration | 4 629 475 packets sent — 6 h 26 min |
| Received | 4 621 493 of 4 624 326 |
| Lost | 2 833 — **0.061 %** |
| Loss shape | 1 684 gaps, 1.68 packets per gap, longest 8 |
| Jitter | **1.31 ms** |
| Duplicates / reorder | 0 / 0 |
| Source underrun | 150 240 samples — **unchanged from the first minute** |
| Send errors | 0 |
| Late wakes | 89 437 — 1.93 % of iterations |
| Worst stall / worst send | 90 ms / 36 ms |

### What it establishes

**The station never dropped.** `underrunSamples` is the same 150 240 it
reached in the first two seconds — the ring filling from empty at startup
— and did not move again in six and a half hours. The reconnect path was
never exercised, which is the best thing a reconnect path can do.

**The node did not reboot.** The consumer counted 4.6 million packets in
one unbroken run. Before firmware 0.14.0 an ADC-enabled node overflowed
its control server's stack and restarted roughly every ninety seconds
under this exact load. Six hours of continuous reception is what closes
that, and it closes it more convincingly than any uptime figure.

**0.061 % loss, 1.31 ms jitter, no reordering.** Comparable with the best
of runs 14-16 and achieved while crossing a wireless hop to the speaker.

### A correction the long run forced

A 64-second sample earlier the same evening showed **0.542 %** loss and
prompted a theory: that the Hub's catch-up sender releases bursts where a
node producer paces steadily, and bursts overflow an access point's
per-client queue. The mechanism is real and the arithmetic was plausible.

Six hours says 0.061 % — nearly ten times better, and better than the
short vinyl samples in run 18. **The minute-long sample was noise, and the
explanation built on it was an explanation of nothing.** Worth recording
because it is the same mistake this document keeps catching: a number
taken over a short window, believed, and reasoned from.

The residual is still visible — 1.93 % of iterations wake late, and losses
still arrive in clumps of 1.68 rather than singly, which is the signature
the theory predicted. So the burst mechanism is probably real and small.
It is not worth chasing at 0.061 %.

### The week behind this run

Everything the Hub produced was inaudible or broken until the day before,
and four separate faults had to fall:

- The send loop logged a warning on every stall over 20 ms, **from inside
  a loop running 200 times a second**, into a Windows service's Event Log.
  A stall logged, the log blocked the sender, the block lengthened the
  next stall. It ratcheted a hiccup into 800 ms. The diagnostic caused the
  fault it was measuring.
- `Mp3Frame.LoadFromStream` reads `input.Position` before checking
  `CanSeek`. A live HTTP body cannot answer, and the resulting
  `NotSupportedException` says "Specified method is not supported." —
  naming neither position, nor seeking, nor the decoder.
- The same decoder assumes one `Read` fills the buffer. True of files,
  false of sockets, so every frame straddling a segment boundary read as a
  truncated file.
- And the volume taper is cube-law: 50 % is −18 dB. With a turntable
  running 15 dB hotter than everything else, vinyl sounded right while
  Spotify sounded broken.

None of the four was found by reading code. Each was found by a number or
a message off the hardware — `underrunSamples: 0`, `worstStallMs: 800`,
`NotSupportedException`, and a volume slider at 25 %.

## Run 20: a turntable, same access point and across two

**2026-08-12. The measurement run 18 asked for.** A record, node to node,
firmware 0.14.0 both ends, wired mesh backhaul. Run 18 established that
vinyl works and said explicitly that it was "not a number worth quoting"
because the nodes were on different access points over a wireless
backhaul. These are the numbers.

Input at **−5 / −7 dBFS** — healthy, after a session at −1 / −2 that was a
decibel from clipping.

| | Both on one AP | Across two APs |
| --- | --- | --- |
| Consumer signal | −67 dBm | −53 dBm |
| Duration | 23 min | 8.8 min |
| Received | 275 928 of 276 927 | 105 636 of 105 788 |
| Lost | 999 — **0.361 %** | 152 — **0.144 %** |
| Loss shape | 384 gaps, 2.60/gap, **longest 44** | 73 gaps, 2.08/gap, **longest 4** |
| Jitter | **0.88 ms** | 1.38 ms |
| Reorder | 0 | 0 |

Producer, over the same period: **114 128 packets, 0 late, 0 send errors,
0 retries.**

### The gap length matters more than the percentage

A consumer holds about 200 ms of playout. The cross-AP runs never lost more
than **4 packets in a row — 20 ms**, which the buffer absorbs completely and
nobody hears. The same-AP run lost **44 in a row — 220 ms**, past what the
buffer can cover, and that one is an audible dropout rather than a
statistic.

Two configurations differing by a factor of two in loss can differ by
everything in how they sound, and the loss shape is where that shows.

### Vinyl has the best timing and the worst delivery

Jitter of 0.88 ms is the lowest figure in this document — better than the
Hub's own streams, because a node paces from its own timer where the Hub's
loop catches up in bursts.

The loss is structural. The Hub sits on Ethernet, so a Hub stream crosses
the air once. Node to node crosses twice — producer to access point, access
point to consumer — and **both nodes share one 2.4 GHz channel**, so those
two hops compete for the same airtime. ESP32-S3 has no 5 GHz radio, so
this is not avoidable by moving the nodes to another band.

Against internet radio's 0.061 % to the same speaker, six times the loss
for one extra contended hop is about the right order.

### The cross-AP column is confounded, though no longer short

The consumer moved 14 dB closer to its access point at the same time as it
changed access point, and 14 dB of margin removes retries on its own.
**Both access points are on channel 7**, so the mechanism that would make
cross-AP genuinely better — two hops on different channels, joined by wire
— was not in play at all. Signal strength is the likelier cause of the
improvement, and it is the lever worth reaching for first: choose an access
point for signal, not for separation.

The result did hold as the window grew, 0.167 % at 2.6 minutes settling to
0.144 % at 8.8, so it is a real difference rather than the sampling noise
that caught this document twice already.

The configuration worth testing is the access points on non-overlapping
channels, 1 and 11. Until then run 18's advice is neither confirmed nor
reversed.

### Task 26 closes, and the diagnosis was wrong

An ADC-enabled producer once reported **4384 packets sent, 4384 late** —
every packet. The standing theory was task priority: capture at 7 starving
the producer at 6.

Those priorities are unchanged in 0.14.0 and the count is now zero. So the
theory was wrong. The likeliest actual cause is that the original figure
was measured on a node whose control server was overflowing its stack and
restarting every ninety seconds — the producer's pacing was measured on a
node that was not healthy, and read as a pacing fault.

That is inference, not proof. The conditions no longer exist to reproduce
it, which is the honest state to leave it in: fixed, cause probable,
unproven.

### Eleven hours

Both nodes showed **11 h uptime** through this run. The same load restarted
an ADC-enabled node every ninety seconds before 0.14.0.

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

**A refused send is a symptom of the channel, not a defect in the sender.**
This cost three firmware revisions to learn. Runs 8 and 9 were attempts to
make the producer retry a refused send harder — first by spinning, then by
blocking on a faster tick — and refusals rose from 0.015% to 5% and then
29%. The obvious reading was that each change had made things worse.

It was the wrong reading. Run 10 used firmware byte-identical to run 7's
apart from the version string, and measured 46.7%. The code was never the
variable. Something in the radio environment had been degrading all
evening, and `ENOMEM` on `sendto` is what that looks like from inside the
node: the MAC layer retries frames against a channel that will not clear,
the transmit queue drains slowly, buffers stay allocated, and the pool
empties. Restarting everything cleared it, and run 11 — same firmware
again, no retry logic at all — refused nothing.

Two rules follow. A measurement that contradicts a known-good baseline is
a claim about the environment until proven otherwise; re-establish the
baseline before changing code. And never ship two changes in one image:
run 9 altered both the retry and `CONFIG_FREERTOS_HZ`, so even its failure
taught nothing.

**A node needs a minute after booting before it can be measured.** Run 13
refused 19 packets with `ENOMEM`, and the consumer lost exactly 19 in a
single contiguous gap — one burst at the start of a stream begun moments
after an OTA reboot. Nothing was lost over the air. Rebooting and waiting
gave run 14: 26,408 packets, nothing refused, nothing lost.

This was a rule in the method below before it was a measurement. It is
also the first time the refusal diagnostics paid for themselves: `ENOMEM`,
"19 retries, none helped", and a single 19-packet gap say *startup
transient, sender-side, retry cannot fix it* at a glance, where three
firmware revisions were once spent inferring less than that from a bare
count.

**The audience can change without disturbing the stream.** Run 15 added a
destination and later removed it while playing, and the consumer already
listening recorded no gap, no reset and no reordering across either
change. A speaker switched on halfway through a record joins what is
already playing rather than interrupting it, which is what makes a
Consumer able to join by asking (decision 9) rather than by having been
present when the music started.

**Nothing has ever been corrupted.** Zero payload errors across every run,
with the pattern source recomputing all 480 samples of every packet. Zero
duplicates and zero reordering throughout. The failure mode of this network
is a missing packet, never a wrong one — so the transport needs concealment
and a jitter buffer, not integrity checking.

## The multicast leg, which is a different network from the audio one

Everything above measures unicast RTP between two nodes. Discovery is
multicast, it travels by different rules, and one can be perfect while the
other is broken — the whole of 2026-08-02 went into learning that. Kept
here because the causes are network facts, not Spotify facts, and they
will resurface with AirPlay or anything else that announces itself.

**A VPN interface can carry the announcements away.** A socket bound to
`0.0.0.0` lets the operating system choose which interface multicast
leaves by, and it chooses on route metric. On the machine this was found
on, Tailscale sat at metric 5 against Wi-Fi's 50:

```
InterfaceAlias   InterfaceMetric   ConnectionState
Tailscale                      5         Connected
Wi-Fi                         50         Connected
```

The symptom is precise and misleading: the host is reachable at its
address — a phone's browser gets a full answer from its HTTP port — and
completely undiscoverable, because being found and being reached travel
different paths. `LocalAddressSelector` was written after this VPN caused
a different failure and its remarks already name Tailscale; the same
reasoning now picks the announcement interface (`LIBRESPOT.md`). Hyper-V,
WSL, Docker and VMware all create adapters that can do this. **Always bind
announcements to the address on the subnet the listeners are on, never to
`0.0.0.0`.**

**A Windows host can be deaf to multicast while appearing perfectly
healthy — and this was the actual cause here.** Windows classifies
networks itself and gets wired home LANs wrong; on a network it has
labelled **Public** it disables its own inbound mDNS rules. The host then
sends fine and receives nothing.

The symptom is as misleading as a symptom can be. Every outbound test
passes. The receiver answers any request made directly to its address. A
phone's browser fetches its status page. It logs its announcements going
out. And it is undiscoverable, because a discovery request is a question
that has to arrive, and none did.

What made it visible in the end was watching what the host *received*.
With `RUST_LOG=libmdns=trace`, a healthy host on this network shows a
steady stream of neighbours asking about `_googlecast`, `_shelly`,
`_home-assistant`, `_workstation`. The broken one showed only packets from
its own VPN address. **A host that hears nothing from its own LAN is the
finding; everything else was instrumentation.**

Two commands fixed it:

```powershell
Set-NetConnectionProfile -InterfaceAlias "Ethernet" -NetworkCategory Private
netsh advfirewall firewall add rule name="mDNS in" dir=in action=allow protocol=UDP localport=5353 profile=any
```

`install-service.ps1` now opens 5353 alongside 41000 and warns when
Windows has classified a network Public, because the Hub's own discovery
listens on multicast `239.255.41.10:41000` and would have failed in
exactly the same silent way.

**Mesh Wi-Fi drops mDNS between its nodes.** Others report it for exactly
this hardware — an SNBForums thread titled *"ASUS ZenWifi AX (XT8) mesh:
devices only accessible if on the same node"* — and librespot discussion
#1314 records the general case: *"everything works only if the client is
in the same cell"*.

The reported fix is counter-intuitive: **disable IGMP Snooping, per band**
(Wireless → Professional → band → Enable IGMP Snooping → Disable). The
XT8 is tri-band, so 2.4 GHz, 5 GHz-1 and 5 GHz-2 each need it, and doing
one is a common half-fix. Snooping should always flood the 224.0.0.0/24
link-local range that mDNS lives in; Broadcom's implementation on this
platform does not reliably do so. LAN → IPTV carries the same two toggles
for the wired side. Reboot the router and then the nodes afterwards —
AiMesh nodes take configuration on sync.

Note this cuts the other way from the audio findings above. Crossing the
mesh backhaul costs unicast RTP about 6x the loss; for multicast it can
cost everything.

**Unmanaged switches are not suspects.** They have no IGMP snooping, so
they flood multicast to every port. A wired segment built from them is
clean by construction, which usefully removes a variable.

**Wiring the Hub is worth more than it looks.** Once the Hub is the
producer, a wireless Hub means every packet goes Hub → air → AP → air →
speaker. Wired, the Hub's own transmission leaves the air entirely,
halving the airtime each stream costs — the same third-hop arithmetic as
above, applied to the hop that carries all of the audio.

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
6. When comparing two firmware images, measure A, then B, then A again.
   The radio environment drifts on its own and has done so by three orders
   of magnitude in an evening; without the second A there is no way to tell
   a change in the code from a change in the air.
7. If a run disagrees with the established baseline, restart the access
   points, the nodes and the Hub, and re-run the baseline before believing
   anything. Runs 8-10 above are what happens otherwise.
8. The second A is only needed when B disagrees with the first. Runs 12 and
   14 both lost nothing at all, and a third run cannot explain a difference
   that is not there.
