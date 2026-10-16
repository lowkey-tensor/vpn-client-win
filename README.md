# VPN Client for Windows

**English** · [Русский](README.ru.md)

A lightweight Windows VPN client in the spirit of Happ and Hiddify: paste a link or a subscription, press one button.
Powered by [sing-box](https://github.com/SagerNet/sing-box) and [Xray-core](https://github.com/XTLS/Xray-core).

## Install

1. Download the latest `VpnClient-*-win-x64.zip` from **Releases** and unzip it anywhere.
2. Run `VpnClient.exe` and accept the administrator prompt (needed for TUN mode and autostart).
3. Paste a node link or a subscription URL into the field at the top, press `+`, select a node and press the big round button.

Windows may show "Windows protected your PC" because the app is not code-signed.
Click **More info → Run anyway**. No .NET installation is required, and the cores are bundled (`core` folder).

## Supported protocols

| Protocol | Link | Notes |
|---|---|---|
| VLESS | `vless://` | Reality, TLS, Vision; tcp / ws / grpc / h2 / httpupgrade transports; **VLESS encryption (mlkem) and XHTTP** via Xray |
| VMess | `vmess://` | v2rayN (base64 JSON), `vmess://uuid@host` and Shadowrocket formats |
| Trojan | `trojan://` | TLS, Reality, ws / grpc |
| Shadowsocks | `ss://` | SIP002 and legacy formats, SS-2022, obfs-local and v2ray-plugin |
| TUIC | `tuic://` | v5 |
| Hysteria 2 | `hysteria2://`, `hy2://` | salamander obfs, port hopping |
| Hysteria | `hysteria://` | v1 (udp) |
| AnyTLS | `anytls://` | |
| SOCKS5 | `socks://`, `socks5://` | with or without credentials |

Not supported: WireGuard links, mKCP, Shadowsocks with shadow-tls. The client explains why in its log.
Every protocol has been tested end to end against local test servers (link → config → working connection).

## Features

- Modes: **system proxy** and **TUN** (all traffic on the computer).
- Import: links, subscriptions (plain text and base64), QR code of a node for the phone.
- Russian sites direct (banks, marketplaces and government sites don't see the VPN) — a switch.
- Ad and tracker blocking at the DNS level.
- Split tunneling: programs and sites can go "direct" or "always through the VPN".
- Live speed, traffic and real latency through the VPN; latency to the servers in the list.
- Auto-recovery: reconnect, switch to another node, react to sleep and wake-up.
- Autostart at Windows sign-in (a Task Scheduler job with admin rights, no UAC prompt), lives in the tray.
- Multiplexing (mux) is enabled for VLESS via Xray: dozens of application connections share a few TCP connections to
  the server. Without it, servers and ISPs that limit new connections stall most requests in TUN mode.
- Log files (`%APPDATA%\VpnClient\logs`, kept for 7 days).

## Build from source

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) and Windows 10/11 x64.

```powershell
powershell -ExecutionPolicy Bypass -File scripts\fetch-core.ps1   # downloads the cores into core\ (SHA256-verified)
dotnet build src\VpnClient -c Release -o app
app\VpnClient.exe
```

A self-contained archive (no .NET needed on the target machine):

```powershell
powershell -ExecutionPolicy Bypass -File scripts\package.ps1 -Version 4.1
```

## Where data is stored

`%APPDATA%\VpnClient\state.dat` holds the node list and settings, encrypted with Windows DPAPI (readable only by your
Windows account). Temporary configs containing keys are wiped 3 seconds after the core starts.
Keys and links never enter the repository (see `.gitignore`).

## Uninstall

Quit the client (tray icon → Exit), turn off "Start with Windows" in the settings and delete the program folder.
User data lives in `%APPDATA%\VpnClient`.

## Phone

The `mobile/` folder contains ready-made rules for Shadowrocket (iOS) with the same behavior as the desktop client.

## Cores and licenses

The sing-box and Xray-core binaries are not stored in this repository: `scripts/fetch-core.ps1` downloads them from
the official releases. They are distributed under their own licenses; see the projects' pages.
The software is provided "as is", without warranty of any kind.
