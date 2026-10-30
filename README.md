# PhoneGamepadBridge

Relays a Bluetooth game controller paired to an **iPhone 11** into a **virtual
Xbox 360 pad on Windows**, over the Lightning cable.

```
BT controller ──BR/EDR──> iPhone (GameController framework)
                              │
                              │  20-byte frames @ 125 Hz, TCP
                              ▼
                          usbmux / iproxy  (Lightning, USB 2.0)
                              │
                              ▼
                    PhoneGamepadBridge.exe ──> ViGEmBus ──> XInput
                                                              │
                                                              ▼
                                                       games see a
                                                    native Xbox 360 pad
```

Two native components, no web layer anywhere:

| Path        | Language | Role                                            |
|-------------|----------|-------------------------------------------------|
| `windows/`  | C# / .NET 8 | Console service: usbmux tunnel, frame decode, ViGEmBus |
| `ios/`      | Swift / SwiftUI | Foreground app: samples the pad, serves frames over TCP |
| `PROTOCOL.md` | —      | Wire format, shared by both                     |

## Scope — read this first

This bridges **game controllers only**. It is not a Bluetooth adapter and cannot
be made into one. iOS gives apps no HCI, no raw HID, and no Classic-Bluetooth
access; `GameController` is the single supported hook, and it hands you parsed
input events rather than the radio. Keyboards, mice, headphones and generic
Bluetooth accessories are out of reach at any level, jailbreak included — and
even with raw HCI on the phone, Windows has no virtual HCI transport driver to
accept it.

If you want general Bluetooth on this PC, a $10 USB dongle remains the correct
answer. Build this only because you specifically want the controller path.

## Building the iOS app without a Mac

Compiling requires Xcode, which is macOS-only. **Signing and installing are
not** — Sideloadly does both on Windows. So the split is:

- **Compile** on a GitHub Actions `macos-latest` runner (free for public repos;
  macOS minutes bill at 10× on private, and this build takes 2–4 minutes).
- **Sign + install** on Windows with Sideloadly and a free Apple ID.

CI builds the app **unsigned**, so there are no certificates, provisioning
profiles, or repository secrets involved. Sideloadly attaches your own signing
identity at install time.

No `.xcodeproj` is committed — one can't be created without a Mac. CI generates
it with [XcodeGen](https://github.com/yonaskolb/XcodeGen) from `ios/project.yml`.
Edit that file, never the generated project.

## Prerequisites

**Windows**
- [.NET 8 SDK](https://dotnet.microsoft.com/download) — `winget install Microsoft.DotNet.SDK.8`
- [ViGEmBus driver](https://github.com/nefarius/ViGEmBus/releases) — signed, installs normally, no test-signing needed
- Apple Mobile Device Support (bundled with iTunes) for the usbmux service
- `iproxy.exe` from [libimobiledevice](https://github.com/libimobiledevice-win32/imobiledevice-net) on `PATH`

**For the phone side (all from Windows)**
- A GitHub account, to run the build workflow
- [Sideloadly](https://sideloadly.io/) for Windows
- An Apple ID. A free one works but apps expire every **7 days** (3 sideloaded
  apps max; Sideloadly's daemon can auto-refresh). A paid Developer account
  ($99/yr) extends that to a year. This app will never pass App Store review, so
  sideloading is the permanent state, not a stepping stone.

## Build

### Windows

```powershell
cd C:\Users\gfddf\PhoneGamepadBridge\windows
dotnet add package Nefarius.ViGEm.Client   # only if the pinned version has moved
dotnet build -c Release
```

### iOS — get the IPA

Push this project to a GitHub repo:

```powershell
cd C:\Users\gfddf\PhoneGamepadBridge
git add .
git commit -m "Initial commit"
```

Then create an empty repo at <https://github.com/new> (no README, no
`.gitignore`) and push to it:

```powershell
git remote add origin https://github.com/<you>/PhoneGamepadBridge.git
git push -u origin main
```

Or, with the [GitHub CLI](https://cli.github.com/) installed
(`winget install GitHub.cli`), skip the web step:

```powershell
gh repo create PhoneGamepadBridge --private --source=. --push
```

The **Build unsigned IPA** workflow runs on push to `main`, or manually from the
Actions tab. When it finishes, download the `GamepadRelay-ipa` artifact and
unzip it to get `GamepadRelay.ipa`.

### iOS — install with Sideloadly

1. Plug the iPhone in, trust the computer.
2. Open Sideloadly, drag in `GamepadRelay.ipa`, enter your Apple ID.
3. Start. Sideloadly signs and installs it.
4. On the phone: **Settings > General > VPN & Device Management** > trust the
   developer profile.
5. First launch will ask for local network permission — allow it.

Re-run steps 2–3 every 7 days on a free Apple ID, or enable Sideloadly's
auto-refresh.

## Run

1. Pair the controller to the iPhone in **Settings > Bluetooth** as usual.
2. Open **Gamepad Relay** on the phone and leave it foreground. It should read
   *Waiting for PC*.
3. Plug the phone into the PC and trust the computer if prompted.
4. On the PC:

```powershell
.\windows\bin\Release\net8.0-windows\PhoneGamepadBridge.exe -v
```

The phone flips to *Connected*, and a virtual Xbox 360 pad appears in
`joy.cpl`. Ctrl+C to stop.

Useful flags: `--port <n>` (default 5111, must match the app),
`--no-iproxy` if you're managing the tunnel yourself, `--udid <udid>` with
several devices attached, `-v` for a frames/sec readout.

## Design notes

**Why the phone is the server.** usbmux forwards a PC port *to* a port the
device listens on. The direction is imposed by the transport, not chosen.

**Constant-rate sending.** The phone transmits every 8 ms whether or not
anything changed. That costs 2.5 KB/s — nothing — and gives the PC a free
watchdog: 400 ms of silence releases every input, so a yanked cable or a
controller that powers off mid-game can't leave a stick latched.

**Backpressure.** The phone drops frames once 4 sends are unacknowledged. Stale
input is worthless, so dropping beats queueing.

**One `ViGEmSink` for the process lifetime.** The virtual pad survives TCP
reconnects; recreating it would make the device churn in Device Manager and
some games would drop the controller.

**`SWIFT_STRICT_CONCURRENCY: minimal`.** `RelayEngine` enforces its threading by
convention — sampling state on one serial queue, `@Published` writes on main —
which is correct but not expressible to Swift 6's isolation checker. Raising
this to `complete` will produce warnings until the engine is restructured
around an `actor`.

## Latency

| Hop                        | Approx.   |
|----------------------------|-----------|
| Controller → phone (BT)    | 5–10 ms   |
| Sampling (8 ms period)     | 4–8 ms    |
| usbmux over Lightning      | 1–3 ms    |
| ViGEmBus → XInput          | ~1 ms     |
| **Added total**            | **15–25 ms** |

A USB dongle is ~5–8 ms end to end. This is fine for single-player and couch
games, and not fine for anything competitive.

## Known limitations

- **Foreground only.** No iOS background mode covers input polling. Screen must
  stay on (the app disables the idle timer). Backgrounding closes the socket
  deliberately, so the PC releases the pad rather than waiting out the watchdog.
- **One controller.** The protocol carries a single pad; ViGEm supports more, so
  extending it means adding a pad index to the frame header.
- **Guide button** (bit 14) is usually swallowed by iOS and may never fire.
- **No rumble.** Force feedback would need a PC→phone reverse channel plus
  `CHHapticEngine`; the protocol is one-directional today.
- **Battery.** Sampling at 125 Hz with the screen on drains meaningfully. Leave
  the phone charging — which the cable is doing anyway.

## Verification status

The Swift and C# here have been written and reviewed but **not compiled** — this
machine has the .NET runtime without the SDK, and no macOS toolchain. Expect to
fix trivia on first build.
