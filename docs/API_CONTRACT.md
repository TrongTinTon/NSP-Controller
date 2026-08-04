# Controller ↔ Edge API contract

All routes are `POST /v1/<route>` and require Core API authentication.

## Heartbeat

Route: `heartbeat`

```json
{
  "controller_code": "CTRL-01"
}
```

## Pull Reader configuration

Route: `controller/device-config/pull`

Request:

```json
{
  "controller_code": "CTRL-01"
}
```

Relevant response data:

```json
{
  "devices": [
    {
      "serial_number": "RDR-SN-001",
      "reader_parameters": {
        "power_dbm": 30,
        "read_interval_ms": 200,
        "tid_start_address": 2,
        "tid_length": 4
      },
      "ports": [
        {"port_no": 1},
        {"port_no": 2}
      ]
    }
  ]
}
```

The Edge payload does not contain physical IP/COM configuration. Driver, endpoint, TCP/COM port and vendor options are Controller-local settings keyed by Reader serial number.

## Report Reader status

Route: `devices/report`

```json
{
  "controller_code": "CTRL-01",
  "devices": [
    {
      "serial_number": "RDR-SN-001",
      "ports": [1, 2],
      "device_status": "online",
      "last_seen_at": "2026-08-04T07:30:00Z",
      "firmware_version": "1.0.0",
      "power_dbm": 30,
      "read_interval_ms": 200
    }
  ]
}
```

## Push raw Parking detections

Route: `parking/detections/push`

Maximum batch: 1000.

```json
{
  "controller_code": "CTRL-01",
  "detections": [
    {
      "event_uid": "CTRL-01-RFID-...",
      "serial_number": "RDR-SN-001",
      "port_no": 1,
      "detected_at": "2026-08-04T07:30:00.125Z",
      "tid": "E280689400001111"
    }
  ]
}
```

The Controller sends raw technical observations only. Edge resolves TID through its runtime assignment projection and performs Parking classification.

## Pull Lane Calibration

The current Edge route names retain `measurement` for API compatibility. Controller domain and UI use the term Lane Calibration.

Route: `controller/measurement/pull`

```json
{
  "controller_code": "CTRL-01",
  "current_measurement_code": "CAL-0001"
}
```

Relevant response data:

```json
{
  "data": {
    "measurement_available": true,
    "measurement_code": "CAL-0001",
    "status": "ready",
    "desired_state": "running",
    "revision": 2,
    "readers": [
      {
        "serial_number": "RDR-SN-001",
        "power_dbm": 28,
        "read_interval_ms": 100,
        "ports": [1, 2]
      }
    ]
  }
}
```

Selected Reader configurations temporarily override operational power, interval and active Port set. Readers participating in Lane Calibration are isolated from Parking delivery until the session stops.

## Push Lane Calibration events

Route: `controller/measurement/events`

Maximum batch: 100.

```json
{
  "controller_code": "CTRL-01",
  "measurement_code": "CAL-0001",
  "events": [
    {
      "event_uid": "CTRL-01-CAL-...",
      "revision": 2,
      "power_dbm": 28,
      "read_interval_ms": 100,
      "serial_number": "RDR-SN-001",
      "port_no": 1,
      "tid": "E280689400001111",
      "read_at": "2026-08-04T07:31:00.125Z",
      "rssi_dbm": -48.5
    }
  ]
}
```

The event stores the actual runtime power and interval applied by the Controller. Item results `processed`, `duplicate`, and `ignored` are final delivery states. `rejected` is moved to the local dead state.

## Report Lane Calibration status

Route: `controller/measurement/status`

```json
{
  "controller_code": "CTRL-01",
  "measurement_code": "CAL-0001",
  "status": "running",
  "occurred_at": "2026-08-04T07:31:00Z",
  "message": "Controller started Lane Calibration"
}
```

Allowed status values are controlled by Edge. Controller currently reports `running` after applying a `ready` configuration.
