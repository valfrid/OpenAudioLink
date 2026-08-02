# Getting librespot for Windows

The librespot project does not publish Windows binaries and has decided
not to ([issue #727](https://github.com/librespot-org/librespot/issues/727),
closed `wontfix`), so it has to be built. There are two ways, and **the
first one is almost certainly the one you want.**

---

# Option A — let CI build it (recommended)

Same as the firmware images and the Hub package: a GitHub Actions runner
builds it and you download the result. Nothing is installed on your PC.

1. Open the repository on GitHub → **Actions** tab.
2. Pick **librespot** in the left-hand list of workflows.
3. Click **Run workflow**. It asks for a version; the default is fine.
4. Wait. The first run took **5 minutes 27 seconds**.
5. Open the finished run. The summary shows the size and SHA256 of what
   it built, and **Artifacts** at the bottom has
   `librespot-0.8.0-win-x64`. Download it and unzip.
6. Put `librespot.exe` beside the Hub executable, then do **step 6
   (firewall)** and **step 7 (test)** from Option B below — those two
   still apply however you got the binary.

The runner already has Rust and the Microsoft C++ toolchain, which is the
entire reason Option B is long. The workflow also runs the binary once
before uploading it, so an artifact that exists is an artifact that starts.

**Verified 2026-08-02.** Run 1 built librespot 0.8.0 on `windows-latest`
and the binary reported

```
librespot 0.8.0 (Built on 2026-08-02, Build ID: hJW4W01L, Profile: release)
```

Two things that run showed and that are worth knowing:

- **Artifacts expire after 90 days.** If the download link on an old run
  is dead, re-run the workflow rather than hunting for the file.
- The build prints a future-incompatibility warning about `num-bigint-dig`,
  a dependency several levels below librespot. Harmless today; it means a
  future Rust release may refuse to build this pinned version, and the fix
  then is a newer librespot, not a change here.

Why this is not just committed to the repository: a binary in Git is a
binary nobody can audit and everybody has to trust. Building it on demand
from a pinned version, with the command visible in
`.github/workflows/librespot.yml`, keeps it reproducible and keeps the
licensing question where `CAST-POINTS.md` puts it — with the operator who
chose to run it.

---

# Option B — build it on your own machine

Worth it if you want to work on librespot itself, or you would rather not
depend on CI. A step-by-step for someone who has never set up a Rust
build; nothing below assumes prior knowledge. Allow about an hour, most
of it waiting for downloads.

## Do not use WSL

WSL builds **Linux** programs. The Hub is a Windows process that starts
librespot as a child process, and Windows cannot start a Linux binary —
you would end up with a file that runs fine inside WSL and is invisible to
the Hub.

Cross-compiling a Windows `.exe` from inside WSL is possible, but it means
installing a second toolchain to produce something the native toolchain
produces directly. Build it on Windows, in an ordinary Windows terminal.
Everything below happens there.

## What you are installing, and how much room it needs

| | Roughly |
| --- | --- |
| Visual Studio Build Tools (the C++ linker) | 3–7 GB |
| Rust toolchain | 1.5 GB |
| Downloaded source and build output | 2 GB |

The build output can be deleted afterwards; the rest is worth keeping if
you ever rebuild.

---

## Step 1 — the C++ build tools

Rust compiles the code, but on Windows it hands the final assembly step to
Microsoft's linker, `link.exe`. That does not come with Windows.

The thing you need is called **Build Tools for Visual Studio**. It is the
compiler and linker without the Visual Studio editor — the full IDE is
several times larger and buys you nothing here. It is free.

### Where the installer is

The file is always named `vs_BuildTools.exe` and is about 4 MB; it is a
downloader that fetches the real payload once you have chosen what you
want.

**Direct links** (Microsoft's permanent shortlinks; `18` is Visual Studio
2026, `17` is 2022 — either works, take the newer unless you have a reason):

- <https://aka.ms/vs/18/stable/vs_buildtools.exe>
- <https://aka.ms/vs/17/release/vs_BuildTools.exe>

**By navigation**, if those ever move:

1. Go to <https://visualstudio.microsoft.com/downloads/>.
2. Scroll past the three big Visual Studio editions to **"All Downloads"**.
3. Expand **"Tools for Visual Studio"**.
4. **"Build Tools for Visual Studio"** is in that list. Click Download.

**By command line**, if you would rather not use a browser — `winget` is
built into Windows 10 and 11. Find the current package id with

```
winget search "Visual Studio Build Tools"
```

then install it with the C++ workload, which is the part that matters:

```
winget install --id Microsoft.VisualStudio.2022.BuildTools --override "--passive --wait --add Microsoft.VisualStudio.Workload.VCTools --includeRecommended"
```

Without that `--override`, winget installs the installer shell with no
compiler in it, and the build still fails on a missing `link.exe`.

### Running it

1. Run `vs_BuildTools.exe`. It updates itself, then opens the **Visual
   Studio Installer** showing a grid of tiles headed **Workloads**.
2. Tick **"Desktop development with C++"**. That single tile is the whole
   requirement.
3. Leave the **Installation details** panel on the right at its defaults.
   The MSVC compiler and a Windows SDK are what Rust needs, and both are
   already selected.
4. Click **Install**. This is the long download — 3–7 GB depending on the
   optional components.
5. Close the installer when it finishes. A reboot is usually not needed.

### rustup may offer to do this for you

When you run `rustup-init.exe` in step 2 without the C++ tools present, it
detects that and offers to launch the Visual Studio installer itself. If
you get that prompt, take it — it is this step, done for you, with the
right workload preselected. It is worth knowing what it is installing and
why, which is what this section is for.

### If you would rather not install Visual Studio at all

There is a second Rust toolchain that uses the GNU linker and needs none
of the above:

```
rustup toolchain install stable-x86_64-pc-windows-gnu
rustup default stable-x86_64-pc-windows-gnu
```

Run those *after* step 2. The resulting `librespot.exe` is an ordinary
Windows program and works identically. The MSVC route is the more
travelled one, so prefer it if you have the disk space.

---

## Step 2 — Rust

1. Go to <https://rustup.rs>.
2. Download **`rustup-init.exe`** (the 64-bit one).
3. Run it. A black console window opens and prints a page of text ending
   in a menu:

   ```
   1) Proceed with standard installation (default - just press enter)
   2) Customize installation
   3) Cancel installation
   ```

4. Press **Enter**. Do not customise anything.
5. If it says Rust requires the Microsoft C++ build tools and offers to
   install them, say yes and let it finish (this is step 1 done for you).
6. Wait. It ends with:

   ```
   Rust is installed now. Great!
   ```

7. **Close the console window, and close every Command Prompt and
   PowerShell window you have open.** The installer added a folder to your
   `PATH`, and only windows opened *after* that will know about it. This
   is the single most common reason the next step appears to fail.

---

## Step 3 — check it worked

Open a **new** PowerShell or Command Prompt (Start menu → type
`powershell` → Enter) and run:

```
rustc --version
cargo --version
```

You should get two lines with version numbers, something like
`rustc 1.9x.0` and `cargo 1.9x.0`. The exact numbers do not matter.

If you instead get *"'cargo' is not recognized..."*, you did not reopen
the window after installing. Close it and open a new one.

---

## Step 4 — build librespot

In that same window, run this **as one line**:

```
cargo install librespot --locked --no-default-features --features "native-tls,with-libmdns"
```

What each part is for:

- **`--locked`** builds with the exact dependency versions the librespot
  authors tested, rather than whatever is newest today. It makes the build
  reproducible and avoids a class of "it broke this week" failures.
- **`--no-default-features`** drops `rodio-backend`, which exists to talk
  to a sound card. This Hub reads audio from a pipe, so that code is
  weight we do not need.
- **`with-libmdns` must stay.** It is what announces each receiver on the
  network. Leave it out and you get a librespot that runs perfectly and
  never appears in the phone's Spotify list — which looks exactly like a
  network fault, for as long as you are willing to chase one.
- **`native-tls`** uses Windows' own SChannel for encryption, so there is
  no OpenSSL to install.

You do not need to ask for the pipe backend. It is always built in.

Now it runs for a while. You will see hundreds of lines like
`Compiling serde v1.0.x`, a progress counter, and long pauses. **Five to
twenty minutes** is normal depending on the machine. Warnings scrolling
past are normal and not your problem.

It has worked when the last line reads:

```
Installed package `librespot v0.8.0` (executable `librespot.exe`)
```

---

## Step 5 — put it where the Hub looks

The build placed the program here:

```
%USERPROFILE%\.cargo\bin\librespot.exe
```

which is usually `C:\Users\<you>\.cargo\bin\librespot.exe`. Check it is
real and runnable:

```
%USERPROFILE%\.cargo\bin\librespot.exe --version
```

Then copy it into the folder containing the Hub's executable. The Hub
looks there first and needs no configuration:

```
copy %USERPROFILE%\.cargo\bin\librespot.exe C:\path\to\the\hub\
```

(Leaving it in `.cargo\bin` also works, because that folder is on your
`PATH` — but copying it beside the Hub keeps the two together if you ever
move the Hub to another machine.)

---

## Step 6 — the firewall

**This step is easy to skip and will cost you an evening if you do.**

For the phone to see the receiver, other machines have to be able to reach
it. Windows Firewall blocks that by default.

If you start the Hub by double-clicking it, Windows shows a *"Windows
Defender Firewall has blocked some features"* dialog the first time
librespot runs. Tick **Private networks** and allow it.

If the Hub runs as a **Windows service**, no dialog ever appears — there
is no desktop to show it on — and the receiver silently never shows up. In
that case add the rule yourself. Open PowerShell **as Administrator**
(right-click → Run as administrator) and run, with the real path:

```
netsh advfirewall firewall add rule name="librespot" dir=in action=allow program="C:\path\to\the\hub\librespot.exe" enable=yes profile=private
```

---

## Step 7 — test the binary from the command line

Four checks, each one proving more than the last. Do them in order: the
first that fails tells you where the problem is, and there is no point
looking further down until it passes.

These apply however you got the binary — built locally or downloaded from
the CI artifact. Replace `librespot.exe` below with the actual path if it
is not in the folder you are standing in.

### 7.1 — it is a program

```
librespot.exe --version
```

```
librespot 0.8.0 (Built on 2026-08-02, Build ID: ..., Profile: release)
```

Proves the file is not a truncated download and is built for your
architecture. If Windows says it is not recognised as an internal or
external command, you are in the wrong folder or the file is not there.

### 7.2 — this build has what the Hub needs

```
librespot.exe --help
```

A long list of options. Check that `--backend`, `--format`,
`--device-type` and `--zeroconf-port` are all in it — those are what the
Hub passes, and a build missing one would fail at a much more confusing
moment.

### 7.3 — it announces itself (no phone required)

This is the one that matters, and it can be done entirely on the PC.

**In one Command Prompt window**, start it with the zeroconf port pinned
so the next command knows where to look:

```
librespot.exe --name "Test speaker" --backend pipe --format F32 --device-type speaker --zeroconf-port 41200 >NUL
```

In PowerShell the redirect is `> $null` instead of `>NUL`. The redirect
matters: the pipe backend writes raw audio to standard output, and without
it your terminal fills with binary garbage. Log messages go to standard
*error*, so they stay visible — that is deliberate and useful.

**In a second window**, ask the receiver about itself:

```
curl.exe "http://localhost:41200/?action=getInfo"
```

**Use `curl.exe`, with the extension.** In PowerShell, plain `curl` is an
alias for `Invoke-WebRequest`, which takes different arguments and will
look like a failure that is really a different program.

A block of JSON comes back, including `"remoteName":"Test speaker"` and
`"deviceType":"SPEAKER"`. That is Spotify's own discovery endpoint
answering — the exact thing a phone talks to. If this works, librespot is
running correctly and anything still wrong is network or firewall.

If it hangs or refuses the connection, the process is not up: look at the
first window, which will say why.

### 7.4 — audio actually comes out

The previous test proved it can be *found*. This proves it can *play*.

Restart it, sending the audio to a file instead of discarding it:

```
librespot.exe --name "Test speaker" --backend pipe --format F32 --device-type speaker --zeroconf-port 41200 > C:\temp\test.raw
```

(Create `C:\temp` first if it does not exist.)

On a phone on the same Wi-Fi, open Spotify, play any track, and pick
**"Test speaker"** from the device picker (the speaker icon). The phone
goes quiet, which is correct — the audio is going to the PC. The first
window logs the login and the track name.

Let it play about ten seconds, then **Ctrl+C**, and look at the file:

```
dir C:\temp\test.raw
```

**The size is the real result.** At 44.1 kHz, two channels, four bytes per
sample, `F32` produces **352,800 bytes per second**, so ten seconds is
roughly **3.5 MB**. Getting that means the whole chain works: login,
streaming, decoding, and the pipe.

| File size after ~10 s | What it means |
| --- | --- |
| ~3.5 MB | Correct. `F32`, 44.1 kHz stereo, as expected. |
| ~1.8 MB | Half. The build is emitting `S16`, not `F32` — check the `--format` argument. |
| 0 bytes | It was found but never played. Spotify Premium is required. |
| No file at all | The redirect did not happen; retype the command. |

Curious what is in it? Audacity can open raw PCM: **File → Import → Raw
Data**, then set 32-bit float, little-endian, 2 channels, 44100 Hz. It
should be the track you played.

### What each outcome tells you

- **7.3 and 7.4 both pass:** librespot is fine. Any later problem is the
  Hub, the network to the speakers, or the speakers.
- **7.3 passes, phone cannot see it:** firewall (step 6), or the phone is
  not on the same network. See the network traps below.
- **7.3 fails:** the build or the arguments. See the table below.

---

## When it does not work

| What you see | What it means |
| --- | --- |
| `'cargo' is not recognized` | You did not open a new terminal after installing Rust. |
| `error: linker 'link.exe' not found` | Step 1 is missing. Install the C++ build tools, or switch to the GNU toolchain. |
| `error: failed to run custom build command for openssl-sys` | The `--features` part of the command was lost. Retype step 4 as a single line. |
| Build succeeds, name never appears in Spotify | In order of likelihood: the firewall (step 6); `with-libmdns` was left out of the build; the phone is on a different network from the PC. |
| Name appears, but selecting it fails | Spotify **Premium** is required. This is the service's rule, not ours. |
| `Credentials are required if discovery and oauth login are disabled` | Discovery was disabled — which is what happens if `with-libmdns` was omitted. Rebuild with it. |

### Two network traps worth knowing about

**mDNS does not cross subnets.** The announcement is a local-network
broadcast. If the phone is on a guest network, a separate VLAN, or a
different Wi-Fi band that the router isolates, it will never see the
receiver no matter how correct everything else is. Put both on the same
network first, then get clever.

**A VPN on either device can hide it.** A VPN client that accepts routes
for your home network will quietly capture traffic meant for the LAN. This
project has already lost an evening to exactly that — see the note in
`LINK-MEASUREMENTS.md` about a subnet route with a better metric than the
local interface. If discovery misbehaves and everything else looks right,
turn the VPN off and try again.

---

## Keeping it up to date

Re-run the step 4 command. Cargo replaces the installed binary. Copy the
new one beside the Hub again and restart the Hub.

---

## What was verified

Checked against **librespot 0.8.0** (crates.io, November 2025) by reading
the source at that tag:

- `--name`, `--backend`, `--format`, `--device-type`, `--bitrate`,
  `--initial-volume`, `--cache`, `--disable-audio-cache` all exist under
  exactly those names — these are the arguments the Hub passes.
- `--format` accepts `F32`, which is what the Hub asks for by default.
- `--version` and `--help` exist.
- No option is mandatory, except that credentials become required if
  discovery is disabled — the failure mode of building without
  `with-libmdns`.
- The pipe backend is not behind a feature flag; discovery is, behind
  `with-libmdns`.
