# OpenAudioLink Audio Transport (RTP)

Version: 0.1 (draft)
Part of protocol-suite 0.1.

## Overview

Audio travels from the active Producer directly to the selected Consumers
as RTP over UDP. The Hub coordinates the stream but does not relay it.

OpenAudioLink deliberately uses a **standard, publicly specified payload
format** rather than a private one, so any conforming RTP receiver can
decode an OpenAudioLink stream. This makes third-party software a
first-class verification tool: a Windows Producer can be proven correct
against GStreamer, ffmpeg or a professional AES67 device before any
OpenAudioLink receiver firmware exists.

The profile below is a subset of **AES67-2018**, the professional
audio-over-IP interoperability standard (the common ground between Dante,
RAVENNA and Livewire+). OpenAudioLink does not claim full AES67 compliance
in 0.1 — clock synchronisation is the gap (see *Deviations*) — but the
packets on the wire are AES67-shaped, so standard tooling decodes them.

## Payload format

| Property        | Value                                              |
| --------------- | -------------------------------------------------- |
| Encoding        | `L24` — 24-bit linear PCM (RFC 3190)               |
| Sample rate     | 48 000 Hz                                          |
| Channels        | 2 (stereo), interleaved left then right            |
| Byte order      | **Big-endian** (network order), most significant byte first |
| Payload type    | Dynamic, default `96`, declared in SDP             |
| RTP clock rate  | 48 000 (one tick per sample per channel)           |

`L16` (16-bit, RFC 3551) is a permitted alternative for constrained links
and is declared the same way; 24-bit is the reference format.

> **Byte order is the classic implementation trap.** RTP L24/L16 are
> big-endian, while Windows WASAPI, I²S peripherals and ESP32 memory are
> little-endian. The Producer must byte-swap on packetisation and the
> Consumer on depacketisation. A stream that "plays as loud static" is
> almost always a missing swap.

## Packetisation

One RTP packet carries a whole number of frames (a frame = one sample for
every channel). Packet time (`ptime`) is a stream property:

| ptime  | Frames/packet | Payload bytes | Packets/s | Notes                          |
| ------ | ------------- | ------------- | --------- | ------------------------------ |
| 1 ms   | 48            | 288           | 1000      | AES67 baseline; wired networks |
| 5 ms   | 240           | 1440          | 200       | **OpenAudioLink default**      |

The 5 ms default is a deliberate deviation from the AES67 baseline: at
1 ms an ESP32 receiver must service 1000 packets per second over Wi-Fi,
which costs far more CPU and airtime than the 5 ms equivalent while saving
4 ms of latency that a multi-room system does not need. A 5 ms L24 stereo
payload (1440 bytes) plus RTP and IP/UDP headers still fits inside a
1500-byte MTU without fragmentation — do not exceed it.

Implementations must accept any `ptime` announced in SDP and must not
assume a fixed packet size.

## RTP header

Standard RTP (RFC 3550), 12-byte header, no extensions, no CSRCs:

| Field           | Value                                                    |
| --------------- | -------------------------------------------------------- |
| Version         | 2                                                        |
| Padding, Extension, CC | 0                                                 |
| Marker          | 0, except on the first packet of a stream or after a deliberate discontinuity |
| Payload type    | As declared in SDP (default 96)                           |
| Sequence number | Random start, increments by one per packet, wraps at 2^16 |
| Timestamp       | Media clock in samples; increments by frames-per-packet   |
| SSRC            | Random per stream, stable for the stream's lifetime       |

The timestamp is a *media* clock, not a wall clock: it advances by exactly
the number of frames sent, regardless of when the packet was transmitted.
Receivers use it for jitter buffering and drift correction.

## Addressing

- **Unicast (default).** The Controller assigns each Consumer a UDP port
  from 41100 upward (even ports for RTP, the following odd port reserved
  for RTCP), and the Producer sends a copy to each selected Consumer.
- **Multicast (optional).** The Controller may instead assign a group from
  `239.69.0.0/16`, the range conventionally used for AES67 streams.

Unicast is the default because OpenAudioLink nodes are on Wi-Fi, where
multicast frames are transmitted at a low basic rate without
acknowledgement and are noticeably less reliable than unicast. Multicast
becomes attractive on wired segments or with many receivers.

## Stream description (SDP)

Every stream is described by an SDP document (RFC 4566), which is what
lets standard receivers decode it. The Hub serves the SDP for a stream
over HTTP so tools can be pointed straight at a URL.

```text
v=0
o=- 1 1 IN IP4 192.168.1.10
s=OpenAudioLink Kitchen
c=IN IP4 192.168.1.40
t=0 0
m=audio 41100 RTP/AVP 96
a=rtpmap:96 L24/48000/2
a=ptime:5
a=recvonly
```

An AES67 stream additionally carries `a=ts-refclk:` and `a=mediaclk:`
attributes identifying the PTP grandmaster and media clock offset;
OpenAudioLink 0.1 omits them (see *Deviations*).

## Verifying a Producer with third-party software

A Producer is correct when software that has never heard of OpenAudioLink
plays its stream. Recommended checks, in order:

**GStreamer** (most reliable L24 support, no SDP file needed):

```bash
gst-launch-1.0 udpsrc port=41100 \
  caps="application/x-rtp,media=(string)audio,clock-rate=(int)48000,\
encoding-name=(string)L24,channels=(int)2,payload=(int)96" \
  ! rtpjitterbuffer ! rtpL24depay ! audioconvert ! autoaudiosink
```

**ffmpeg / ffplay** (point at an SDP file or the Hub's SDP URL):

```bash
ffplay -protocol_whitelist file,rtp,udp -i stream.sdp
```

**Wireshark** for transport-level truth: *Decode As → RTP*, then
*Telephony → RTP → Stream Analysis* reports lost packets, jitter and
sequence errors — the quickest way to separate "the format is wrong" from
"the network is dropping packets".

An AES67-capable receiver (a RAVENNA/Dante device, or the open-source
`aes67-linux-daemon`) is the strongest interoperability check, though
without PTP it will report the stream as unsynchronised.

## Deviations from AES67 in 0.1

| Area              | AES67                          | OpenAudioLink 0.1                     |
| ----------------- | ------------------------------ | ------------------------------------- |
| Clock             | PTP (IEEE 1588-2008) mandatory | Producer's own clock; receivers correct drift against the RTP timestamp |
| Default `ptime`   | 1 ms                           | 5 ms (1 ms selectable)                |
| Discovery         | SAP announcements (RFC 2974)   | OpenAudioLink discovery + SDP over HTTP |
| RTCP              | Required                       | Not sent in 0.1                       |
| Transport         | Multicast typical              | Unicast default, multicast optional   |

None of these affect whether a standard receiver can decode the audio;
they affect sample-accurate synchronisation between multiple receivers,
which is Phase 3 work. Adding PTP later does not change the payload
format, so streams produced now stay decodable.

## Revision history

- 0.1 — initial draft: AES67-subset profile, L24/48 kHz/stereo, 5 ms
  default packet time, unicast default, SDP description.
