# OpenAudioLink Hub

The Hub is the centre of an OpenAudioLink system. Per the Phase 1
architecture it implements the Controller, the Producer for Windows-hosted
sources, and the Provisioner.

Current state: health endpoint, JSON configuration storage, device
inventory, discovery listener/announcer, web UI, device commands
(reboot) and OTA management (upload firmware images, push updates to
devices, which pull them from `/firmware/{file}`). Audio capture, RTP
production and USB flashing come in later phases.

## Projects

| Project                    | Purpose                                            |
| -------------------------- | -------------------------------------------------- |
| `OpenAudioLink.Core`       | Device model and protocol-suite implementation (no host dependencies) |
| `OpenAudioLink.Hub`        | ASP.NET Core host: REST API, web UI, discovery service; runs as console app or Windows service |
| `OpenAudioLink.Core.Tests` | xUnit tests for the Core library                   |

## Running

### Without any toolchain (Windows)

Every CI run publishes a self-contained win-x64 build — no .NET installation
required. On GitHub go to **Actions → the latest CI run → Artifacts** and
download `OpenAudioLink-Hub-win-x64`, extract it, and run
`OpenAudioLink.Hub.exe`. Windows Firewall will ask to allow network access
on first start; allow it on private networks so discovery (UDP 41000) and
the web UI (TCP 41080) work.

### From source

```bash
dotnet run --project src/OpenAudioLink.Hub
```

Then open <http://localhost:41080>. The API lives under `/api`:

- `GET /api/health` — Hub identity, version, protocol-suite version
- `GET /api/devices` — discovered device inventory
- `GET /api/devices/{id}` — single device by identity

The Hub participates in discovery per `protocol/DISCOVERY.md`: it listens
for announces on UDP 41000 (multicast group 239.255.41.10), probes on
startup, and announces itself every 5 seconds.

Persistent Hub state (identity, name) is stored as JSON in the data
directory (`Hub:DataDirectory`, default `./data` next to the binary).

## Verifying the audio path without hardware

The Hub can stream a generated tone as RTP, so the wire format can be
proven before capture code or receiver hardware exists. Start one aimed
at the machine running your player:

```bash
curl -X POST http://localhost:41080/api/test-tone \
  -H "Content-Type: application/json" \
  -d '{"address":"192.168.1.20","port":41100}'
```

Receive it with **GStreamer**, which has the most dependable L24 support:

```bash
gst-launch-1.0 udpsrc port=41100 \
  caps="application/x-rtp,media=(string)audio,clock-rate=(int)48000,\
encoding-name=(string)L24,channels=(int)2,payload=(int)96" \
  ! rtpjitterbuffer ! rtpL24depay ! audioconvert ! autoaudiosink
```

or with **ffplay**, pointing at the Hub's generated SDP:

```bash
ffplay -protocol_whitelist file,rtp,udp,http -i http://localhost:41080/api/test-tone.sdp
```

**VLC** does not reliably handle L24; start the stream with
`"encoding":"L16"` for it, then open the same SDP URL. Stop with
`curl -X DELETE http://localhost:41080/api/test-tone`.

Hearing a clean 1 kHz tone proves packetisation, byte order, timestamps
and pacing end to end. Wireshark (*Decode As → RTP*, then *Telephony →
RTP → Stream Analysis*) separates format problems from packet loss.

## Tests

```bash
dotnet test
```
