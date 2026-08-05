# Controller API contract

## Physical Reader observations

`POST devices/report`

```json
{
  "controller_code": "CTRL-01",
  "devices": [
    {
      "serial_number": "24523021",
      "endpoint": "COM6",
      "status": "online",
      "last_seen_at": "2026-08-05T06:38:57Z",
      "firmware_version": "1.0.5.5",
      "power_dbm": 5,
      "read_interval_ms": 500,
      "ports": [1, 2, 3, 4]
    }
  ]
}
```

The report contains physical observations only. Controller does not send `reader_code` and does not validate the Reader against Server runtime scope.

## Raw RFID detections

Controller sends actual SDK `serial_number`, `port_no`, `tid`, timestamp, and RSSI when available. Edge owns all mapping and business validation.

## Lane Calibration

Routes use `controller/lane-calibrations/*` and `lane_calibration_*` fields. While a calibration session is active, Controller forwards raw detections without filtering Readers or Reader Ports.

## Reader acquisition ownership

- `power_dbm` is a Reader-wide technical setting. Controller clamps it to 0-33 dBm for CF-E718 and applies it through `SetRfPower`.
- `port_no` is raw SDK observation data and is forwarded unchanged.
- Antenna topology and routing are owned by Edge/Server and are not configured by Controller.
