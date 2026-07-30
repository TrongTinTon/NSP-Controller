# Controller <-> Edge API Contract

This contract follows the current NSP Gatekeeper architecture and the current Odoo source.
The Controller sends physical/device facts only. Parking business decisions remain on Edge.

## Core API routing

Public Core API routes are exposed directly as `/v1/...`.
There is no public `service_code`, `server_code` or `gateway_base` URL prefix.

## 1. Authenticate

`POST /auth/token`

Request contains exactly:

```json
{
  "client_id": "...",
  "client_secret": "..."
}
```

Response:

```json
{
  "status": "success",
  "message": "OK",
  "data": {
    "access_token": "...",
    "refresh_token": "...",
    "token_type": "Bearer",
    "expires_in": 86400,
    "refresh_expires_in": 2592000
  }
}
```

Refresh is a different route:

`POST /auth/refresh`

```json
{
  "refresh_token": "..."
}
```

Refresh tokens rotate. The Controller must save the newly returned pair in memory and must not reuse the old refresh token.

## 2. Heartbeat

`POST /v1/heartbeat`

```json
{
  "controller_code": "CTRL-01"
}
```

## 3. Pull Reader/Antenna configuration

`POST /v1/controller/device-config/pull`

```json
{
  "controller_code": "CTRL-01"
}
```

Current Edge payload uses `serial_number` as Reader identity and returns server-managed technical settings:

```json
{
  "serial_number": "241130001",
  "reader_parameters": {
    "power_dbm": 30,
    "read_interval_ms": 200,
    "tid_start_address": 2,
    "tid_length": 4
  },
  "antennas": [
    {
      "antenna_no": 1
    }
  ]
}
```

Antenna configuration contains only the physical `antenna_no`. RSSI remains telemetry/Measurement data and is not used as an operational validity threshold.

`device_code` is not a Controller runtime identity and is not sent back to Edge.

Physical endpoint/driver information is Controller-local when the current Edge payload does not provide it. The built-in CF-E718 driver can use SDK Auto COM discovery when endpoint is empty; Ethernet endpoints can be set in the Readers tab and are preserved across server config pulls.

## 4. Reader health report

`POST /v1/devices/report`

```json
{
  "controller_code": "CTRL-01",
  "devices": [
    {
      "serial_number": "241130001",
      "antennas": [1, 2],
      "device_status": "online",
      "last_seen_at": "2026-07-27T04:00:00Z",
      "firmware_version": "",
      "power_dbm": 30,
      "read_interval_ms": 200
    }
  ]
}
```

Allowed status values are `online`, `offline`, `degraded`.
The Controller does not send `message`, `device_code`, model, endpoint or parking topology in this runtime report.

## 5. Parking detections

`POST /v1/parking/detections/push`

```json
{
  "controller_code": "CTRL-01",
  "detections": [
    {
      "event_uid": "CTRL01-RFID-000001",
      "serial_number": "241130001",
      "antenna_no": 1,
      "detected_at": "2026-07-27T04:00:01.123Z",
      "tid": "20006023044D649E"
    }
  ]
}
```

Parking detection contains exactly physical facts. It does not send:

- `direction`
- `event_type`
- `vehicle_id`
- `user_id`
- `parking_area_code`
- `lane_code`
- `decision`
- `rssi_dbm`

A request may contain up to 1000 detections. Unknown TIDs are terminally ignored by Edge and acknowledged with HTTP 200 success so Controller does not retry them.

## 6. Measurement

### Pull

`POST /v1/controller/measurement/pull`

```json
{
  "controller_code": "CTRL-01",
  "current_measurement_code": ""
}
```

Response configuration contains only the Reader runtime subset owned by this Controller. RFID target lists remain on Cloud/Edge:

```json
{
  "measurement_available": true,
  "measurement_code": "MSR-...",
  "revision": 3,
  "desired_state": "running",
  "readers": [
    {"serial_number": "241130001", "power_dbm": 22, "read_interval_ms": 150, "antennas": [1, 3]},
    {"serial_number": "241130002", "power_dbm": 18, "read_interval_ms": 200, "antennas": [1, 2]}
  ]
}
```

### Events

`POST /v1/controller/measurement/events`

```json
{
  "controller_code": "CTRL-01",
  "measurement_code": "MSR-...",
  "events": [
    {
      "event_uid": "CTRL01-MEAS-...",
      "revision": 3,
      "power_dbm": 22,
      "read_interval_ms": 150,
      "serial_number": "241130001",
      "antenna_no": 1,
      "tid": "20006023044D649E",
      "read_at": "2026-07-27T04:01:00.125Z",
      "rssi_dbm": -41.5
    }
  ]
}
```

Maximum: 100 events/request. Controller persists `revision`, temporary Reader `power_dbm`, and `read_interval_ms` with every observation so pending data from an earlier Measure Again revision can never be misclassified as the current revision. Every TID detected on a selected Measurement Reader/Antenna is queued. Edge matches the configured RFID target list and ignores non-target reads.

### Status

`POST /v1/controller/measurement/status`

```json
{
  "controller_code": "CTRL-01",
  "measurement_code": "MSR-...",
  "status": "running",
  "occurred_at": "2026-07-27T04:01:00Z",
  "message": "Controller started Measurement mode"
}
```

A `ready` Measurement Session with a future `planned_start_at` remains in Parking mode until that time. Every Reader assigned to an active Measurement Session is isolated from Parking delivery while that session is active. Each Reader uses temporary Reader Power and Read Interval; only the `antennas` selected for that Reader participate. Readers outside the session continue Parking operation. If `planned_end_at` is reached while the session remains `ready`/`running`, the Controller reports `completed` and restores Parking mode.

## Zeroconf fallback

Zeroconf is used only when the saved Server URL fails at network/transport level. HTTP credential, permission or contract errors do not trigger discovery.

The DNS-SD service `_nsp._tcp.local` advertises the Discovery Service SRV port, normally `9000`. Controller uses only these TXT properties:

```text
port=8069
auth_path=/auth/token
```

Flow:

```text
_nsp._tcp.local
    -> discovered IP + SRV service port 9000
    -> TXT port=8069, auth_path=/auth/token
    -> Core API base URL http://<IP>:8069
    -> POST /auth/token
    -> save URL only after authentication succeeds
```

No other Zeroconf metadata is required.
