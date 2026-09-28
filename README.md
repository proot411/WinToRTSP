# WinToRTSP — Windows Screen Broadcasting Application

> **Lightweight, secure, hardware-accelerated screen casting for Windows.** Broadcasts your primary display as an RTSP stream with an embedded Web UI dashboard. Designed to run efficiently on legacy hardware (Intel 2nd Gen CPUs and above).

---

## Network Security Warning (Do Not Expose to WAN)

> ### ⚠️ Do NOT expose the RTSP port (default `8554`) or the Web UI port (default `8080`) to the public internet / WAN.

WinToRTSP is built for **local and trusted networks only**:

- Both the RTSP server and the web server **bind to all network interfaces** (`IPAddress.Any`). The ports are reachable from *every* network the machine is connected to — office LAN, public Wi-Fi, Tailscale/VPN, etc.
- The Web UI is served over **plain HTTP**. There is no TLS/HTTPS, so passwords, session cookies and control requests travel unencrypted to any host on the path.
- RTSP playback uses **RFC 2617 Digest authentication (MD5)** — fine for keeping casual viewers out on a LAN, but not a substitute for keeping the port off the internet.

**Do this instead:**

- **Never port-forward** `8554`/`8080` on your router, and **block inbound WAN access** to them in your firewall.
- If you must share access across machines, restrict the ports with **Windows Firewall rules to private networks / your subnet**.
- For remote viewing, use a **private VPN** (Tailscale, WireGuard, etc.) and reach the stream through the VPN — never by opening the ports.
- **Change the default password** immediately after first launch (see [Default Credentials](#default-credentials)).

---

## Table of Contents

1. [Network Security Warning (Do Not Expose to WAN)](#network-security-warning-do-not-expose-to-wan)
2. [Installation](#installation)
3. [Architecture & Internal Mechanics](#architecture--internal-mechanics)
4. [Features](#features)
5. [Requirements](#requirements)
6. [Compilation & Build Instructions](#compilation--build-instructions)
7. [Packaging Instructions](#packaging-instructions)
8. [Running the Application](#running-the-application)
9. [Connecting to the Stream](#connecting-to-the-stream)
10. [Security Model](#security-model)
11. [VM & Legacy Hardware Notes](#vm--legacy-hardware-notes)
12. [Default Credentials](#default-credentials)
13. [File Structure](#file-structure)
14. [Credits](#credits)

---

## Installation

WinToRTSP is **portable — there is nothing to install** for the standard case:

1. **Get `WinToRTSP.exe`** — either use a pre-built portable executable (~80 MB, fully self-contained: no .NET runtime or other prerequisites required) or build one yourself from source (see [Compilation & Build Instructions](#compilation--build-instructions) and [Packaging Instructions](#packaging-instructions)).
2. **Run it** — double-click `WinToRTSP.exe`, or start it minimized to the system tray with `WinToRTSP.exe --minimized`.
3. **First launch** creates `%LOCALAPPDATA%\WinToRTSP\config.json` with default settings (RTSP `8554`, Web UI `8080`, authentication enabled) and sets the default password (see [Default Credentials](#default-credentials)).
4. **Start the stream** with the *Start Stream* button in the window or in the web dashboard, then connect any RTSP player to the URL shown.
5. *(Optional)* Configure startup in the **System Integration** settings panel: check **Start with Windows Boot (opens minimized to tray)** to launch the app when Windows starts, and/or **Start Streaming Automatically on Launch** to begin broadcasting as soon as the app opens — enable both for a fully unattended stream after every boot (see [Startup Options](#startup-options)). Alternatively install via the Inno Setup package (see [Packaging Instructions](#packaging-instructions)) for Start Menu/Desktop shortcuts and clean uninstallation.

**Uninstalling:** portable build — delete the `.exe` and `%LOCALAPPDATA%\WinToRTSP`. Installer build — remove it via *Apps & Features*.

---

## Architecture & Internal Mechanics

```
┌─────────────────────────────────────────────────────────────────┐
│                         WinToRTSP.exe                          │
│                                                                  │
│  ┌─────────────────────────────────────────────────────────┐   │
│  │               Screen Capture Engine                      │   │
│  │  1. DXGI Desktop Duplication (GPU-accelerated)          │   │
│  │  2. GDI BitBlt (CPU, fallback)                          │   │
│  │  3. WinForms CopyFromScreen (VM-compatible, last resort) │   │
│  └──────────────────────────┬──────────────────────────────┘   │
│                             │ CapturedFrame (ArrayPool<byte>)   │
│                             ▼                                   │
│  ┌──────────────────────────────────────────────────────────┐  │
│  │                    H.264 Encoder                          │  │
│  │  1. Windows Media Foundation (hardware + low-latency)    │  │
│  │  2. FFmpeg libx264 (ultrafast/zerolatency, if present)   │  │
│  └──────────────────────────┬─────────────────────────────-─┘  │
│                             │ EncodedPacket (Annex-B)           │
│                             ▼                                   │
│  ┌──────────────────────────────────────────────────────────┐  │
│  │              Embedded RTSP Server (TCP)                   │  │
│  │  - RFC 2326 RTSP/1.0 (OPTIONS, DESCRIBE, SETUP, PLAY)   │  │
│  │  - RFC 6184 H.264 RTP Packetization (FU-A fragmentation) │  │
│  │  - RFC 2617 Digest Authentication (MD5-based)            │  │
│  │  - RTP L16 audio packets (WASAPI loopback 48kHz stereo)  │  │
│  └──────────────────────────────────────────────────────────┘  │
│                                                                  │
│  ┌──────────────────────────────────────────────────────────┐  │
│  │            Kestrel Minimal API (Web Server)               │  │
│  │  - Single-page HTML dashboard (dark/light theme)          │  │
│  │  - HTTP-only cookie sessions (12-hour TTL)                │  │
│  │  - Anti-CSRF (X-CSRF-Token header)                        │  │
│  │  - Server-Sent Events for live metrics (500ms interval)   │  │
│  │  - REST endpoints: start/stop, settings, password         │  │
│  └──────────────────────────────────────────────────────────┘  │
│                                                                  │
│  ┌──────────────────────────────────────────────────────────┐  │
│  │          Security Manager                                  │  │
│  │  - Argon2id password hashing (salt + 19 MB memory cost)  │  │
│  │  - Brute-force protection: 5 attempts → 15 min IP ban     │  │
│  │  - RTSP HA1 precomputed = MD5(user:realm:password)        │  │
│  └──────────────────────────────────────────────────────────┘  │
│                                                                  │
│  ┌────────────────────────────┐  ┌─────────────────────────┐  │
│  │   WPF Main Window          │  │   System Tray Icon       │  │
│  │   (Dark/Light Theme)       │  │   (NotifyIcon + Menu)    │  │
│  └────────────────────────────┘  └─────────────────────────┘  │
└─────────────────────────────────────────────────────────────────┘
```

### Capture Pipeline

The capture engine automatically selects the best available method at startup:

| Method | Mechanism | GPU Offload | VM Support |
|--------|-----------|-------------|------------|
| **DXGI Desktop Duplication** | `IDXGIOutputDuplication` | ✅ Yes | ❌ Blocked |
| **GDI BitBlt** | `CreateDIBSection` + `BitBlt` | ❌ CPU | ⚠️ Partial |
| **WinForms CopyFromScreen** | `Graphics.CopyFromScreen` | ❌ CPU | ✅ Full |

All methods share the same `CapturedFrame` data model backed by `ArrayPool<byte>` for zero-allocation frame reuse.

### Encoding Pipeline

| Encoder | Priority | Notes |
|---------|----------|-------|
| **Windows Media Foundation** | 1st | Built into Windows, hardware-accelerated Intel/AMD/NVIDIA Quick Sync, NVENC |
| **FFmpeg libx264** | 2nd | Requires `ffmpeg.exe` in app directory or PATH. Uses `-preset ultrafast -tune zerolatency`. |

Input format is BGRA (from capture). Converted to NV12 via integer SIMD math for YUV conversion before encoding.

### RTSP Protocol

The embedded RTSP server implements:
- **RTSP/1.0** over TCP (interleaved RTP/RTCP)
- **RFC 6184** H.264 packetization — single NAL packets for small frames, **FU-A fragmentation** for large ones (max 1400-byte RTP payload, MTU-safe)
- **RFC 2617** Digest Authentication with per-connection nonces

---

## Features

- 🖥️ **Screen Capture** — DXGI (GPU), GDI, or WinForms fallback
- 🎬 **H.264 Encoding** — Media Foundation hardware encoder + FFmpeg software fallback
- 📡 **Embedded RTSP Server** — No external tools required
- 🔊 **WASAPI Audio Loopback** — Desktop audio included in stream (toggleable)
- 🌐 **Web UI Dashboard** — Real-time metrics, controls, dark/light theme
- 🔒 **Digest Auth** — RTSP + Web session authentication with brute-force protection
- 🎨 **WPF GUI** — Dark/Light/Auto theme, minimize to tray, system integration
- 🚀 **Auto-Start Options** — start streaming automatically on launch, and/or auto-start the app with Windows (Registry `HKCU\...\Run`) — enable both for a stream after every boot
- 📦 **Single-File Portable** — No installer, no prerequisites, single `.exe`

---

## Requirements

### Runtime
- **Windows 10 version 2004** (Build 19041) or later — required for DXGI Desktop Duplication
- **Windows 7/8/8.1/10 older builds** — Supported via WinForms fallback capture
- **.NET 8 runtime** — Embedded in the self-contained build (no separate install needed)
- **DirectX 11** capable GPU — for DXGI capture; automatically falls back to CPU capture if unavailable

### Build Environment
- **.NET 8 SDK** — Download from https://dotnet.microsoft.com/download/dotnet/8.0
- **Visual Studio 2022** (optional) or any IDE with .NET support
- **Inno Setup 6** (optional, for installer only) — https://jrsoftware.org/isdl.php

---

## Compilation & Build Instructions

### Quick Build (Debug)

```powershell
cd WinToRTSP
dotnet restore
dotnet build
```

### Running in Debug Mode

```powershell
dotnet run --project .
```

### Command-Line Flags

| Flag | Behavior |
|------|----------|
| `--minimized` | Start hidden to system tray (used by auto-start registry entry) |
| `--headless` | Alias for `--minimized` |

```powershell
.\WinToRTSP.exe --minimized
```

### Release Build

```powershell
dotnet build -c Release
```

---

## Packaging Instructions

### A. Standalone Portable Executable (Single-File `.exe`)

Run the provided script:

```powershell
# Option 1: PowerShell script
.\publish-portable.ps1

# Option 2: Batch file (double-click or run from cmd)
publish-portable.bat
```

Or manually via `dotnet publish`:

```powershell
dotnet publish `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -p:DebugType=none `
  -p:DebugSymbols=false `
  -o .\publish\portable
```

> **Note:** Keep all of the flags above — omitting `-p:DebugType=none -p:DebugSymbols=false` embeds symbols and roughly doubles the output size (~180 MB instead of ~80 MB).

Output: `publish\portable\WinToRTSP.exe` (~80 MB, fully self-contained, no prerequisites)

> **Targeting 32-bit:** Use `-r win-x86` instead of `-r win-x64`

### B. Windows Installer (via Inno Setup)

**Step 1** — Install [Inno Setup 6](https://jrsoftware.org/isdl.php)

**Step 2** — Build the portable executable first (required):

```powershell
.\publish-portable.ps1
```

**Step 3** — Run the installer build script:

```powershell
.\build-installer.ps1
```

This automatically detects Inno Setup, compiles `installer\WinToRTSP.iss`, and outputs `publish\installer\WinToRTSP-Setup-v1.0.0.exe`.

**Alternatively**, compile the `.iss` script directly:

```powershell
& "C:\Program Files (x86)\Inno Setup 6\iscc.exe" "installer\WinToRTSP.iss"
```

**The installer handles:**
- Directory selection and shortcut creation (Desktop + Start Menu)
- Optional "Start on Windows Boot" task during setup
- Clean uninstallation (removes registry keys, `%LOCALAPPDATA%\WinToRTSP`, and residual files)

---

## Running the Application

1. **Launch** `WinToRTSP.exe` — the WPF window opens
2. **Start Stream** — click "Start Stream" in the window or Web UI
3. **Connect** a media player to the RTSP URL shown in the dashboard

The application starts the **Kestrel Web Server** automatically on the configured Web UI port (default: `8080`).

### Startup Options

Checkbox options live in the **System Integration** panel of the settings:

| Option | Effect | Backed by |
|--------|--------|-----------|
| **Start Streaming Automatically on Launch** | The stream starts by itself as soon as the app opens — including `--minimized` launches | `AutoStartStream` in `config.json` |
| **Start with Windows Boot (opens minimized to tray)** | The app launches when Windows starts, hidden to the tray | Registry `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` |
| **Minimize to System Tray on Close** | Closing the window hides it in the tray instead of exiting | `MinimizeToTray` in `config.json` |

> **Tip:** Enable the first two together — the PC boots, the app starts hidden to the tray and begins streaming with no clicks at all. Starting the app manually behaves the same way.

---

## Connecting to the Stream

### RTSP Clients

| Client | Command |
|--------|---------|
| **VLC** | Open `rtsp://<IP>:8554/live/screen` |
| **ffplay** | `ffplay rtsp://<IP>:8554/live/screen` |
| **OBS** | Add "Media Source" → uncheck "Local File" → enter RTSP URL |
| **MPV** | `mpv rtsp://<IP>:8554/live/screen` |
| **Android/iOS** | VLC mobile app |

> If authentication is enabled, VLC will prompt for credentials automatically (same username/password as the Web UI).

> **Which port?** The code default is `8554`, but the RTSP port and stream path are stored in `%LOCALAPPDATA%\WinToRTSP\config.json` and may have been changed (e.g. `554`). The desktop window and the web dashboard always show the exact URL for your installation — copy it from there.

### Web Dashboard

Open a browser on any device on the same network:
```
http://<host-ip>:8080
```

### Allowing LAN Access (Windows Firewall)

Windows Firewall **blocks unsolicited inbound connections by default**, so players on your LAN will not reach the app until the two ports are allowed. Typical symptoms: VLC on another machine times out or never shows a picture, and `nmap` reports the ports as *filtered* — while overlay VPNs (Tailscale, NetBird, …) still work, because their traffic rides outbound-initiated connections that the stateful firewall already permits.

Run the following in an **elevated** PowerShell on the streaming PC (adjust the RTSP port to match `RtspPort` in `config.json`):

```powershell
New-NetFirewallRule -DisplayName "WinToRTSP RTSP (LAN)" -Direction Inbound -Action Allow -Protocol TCP -LocalPort 8554 -Profile Private
New-NetFirewallRule -DisplayName "WinToRTSP Web UI (LAN)" -Direction Inbound -Action Allow -Protocol TCP -LocalPort 8080 -Profile Private
```

- Use `-Profile Private` only — this keeps the ports closed on public networks, in line with the [network security warning](#network-security-warning-do-not-expose-to-wan).
- Verify your network profile is **Private** (`Get-NetConnectionProfile`); home LANs usually already are.
- Optional, so `ping`/plain `nmap` (without `-Pn`) can find the host: `New-NetFirewallRule -DisplayName "WinToRTSP ICMP echo (LAN)" -Direction Inbound -Action Allow -Protocol ICMPv4 -IcmpType 8 -Profile Private`

---

## Security Model

| Layer | Mechanism |
|-------|-----------|
| **Password Storage** | Argon2id hash (19 MB memory, 2 iterations, 2 parallelism) |
| **RTSP Auth** | RFC 2617 Digest Auth — MD5(user:realm:password) HA1, per-connection nonce |
| **Web Auth** | Session cookie (HTTP-only, SameSite=Strict, 12-hour TTL) |
| **CSRF Protection** | `X-CSRF-Token` header required on all mutating requests |
| **Brute Force** | 5 failed attempts in 5 min → IP banned for 15 min |
| **Minimum Password** | 12 characters enforced at both UI and API level |

---

## VM & Legacy Hardware Notes

### Virtual Machines (Hyper-V, VMware, VirtualBox)

DXGI Desktop Duplication is **blocked by hypervisors** as it requires direct GPU access. WinToRTSP automatically falls back through:

1. ~~DXGI (GPU)~~ — blocked by VM
2. ~~GDI BitBlt~~ — may fail without proper display driver
3. **WinForms `Screen.CopyFromScreen`** ✅ — always works in VMs

Performance in a VM will be **CPU-based**, which is expected. The capture still feeds the H.264 encoder normally.

### Intel 2nd Gen CPUs (Sandy Bridge)

These CPUs support DirectX 11 via Intel HD Graphics 2000/3000, so DXGI capture will work. Media Foundation will use Intel Quick Sync for hardware H.264 encoding if available.

**Expected Resource Usage (1080p @ 30 FPS, Sandy Bridge):**

| State | CPU | RAM |
|-------|-----|-----|
| Idle (stopped) | < 0.5% | ~35 MB |
| Streaming (DXGI + MF) | 4–8% | ~60–80 MB |
| Streaming (WinForms fallback) | 10–20% | ~70–90 MB |

---

## Default Credentials

| Field | Value |
|-------|-------|
| **Username** | `admin` |
| **Password** | `WinToRTSP_Admin2026!` |

- Used for the **Web UI login** (`http://<host>:8080`) **and** for **RTSP digest authentication** (players like VLC will prompt with the same credentials).
- The default password is applied automatically on **first launch** (when no password has been stored yet). After that it lives only as an Argon2id hash in the config file.
- **⚠️ Change this immediately** after first launch via the *Change Password* field in the Web UI settings panel (minimum 12 characters), especially before allowing anyone else on your network to reach the stream.

Configuration (including the password hash) is saved to: `%LOCALAPPDATA%\WinToRTSP\config.json`

---

## File Structure

```
WinToRTSP/
├── App.xaml / App.xaml.cs          # Application entry point, --minimized flag
├── MainWindow.xaml / .cs           # WPF UI, theme engine, metrics display
├── WinToRTSP.csproj                # .NET 8 project, AspNetCore framework ref
│
├── Audio/
│   └── WasapiAudioCapture.cs       # WASAPI loopback → PCM16 conversion
│
├── Capture/
│   ├── IScreenCapture.cs           # Common capture interface
│   ├── CapturedFrame.cs            # Pooled frame buffer (ArrayPool<byte>)
│   ├── DxgiScreenCapture.cs        # GPU: DXGI Desktop Duplication
│   ├── GdiScreenCapture.cs         # CPU: GDI BitBlt + DIBSection
│   ├── WinFormsScreenCapture.cs    # VM: Screen.CopyFromScreen fallback
│   └── ScreenCaptureEngine.cs      # Auto-select capture with fallback chain
│
├── Config/
│   └── AppConfig.cs                # JSON config + ConfigManager singleton
│
├── Encoder/
│   ├── IVideoEncoder.cs            # Common encoder interface
│   ├── H264Utils.cs                # NAL unit parser (Annex-B)
│   ├── MediaFoundationH264Encoder.cs  # Windows MF hardware encoder
│   ├── FFmpegH264Encoder.cs        # FFmpeg libx264 software encoder
│   └── EncoderFactory.cs           # Auto-select encoder
│
├── Rtsp/
│   ├── RtspDigestAuth.cs           # RFC 2617 Digest Auth implementation
│   ├── RtpPacketizer.cs            # RFC 6184 H.264 + L16 audio packetizer
│   ├── RtspClientSession.cs        # Per-client session state + async send queue
│   └── RtspServer.cs               # Embedded RTSP/1.0 TCP server
│
├── Security/
│   └── SecurityManager.cs          # Argon2id, sessions, CSRF, IP banning
│
├── Services/
│   ├── StreamService.cs            # Central coordinator (capture→encode→broadcast)
│   └── SystemIntegration.cs        # Windows Registry autostart, theme detection
│
├── UI/
│   └── TrayIconManager.cs          # System tray icon + context menu
│
├── Web/
│   ├── WebAssets.cs                # Embedded SPA HTML (dark/light theme)
│   └── WebServer.cs                # Kestrel Minimal API server
│
├── Resources/
│   └── IconGenerator.cs            # Runtime icon generation
│
├── installer/
│   └── WinToRTSP.iss               # Inno Setup 6 installer script
│
├── publish-portable.ps1            # Build single-file portable executable
├── publish-portable.bat            # Batch wrapper for above
├── build-installer.ps1             # Build portable + compile installer
└── LICENSE                         # MIT License
```

---

## Credits

**Built by MiMo — an AI coding agent.**

All of the code in this project was designed, written, tested and debugged by **MiMo** (`mimo-v2.6-flash-free`), running in the **OpenCode** coding-agent harness. That includes the capture pipeline, the Media Foundation / FFmpeg H.264 encoders, the embedded RTSP/1.0 server and RFC 6184 packetizer, the Kestrel web dashboard, the WPF UI and theme system, the security layer, and the crash/diagnostics work (including root-causing a Control Flow Guard fail-fast in the Media Foundation cross-apartment COM calls).

Third-party building blocks:

| Component | Credit |
|-----------|--------|
| .NET 8, WPF, ASP.NET Core / Kestrel | Microsoft |
| [Vortice.Direct3D11](https://github.com/amerkoleci/Vortice.Windows) | Amer Koleci |
| [NAudio](https://github.com/naudio/NAudio) | Mark Heath |
| [Konscious.Security.Cryptography.Argon2](https://github.com/kmaragon/Konscious.Security.Cryptography) | Konscious |

Released under the MIT License (see `LICENSE`).
