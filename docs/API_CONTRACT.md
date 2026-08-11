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

Controller sends actual SDK `serial_number`, `port_no`, `tid`, timestamp, and `rssi_dbm` when RSSI is available. Edge owns all mapping and business validation.

## Lane Calibration

Routes use `controller/lane-calibrations/*` and `lane_calibration_*` fields. `status=ready` and `status=running` are both active Controller runtime states. While a calibration session is active, Controller displays Lane Calibration mode, applies temporary Reader-level acquisition settings for matching discovered serials, and forwards raw detections without filtering Readers or Reader Ports. Edge owns the `ready` to `running` business transition after the first accepted raw event.

Controller does **not** consume the Cloud topology schema directly. `nsp_business_gatekeeper` projects the released Server -> Controller -> Reader tree into a Controller-scoped runtime payload. Each Reader entry contains `serial_number`, `power_dbm`, `read_interval_ms`, `tid_start_address`, and `tid_length`. Reader Ports remain Edge-owned scope and are not used by Controller to filter acquisition.

`POST controller/lane-calibrations/events` returns a batch acknowledgement with aggregate counters: `received`, `stored`, `duplicates`, `ignored`, and `rejected`. Controller marks the local outbox batch as sent only after a valid Edge acknowledgement and records these counters in its log. Per-event business decisions remain owned and logged by Edge. `power_dbm` and `read_interval_ms` in an event are technical evidence of the applied hardware state; Edge must not reject an otherwise valid raw event only because the hardware normalized these values.

If `POST controller/lane-calibrations/status` is used, the payload includes the Lane Calibration `revision` together with `controller_code`, `lane_calibration_code`, `status`, and `occurred_at`.

## Reader acquisition ownership

- `power_dbm` is a Reader-wide technical setting. Controller clamps it to 0-33 dBm for CF-E718 and applies it through `SetRfPower`.
- `port_no` is raw SDK observation data and is forwarded unchanged.
- Antenna topology and routing are owned by Edge/Server and are not configured by Controller.

## Controller Runtime Context

`controller/device-config/pull` also returns `parking_layouts` assigned to the Controller. Each row contains Parking Area code/name/state/revision and only the Lanes using that Controller. Controller caches and displays this context but does not process Parking topology.
