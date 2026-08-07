# Installing and upgrading the Hub

The Hub is a self-contained Windows program with no runtime to install.
It runs as a Windows service, keeps its data outside its own folder, and
upgrades with one command.

## First install

1. Download `OpenAudioLink-Hub-win-x64-<version>.zip` from the
   [releases page](https://github.com/valfrid/OpenAudioLink/releases).
2. Extract it anywhere — the folder you extract to is temporary.
3. From an **elevated** PowerShell prompt, in that folder:

   ```powershell
   .\scripts\install-service.ps1
   ```

That copies the Hub to `C:\Program Files\OpenAudioLink`, registers the
service, opens the three firewall ports it needs, starts it and then asks
it what version it is actually running.

Then open **http://localhost:41080/play**.

### Spotify Connect needs one more file

The Hub does not ship librespot — it is GPL and a separate project, so
you supply the binary (`docs/LIBRESPOT.md` has the reasoning and
`docs/BUILDING-LIBRESPOT-WINDOWS.md` has the build).

Copy `librespot.exe` into `C:\Program Files\OpenAudioLink` and re-run the
install script. It adds a firewall rule for it and says so; without it
the script tells you cast points will not be offered to Spotify.

Upgrades keep it. It is one of two files the installer never replaces.

## Upgrading

```powershell
& "$env:ProgramFiles\OpenAudioLink\scripts\update-hub.ps1"
```

That asks GitHub for the latest release, stops if you already have it,
and otherwise downloads and installs it. No login, no browser, no
unzipping — a release asset is a plain public URL, which is exactly why
upgrades come from releases rather than from CI artifacts.

If you would rather download by hand, or want a build that has no release:

```powershell
.\scripts\install-service.ps1 -FromZip $HOME\Downloads\OpenAudioLink-Hub-win-x64.zip
```

Either way the same rules apply.

### What an upgrade keeps

| Kept | Why |
| --- | --- |
| The data directory | The Hub's identity, your cast points, saved stations, and each room's Spotify credentials |
| `appsettings.json` | Yours if you have edited it — see below |
| `librespot.exe` | You supplied it; the package has never contained it |
| The port and data directory | Recovered from the registered service, not reset to the defaults |

That last one matters more than it sounds. An upgrade that silently moved
the data directory would look exactly like a Hub that had lost every room
in the house.

**Everything else under `C:\Program Files\OpenAudioLink` is replaced.** A
file an older version shipped and this one does not is gone rather than
left behind to confuse a later diagnosis.

### When settings change

If you have edited `appsettings.json` and the new version ships a
different one, yours is kept and the new one lands beside it as
`appsettings.json.new`, with a message saying so. Nothing is silently
discarded and nothing is silently withheld.

To take the new defaults instead: `-ResetSettings`.

## Where things live

| | |
| --- | --- |
| Program | `C:\Program Files\OpenAudioLink` |
| Data | `C:\ProgramData\OpenAudioLink` |
| Logs | Event Viewer → Windows Logs → Application |
| Service | `OpenAudioLinkHub`, automatic (delayed start) |

Delayed start because the network stack should be up before discovery
binds its multicast socket. The service restarts itself after a crash: 5
seconds, then 10, then every minute.

### Why the data is not beside the program

So that replacing the program cannot replace the data. Everything the Hub
keeps is something you set up by hand once and must never have to set up
twice — and a service account should not be writing under Program Files
anyway.

If you want the old portable arrangement, set `Hub:DataDirectory` in
`appsettings.json`. A relative path is resolved against the executable,
not against whatever the working directory happens to be — which, for a
service, is `C:\Windows\System32`.

## Publishing a release

Upgrades come from releases, so there has to be one:

```
git tag hub-v0.7.0
git push origin hub-v0.7.0
```

The release workflow checks the tag against `<Version>` in
`hub/Directory.Build.props` and refuses the build if they disagree — an
installer that reports a different version from the one on the tin is
worse than no installer. Then it publishes the zip.

## Uninstalling

```powershell
.\scripts\uninstall-service.ps1
```

Stops and removes the service and its firewall rules. Files and data are
kept, so reinstalling finds the house exactly as it was. Add
`-RemoveFiles` to delete both.

## Two things that will bite

**Capturing this computer's audio does not work from a service.** Windows
services run in session 0 and cannot reach a logged-in user's audio
endpoints, so "Stream this computer's audio" fails there. Everything else
— discovery, control, OTA, radio, Spotify, the vinyl path, the
switchboard — works normally. Run the Hub from a console when you need
loopback capture. See `docs/WINDOWS-AUDIO-CAPTURE.md`.

**Windows classifies networks by itself and gets wired home LANs wrong.**
On a network it has decided is Public, it disables its own inbound mDNS
rules, and the symptom is perfect: the Hub reaches everything, answers
every request made to its address, and hears no multicast at all — so
nothing discovers it and it discovers nothing. The install script checks
for this and says so, with the command to fix it.
