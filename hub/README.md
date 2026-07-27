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

## Test tone

The Hub can stream a generated sine tone as RTP. It is a permanent
diagnostic, not a development stopgap: sending a tone to a receiver
answers "is this speaker working?" without involving any source, and
sending one to your own machine proves the Hub's output path.

In the web UI, the **Test tone** section streams either to a discovered
receiver or to the computer you are browsing from. Addressing a device by
name rather than address means the tone follows it if DHCP moves it.

The same thing over the API — with no destination given, the tone is sent
to whoever made the request:

```bash
curl -X POST http://localhost:41080/api/test-tone \
  -H "Content-Type: application/json" -d '{}'

# or explicitly, by device or by address
curl -X POST http://localhost:41080/api/test-tone \
  -H "Content-Type: application/json" \
  -d '{"deviceId":"mac-a0b1c2d3e4f5","frequencyHz":1000}'
```

Because RTP here is push-based, nothing streams until asked and the Hub
must know where to send: a player cannot "connect" to the Hub, it only
listens for packets that arrive.

### Receiving it on a computer

**GStreamer** has the most dependable L24 support and is the recommended
receiver. On Windows, install the **MSVC 64-bit runtime** package from
<https://gstreamer.freedesktop.org/download/> and choose the **Complete**
install — a typical install can omit plugin sets, and `rtpL24depay` lives
in gst-plugins-good. Confirm it is present before anything else:

```text
gst-inspect-1.0 rtpL24depay
```

If that reports "no such element", the plugin set is missing; rerun the
installer and pick Complete. If the command is not found at all, add the
bin directory to PATH — by default
`C:\gstreamer\1.0\msvc_x86_64\bin`.

Then, as a single line (Windows `cmd`, or any shell):

```text
gst-launch-1.0 udpsrc port=41100 caps="application/x-rtp,media=(string)audio,clock-rate=(int)48000,encoding-name=(string)L24,channels=(int)2,payload=(int)96" ! rtpjitterbuffer ! rtpL24depay ! audioconvert ! autoaudiosink
```

The same broken across lines for readability on Linux and macOS:

```bash
gst-launch-1.0 udpsrc port=41100 \
  caps="application/x-rtp,media=(string)audio,clock-rate=(int)48000,\
encoding-name=(string)L24,channels=(int)2,payload=(int)96" \
  ! rtpjitterbuffer ! rtpL24depay ! audioconvert ! autoaudiosink
```

**ffplay** is a lighter alternative — a zip rather than an installer —
and can be pointed straight at the Hub's generated SDP:

```text
ffplay -protocol_whitelist file,rtp,udp,http -i http://localhost:41080/api/test-tone.sdp
```

**VLC** does not reliably handle L24; select the L16 format in the UI (or
`"encoding":"L16"` over the API), then open the same SDP URL.

Hearing a clean 1 kHz tone proves packetisation, byte order, timestamps
and pacing end to end. Wireshark (*Decode As → RTP*, then *Telephony →
RTP → Stream Analysis*) separates format problems from packet loss.

### If nothing plays

- **Run the Hub and the player on the same machine first.** That removes
  the network and the firewall from the picture entirely; the UI's
  "This computer" default resolves to loopback when you browse from the
  same PC.
- **Windows Firewall** silently blocks inbound UDP from another machine.
  Allow the receiving application on private networks, or test locally
  first.
- **Check packets are arriving at all** with Wireshark filtered on
  `udp.port == 41100`. Packets arriving but no sound points at the
  pipeline or plugin; no packets points at the network, the firewall, or
  a stream that was never started.

## Tests

```bash
dotnet test
```
