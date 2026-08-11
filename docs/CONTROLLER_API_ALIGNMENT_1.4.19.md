# Controller API alignment — 1.4.19

This release aligns the Windows Controller client with the current `nsp_business_gatekeeper` Controller-facing API contract.

## Contract boundary

Cloud/Edge Lane Calibration synchronization uses topology schema v4. The Windows Controller does not consume that topology schema. Edge resolves the released topology for the authenticated Controller and returns only the Reader runtime configuration required for acquisition.

## Changes

- Lane Calibration pull Reader entries now include `tid_start_address` and `tid_length` in addition to `serial_number`, `power_dbm`, and `read_interval_ms`.
- Lane Calibration settings override the corresponding Reader runtime TID settings for the active calibration revision. A TID shape change may require a Reader runtime restart.
- Parking raw detections now include `rssi_dbm` when available.
- Lane Calibration status requests include `revision`, matching the Edge validation contract.
- Reader Ports are not pulled as Controller filtering rules. The Controller forwards the SDK-reported `port_no`; Edge owns Reader Port scope validation.

## Ownership

Controller remains acquisition and execution only. It does not infer Server/Controller/Reader topology, validate Lane Calibration Reader Ports, resolve RFID assignments, or make Parking business decisions.
