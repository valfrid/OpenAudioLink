# Link measurements

> **This is history, not reference.** Thirty-four runs in reverse order,
> including the wrong turns — the channel-7 theory, three attempts to fix
> radio noise with scheduling, and a series where the numbers improved
> because a buffer drifted rather than because anything was changed.
>
> For what to actually set, see `docs/TUNING.md`. Run 34 is the one that
> produced the current values.

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

## Run 21: lossless internet radio, end to end

The first lossless *source* this project has had. The transport has
carried uncompressed L24 since the first packet, so the station was the
only lossy link left in the chain; with Ogg-FLAC decoded through libFLAC
it no longer is.

| | |
| --- | --- |
| Source | Radio Paradise, `http://stream.radioparadise.com/flac` |
| Container | Ogg-FLAC, 44.1 kHz, resampled 147:160 to 48 kHz |
| Hub | 0.14.0, wired |
| Consumer | PartySpeaker |
| Packets | 20 409 of 20 410 |
| Loss | 0.005%, one gap, isolated |
| Jitter | 1.27 ms |

Comparable with run 19's six hours of MP3 radio (0.061%, 1.31 ms), which
is the point: **the decoder changed and the link did not.** Nothing about
carrying lossless audio costs the network anything, because the wire
format never varied — 48 kHz L24 whatever the source. The only thing that
improved is what went into it.

### What this run actually cost

Eight Hub releases, and the measurement that mattered was not taken until
the seventh. The station was silent from 0.13.3 to 0.13.8 while every
counter reported health: the stream ran, packets flowed, loss was zero,
and all of it was silence. Each release fixed something genuinely broken —
a four-byte signature check that could not see past a preamble, an MP3
decoder that can never report failure on a stream that never ends, a
sync-only header test that read every FLAC frame as an MP3 frame — and
none of them was the cause.

The cause was that **Windows ships no Ogg demuxer**, and Media Foundation,
asked to open Ogg-FLAC anyway, does not refuse. It never returns. A
component that hangs rather than failing makes every diagnosis downstream
of it a guess.

Three things ended it, and all three were measurements:

1. **Underrun arithmetic.** 90 103 680 underrun samples ÷ 96 000 = 938 s,
   and 187 717 packets × 240 frames = 938 s. Identical, so *every sample
   ever sent* had been an underrun. That ruled out "playing quietly".
2. **A stage marker in the stream description** — connecting, reading the
   first bytes, opening with Media Foundation, decoding. It reported
   `opening with Media Foundation as FLAC` and stayed there, which located
   the hang in one reading instead of by inference.
3. **Thirty-two bytes of the stream**, fetched by hand: `Content-Type:
   application/ogg`, first bytes `OggS`, and no redirect. That named the
   container and killed two of my theories at once.

The generalisable part is in `ROADMAP.md`: a green build is not a
measurement, and neither is a plausible chain of reasoning. Nothing here
was known until something was counted.

## Run 24: the node is jamming its own receiver

**2026-08-20, 21 minutes.** The dongle Consumer on the *good* access point
at **−45 dBm** — the best signal any of these runs has had.

| | |
| --- | --- |
| loss | **3 765 ppm** (0.38 %) |
| underruns | 168 in 1 254 s — **one every 7.4 s** |
| silence inserted | 4.33 s |
| audio lost on air | 4.71 s |
| gaps | 2.63 packets mean, longest 10 |
| speaker, same AP, lifetime | one underrun every **356 s** |

**The underruns are the loss**, to within 8 % — nothing is stalling, the
audio genuinely never arrived. And the loss is 0.38 % at −45 dBm on the same
radio the speaker uses without trouble.

So it is neither the access point (run 23, corrected above) nor scheduling
(the core-1 and core-0 moves changed little). **It is this node.**

### RSSI measures the beacon, not the noise floor

Which is the thread running through every measurement here. Four runs, and
the signal reading has been useless in all of them — −47 dBm worse than
−73 dBm, −45 dBm no better than −74 dBm. That is not noise in the data; it
is a measurement answering a different question than the one being asked.
RSSI says how loud the access point arrives. It says nothing about what
else is arriving with it.

**And this node has a USB dongle plugged directly into the board, a couple
of centimetres from the Wi-Fi antenna.**

That is the observation. The mechanism first offered for it was wrong, and
the correction weakens the case rather than strengthening it:

- **The MAX97220 is Class AB, not Class D.** It is a DirectDrive headphone
  amplifier — a linear output stage, with a charge pump to make the negative
  rail that lets it drop the output coupling capacitors. The charge pump is
  a switcher, but at a few hundred kilohertz; its harmonics at 2.4 GHz are
  nothing. `HARDWARE.md`'s Class-D caution was written about the MAX98357A
  and does not transfer to this part.
- **This is full-speed USB, 12 Mbit/s.** The well-known 2.4 GHz desense
  stories are USB 3.0, whose 5 Gbit/s signalling puts a fundamental
  *inside* the band. A 12 MHz bus reaching 2.4 GHz means the two-hundredth
  harmonic, which is not where the energy is.

So the honest position is that **the radio-noise hypothesis is thinner than
it was first stated**, and a plausible mechanism has not been identified. It
also is not dead: coupling into a 5 cm pigtail and a sheet antenna lying
against the board is conduction and common-mode, not radiation at 2.4 GHz,
and does not need a harmonic to reach the band.

**And there is a variable nobody has named yet.** The two nodes are in
different rooms. Different multipath, different neighbours, different
everything — and the dongle node has been carried around during testing
while the speaker has sat still for five days.

### The node could not answer "why that access point"

Recorded because it cost a day and a half of wrong conclusions. A node
carried to within **one metre of one access point, twenty metres from
another**, kept rejoining the far one — through reboots and a power cycle.
It only moved when a hand was cupped over the antenna during boot, which
changed the scan enough to change the outcome.

The selection rule is supposed to prevent exactly that: every boot scans all
channels and takes the strongest. Something between that intent and the
radio is not doing what the code says, and **the node had no way to be
asked.** `/status` reports the access point it landed on and nothing about
the ones it did not, so in a mesh — one SSID, several radios — the only
available instrument was walking around the house with the board.

Two endpoints, in firmware 0.15.6:

- **`GET /wifi/scan`** — every access point the node can see, with BSSID,
  channel, RSSI, and which one it is on. Costs a second of connection, so it
  is a command and not something to poll.
- **`POST /wifi/rejoin`** — forget the current access point and join again
  from a fresh scan. No reboot, no hand.

Neither changes the selection rule. The first makes it observable and the
second makes it repeatable, which is what was missing when three separate
explanations were tried on data that could not distinguish them. **Proper
roaming — leaving an access point because the link is measurably bad rather
than because it disappeared — is a real question and is still open.** It
wants the loss counters this node already keeps, not an RSSI threshold; run
23 and 24 are four demonstrations that RSSI cannot see what matters here.

### Three tests, cheapest first, one at a time

This node runs an **external antenna** — a 5 cm pigtail to a sheet antenna,
currently lying beside the board and therefore beside the dongle. That gives
two independent things to move, and they must be moved separately or a joint
result says nothing about which mattered.

**0. Unplug the dongle. Costs nothing, needs no parts, and is now the test
that matters most.** It is the only one that holds *everything* constant —
same room, same antenna, same access point, same minute — and changes only
whether a dongle is drawing current and moving data. Location, multipath
and antenna quality all cancel. With
`output=usb` and no device attached the node still joins, still receives RTP
and still counts it — `outputReady` goes false and playout leaves the ring
alone, but `stats` keeps working. So `lossPpm` with the dongle out, against
`lossPpm` with it in, is a clean A/B on the same radio, the same minute, with
no USB traffic at all in one arm. **If loss does not change, the radio noise
theory is dead** and this document needs a fourth explanation.

**1. Move the antenna, not the dongle.** A longer pigtail — 20 cm — puts the
receiver out of the near field while the dongle stays in the socket. If this
works, the deployment everybody prefers survives: a dongle pushed straight
into a XIAO, with the antenna on a lead where it belongs anyway.

**2. Move the dongle.** A USB extension, antenna unchanged. The same
question asked from the other end, and worth doing even if test 1 succeeds,
because knowing *which* body is the noisy one is worth a cable.

What the answer changes: nothing in the firmware. The output stage is
already doing its job — zero write errors across every run, and silence that
matches the loss to within 8 %. If this is radio noise, no scheduling
change, buffer size or access-point rule fixes it, and three attempts at
exactly those is what this entry exists to stop repeating.

## Run 36: the first long run without the connection bug (planned)

**Not yet performed.** Written down before it is run so the result is
read against a stated expectation rather than against whatever we hoped
for afterwards.

### Why it is needed

Every long run in this series was made by a Hub whose `DeviceCommandClient`
had no connection limit — unbounded pooling per node, held open a minute
idle, on a device with seven sockets in total. Hub 0.65.0 fixed that, and
the short run immediately after showed **stalls falling from 27 % to 2.6 %
on one node and 35 % to 6.3 % on the other**, with a 1 484-packet dropout
disappearing entirely.

Which means the series has no valid baseline. Every buffer conclusion,
every tuning constant and every "the sender is stalling" diagnosis was
measured against a Hub that was quietly starving its own nodes of sockets.
Some of those conclusions will survive and some will not, and there is no
way to know which without a long run on a Hub that does not have the bug.

### Setup

Hub 0.65.0, firmware 0.39.0 on both consumers. Internet radio, the source
with the 5 s cushion, so a station hiccup cannot be confused with a
sender stall. `ringMs` 400, `delayMs` 100 on both nodes — unchanged, so
this is comparable with run 34. **Switchboard closed for the duration**:
an open page asks each node for `/stream` about once a second and the
point is to measure the system, not the observer.

Several hours at least. Overnight is better.

### What to record at the end

| | |
| --- | --- |
| Duration, packets sent | |
| Received / expected, per node | |
| Lost, and its shape | |
| Arrival stalls: count, ppm, worst | |
| Late packets, per node | |
| Jitter | |
| Clock ppm and its ±, per node | |
| Wi-Fi drops and last reason, per node | |
| Source underrun samples | |
| Late wakes, worst stall, worst send, GC pause | |
| Speaker offset: typical and worst | |

### What each outcome would mean

**Stalls stay near 2-6 % and the offset stays under 20 ms.** The system is
where it should be, the connection bug was the dominant fault all along,
and the catch-up cap's broken invariant — a 100 ms burst against 75 ms of
headroom — is real but no longer reached often enough to matter. Nothing
further to fix; tag it.

**Stalls stay low but the offset still spikes past 40 ms.** The cap
invariant is the live fault after all, and fixing it is the next change:
either the cap comes down to the headroom or the steering setpoint moves
below the midpoint to make room.

**Stalls climb back toward 25 %.** Something outside the Hub's control
plane is doing it, and the per-window figures — `recentStallMs` against
`recentSendMs` against `recentGcPauseMs` — say which. Send time larger is
the network stack; stall larger with the collection tracking it is
allocation in the source pipeline; stall larger with the collection flat
is the machine or its power management.

**One node's figures diverge from the other's.** That node's link, not the
sender. The differential rule has held every time in this series: what
both nodes see together comes from the sending end, what one sees alone is
its own radio.

## Run 35: the turntable, and what a second air hop costs

**2026-08-22.** Vinylspelare (PCM1808 ADC) producing to two consumers,
firmware 0.29.0 throughout, 400 ms ring and 100 ms delay on the consumers.
Two sittings of roughly 17 minutes each.

### The producer is not the problem

```
"packetsSent": 205606, "datagramsSent": 205606,
"sendErrors": 0, "sendRetries": 0, "lastSendErrno": 0, "latePackets": 0
```

Not one refused send, not one retry, not one missed deadline, across two
runs. `readErrors` is 0 as well, so the send loop is keeping up with the
converter and nothing is being dropped at capture.

This killed the change that was about to be written. `send_one()` tries
twice and then drops the packet permanently, and the sequence number
advances regardless, so a refusal reaches the consumer as loss that no
buffer can recover. The plan was a producer-side send queue, mirroring the
socket-queue-to-ring fix on the receive side. **There is nothing to queue.**

Worth recording as the reason to measure first: the fix was plausible,
symmetric with something already proven, and entirely unnecessary.

### Two air hops, one channel

| | consumer A | consumer B |
| --- | --- | --- |
| late | 9.8 ppm (2 of 203 152) | 11 ppm (2 of 186 483) |
| lost | 0 | 0 |
| dropped frames | 0 | 0 |
| underruns | 2 | — |
| full cushion | 99.76 % | 99.8 % |
| **stalls** | **12 965 ppm** | **12 338 ppm** |
| worst stall | 254 ms | 248 ms |

Both nodes and the producer are on **the same access point** — BSSID
`7c:10:c9:7a:13:b1`, channel 9, RSSI −45 and −51, `roams: 0` on every one.
There is no mesh and no backhaul in this path.

It is still two hops, and that is the whole finding: the audio crosses the
air **twice on one channel**, up from the producer and back down to the
consumer. The Hub reaches the AP over Ethernet, so a Hub-sourced stream
crosses once.

| path | stalls |
| --- | --- |
| Hub → node, one air hop | 6 740 – 9 006 ppm |
| node → AP → node, two air hops | **12 338 – 12 965 ppm** |

Roughly double, which is what doubling the airtime per packet predicts:
400 transmissions a second on channel 9 instead of 200, plus acknowledgements.

The two consumers agreeing to within 5 % is what localises it. Every
consumer receives the same packets from the same producer, so a stall on a
consumer's own last hop would differ between them; a stall on the part of
the path they share appears on both. With signal strong and no roaming, the
shared cause is airtime on the channel.

### What that means

**The buffer absorbs it.** A stall rate half again as high as anything
previously measured produced 2 late packets in 203 152 — the same 10 ppm
the one-hop path gives. Zero loss, zero overflow drops, and the ring never
came below 265 ms.

**Island mode is the fix for the turntable, not more buffering.** When the
producer is itself the access point, the packet crosses the air once, and
the extra 6 000 ppm of stalls is not incurred in the first place. That is
also the original use case for this project: a turntable and a PA speaker,
five to ten metres, nothing else involved.

**A wireless producer is inherently costlier than a wired one**, and that is
a property of the medium rather than a fault to be fixed. Worth knowing
before designing anything around a node-sourced stream.

### Incidental

`hz: 47999` — the ADC's measured rate, 21 ppm below nominal, which rules
out converter drift as a contributor. `heapFree` at 8.5 MB confirms PSRAM
is live and in the allocator.

And the reading that started a correction to decision 17: `"state": "new"`
on the running slot of both nodes, where a rollback-armed bootloader would
have moved it to PENDING_VERIFY and then VALID. Rollback is not active on
any node that has only ever been updated over the air.

## Run 34: the buffer series — 200 ms to 400 ms with a 100 ms delay

**2026-08-22.** PartySpeaker, firmware 0.28.0/0.29.0, one node changed at a
time while the others stayed put. All four readings are from `/stream` on
the node, normalised per packet because the runs are different lengths
(92 099, 45 203, 997 220 and 256 878 packets submitted).

| per packet | 200 ms ring | 400 ms | 400 ms, long | 400 ms + 100 ms delay |
| --- | --- | --- | --- | --- |
| `droppedFrames` | 2.158 | 0.134 | 0.038 | **0** |
| `silenceFrames` | 2.637 | 0.932 | 0.223 | **0.036** |
| `latePackets` | 2 530 ppm | 1 615 ppm | 234 ppm | **43 ppm** |
| `tightPackets` | 16 428 ppm | 7 123 ppm | 1 365 ppm | **269 ppm** |
| worst stall | 287 ms | 121 ms | 373 ms | 211 ms |

Loss was **zero in every one of the four**. Nothing was ever dropped by the
radio; everything here is a buffer arriving too late to be used, which is
why the loss column had nothing to say all series.

### What each step actually bought

**200 → 400 ms of ring killed the overflow.** The socket queue holds 64
packets, 320 ms, deliberately sized in run 28 so a stall's worth could be
held rather than dropped. The ring downstream was 200 ms and physically
could not accept what the queue had saved: the burst at the end of a stall
overflowed and the *oldest* audio was discarded. `fillMaxFrames` hit 11 736
frames — 244 ms — in the second run, a peak the old ring could not have
contained, which is the one figure in the series no difference in network
conditions can explain away.

**The 100 ms delay killed what was left.** A bigger ring does not stop the
ring running dry: that is set by the target, and a stall longer than the
target starves the speaker however much spare capacity sits above it. Going
from a 100 ms to a 200 ms target took late packets from 234 ppm to 43.

### The trap in the middle of this series

Between the second and third readings nothing was changed at all. Late
packets still fell 1 615 → 234 ppm, because the fill had drifted upwards on
its own.

The fill does not sit at the target. It floats to just below wherever
trimming begins, since bursts raise it faster than a servo removing one
frame per 5 ms chunk lowers it — and `trim_above` was
`target + (capacity - target) / 4`, which moved with the ring. Raising the
capacity raised the trim line and deepened the buffer without anyone asking:
a measured fill of 221 ms, later 336 ms, against a 100 ms target.

So `ringMs` was two knobs wearing one hat, and the accidental one was doing
much of the work credited to the deliberate one. Firmware 0.29.0 makes
`trim_above` `target * 3/2`, clamped to seven eighths of capacity. Latency
follows `delayMs` alone and `ringMs` buys burst headroom and nothing else.

### Two counters that lie, and cost two wrong diagnoses

`arrivalGaps` is the delivery-stall measure. The switchboard's shape column
read `lossEvents` instead and printed "no gaps" in grey whenever nothing was
lost — so a node measuring 890 stalls with a 287 ms worst case displayed as
a clean link, and two diagnoses were built on that reading before the raw
document contradicted both. Fixed in Hub 0.36.0, which shows stalls and loss
bursts separately.

`payloadErrors` compares every sample against the synthetic test pattern, so
with music playing it counts every sample as an error: 92 099 packets ×
480 samples = 44 207 520, against 44 207 518 reported. Meaningless outside a
pattern-source run and now labelled as such.

The counters also keep different epochs. `/stream/stop` clears the
consumer's state but not the playout's, and `framesPlayed` runs from boot
including idle silence — in the third run they implied 1.0, 1.4 and
2.8 hours respectively. Normalise playout figures against
`packetsSubmitted` and nothing else.

### Where it landed

400 ms ring, 200 ms target, about 240 ms from air to ear including the sink.
Snapcast territory, arrived at from the opposite direction. 43 ppm late,
zero drops, zero loss.

Worst stall seen across the series is 373 ms, which a 200 ms target still
cannot cover, so the remaining 43 ppm is that tail. Closing it needs a
400 ms target and a 600 ms ring — and roughly 400 ms of latency, which is a
listening decision rather than a measurement one.

## Run 33: the Hub is late every second, and both access points are on channel 7

**2026-08-21, 00:19.** Hub 0.22.0, per-window counters, both consumers.

### Every second is an event

The log was written to print a line only when `recentStallMs` or
`recentSendMs` was non-zero, on the assumption that most seconds would be
clean. **Every second printed.**

| | range |
| --- | --- |
| `recentStallMs` | **10–30 ms, every second** |
| `recentSendMs` | 1–6 ms |
| `recentLateWakes` | 2–9 |
| `gcPauseMs` | 209 → 214 across 24 s |
| `gen2Collections` | **4, flat throughout** |

So the send loop is late by 10–30 ms *continuously*. Not during storms —
always. This machine has not had a clean second all night.

And the garbage collector is exonerated a third time: 5 ms of pause across
24 seconds in which the loop lost 10–30 ms every one of them. `recentSendMs`
of 1–6 ms says the sends themselves are not blocking either. The thread is
simply not being scheduled on time.

### But it is not the storms

The nodes, over the same seconds:

| | `und+` per 10 s | loss |
| --- | --- | --- |
| Dongle | 0, then 2 | 1 533 ppm |
| PartySpeaker | 0, then 1 | **0** |

Three underruns between them while the Hub was late every single second.
`MaxCatchUpPackets` is 100 ms and the playout ring holds 120 ms; a 30 ms
stall disappears into that without touching a converter.

**This settles the overload question, and not simply.** The Hub *is*
chronically late — that intuition was right, and no measurement before this
one could have shown it. But it is late by tens of milliseconds, not the
hundreds a gap needs, and the receivers absorb it. It is a real defect that
is not the audible one.

### The channel theory, and why it is wrong

Run 33 first concluded that channel 7 was the remaining fault, on the
reasoning that a partially-overlapping channel collides with neighbours on
6 and 11 without being able to back off against them. A scan from the
dongle settles it, and the answer is no.

```
  1    -85 dBm  ChargeAmps
  2    -71 dBm  shellyuni-3494547908ED
  2    -85 dBm  The Matrix
  6    -94 dBm  PG
  6    -94 dBm  AP-WH3E-2C3B70002F6B
  7    -44 dBm  valfrid-n     <-- this node's access point
  7    -75 dBm  valfrid-n     <-- the other one
 10    -92 dBm  Alexius2.4
 11    -90 dBm  The Matrix
```

**There is nothing there.** The strongest radio that is not ours reads
−71 dBm, twenty-seven decibels below our own access point, and everything
else sits between −85 and −94 — the noise floor. Channels 1, 6 and 11
scored 7.0 e−8, 1.8 e−8 and 1.9 e−9; all three are empty in any practical
sense, and moving would buy a few decibels of a floor nothing is standing
on.

The air is clean. The channel is not the problem, and neither is the fact
that both access points share it — with no third party to collide with,
two nodes of one mesh carrier-sense each other correctly.

### Which leaves the node itself

The same scan reads this node's own access point at **−44 dBm**. That is a
strong link. And it loses 1 533 ppm while a node on a different access
point, four decibels weaker, loses **nothing**.

Everything else is now eliminated: the Hub sends on time enough (run 33),
the garbage collector is idle (runs 30, 31, 33), the receive queue is
fixed (run 29), the NIC is fixed (run 30), roaming is not happening
(`roams` 0), and the spectrum is empty (here). What remains is physical and
specific to the dongle node — its antenna, its placement, or its own
emissions.

The one experiment that separates those, and it needs no equipment: **put
the dongle node beside the speaker.** Same room, same distance, same access
point. If it still loses while the speaker does not, the fault travels with
the hardware and the answer is the antenna or the dongle's own noise. If
the loss disappears, it was the location all along.

### Both access points are on channel 7

```
D  ch=7  ap=7c:10:c9:7a:13:b1  rssi=-52
S  ch=7  ap=7c:10:c9:7a:0b:d0  rssi=-47
```

Two access points, different BSSIDs, **the same channel**.

Recorded because it is true, not because it matters: the scan above shows
nothing else on or near channel 7, and an AiMesh keeps its nodes on one
2.4 GHz channel by design so that clients can roam under a single SSID. Two
access points of one mesh hear each other perfectly and share airtime the
way the standard intends. It is not a misconfiguration and, on this
spectrum, it is not a cost.

The ZenWiFi does not expose a per-node channel in any case.

### And the gaps are not roaming

`roams` read **0 on both nodes**. The dongle's 224-packet hole in run 32 was
not a re-association, so the sticky-BSSID rule is not implicated. Fades or
interference, on a channel it shares with a second access point.

## Run 32: a gap of two hundred and twenty-four packets

**2026-08-20, 23:37–23:42.** Both nodes, 154 125 packets each, same Hub,
same stream, same minutes.

| | lost | gaps | longest gap | jitter |
| --- | --- | --- | --- | --- |
| Dongle | 439 (0.285 %) | **94** | **224 packets** | 3.21 ms |
| PartySpeaker | 3 (0.002 %) | 1 | 3 packets | 1.96 ms |

**224 consecutive packets is 1.12 seconds of audio.** Not scattered loss —
a hole.

### It is not the Hub, and this time that is proven rather than argued

Across every row from 23:37:03 to 23:41:57, while the dongle was losing
those packets:

- `worstStallMs` **40**, unchanged in all twenty rows
- `worstSendMs` **32**, unchanged in all twenty rows
- `gcPauseMs` +81 ms over 294 s, `gen2Collections` 4 → 5
- `HUB u` **0** — the source never starved

Nothing at the sender moved. And the speaker, receiving the identical
packets from the identical socket in the identical seconds, lost **three**.

### It is not the receive queue either

The queue is 64 packets, 320 ms. A queue overflow drops what will not fit
while the link keeps delivering — scattered singles, which is exactly what
run 29 cured. It cannot produce 224 *consecutive* missing packets. For 1.12
seconds nothing arrived at all.

### What did move

| | dongle | speaker |
| --- | --- | --- |
| RSSI range over the window | **−46 to −62** | **−47, every row** |

Sixteen decibels of swing against a flat line, on two radios in one house
listening to one sender. At 23:39:36 the dongle read −62; in that same poll
`pad` jumped 668 frames and `underruns` 38. At 23:40:23 the poll itself
timed out, and the next row showed loss at 3 349 ppm.

The two nodes are on different access points, but the backhaul is wired and
this is a Hub-sourced stream, so there is no shared wireless leg to blame
(see the correction in run 31). Each node is reached over the wire and then
**one radio hop**. The speaker's hop is flawless; the dongle's is not.

So the fault is in the last hop to the dongle and nothing else: its
antenna, its position, or its access point's channel. It is fading and
recovering on a second timescale while an identical radio thirty feet away
reads the same number twenty times running.

`roams` in `/status` says whether these are fades or re-associations, and
`channel` says whether the two access points are even on the same channel.
Both have been in `/status` all along and neither is being printed.

## Run 31: two consumers, and a confound the whole series may have shared

**2026-08-20, 23:27–23:33.** Both nodes streaming again, Hub 0.20.0, EEE
off. The trace summary:

| | packets | lost | gaps | jitter |
| --- | --- | --- | --- | --- |
| Dongle | 20 533 of 20 534 | **1 (0.005 %)** | 1, isolated | 4.67 ms |
| PartySpeaker | 20 534 of 20 534 | **0 (0.000 %)** | none | 2.40 ms |

Fifty ppm on a node that read 3 700 this morning.

### The nodes are on different access points

The trace's own footnote: *"Nodes on different access points
(7c:10:c9:7a:13:b1 and 7c:10:c9:7a:0b:d0) — three hops, across the mesh
backhaul."*

**That footnote was wrong, twice, and the page has been fixed.**

The backhaul on this network is **wired**, which run 20 already recorded.
The page cannot see how access points are linked to each other and never
could; it asserted a wireless mesh anyway, in a warning colour.

Worse, this was a **Hub-sourced** stream. There is no leg between the two
access points in that path at all: the Hub is on the wire, and each node is
reached over the wire and then one radio hop. Two nodes on two access
points share nothing. The "three hops" reasoning belongs to node-to-node
runs, and the page applied it to a case it does not describe.

So different access points is not a defect here. If anything it is an
advantage — the two consumers are not competing for one radio's airtime.

No run in this series recorded which access point each node was associated
with, and the sticky-BSSID hysteresis in `oal_wifi.c` means a node keeps
whatever it joined at boot. **Every comparison in this series may have been
across two different network paths without saying so.**

Not for want of the data. `/status` has reported `bssid`, `channel` and
`roams` all along, and the comment above `format_wifi` says exactly why:
*"in a mesh every node advertises the same SSID, so a weak RSSI on its own
cannot distinguish 'far from the right access point' from 'attached to the
wrong one'."* Written, shipped, and then left out of every poll line for
nine runs while RSSI was compared between two nodes as though it meant the
same thing on both. The `/wifi/scan` and `/wifi/rejoin` endpoints were
built for this too, and were never used as controls.

That does not overturn the two findings that rest on a node's own
before-and-after — the receive buffer (run 29) and EEE (run 30) — but any
conclusion drawn from comparing the two *nodes* needs re-reading with this
in mind.

### The second stream costs the dongle, not the speaker

After 23:29:51 (adding the speaker restarted the stream and reset the
sender's counters), over 167 seconds:

| | alone (run 29) | with both |
| --- | --- | --- |
| dongle underruns | 1.1/min | **9.7/min** |
| dongle padded frames | 0.63/s | **2.35/s** |
| speaker underruns | — | **0.5/min** |
| speaker loss | — | **0** |

The dongle loses margin when a second stream exists; the speaker does not.
With the topology above, that is what contention on a mesh backhaul would
look like, and it is no longer necessary to invoke anything about the node.

Hub side across the same window: 3.8 late wakes/s, `worstStallMs` 40,
`worstSendMs` 12 — unchanged from the single-stream case. **The Hub does not
care how many consumers there are.** And `gen2Collections` ticked 3 → 4 once,
costing about 10 ms of `gcPauseMs`: a gen 2 collection measured, and still
an order of magnitude below a stall.

### The next control

Move both nodes onto the same access point with `/wifi/rejoin` and repeat.
That is the one comparison this series has never actually run.

## Run 30: Energy Efficient Ethernet, and the end of the GC theory

**2026-08-20, 23:21.** Hub 0.20.0, reporting `gen2Collections` and
`gcPauseMs` beside the send-loop counters. One change made between the two
measurements: **Energy Efficient Ethernet off** on the Intel I219-LM, and
the stream restarted so the high-water marks started clean.

| | EEE on | EEE off | |
| --- | --- | --- | --- |
| `worstStallMs` | 145 | **40–45** | 3× |
| `worstSendMs` | 101 | **11–12** | 8× |
| late wakes | 4.3/s | 4.5/s | unchanged |

A NIC power-saving setting was costing the sender a hundred milliseconds
inside a single send. The I219 parks the link into low-power idle between
bursts, and 200 small packets a second with 5 ms gaps is precisely the
traffic shape that keeps re-triggering it. Nothing in the Hub, nothing in
the firmware, nothing on the air.

Note what did **not** change: the *frequency* of late wakes. EEE was making
each overshoot much worse; it was not making them happen. The loop still
wakes to find more than one packet due about four and a half times a
second, and that was true on this machine before any of this started — the
csproj records the same rate when Workstation GC was chosen.

### The garbage collector is not stopping the sender

Over 106 seconds:

- `gen2Collections` read **3, and never moved**.
- `gcPauseMs` went 86 → 112: **26 ms of pause in 106 seconds**, largest
  single 15-second increment **7 ms**.

Against stalls of 40–45 ms. The runtime pauses this process for a quarter
of a millisecond per second, and no single pause comes close to a stall.

That closes the branch the counters were added to settle. The send loop
already ran on its own thread at AboveNormal, waited on `Thread.Sleep(1)`
rather than the thread pool, and held a 1 ms timer; the one thing left that
could stop it regardless of priority was a blocking collection, and it
measurably is not happening. **The cause is outside the process** — which
is exactly where EEE turned out to be.

### Still open

No storm occurred in this window, so this run says nothing about the 900 ms
events. What it does is remove two candidates and fix one real fault. The
question that remains is whether `worstStallMs` and `worstSendMs` jump when
a storm does — and now they can be read cleanly, without a NIC power state
inflating them.

Suspects still standing, both visible on the same machine: a **Tailscale**
tunnel adapter, whose filter driver sits in the outbound path of every
adapter on the host, and general machine pressure — 6.7 of 7.9 GB of RAM in
use and the CPU running downclocked at 1.89 GHz.

## Run 29: the dongle node beats the speaker

**2026-08-20, 20:22–20:53.** Firmware 0.17.1 on the dongle, speaker still on
0.14.0 as the control, both streaming, polled every 30 s. `rxBufferBytes`
reads **92 928** — sixty-four packets, 320 ms. The option that had been
silently refused since the stream existed is in force.

### Ten minutes, one underrun

The quietest window, 20:43:21 → 20:52:59, 578 seconds:

| | run 28 | run 29 | |
| --- | --- | --- | --- |
| dongle underruns | 5.6/min | **0.10/min** | 55× |
| dongle padded frames | 42/s | **1.03/s** | 41× |
| dongle ring | ~3 000 (62 ms), wandering | **5 760 (120 ms), pinned** | |
| dongle ongoing loss | ~3 700 ppm | **~0** | |

`underruns` read **533 across fifteen consecutive polls.** One dropout in
ten minutes.

And the number that matters most for this whole series — the I²S speaker,
untouched, over the same 578 seconds: **4.9 underruns per minute.**

**The dongle node is now forty-nine times steadier than the soldered DAC.**
Every run from 23 to 28 was an attempt to explain why the dongle was worse.
It is not worse. It was dropping packets it had already received, because a
`setsockopt` call nobody checked had been failing since the beginning.

`paddedFrames` is the measurement to trust here: it counts how often the
ring dipped below three quarters of target, computed inside the node,
independent of any loss accounting. 42/s to 1.03/s.

### The storms are still there, exactly as predicted

Two, in thirty-one minutes:

| | dongle | speaker |
| --- | --- | --- |
| **20:32:40**, 68 s | +138 underruns, +4 598 padded | +762 underruns |
| **20:53:29**, 30 s | +148 underruns, +1 726 padded | +371 underruns |

Run 28's prediction was that this fix would attenuate the storms and not
remove them — 320 ms of receive queue plus a 200 ms ring is 520 ms of
absorption against measured stalls of 900 ms. That is what happened. The
dongle rides them 2.5–5.5× better than the unfixed speaker, and still takes
a visible excursion.

`+4 598` padded frames in one storm is 96 ms of repeated audio: the servo
spending everything it has to keep the ring off the floor.

### The poll itself timed out

At **20:32:40** the log reads `POLL FAILED: The operation has timed out.`
The storm took the *control plane* down with it — an HTTP request from the
Hub machine to a node, nothing to do with the audio path, could not
complete inside its timeout.

That is a strong constraint. Whatever stalls the stream also stalls
unrelated TCP between the same two machines, which puts it in the network
rather than anywhere in the audio pipeline.

### The open question is the Hub machine's own link

Runs 27–29 all show the same shape: both nodes stalling in the same second,
RTT spiking to both at once, and now an HTTP timeout too. Every one of
those observations is taken **from the Hub PC**, over the Hub PC's own link
to the access point.

If that machine is on Wi-Fi, its uplink entering and leaving power save
would produce every symptom recorded here — packets held then flushed,
both nodes affected identically, pings and HTTP spiking together. It is the
one shared element that has never been tested, and testing it costs
nothing: put the Hub on Ethernet and run the same poll.

## Run 28: a hundred and thirty-five underruns, and not one lost packet

**2026-08-20, 19:36–19:56.** Run 27's discriminator, executed: a
half-second ping to both nodes logged with timestamps, alongside the
half-minute poll, with RSSI added. Twenty minutes, both nodes streaming,
Hub polled on the same line.

### The speaker lost nothing and underran anyway

`lossPpm` read **0** on the speaker for nineteen consecutive polls —
19:36:34 through 19:54:48, zero lost packets — while `underruns` went from
9 622 to 9 757.

**135 dropouts with a complete packet sequence.** Every packet arrived.
The ring emptied anyway.

There is only one way for that to happen: the packets arrived **late**. Not
lost, not corrupted, not refused — late, and in bursts, with the ring
drained dry in the gap before the burst landed.

Runs 23 through 26 measured loss and argued about loss. Run 27 caught the
rings going full before they went empty. This is the confirmation, and it
comes from the node's own sequence counter rather than from any inference:
**the problem is latency, and it always was.**

### And the dropouts are one event, not a rate

| window | speaker underruns |
| --- | --- |
| 19:36:34 → 19:37:07 | +12 |
| **19:37:07 → 19:37:37** | **+93** |
| 19:37:37 → 19:54:48 (17 min) | +30, or 1.8/min |

Ninety-three of the hundred and thirty-five landed in one thirty-second
window. Here is the ping log across that window:

```
19:37:05 dongle=  122 spk=  456
19:37:06 dongle=  468 spk=  885
19:37:08 dongle=   34 spk=  599
19:37:09 dongle=   28 spk=  174
```

Baseline is 2–10 ms. The poll at 19:37:07 caught the speaker's ring at
`buf=0`.

**A 900 ms round-trip excursion, on both nodes at once, emptied a 200 ms
buffer.** That is the mechanism, caught in the act, with a clock on both
logs.

### The buffer is smaller than the stalls

`CAPACITY_PACKETS 40` × 240 frames is **200 ms**, target fill 100 ms. The
network demonstrably stalls for **900 ms**. No servo, trim or pad rescues a
ring from a stall four times its own depth; it can only refill faster
afterwards.

### What the ping does and does not prove

Excursions arrive on both nodes in the same second — 19:37:06, 19:39:08,
19:51:14, 19:52:45, 19:55:09 — which is a shared cause, not two nodes
having private trouble. Two independent devices do not stall together.

But an ICMP reply from an ESP32 waits on that node's scheduler, so RTT here
measures the node as well as the path, and the ping leaves from the Hub PC
over the Hub PC's own link. **This does not yet separate the Hub's machine
from the access point.** Adding the gateway as a third ping target does:
gateway flat while nodes spike puts the stall on the AP→node leg; gateway
spiking with them puts it at or before the AP.

### The other half: the dongle loses and the speaker does not

In the same twenty minutes, on the same access point:

| | dongle | speaker |
| --- | --- | --- |
| RSSI | **−48 dBm** | −52 dBm |
| `lossPpm` | **3 500–4 000** | **0** |
| underruns | 5.6/min | 1.8/min |
| padded frames | 42/s | (no servo on 0.14.0) |

**Better signal, more loss.** Four decibels better, and 3 500 ppm against
nothing at all. Whatever this is, it is not the radio, and after five
attributions to the radio that is worth stating plainly.

The comment above the socket setup in `oal_stream_consumer.c` named this
failure before it was ever seen:

> the drops would be counted as network loss — measuring our own scheduling
> and calling it Wi-Fi.

It then asked for a receive buffer sixteen packets deep to prevent it — and
the request was never granted. lwIP compiles `SO_RCVBUF` in only when
`CONFIG_LWIP_SO_RCVBUF` is set; this build never set it, so `setsockopt`
returned an error every time, and the return value was discarded. The
actual ceiling was `CONFIG_LWIP_UDP_RECVMBOX_SIZE` — a handful of packets,
tens of milliseconds. A node held off the socket for longer than that drops
what it has already received, and the node hosting a USB stack is held off
more often than the one driving I²S.

Fixed in firmware 0.17.0: both options set, sixty-four packets (320 ms), and
the result **logged rather than assumed** — the failure mode here was a
silent no-op, so the next build reports what it actually got.

That is a node-side attribution, and this document has been wrong about
five of those. What makes this one different is that it does not rest on a
theory of the radio: it rests on two nodes measured in the same second,
where the one with the better signal lost more.

## Run 27: both nodes, one minute apart, for forty-one minutes

**2026-08-20.** The first run in this series with a control. Dongle
Consumer on **0.16.0** (the pad servo) and the I²S speaker deliberately
left on **0.14.0**, both in one cast point playing the same source, both
polled every 60 s — and, new this time, the Hub's own `/api/stream` polled
on the same line.

### The Hub never starved

`underrunSamples` read **297 120 in all forty rows**. Not one sample of
source starvation in forty-one minutes, through everything below. The
counter is a fixed backlog from stream start, and it did not move.

That clears the Hub's *source* — the decoder feeding the encoder. It does
**not** clear the Hub's send path, which has no counter of its own. Keep
that distinction; the rest of this entry needs it.

### A twelve-minute storm, on both nodes, at the same time

| window | | dongle 0.16.0 | speaker 0.14.0 |
| --- | --- | --- | --- |
| 1300–1724 s | underruns | 18/min | 28/min |
| | loss (approx.) | 4 100 ppm | 2 100 ppm |
| **1724–2427 s** | **underruns** | **171/min** | **611/min** |
| | loss (approx.) | 29 000 ppm | 5 800 ppm |
| 2427–3754 s | underruns | 11/min | 8/min |
| | loss (approx.) | 800 ppm | 2 700 ppm |

Loss is differentiated from the cumulative `lossPpm` against 200 pps and
node uptime, so treat it as a rate, not a reading.

The storm **starts on both nodes in the same minute and ends on both in the
same minute.** Nothing in either firmware changed at 1724 s or at 2427 s.
Two nodes, two different output stages, two different firmware versions,
one event.

### The rings went full before they went empty

At 1789 s the dongle's ring read **9 600** and the speaker's **9 595** —
both at capacity, in the same sample. Four hundred seconds later the
dongle read **1**, then **240**.

Full, then empty, then full. That is not loss; steady loss drains a ring
and keeps it drained. That is delivery **stalling and then flushing** —
something holds packets and releases them in a burst, twice a minute,
swinging both nodes across a 200 ms buffer end to end.

Every previous run in this series measured loss and reasoned about loss.
This is the first one to catch the jitter, and the jitter is bigger than
the buffer.

### The control node, on old firmware, was hit three times harder

The speaker underran **3.6× more than the dongle** during the storm while
losing about **five times fewer packets**. The difference between them is
the pad servo, and this is the first direct evidence that it works: the
dongle absorbed five times the loss with a quarter of the dropouts, at a
cost of 75 padded frames per second while it was converging (1.6 ms of
repeated audio per second, ~1 500 ppm — under three cents of pitch, and it
falls to 8 frames/s once the storm passes).

The servo does not fix the cause. It buys about a factor of twenty in how
much loss a node can eat before it is audible.

### What is left

Three candidates survive, and the flat Hub counter kills none of them:

1. **The Hub's send path** — a stall in the sender (scheduling, GC, timer
   resolution) bursts packets without touching the source counter.
2. **The access point** — buffering, a background scan, or its own CPU.
3. **Airtime** — 400 pps of small unicast frames to two stations, with
   retries on the weaker one stealing time from the other.

The discriminator is **latency, not loss**: run a timestamped ping to both
nodes alongside the poll. If RTT spikes on both at the moment the rings
swing, the stall is in the network and the Hub is innocent. If RTT stays
flat while the rings swing 200 ms, the stall is in the Hub's sender.

That test needs no firmware. Note that five firmware mechanisms have now
been proposed and eliminated across runs 23–26, and this run's control node
— untouched, on 0.14.0 — took the worst of it.

## Run 26: the speaker loses just as much, and that ends the argument

**2026-08-20.** The I²S speaker, `/stream` read while playing, on the same
access point at −49 dBm:

| | speaker | dongle node |
| --- | --- | --- |
| `lossPpm` | **1 963** | **2 307** |
| underrun every | **380 s** | **11 s** |
| trimmed frames per second | 0.99 | 0.02 |
| ring | 60 ms | 50 ms |

**The same loss. Thirty-six times the dropouts.** Every mechanism proposed
across runs 23–25 was an attempt to explain why the dongle node lost more
packets, and it never did. The network loses about 2 000 ppm to both nodes,
and has all along.

### What actually differs is how much margin each ring had left

The speaker trims about one frame a second: its fill rides high, so a gap
eats into depth it can spare. The dongle trims one frame every fifty
seconds — no surplus — so every gap costs depth **permanently**.

`oal_playout.c` has said why since it was written, in the comment above the
underrun path: *"sender and DAC run at the same average rate: once the
margin is spent, only not playing rebuilds it."* At the natural surplus of
one frame a second, rebuilding 50 ms takes **forty minutes**, and this node
underran every 10.5 seconds. It is a vicious circle: an underrun inserts
silence, silence is extra consumption, the ring sinks further.

`ROADMAP.md` records the same gap from the other side — "nothing pulls the
fill toward the target" — noted there as a ring riding *high* and never
coming down. The floor is where it bites.

### The fix is the trim, backwards

The trim drops single frames to walk the fill down. Firmware 0.16.0 adds
the missing half: while the fill is below three quarters of target, repeat
one frame occasionally. Repeating a frame is consuming slower, which is the
only thing that rebuilds margin.

Two speeds, because the cost is a pitch error while it converges: one frame
per chunk when the ring is under half target (0.42 %, about seven cents,
worth it when it is nearly empty), one per four chunks otherwise (0.10 %,
under two cents — inaudible, and fifty times the natural surplus).

Counted as `paddedFrames` beside `trimmedFrames`, so the servo is visible
rather than assumed.

**This changes the speaker too**, whose ring also sits below target, and
that is deliberate — but it is the first change in this whole sequence to
touch a node that was working. Watch its underrun rate as well as the
dongle's.

## Run 25: a day of it, and five explanations eliminated

**2026-08-20, 7.3 hours continuous.** Dongle Consumer, firmware 0.15.6, on
the good access point at −49 dBm.

| | |
| --- | --- |
| underruns | 2 501 in 26 358 s — one every **10.5 s** |
| speaker, same AP, lifetime | one every **356 s** — 34× fewer |
| silence inserted | 60.4 s |
| audio lost on air | 56.6 s |
| loss | 2 307 ppm, 4 565 events, only 30 % a single packet |
| gaps | 2.48 packets mean, **longest 12 — 60 ms** |
| jitter / `tooLate` | 0.88 ms / **0** |

**The underruns are the loss** for the third measurement running. And the
shape says what kind of loss: jitter under a millisecond and nothing
arriving too late, so packets that arrive are on time. This is not a
marginal link delivering late. A twelve-packet gap is 802.11 retries
failing repeatedly for **60 milliseconds** — the channel being unavailable,
not weak.

### And the channel is clean, so that is not it either

`GET /wifi/scan` from the node, the endpoint added the same morning:

| SSID | channel | RSSI | |
| --- | --- | --- | --- |
| valfrid-n | 7 | −43 dBm | current |
| valfrid-n | 7 | −73 dBm | the other mesh radio |
| shellyuni | 2 | −81 dBm | |
| The Matrix | 2 | −84 dBm | |
| The Matrix | 2 | −88 dBm | |
| ChargeAmps | 11 | −91 dBm | |

**No foreign network on channel 7.** Everything else is 38 dB down and on
another channel. Co-channel contention from neighbours is eliminated; the
only company is the house's own second mesh radio.

Worth noting for the network rather than for this: **both mesh radios sit on
channel 7**, thirty decibels apart. Whether that is deliberate for roaming
or an artefact, it means every frame either one sends is airtime the other's
clients defer to.

### Five mechanisms, five eliminated

Recorded together because the pattern is the finding:

| Proposed | Killed by |
| --- | --- |
| USB tasks contending on Wi-Fi's core | moving them changed almost nothing |
| Receive path scheduled onto the USB core | pinning to core 0 changed almost nothing |
| The access point | 7 s on the bad AP, 7–10 s on the good one |
| Radio noise from the dongle | Class AB, full-speed USB — no plausible path to 2.4 GHz |
| Channel congestion | channel 7 is clean |

What every one of them has in common: it was proposed from data that could
not distinguish it from the alternatives, and each cost a firmware release.
**The two experiments that would discriminate have still not been run**, and
both are free.

### The two that are left, and they are not firmware

The node has never been separated from its location or from its dongle.

**Unplug the dongle, ten minutes, same place.** Holds room, antenna,
access point and minute constant; changes only whether a dongle draws
current and moves data. `outputReady` goes false and playout leaves the ring
alone, but the RTP statistics keep counting.

**Carry the node to the speaker's room, dongle attached.** Changes the
location and nothing else.

Between them: loss that follows the dongle, loss that follows the location,
or loss that follows the board — and the third is worth naming, because
**this is a different XIAO from the one in the speaker**, on an external
antenna the speaker does not have.

## Run 23: the access point matters, and the signal does not

**2026-08-20.** The dongle Consumer (`docs/USB-AUDIO.md`) against the I²S
speaker in the same house, on the same mesh SSID, same channel 7. Four
readings, taken over two days while chasing what looked like a fault in the
USB output stage.

| | AP | RSSI | underrun every | silence |
| --- | --- | --- | --- | --- |
| dongle, overnight | `…13:b1` | −47 dBm | 7 s | 0.354 % |
| dongle, after roaming | `…0b:d0` | −73 dBm | **32 s** | **0.062 %** |
| dongle, now | `…13:b1` | −74 dBm | 5 s | 0.495 % |
| speaker, lifetime | `…0b:d0` | −53 dBm | **356 s** | **0.006 %** |

> **Corrected the same day, before this entry was a day old.** The good
> reading below is 193 seconds and six events. A 21-minute run on the *same*
> good access point, at −45 dBm, gives **one underrun every 7.4 seconds** —
> the same as the bad one. The access point is not the dominant variable,
> and the table below reads like it is only because two of its four rows are
> short samples. What survives the correction is that **RSSI predicts
> nothing**, which turns out to matter more than the AP did. See run 24.

**Four for four on the access point. Nothing on the signal.** −47 dBm on
`13:b1` is worse than −73 dBm on `0b:d0`, by a factor of five. The two
readings with the best signal are the two worst results.

This is worth writing down because it wasted most of a day. The dongle node
and the speaker were compared directly and the dongle came out 61 times
worse — and that number was attributed to the USB host stack, twice, with
two firmware changes made on the strength of it. **They were on different
access points the whole time.** On the same access point the gap is about
11×, on a short sample, and may yet be nothing.

Method step 1 in this document says to record RSSI before anything else.
That is no longer sufficient: **record the BSSID too.** A mesh SSID looks
like one network from the node's status and is not one, and a node that
roams between two of them changes its link quality without changing
anything a signal reading would show.

The bad AP is presumably a satellite whose backhaul is the constraint —
decision 2 already records that shape from run 18, where two nodes three
hops apart across a mesh lost the occasional sample and it was called a
network condition rather than a result.

**A second sign, on the node rather than the air.** In the run on `13:b1`
above, 3.0 s of silence was inserted while packet loss accounts for only
0.98 s — three times more. The ring simultaneously overflowed (22 463
frames dropped) and ran dry (112 underruns), sitting *above* its target at
5 520 of 4 800. Loss alone does not do that. Packets arriving in clumps
does, which is what a congested backhaul looks like from the far end.

## Run 22: the best link this project has measured

**2026-08-14, overnight, unattended.** Internet radio through the Hub to
one Consumer, left running after the FLAC bring-up. Hub 0.14.0, firmware
0.14.0, wired mesh backhaul and wired Hub — the same network as run 19.

| | |
| --- | --- |
| Duration | 4 912 606 packets — **6 h 49 min** |
| Received | 4 912 602 of 4 912 606 |
| Lost | **4 packets — 0.00008 %** |
| Loss shape | 3 gaps, 1.33 packets per gap, longest 2 |
| Consumer signal | **−44 dBm** — the access point is close to this speaker |

Four packets in nearly seven hours. Twenty milliseconds of audio missing
in total, in three isolated events, none longer than two packets.

### It is the signal strength, and there is now a curve

Not the code. Run 19 measured 0.061 % over 6 h 26 min on **the same
network** with the wired backhaul already in place, and nothing changed
between them that could account for 750×: the codec cannot matter, since
the wire carries 48 kHz L24 at 200 packets per second whatever the
source, and the sender's late wakes and worst stalls overlap between the
two runs.

What did change is where the speaker sits relative to the access point.
At **−44 dBm** this consumer has a very strong link, and run 20 already
measured the two points below it:

| Consumer signal | Loss | Longest gap | Run |
| --- | --- | --- | --- |
| −67 dBm | 0.361 % | 44 packets — 220 ms | 20, same AP |
| −53 dBm | 0.144 % | 4 packets — 20 ms | 20, across APs |
| **−44 dBm** | **0.00008 %** | **2 packets — 10 ms** | 22 |

Three points, monotonic, and steep: **23 dB of link margin spans four
orders of magnitude in loss.** That is the retry mechanism showing its
shape. A unicast frame is acknowledged and retried; with margin to spare
it succeeds on the first attempt, and as margin falls the retries climb
until the budget is exhausted and a packet is finally lost. Loss is not
really a measure of the air — it is a measure of how often the retries
ran out.

It also explains the gap lengths, which matter more than the percentages:
weak links lose *runs* of packets because the condition that beat the
retries persists for milliseconds, while a strong link loses the odd
isolated frame.

So this is a result about **placement**, and it is worth more than a code
change: moving a speaker closer to an access point, or adding a point
near a speaker, buys more than anything in this repository has.

### The method gap this exposed

**Run 19 did not record the consumer's signal strength**, which is why
this took a message from the operator to explain rather than a glance at
the table. Every run from here records it — it is now the first line of
the method, because on the evidence above it predicts the result better
than any other single number.

### What it does establish, independent of the loss figure

**Nearly seven hours of continuous reception**, 4.9 million packets, no
reboot, no reconnect, three isolated single-packet events. Whatever the
air was doing, the Hub decoded Ogg-FLAC through a native library across a
P/Invoke boundary for almost seven hours without a leak, a crash, or a
stall worth noticing — on code whose first frame had been decoded only
hours earlier. That is the part worth trusting.

## Method

1. **Record every node's signal strength before anything else.** Run 22
   reached 0.00008 % at −44 dBm where run 20 lost 0.361 % at −67, and
   those three points make a monotonic curve four orders of magnitude
   deep. Nothing else in this document predicts a result as well. A run
   without an RSSI figure cannot be compared with another run, which is
   what made run 19 impossible to explain for a day.
2. Put both nodes on the same access point. They pick by signal strength,
   so when they sit equidistant between two mesh points they will split.
   Power down the far point, or move them within a metre of the near one,
   then reboot both so they re-scan.
3. Select the producer and consumers, source **Pattern** (the tone is
   audible but unverifiable), and press **Start link**.
4. Let it run several minutes. Loss at these rates is a few events per
   minute; a 30-second run measures noise.
5. Read the access-point line under the results before trusting anything
   above it.
6. Subtract the producer's send errors from the consumer's loss.
7. When comparing two firmware images, measure A, then B, then A again.
   The radio environment drifts on its own and has done so by three orders
   of magnitude in an evening; without the second A there is no way to tell
   a change in the code from a change in the air.
8. If a run disagrees with the established baseline, restart the access
   points, the nodes and the Hub, and re-run the baseline before believing
   anything. Runs 8-10 above are what happens otherwise.
9. The second A is only needed when B disagrees with the first. Runs 12 and
   14 both lost nothing at all, and a third run cannot explain a difference
   that is not there.
