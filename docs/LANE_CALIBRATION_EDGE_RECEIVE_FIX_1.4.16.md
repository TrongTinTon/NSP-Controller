# Lane Calibration Edge receive fix — Controller 1.4.16 / Edge 19.0.10.6.0

## Fault

Controller previously marked a Lane Calibration outbox batch as `sent` from HTTP 200 alone. Edge could return HTTP 200 after every event in the batch had been ignored or rejected by business validation, leaving no `nsp.measurement.event` row while Controller logged the batch as acknowledged.

## Corrected flow

1. Controller pushes raw events with `event_uid`, calibration code/revision, actual Reader serial/port/TID/timestamp/RSSI and technical acquisition settings.
2. Edge authenticates the Controller and validates the released calibration snapshot.
3. Edge stores accepted events idempotently, retains per-event ignored/rejected reasons in the Edge log, and returns aggregate counters.
4. Controller verifies the acknowledgement count, logs the Edge counters and then marks the local outbox batch as sent.
5. Edge changes `ready` to `running` only after at least one new accepted event is stored.

## Additional corrections

- Edge validates target TIDs against the released Lane Calibration target lines, matching Cloud behavior. It no longer re-reads a mutable RFID runtime assignment on every event.
- Edge uses `revision` as the current-snapshot guard. Actual `power_dbm` and `read_interval_ms` remain technical evidence and no longer cause a valid raw detection to be silently discarded merely because hardware-normalized values differ from desired settings.
- Controller remains acquisition/execution only. It does not filter Reader Ports, configure Antenna topology, call `SetAntennaMultiplexing`, or call `SetAntennaPower`.
