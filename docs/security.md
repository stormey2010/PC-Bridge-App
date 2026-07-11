# Security model

The primary boundary is the authenticated Home Assistant WebSocket connection plus local Windows policy. The agent has no inbound listener. Home Assistant tokens are accepted only by Home Assistant and stored locally as DPAPI ciphertext.

## Phase 1 controls

- TLS works through `https` → `wss`; certificate errors are not bypassed.
- Secrets are absent from settings, logs, command metadata, and diagnostics.
- The integration associates an installation ID with the exact authenticated socket that registered it.
- Every command is checked against local `EnabledControls`, a fixed handler table, a duplicate-ID guard, strict typed parameters, and a 30-second timeout.
- Destructive actions are disabled by default and success is returned only after the Windows API/process accepts the request.
- Keep-awake is released on disable, timeout, and service disposal.
- Provider failures cannot turn into remote code execution or crash the other providers.

## Token risk

A long-lived token has the Home Assistant permissions of its user. DPAPI protects the token at rest, not against an attacker already executing as SYSTEM/administrator on that PC. Prefer a dedicated least-privilege Home Assistant user where policy permits, use HTTPS, and revoke the token immediately if either system is compromised.

## Advanced Command Mode

Advanced Command Mode is **not safe command filtering**. Once enabled in Phase 3, it is authenticated remote code execution under the selected Windows account. It can destroy files, expose credentials, disable security tools, install malware, or compromise the entire PC. It must remain off across installs/upgrades unless locally enabled through PC-name confirmation, explicit warning acknowledgement, authorization selection, and (separately) administrator approval for elevation. Elevation must use supported Windows service/broker/UAC flows and never store an administrator password.

Phase 1 rejects arbitrary command payloads and does not register unrestricted Home Assistant services.

## Reporting

Report vulnerabilities privately to repository maintainers. Do not include live tokens, command output, usernames, or sensitive paths. Rotate any credential accidentally included in a report.
