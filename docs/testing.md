# Testing and release checklist

## Automated

```powershell
dotnet build PcBridge.sln -c Release
dotnet test PcBridge.sln -c Release
python -m compileall -q custom_components tests
pytest
ruff check custom_components tests
```

Windows tests cover protocol serialization, replay protection, stable settings, destructive defaults, and DPAPI ciphertext/roundtrip. Home Assistant tests cover config flow, singleton hub behavior, state push, and availability preserving last values.

## Manual Windows/Home Assistant

- Complete first-run setup with a valid token; invalid token must not save.
- Verify no token appears in `settings.json`, logs, diagnostics, or process arguments.
- Restart Home Assistant; confirm exponential reconnect and entities recover without duplicates.
- Disconnect/reconnect network and change adapters; confirm recovery without log spam.
- Sleep/wake and hibernate/wake; confirm early reconnect and new state.
- Lock/unlock and wait for state updates.
- Stop the service for more than the availability timeout; confirm unavailable with retained numeric values.
- Send the same command ID twice; verify one execution.
- Send unknown, malformed, oversized, wrong-device, and pre-registration messages; verify rejection.
- Verify restart/shutdown reject while locally disabled.
- Enable destructive controls deliberately in a test VM; verify Windows acceptance is reflected in the result.
- Enable keep awake, timed keep awake, and display keep awake; verify release on disable, timeout, and service stop.
- Remove/default-change the audio device; provider failure must not stop CPU/memory updates.
- Install/upgrade/uninstall with service and startup combinations; verify settings preserved on upgrade and optional removal on uninstall.

## Resource measurement

On release hardware, record service working set, private bytes, CPU over a 15-minute idle period, and log bytes written. Acceptance target: under 1% average CPU, under 100 MB RAM where practical, no one-second all-sensor polling, and no repeating exception storm.
