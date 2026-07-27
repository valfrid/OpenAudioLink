# OpenAudioLink Hub

The Hub is the centre of an OpenAudioLink system. Per the Phase 1
architecture it implements the Controller, the Producer for Windows-hosted
sources, and the Provisioner.

Current state: Phase 2.2 skeleton — health endpoint, JSON configuration
storage, device inventory, discovery listener/announcer and a web UI shell.
Audio capture, RTP production, USB flashing and OTA come in later phases.

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

## Tests

```bash
dotnet test
```
