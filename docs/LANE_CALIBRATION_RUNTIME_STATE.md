# Lane Calibration runtime state

Controller treats Lane Calibration as an active runtime context when the Edge response has:

- `lane_calibration_available = true`; and
- `status = ready` or `status = running`.

`ready` means the Lane Calibration runtime has been assigned and Controller must:

- show `Runtime Mode = Lane Calibration`;
- display the Lane Calibration code, status and revision;
- apply temporary Reader-level `power_dbm` and `read_interval_ms` for matching discovered Reader serials;
- route raw RFID detections to the Lane Calibration outbox.

The Edge remains responsible for changing `ready` to `running` after it receives the first accepted raw event. Controller does not manufacture that business transition.

Terminal or non-runnable statuses are authoritative:

- `draft`
- `completed`
- `failed`
- `cancelled`

For compatibility only, `desired_state = running` is accepted when `status` is absent or unknown. It cannot override a terminal status.
