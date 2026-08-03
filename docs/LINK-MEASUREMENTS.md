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
