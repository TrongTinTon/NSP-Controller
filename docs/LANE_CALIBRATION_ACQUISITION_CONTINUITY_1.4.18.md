# Lane Calibration acquisition continuity — Controller 1.4.18

## Fault

Applying Lane Calibration Reader parameters could replace the physical Reader worker. Transport status could remain visible as connected while the new inventory callback was not yet producing detections. No Lane Calibration event entered the local outbox, therefore `lane-calibration-push` did not appear.

## Correction

- Apply Reader-wide `power_dbm` and `read_interval_ms` on the active SDK session between inventory cycles.
- Preserve the Reader worker and registered RFID callback.
- Restart only for transport, driver, port, or TID inventory-shape changes.
- Log each pipeline boundary: `reader-detection` -> `lane-calibration-route` -> `lane-calibration-outbox` -> `lane-calibration-push`.
- No antenna topology, routing, multiplexing, or per-antenna power configuration is introduced.
