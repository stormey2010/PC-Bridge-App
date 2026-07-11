# Troubleshooting

Logs are in `%ProgramData%\PC Bridge Agent\logs` and rotate daily/at 10 MB, with 14 files retained. Live connection status is also written to `%ProgramData%\PC Bridge Agent\status.json` for the desktop app.

## Cannot authenticate

Create a new long-lived token in the Home Assistant user profile, replace the local credential, and test again. Confirm the URL targets the same Home Assistant instance. Raw server exceptions are kept in technical logs; the UI uses a plain-language error.

## Cannot connect

Open the Home Assistant URL from the PC. Reverse proxies must support WebSocket upgrade for `/api/websocket`. Use a certificate trusted by Windows; PC Bridge does not disable TLS validation. No Windows firewall inbound rule is needed.

## Test connection succeeds but entities never appear

Install and enable the **PC Bridge** Home Assistant integration first, then restart Home Assistant. The Windows app now probes for the integration during Test connection — if it is missing you will see an explicit error instead of a false success.

## Service restart fails from the app

Use **Restart agent** again after approving the UAC prompt. The app waits for Windows to fully stop and start **PC Bridge Agent**. If it still fails, open `services.msc`, restart the service manually, then check the logs folder.

## Settings changed but Home Assistant still looks old

Saving connection, sensor, or control settings writes `%ProgramData%\PC Bridge Agent\settings.json`. The service watches that file and reconnects automatically. If status stays stuck, use **Restart agent**.

## Service does not start

Open Windows Services and inspect **PC Bridge Agent**, then check Event Viewer and local logs. The service needs read access to `%ProgramData%\PC Bridge Agent` and access to the network. Re-run the installer as administrator to repair service registration.

## Audio unavailable from service

Windows audio endpoints belong to user sessions and can differ for LocalSystem. Configure the service account intentionally for production audio control, or use the companion-host mode when implemented in Phase 2. Other providers continue running if audio fails.

## Hardware sensor missing

The agent never invents temperature, GPU, battery, or fan values. Phase 3 hardware providers will report only when a supported driver/provider can return reliable readings.
