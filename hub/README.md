# OpenAudioLink Hub

The Hub is the centre of an OpenAudioLink system. Per the Phase 1
architecture it implements the Controller, the Producer for Windows-hosted
sources, and the Provisioner.

Current state: health endpoint, JSON configuration storage, device
inventory, discovery listener/announcer, web UI, device commands
(reboot), OTA management (upload firmware images, push updates to
devices, which pull them from `/firmware/{file}`), and RTP audio
production from a test tone or from this computer's audio via WASAPI
loopback. USB flashing and clock synchronisation come later.

The UI is a **web application** served by the Hub itself at
<http://localhost:41080> — there is no desktop window. That means it is
reachable from any device on the network, so a phone or tablet works as
a remote control.

## What has been verified

Exercised end to end against GStreamer as an independent receiver:

- 1 kHz test tone, L24, decoded correctly — proving packetisation, RTP
  timestamps, big-endian byte order and send pacing.
- System audio captured with WASAPI loopback and played back at correct
  pitch and speed, with local playback unaffected.
- The same, from a Hub on one machine to a player on another over Wi-Fi,
  including discovery finding the remote machine.

Not yet exercised: the ESP32 receiver, multi-destination streaming
against real receivers, sustained multi-hour streaming, and clock
synchronisation between receivers.

A stream may have several destinations, each receiving identical packets
by unicast replication. Planning threshold is about **4 receivers** per
Producer over Wi-Fi, capped at 8 per stream; see `docs/DECISIONS.md` for
why, and for when multicast is the better choice.

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

### As a Windows service

For a machine that should run the Hub permanently, the artifact includes
install scripts. From an **elevated** PowerShell prompt, in the folder
you extracted:

```powershell
cd <the folder you extracted>\scripts
Set-ExecutionPolicy Bypass -Scope Process -Force
Get-ChildItem -Recurse | Unblock-File
.\install-service.ps1
```

An elevated prompt opens in `system32`, so the `cd` is needed even if you
were already in the right place.

Three details that are easy to trip over, all of them PowerShell rather
than us:

- **The `.\` prefix is required.** PowerShell does not run scripts from
  the current directory without an explicit path.
- **Scripts extracted from a downloaded zip are blocked** by the default
  `RemoteSigned` policy. `Unblock-File` clears that mark, and the
  execution-policy change above applies only to that one PowerShell
  process, leaving the machine's setting untouched.
- **The prompt must be elevated**, or the script stops immediately —
  registering a service and writing firewall rules both need it. Use
  right-click → *Run as administrator*; there is no way to elevate an
  already-open prompt.
- **`Bypass` is a value, not a switch**: it is
  `Set-ExecutionPolicy Bypass -Scope Process`, not
  `Set-ExecutionPolicy -Bypass`.

That copies the Hub to `C:\Program Files\OpenAudioLink`, keeps its state
in `C:\ProgramData\OpenAudioLink`, registers a service that starts
automatically at boot and restarts after a crash, opens the firewall for
TCP 41080 and UDP 41000, and starts it.

Useful details:

- **State lives in ProgramData**, not beside the executable, so upgrading
  by re-running the script keeps the Hub's identity and uploaded
  firmware. Re-running is the supported upgrade path — it stops, replaces
  and restarts.
- **Delayed automatic start**, so the network stack is up before
  discovery binds its multicast socket.
- **Restart on failure** after 5 s, then 10 s, then every 60 s.
- **Logs go to the Windows Event Log** (Application), since a service has
  no console.
- `.\scripts\uninstall-service.ps1` removes the service and firewall
  rules, keeping files and data unless given `-RemoveFiles`.

> **Capturing this computer's audio does not work from a service.**
> Services run in session 0 and cannot reach a logged-in user's audio
> endpoints, so *Stream this computer's audio* will fail. Discovery,
> device control, OTA, the test tone and the web UI all work normally.
> To stream a PC's audio, run the Hub as a console application on that
> PC — see `docs/WINDOWS-AUDIO-CAPTURE.md`.

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

## Streaming this computer's audio

The Hub captures whatever is playing on the Windows default output device
using WASAPI loopback and streams it as RTP. Local playback is
unaffected: the PC keeps its own sound while receivers get a copy, and
nothing is installed or reconfigured in Windows.

In the web UI, **Streaming -> Stream this computer's audio**. Over the
API:

```bash
curl -X POST http://localhost:41080/api/stream/system-audio \
  -H "Content-Type: application/json" -d '{"deviceId":"mac-a0b1c2d3e4f5"}'
```

Requirements and current limits:

- **Windows only.** Loopback capture is a Windows API; the endpoint
  returns an error elsewhere.
- **The output device must be at 48 kHz.** Resampling is not implemented
  yet, so a mismatch is reported with the rate it found rather than
  silently producing wrong-pitch audio. Set it under *Sound settings ->
  Device properties -> Advanced*.
- **A logged-in session is required.** Capture cannot reach the user's
  audio endpoints from a session 0 service, so this works when the Hub
  runs as a console application. See `docs/WINDOWS-AUDIO-CAPTURE.md`.
- **Silence while nothing plays** is expected: loopback delivers no data
  when the endpoint is idle, and the stream sends silence to keep
  receivers' jitter buffers fed.

One stream runs at a time — starting system audio replaces a running
test tone, and vice versa.

### Testing it on one machine

Playing the stream back through the speakers that are being captured
creates a **feedback loop** — the audio is captured, sent, played, and
captured again. To test on a single machine, record to a file instead:

```text
set PATH=%PATH%;C:\Program Files\gstreamer\1.0\msvc_x86_64\bin
cd /d %USERPROFILE%\Desktop
gst-launch-1.0 -e udpsrc port=41100 caps="application/x-rtp,media=(string)audio,clock-rate=(int)48000,encoding-name=(string)L24,channels=(int)2,payload=(int)96" ! rtpjitterbuffer ! rtpL24depay ! audioconvert ! wavenc ! filesink location=capture.wav
```

Play something, let it run, then Ctrl+C and play `capture.wav`.

Three things that catch people out:

- **Run from a writable directory.** GStreamer's install folder is under
  `C:\Program Files`, which is write-protected, so a relative
  `location=` there fails with "Permission denied". Hence the `cd`
  above; the `set PATH` keeps the tools available afterwards.
- **`-e` is required.** Without it Ctrl+C skips the end-of-stream, and
  `wavenc` never goes back to fix the header — the file will not play.
  After Ctrl+C, wait for `Freeing pipeline ...` rather than pressing it
  again.
- **Use forward slashes** in any absolute `location=` path.
  `gst-launch` treats backslash as an escape character.

Alternatively, run the receiver on a second machine and there is no
feedback path at all.

## Test tone

The Hub can stream a generated sine tone as RTP. It is a permanent
diagnostic, not a development stopgap: sending a tone to a receiver
answers "is this speaker working?" without involving any source, and
sending one to your own machine proves the Hub's output path.

In the web UI, the **Streaming** section streams either to a discovered
receiver or to the computer you are browsing from. Addressing a device by
name rather than address means the tone follows it if DHCP moves it.

The same thing over the API — with no destination given, the tone is sent
to whoever made the request:

```bash
curl -X POST http://localhost:41080/api/stream/test-tone \
  -H "Content-Type: application/json" -d '{}'

# or explicitly, by device or by address
curl -X POST http://localhost:41080/api/stream/test-tone \
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
ffplay -protocol_whitelist file,rtp,udp,http -i http://localhost:41080/api/stream.sdp
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
