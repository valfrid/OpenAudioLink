# Installing and upgrading the Hub

The Hub is a self-contained Windows program with no runtime to install.
It runs as a Windows service, keeps its data outside its own folder, and
upgrades with one command.

## First install

Download `OpenAudioLink-Hub-win-x64-<version>.zip` from the
[releases page](https://github.com/valfrid/OpenAudioLink/releases), then
paste this into an **elevated** PowerShell prompt:

```powershell
$zip = "$HOME\Downloads\OpenAudioLink-Hub-win-x64-0.7.0.zip"
$tmp = "$env:TEMP\oal-install"

Expand-Archive -Path $zip -DestinationPath $tmp -Force
Get-ChildItem -Path $tmp -Recurse -Include *.ps1 | Unblock-File
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force

& "$tmp\scripts\install-service.ps1"
```

The folder it extracts to is temporary; the install copies out of it.

**The two middle lines are not optional on a fresh machine.** Windows
marks every file that came from the internet, and PowerShell refuses to
run a marked script — so a first install fails twice before it starts,
once on the mark and once on the execution policy. Neither error mentions
this project, which is why the commands are here rather than left to be
discovered.

An elevated prompt opens in `C:\Windows\system32`, so `.\install-service.ps1`
finds nothing. Run it by full path, as above.

That copies the Hub to `C:\Program Files\OpenAudioLink`, registers the
service, opens the three firewall ports it needs, starts it and then asks
it what version it is actually running.

Then open **http://localhost:41080/play**.

### Spotify Connect needs one more file

The Hub does not ship librespot. Not because of its copyright licence —
librespot is MIT, and bundling it would be permitted — but because
whether a reimplementation of somebody's streaming protocol is licensed
for use with *that service* is the operator's decision rather than this
project's (`docs/CAST-POINTS.md`, "Bundling").

There is no official Windows binary either: the librespot project
declines to publish one. This repository builds it — **Actions →
librespot → Run workflow** — and `docs/BUILDING-LIBRESPOT-WINDOWS.md`
covers that and the local route.

So it is a separate download, fetched only when asked for:

```powershell
.\scripts\install-service.ps1 -WithLibrespot
```

or on a Hub that is already installed:

```powershell
& "$env:ProgramFiles\OpenAudioLink\scripts\get-librespot.ps1"
```

That pulls the binary from a `librespot-v*` release of this repository,
**checks it against the SHA256 published beside it**, and only then puts
it in place. A missing checksum is a refusal rather than a shrug: this is
a downloaded executable that a Windows service will launch.

If no such release exists yet, build one: **Actions → librespot → Run
workflow**, with *Publish* ticked. It takes about ten minutes and only
needs doing again when librespot's own version changes — which is why it
is not built with every Hub release.

Your own build is welcome too; just copy `librespot.exe` into
`C:\Program Files\OpenAudioLink`. `get-librespot.ps1` leaves an existing
one alone unless passed `-Force`, because yours may have features this
one does not.

Either way the install script adds a firewall rule for it and says so.
Upgrades keep it — it is one of two files the installer never replaces.

## Upgrading

```powershell
& "$env:ProgramFiles\OpenAudioLink\scripts\update-hub.ps1"
```

That asks GitHub what has been published, stops if you already have it,
and otherwise downloads and installs. No login, no browser, no unzipping
— a release asset is a plain public URL, which is exactly why upgrades
come from releases rather than from CI artifacts, which need a token and
expire after ninety days.

### Two kinds of build

| | |
| --- | --- |
| `hub-latest` | whatever the working branch last built, marked prerelease. Replaces itself on every push |
| `hub-v0.7.0` | a deliberate, permanent release |

The update script takes whichever is newer, so **upgrading never requires
remembering to tag first**. On a machine somebody else depends on, use
`-StableOnly` and it ignores the rolling build entirely.

Between two commits that did not bump the version, the rolling build
reports the same number as the one installed and the script stops.
`-Force` installs it anyway.

If you would rather download by hand, or want a build that has no
release, the installer already on the machine takes a zip directly:

```powershell
& "$env:ProgramFiles\OpenAudioLink\scripts\install-service.ps1" `
    -FromZip $HOME\Downloads\OpenAudioLink-Hub-win-x64.zip
```

That is an upgrade path and not a first install: `-FromZip` saves
unpacking, but the script has to come from somewhere, and on a machine
with no Hub the only copy is inside the zip.

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

## Publishing

The rolling `hub-latest` build publishes itself on every push to `main`
or a `claude/**` branch, so nothing has to be done for day-to-day
upgrades to work.

A permanent, tagged release is a deliberate act:

```
git tag hub-v0.7.0
git push origin hub-v0.7.0
```

The release workflow checks the tag against `<Version>` in
`hub/Directory.Build.props` and refuses the build if they disagree — a
tagged release is a promise about a version number, and an installer that
reports a different version from the one on the tin is worse than no
installer.

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
