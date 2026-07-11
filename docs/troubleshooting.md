# Troubleshooting

Logs are in `%ProgramData%\PC Bridge Agent\logs` and rotate daily/at 10 MB, with 14 files retained.

## Cannot authenticate

Create a new long-lived token in the Home Assistant user profile, replace the local credential, and test again. Confirm the URL targets the same Home Assistant instance. Raw server exceptions are kept in technical logs; the UI uses a plain-language error.

## Cannot connect

Open the Home Assistant URL from the PC. Reverse proxies must support WebSocket upgrade for `/api/websocket`. Use a certificate trusted by Windows; PC Bridge does not disable TLS validation. No Windows firewall inbound rule is needed.

## Service does not start

Open Windows Services and inspect **PC Bridge Agent**, then check Event Viewer and local logs. The service needs read access to `%ProgramData%\PC Bridge Agent` and access to the network. Re-run the installer as administrator to repair service registration.

## Audio unavailable from service

Windows audio endpoints belong to user sessions and can differ for LocalSystem. Configure the service account intentionally for production audio control, or use the companion-host mode when implemented in Phase 2. Other providers continue running if audio fails.

## Hardware sensor missing

The agent never invents temperature, GPU, battery, or fan values. Phase 3 hardware providers will report only when a supported driver/provider can return reliable readings.
