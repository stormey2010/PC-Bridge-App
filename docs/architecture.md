# Architecture and entity model

## Final Phase 1 decision

PC Bridge Agent uses .NET 8 and separates protocol/configuration (`Core`), Windows-native providers and command handlers (`Windows`), the reconnecting service host (`Service`), and the WPF settings companion (`App`). The Home Assistant integration is an in-process push hub. It registers authenticated WebSocket commands and holds the already-authenticated agent connection for command delivery.

This is deliberately not a webhook: a webhook would require Home Assistant to reach the PC. It is not MQTT in Phase 1 because the Home Assistant WebSocket keeps setup to one trust system and provides a direct authenticated full-duplex connection.

## Lifecycle

1. The service loads non-secret settings and decrypts the token through DPAPI.
2. It opens `/api/websocket`, completes Home Assistant authentication, and registers device metadata/entity descriptors.
3. The integration creates/updates a device identified by the installation UUID and dynamically adds entities.
4. Each provider samples independently. A failed provider logs a warning without stopping other providers.
5. Home Assistant entity calls become versioned command events on the same socket.
6. The agent checks local enablement, de-duplicates the ID, validates parameters, executes with a timeout, then acknowledges the actual result. Keep-awake uses handle-based Windows Power Requests so timer/service-shutdown release is not thread-affine.
7. On disconnect, last values remain; availability expires. Backoff grows exponentially to five minutes with jitter. Network return and wake signal an early retry.

## Entity model

`unique_id = installation_id + "_" + descriptor.key`. Device identifiers use `(pc_bridge, installation_id)`. Names, IPs, and Home Assistant entity IDs are mutable labels and never identity.

Provider descriptors carry platform, name, device class, unit, default enablement, and entity category. Static connected/version/last-seen entities are integration-owned. Command buttons are registered by the agent so disabled-by-default control policy follows that PC.

## Availability

The integration records `last_seen` on every state batch. `available` becomes false after the options-flow timeout (30 seconds by default). Stored values are not overwritten with zero or unknown. Commands fail immediately while unavailable.

## Phase boundaries

Phase 1 contains only the reliable baseline. Phase 2 adds media/display/storage/battery/apps/tray/export. Phase 3 adds optional hardware and privacy providers, temporary pairing, updates, and the separately warned Advanced Command Mode. No future entity is simulated in the current provider set.
