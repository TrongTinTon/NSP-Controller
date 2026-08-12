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

Routes use `controller/lane-calibrations/*` and `lane_calibration_*` fields. `status=ready` and `status=running` are both active Controller runtime states. While a calibration session is active, Controller applies temporary Reader-level acquisition settings only to matching discovered SDK Serials. Detections from those scoped Readers go to the Calibration outbox; other Readers continue normal raw acquisition. Controller never filters by a business Reader Port/Antenna whitelist. Edge owns the `ready` to `running` business transition after the first accepted raw event.

Controller does **not** consume the Cloud topology schema directly. `nsp_business_gatekeeper` projects the released Server -> Controller -> Reader tree into a Controller-scoped runtime payload. Each Reader entry contains `serial_number`, `power_dbm`, `read_interval_ms`, `tid_start_address`, and `tid_length`. Reader Ports remain Edge-owned scope and are not used by Controller to filter acquisition.

`POST controller/lane-calibrations/events` returns a batch acknowledgement with aggregate counters: `received`, `stored`, `duplicates`, `ignored`, and `rejected`. Controller marks the local outbox batch as sent only after a valid Edge acknowledgement and records these counters in its log. Per-event business decisions remain owned and logged by Edge. `power_dbm` and `read_interval_ms` in an event are technical evidence of the applied hardware state; Edge must not reject an otherwise valid raw event only because the hardware normalized these values.

Controller 1.4.21 does not publish a separate Lane Calibration business-status decision. It reports raw Calibration events and lets Edge own session/business state transitions.

## Reader acquisition ownership

- `power_dbm` is a Reader-wide technical setting. Controller clamps it to 0-33 dBm for CF-E718 and applies it through `SetRfPower`.
- `port_no` is raw SDK observation data and is forwarded unchanged.
- Antenna topology and routing are owned by Edge/Server and are not configured by Controller.

## Controller device configuration

`POST controller/device-config/pull` returns only Reader identity and Reader-wide technical execution parameters:

```json
{
  "controller_code": "CTRL-01",
  "devices": [
    {
      "serial_number": "24523021",
      "reader_parameters": {
        "power_dbm": 10,
        "read_interval_ms": 200,
        "tid_start_address": 0,
        "tid_length": 6
      }
    }
  ]
}
```

The response never contains `parking_layouts`, `parking_area_code`, `lane_code`, `lane_name`, `antenna_sequence`, direction or Check-in/Check-out semantics. It also does not send a Port whitelist. Controller forwards the actual SDK-reported `port_no`; Edge owns Port/Antenna interpretation.

## Parking raw detection invariant

Outside an active Lane Calibration execution session, every valid RFID observation from a configured physical Reader is queued and pushed to Edge. Controller does not gate the push based on Parking state. Edge decides whether to ignore, match or convert the observation into business state.
