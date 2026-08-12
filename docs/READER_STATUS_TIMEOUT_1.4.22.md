# Reader Status timeout diagnostics — 1.4.22

- `CoreApiTimeoutSec` remains the transport timeout (15 seconds by default).
- HttpClient request timeout (`TaskCanceledException`) is now surfaced as an explicit `TimeoutException` with endpoint and configured timeout.
- A timeout no longer logs the misleading message `Cannot connect` when TCP connectivity may actually be healthy but the Edge request did not complete in time.
- Reader status reporting remains independent from RFID acquisition; a failed status iteration does not stop Reader workers or detection outboxes.

Paired with `nsp_business_gatekeeper 19.0.10.32.0`, `/devices/report` becomes the sole writer of Reader Observation, removing row-lock contention with Parking and Lane Calibration raw acquisition requests.
