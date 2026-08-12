# Controller boundary refactor — 1.4.20

> **Historical / superseded by 1.4.21.** The 1.4.20 boundary removed Parking business context from Controller, but Calibration routing was still Controller-global. See `CONTROLLER_ARCHITECTURE_AUDIT_1.4.21.md` for the corrected per-Reader execution scope.

## Rule

**Controller sends what it sees; Edge decides what the detections mean.**

Controller owns only physical acquisition and Reader-level execution settings. It does not store or evaluate Parking Layout, logical Lane, Antenna Sequence, direction, Check-in/Check-out, Vehicle/User authorization or Parking Transaction state.

## Device config

`controller/device-config/pull` consumes only `devices[]` with SDK `serial_number` and Reader-level parameters. Port topology is intentionally absent.

## Detection flow

- Active Lane Calibration: raw observations are sent through the Lane Calibration execution channel.
- Otherwise: every valid physical RFID observation is queued to the raw detection outbox and pushed to Edge.
- No Parking runtime flag or Lane assignment is required on Controller.

## Removed legacy state

- Parking Layout/Lane models from Controller domain.
- `controller_runtime_context.parking_layouts_json` local cache.
- Parking runtime gating of RFID detection queue/push.
- Parking Layout display in Controller UI.
