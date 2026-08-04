# Controller API contract

## Reader configuration: Edge → Controller

Route:

```text
POST controller/device-config/pull
```

Controller consumes Reader identity and technical parameters:

```json
{
  "serial_number": "24113001",
  "reader_parameters": {
    "power_dbm": 5,
    "read_interval_ms": 500,
    "tid_start_address": 0,
    "tid_length": 6
  },
  "ports": [
    {"port_no": 1},
    {"port_no": 2}
  ]
}
```

`ports` is accepted for compatibility but intentionally ignored by Controller. An empty or missing list does not disable the Reader and does not prevent COM connection.

## Reader status: Controller → Edge

Route:

```text
POST devices/report
```

```json
{
  "serial_number": "24113001",
  "ports": [1, 2, 3, 4],
  "device_status": "online",
  "last_seen_at": "2026-08-04T11:30:00Z",
  "power_dbm": 5,
  "read_interval_ms": 500
}
```

Status `ports` describes the hardware ports scanned by the driver. It is not a business whitelist.

## Raw Parking detection: Controller → Edge

Route:

```text
POST parking/detections/push
```

```json
{
  "event_uid": "CTRL-01-RFID-...",
  "serial_number": "24113001",
  "port_no": 3,
  "tid": "E280689400001111",
  "detected_at": "2026-08-04T11:30:00.125Z"
}
```

Controller forwards the event even when `port_no` is not configured for a Parking sequence. Edge owns filtering and may return `ignored`.

## Lane Calibration configuration: Edge → Controller

Route:

```text
POST controller/lane-calibrations/pull
```

Request:

```json
{
  "controller_code": "CTRL-01",
  "current_lane_calibration_code": "CAL-0001"
}
```

Response when a session is available:

```json
{
  "data": {
    "lane_calibration_available": true,
    "lane_calibration_code": "CAL-0001",
    "status": "ready",
    "desired_state": "running",
    "revision": 12,
    "readers": [
      {
        "serial_number": "24523021",
        "power_dbm": 5,
        "read_interval_ms": 500
      }
    ]
  }
}
```

Response when no session is available:

```json
{
  "data": {
    "lane_calibration_available": false
  }
}
```

Controller uses the session identity, Reader SDK serial, temporary Power and temporary Read Interval. Controller does not use a business Port list.

## Raw Lane Calibration events: Controller → Edge

Route:

```text
POST controller/lane-calibrations/events
```

Request:

```json
{
  "controller_code": "CTRL-01",
  "lane_calibration_code": "CAL-0001",
  "events": [
    {
      "event_uid": "CTRL-01-CAL-...",
      "revision": 12,
      "power_dbm": 5,
      "read_interval_ms": 500,
      "serial_number": "24523021",
      "port_no": 4,
      "tid": "E280689400001111",
      "read_at": "2026-08-04T11:31:00.125Z",
      "rssi_dbm": -48.5
    }
  ]
}
```

Successful response:

```http
HTTP/1.1 200 OK
```

```json
{
  "message": "Lane Calibration events received"
}
```

Controller treats HTTP 200 as acknowledgement for the complete submitted batch and marks all included outbox rows sent. It does not parse `processed`, `duplicate`, `ignored`, `rejected`, item indexes or business results. Edge owns idempotency, Port filtering and business processing.

The defined server-side failure response is HTTP 500. On HTTP 500 or a transport failure, Controller retains the full batch in the durable outbox and retries with backoff. Unexpected non-200 responses are not interpreted as business rejections; the batch remains pending.

## Lane Calibration status: Controller → Edge

Route:

```text
POST controller/lane-calibrations/status
```

```json
{
  "controller_code": "CTRL-01",
  "lane_calibration_code": "CAL-0001",
  "status": "failed",
  "occurred_at": "2026-08-04T11:31:00.125Z",
  "message": "Reader configuration is invalid"
}
```

Successful response is HTTP 200 with a simple message.
