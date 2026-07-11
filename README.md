# PC Bridge Agent for Windows

PC Bridge Agent connects a Windows 10 or Windows 11 PC to Home Assistant. It runs quietly in the background, pushes system state in real time, and accepts only controls that are enabled locally.

The agent makes an **outbound encrypted connection** to Home Assistant. It does not open an HTTP server on the PC, create a broad firewall rule, or depend on PowerShell scripts.

> Before installing the app, install the [PC Bridge Home Assistant integration](https://github.com/stormey2010/PC-Bridge-HA).

## Download the Windows app

1. Open the [latest PC Bridge Agent release](https://github.com/stormey2010/PC-Bridge-App/releases/latest).
2. Under **Assets**, download `PC-Bridge-Agent-0.1.3-x64-setup.exe` or the newest `-setup.exe` available.
3. Run the installer.
4. Leave **Install the background Windows service** selected so PC Bridge starts when Windows boots.
5. Leave **Open PC Bridge Agent after installation** selected.

The installer is currently unsigned, so Windows may show an unknown-publisher warning. Only download it from this repository's Releases page. Code signing is planned for a later release.

## First-time setup

### 1. Prepare Home Assistant

Install and add the [PC Bridge Home Assistant integration](https://github.com/stormey2010/PC-Bridge-HA#start-here) first.

### 2. Create a Home Assistant token

1. In Home Assistant, select your user name in the bottom-left corner.
2. Open the **Security** tab if shown.
3. Scroll to **Long-lived access tokens**.
4. Select **Create token** and name it `PC Bridge`.
5. Copy the token. Home Assistant only shows it once.

The token has the permissions of the Home Assistant user who created it. A dedicated Home Assistant user is recommended when practical.

### 3. Connect the PC

In the PC Bridge setup window, enter:

- **Home Assistant URL:** for example `https://home.example.com` or `http://homeassistant.local:8123`
- **Long-lived access token:** the token created above
- **Device name:** the name you want shown in Home Assistant
- **Privacy-sensitive sensors:** leave off unless you deliberately want them

Select **Test connection**. PC Bridge will not save setup until Home Assistant accepts the URL and token. Then select **Connect & finish**.

The token is encrypted with Windows DPAPI and is never placed in `settings.json`, the registry, or logs.

### 4. Verify it is working

1. Open **Settings → Devices & services → PC Bridge** in Home Assistant.
2. Your Windows PC should appear as a device with its entities.
3. Use **Configure** on the integration for options, or **Download diagnostics** for redacted logs.

If it does not connect, restart the **PC Bridge Agent** service from Windows Services and check `%ProgramData%\PC Bridge Agent\logs`.

## Phase 1 features

- CPU, memory, uptime, boot time, idle time, lock state, and power state
- Network upload/download rate and local IP
- Volume sensor/control, mute switch, and default audio output
- Real Windows Power Request-based keep awake
- Lock and sleep controls
- Restart and shutdown controls, disabled locally by default
- Automatic reconnection after Home Assistant outages, network changes, and wake
- Exponential backoff without log or network spam
- Stable installation UUIDs that survive PC renames and IP changes
- Fluent dark Windows configuration UI
- Rotating local logs

Hardware temperatures, GPU details, media controls, app allowlists, the full tray experience, temporary pairing codes, and advanced commands are later phases. Unsupported values are not faked.

## Running in the background

The installer creates the **PC Bridge Agent** Windows service and configures it to start automatically. Closing the settings window does not stop the service.

## Changing settings later

Open PC Bridge Agent from the Start menu. The pages are functional in version 0.1.3 and newer:

- **Home Assistant:** edit/test the URL, replace or remove the token, and restart the agent.
- **Sensors:** enable or disable system, audio, network, and keep-awake groups.
- **Controls:** locally allow or deny each remote control, including restart and shutdown.
- **Logs:** open the log directory or export redacted diagnostics.
- **Settings:** control service startup and the privacy-sensitive sensor master permission.

When a service restart is required, the app asks before showing the normal Windows administrator prompt.

To check it:

1. Press `Ctrl + Shift + Esc` and open **Services**, or open the Windows Services application.
2. Find **PC Bridge Agent**.
3. Its status should be **Running** and startup type should be **Automatic**.

## Updating

Download the newer installer from [Releases](https://github.com/stormey2010/PC-Bridge-App/releases) and run it. Your settings and protected credential are preserved during upgrades.

## Uninstalling

1. Open **Windows Settings → Apps → Installed apps**.
2. Find **PC Bridge Agent** and select **Uninstall**.
3. The uninstaller asks whether to remove settings, the protected credential, and logs.

Remove the Home Assistant integration separately if no PCs will use it.

## Troubleshooting

### Home Assistant authentication failed

The token may be incomplete or revoked. Create a new token and replace the saved credential in PC Bridge.

### Home Assistant is unreachable

Open the exact URL from the PC. If you use HTTPS, Windows must trust the certificate. Reverse proxies must support WebSocket connections to `/api/websocket`.

### Audio entities are unavailable

Windows audio endpoints can differ between service and interactive user accounts. Make sure Windows has a default multimedia output device. Other sensor groups continue working if audio fails.

### Logs

Logs are stored in:

```text
%ProgramData%\PC Bridge Agent\logs
```

See [docs/troubleshooting.md](docs/troubleshooting.md) for additional checks.

## Security

- No inbound PC listener or firewall exception
- TLS certificate validation is never disabled
- DPAPI-protected Home Assistant token
- Fixed command handlers and locally enabled controls
- Duplicate-command protection, message-size limits, typed parameters, and timeouts
- No arbitrary PowerShell, CMD, shell, clipboard, or keystroke access in Phase 1

See [docs/security.md](docs/security.md) and [docs/privacy.md](docs/privacy.md).

## Build from source

Requirements: Windows 10/11, .NET SDK 9 with the .NET 8 targeting/runtime packs, and Inno Setup 6 for the installer.

```powershell
dotnet restore .\PcBridge.sln
dotnet test .\PcBridge.sln -c Release
.\installer\build.ps1
```

Self-contained app/service files are placed in `artifacts\publish`; the installer is placed in `artifacts\installer`.

## License

MIT — see [LICENSE](LICENSE).
