# NSP Controller Alignment - v1.1.0

This source is aligned to the current NSP Controller responsibility boundary:

`RFID Reader -> Controller -> Edge -> Parking business processing`

## Implemented Controller capabilities

1. Core API Service authentication: `/auth/token` and rotating `/auth/refresh`.
2. Saved Edge URL first; Zeroconf discovery only after transport/network failure.
3. Controller heartbeat: `/v1/heartbeat`.
4. Reader/Antenna configuration pull: `/v1/controller/device-config/pull`.
5. Reader runtime health report: `/v1/devices/report` using Serial Number identity.
6. Isolated Reader runtimes with reconnect loops and driver registry extension point.
7. Strict server antenna enforcement; disabled/unconfigured antenna callbacks are dropped.
8. Continuous RFID TID inventory with non-blocking ingest queue.
9. Durable Parking outbox, batching, exponential retry and stable `event_uid`.
10. Exact Parking transport contract: `event_uid`, `serial_number`, `antenna_no`, `detected_at`, `tid` only.
11. Measurement pull/events/status APIs with a dedicated outbox and RSSI support.
12. Parking/Measurement routing isolation per Reader participating in an active Measurement Session.
13. Technical UI for connection, Readers, local physical endpoints, Live RFID/Measurement and logs.

## Removed / corrected legacy behavior

- Removed `grant_type` from initial authentication.
- Refresh no longer calls `/auth/token`; it uses `/auth/refresh` with only `refresh_token`.
- Removed public `service_code`, `server_code` and `gateway_base` route construction.
- Removed custom `X-Client-ID` / `X-Controller-ID` transport headers.
- Removed legacy Reader report fields `device_code`, `status`, and `message` from API payloads.
- Controller no longer expects server `device_code`, `driver_key`, or physical endpoint from the current Device Config API.
- Parking outbox no longer stores/sends RSSI, frequency, driver or other fields outside the current Parking Detection API contract.
- No parking business decisions exist in executable Controller C# code.

## Important physical connection rule

The current Edge Controller Device Config payload provides Reader Serial Number, technical Reader operation parameters and physical antenna declarations, but not the physical IP/COM endpoint. Therefore Driver/Endpoint/Port are preserved locally by Serial Number. The built-in CF-E718 adapter supports empty Endpoint as SDK Auto COM discovery.

## Validation status

Static source checks were completed for C# delimiter balance, XML parse, current route presence, legacy route/auth markers and business-logic boundary. A .NET Framework compiler/runtime is not installed in the build sandbox, so this package has not been compiled or run against physical RFID hardware here.
