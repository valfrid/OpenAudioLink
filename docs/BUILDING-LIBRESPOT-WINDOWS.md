# Building librespot on Windows

A step-by-step for someone who has never set up a Rust build. Nothing
here assumes prior knowledge. Allow about an hour, most of it waiting for
downloads.

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

**The easy route:** skip to step 2. Modern `rustup-init.exe` notices the
tools are missing and offers to install them for you. Say yes, let it
finish, then carry on. If it does that, you are done with this step.

**Doing it yourself**, if rustup does not offer or you prefer to look:

1. Go to <https://visualstudio.microsoft.com/downloads/>.
2. Scroll past the big Visual Studio editions to **"All Downloads"** →
   **"Tools for Visual Studio"**.
3. Download **"Build Tools for Visual Studio 2022"**. This is the
   compiler toolchain without the Visual Studio editor — it is what you
   want; the full IDE is several times larger and buys you nothing here.
4. Run the downloaded `vs_BuildTools.exe`. It fetches a small installer
   first, which then shows a grid of tiles called **Workloads**.
5. Tick **"Desktop development with C++"**. Leave the right-hand panel of
   optional components at its defaults — the MSVC compiler and a Windows
   SDK are what matter and both are on by default.
6. Click **Install**. This is the long download.
7. When it finishes you can close the installer. A reboot is usually not
   needed.

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

## Step 7 — test librespot on its own, before involving the Hub

Worth doing once. It tells you which half of the system to look at if
something is wrong later.

In a Command Prompt:

```
"%USERPROFILE%\.cargo\bin\librespot.exe" --name "Test speaker" --backend pipe --format F32 --device-type speaker >NUL
```

In PowerShell the last part is `> $null` instead of `>NUL`.

The redirect matters: the pipe backend writes raw audio to the console,
and without it your terminal fills with binary garbage.

Leave it running. On a phone on the same Wi-Fi, open Spotify, start any
track, and open the device picker (the speaker icon at the bottom).
**"Test speaker" should be listed.** Select it — the phone goes quiet,
because the audio is going into `NUL`, which is the correct result.

Press **Ctrl+C** in the console to stop it.

- **It appeared:** librespot is fine. Any later problem is the Hub or the
  speakers.
- **It did not appear:** see below. Do not move on until it does.

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
