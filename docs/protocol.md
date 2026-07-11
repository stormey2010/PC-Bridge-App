# Connection protocol v1

Transport is JSON over Home Assistant's authenticated WebSocket API. Home Assistant command IDs (`id`) correlate WebSocket API calls. PC Bridge `message_id` values correlate and de-duplicate remote commands.

## Limits

- Protocol version: `1`
- Maximum encoded agent message: 1 MiB
- Maximum state descriptors per registration payload: bounded by the registration-size check
- Maximum states per update: 128
- Command execution timeout: 30 seconds in Phase 1
- Duplicate command retention: one hour

## Register

```json
{"id":1,"type":"pc_bridge/register_agent","registration":{"installation_id":"uuid","device_name":"Office PC","agent_version":"0.1.0","manufacturer":"Example","model":"Desktop","windows_version":"Windows 11","entities":[{"key":"cpu_usage","platform":"sensor","name":"CPU usage","unit":"%"}]}}
```

## State update

```json
{"id":2,"type":"pc_bridge/state_update","device_id":"uuid","message_id":"unique","timestamp":"2026-07-10T20:00:00Z","states":[{"key":"cpu_usage","value":12.4}]}
```

## Command event

```json
{"protocol_version":1,"message_type":"command","message_id":"unique","timestamp":"2026-07-10T20:00:01Z","command":{"command":"system.lock","parameters":{}}}
```

## Result

```json
{"id":3,"type":"pc_bridge/command_result","device_id":"uuid","message_id":"unique","result":{"success":true,"error_code":null,"message":"PC locked successfully."}}
```

Unknown devices, stale sockets, unregistered sessions, unknown entity keys, oversized batches, invalid parameters, disabled controls, and unknown commands are rejected. Future incompatible protocol versions must be rejected before state/commands are accepted.
