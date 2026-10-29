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

**Unblock the archive before unzipping:** right-click the `.zip` → *Properties* → tick **Unblock** → OK. Otherwise Windows marks every
extracted file (including the cores) as downloaded from the internet, and SmartScreen or an antivirus may refuse to start them.

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
- Split tunneling in the **Routes** window: type a site (a full URL, IP or subnet also works) and press **Direct** or **Through VPN**;
  flip or delete rules in one click, copy the whole list and paste it on another computer. Programs can go direct, or (TUN, experimental)
  *only the listed programs* go through the VPN.
- **Steam games go direct** in TUN mode (everything under `steamapps`, plus the Steam client), so online games with anti-cheat see your real connection.
  Roblox, the game PEAK and other programs stay in the VPN. One switch in **Routes**.
- Live speed and traffic; **latency through the VPN** (real HTTP round trip) and ping to the server shown separately.
- Small screens: the window fits the work area (1366×768 and up), scrolls when needed and remembers its size and position.
- Clear start-up errors: if Windows or an antivirus blocks a core, the client says so and what to do instead of a bare error code.
- **Copy support report** and **Report a problem** (Log and support section): a report with personal data (addresses, links, node names,
  your user name) removed is copied to the clipboard.
- Auto-recovery: reconnect, switch to another node, react to sleep and wake-up.
- Autostart at Windows sign-in (a Task Scheduler job with admin rights, no UAC prompt), lives in the tray.
- Multiplexing (mux) is enabled for VLESS via Xray: dozens of application connections share a few TCP connections to
  the server. Without it, servers and ISPs that limit new connections stall most requests in TUN mode.
- Log files (`%APPDATA%\VpnClient\logs`, kept for 7 days).

## Troubleshooting

If it does not connect, open **Log and support** at the bottom of the window first: the client writes the reason there.

| Symptom | Likely cause and fix |
|---|---|
| Message "the program was started from the archive" | You double-clicked `VpnClient.exe` inside the zip. Quit it (tray icon → Exit), unzip the archive fully (*Extract all…*) and run it from the new folder. |
| Red banner "cannot start the core" | An antivirus or Windows removed/blocked `core\sing-box.exe` or `core\xray.exe`. Add the program folder to the antivirus exclusions, unblock the archive (see Install) and unzip again. |
| Error code 225 / 226 | The antivirus considered the core a virus. Same fix as above. |
| Error code 5 (access denied) | The folder is protected (Controlled Folder Access, a corporate policy) or the file is blocked. Move the program to e.g. `C:\Tools\Vpn`. |
| Connects, but nothing loads, latency about 1000 ms | Another VPN or proxy is running at the same time (Happ, Hiddify, Zapret, a browser extension): quit it. Try the other mode (system proxy ↔ TUN) and another node. |
| Only some sites work in TUN | Check **latency through the VPN**. If it is high, the server or provider limits new connections: use a node with multiplexing (VLESS through Xray) or another server. |
| A site that must work directly does not open | Add it in **Routes** → **Direct**, then **Save and reconnect**. |

If it still fails, press **Report a problem**: a page opens and the (anonymised) report is already on your clipboard, just paste it.
## Build from source

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) and Windows 10/11 x64.

```powershell
powershell -ExecutionPolicy Bypass -File scripts\fetch-core.ps1   # downloads the cores into core\ (SHA256-verified)
dotnet build src\VpnClient -c Release -o app
app\VpnClient.exe
```

A self-contained archive (no .NET needed on the target machine):

```powershell
powershell -ExecutionPolicy Bypass -File scripts\package.ps1 -Version 4.4.1
```

## Where data is stored

`%APPDATA%\VpnClient\state.dat` holds the node list and settings, encrypted with Windows DPAPI (readable only by your
Windows account). Temporary configs containing keys are wiped 3 seconds after the core starts.
Keys and links never enter the repository (see `.gitignore`).
Set the `VPNCLIENT_DATA_DIR` environment variable to keep data, logs and temporary configs in another folder (tests, portable use).

## Uninstall

Quit the client (tray icon → Exit), turn off "Start with Windows" in the settings and delete the program folder.
User data lives in `%APPDATA%\VpnClient`.

## Phone

The `mobile/` folder contains ready-made rules for Shadowrocket (iOS) with the same behavior as the desktop client.

## License

This project is released under the [MIT License](LICENSE).

## Cores and third-party licenses

The sing-box and Xray-core binaries are not stored in this repository: `scripts/fetch-core.ps1` downloads them from
the official releases. They are distributed under their own licenses (copies are in `core/`); see the projects' pages.
The software is provided "as is", without warranty of any kind.
