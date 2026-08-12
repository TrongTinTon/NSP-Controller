# Controller architecture audit — 1.4.21

## Boundary

Controller remains Acquisition / Execution only. It may receive an explicit Calibration execution scope, but it does not know Parking Layout, logical Lane, Antenna Sequence, direction, check-in/out, Vehicle/User authorization or Parking Transaction semantics.

## Fixes

### Per-Reader Calibration scope

An active Calibration Session no longer captures every Reader attached to the Controller. Only SDK Serials explicitly listed in the session are routed to the Calibration outbox. Other Readers continue normal raw RFID acquisition and Parking outbox delivery.

### Concurrent outbox delivery

The Parking push worker is no longer globally paused while any Calibration Session is active. Routing happens once, per physical observation, in `ReaderManager`; the Parking and Calibration outboxes then drain independently.

### Runtime configuration transition tracking

`ReaderManager` now updates its manager-side desired runtime snapshot whenever an in-place configuration change is accepted. This is required for correct transitions such as `Edge Runtime -> Calibration -> Edge Runtime`; without it, the manager could compare against stale pre-Calibration state and fail to restore the normal Reader parameters when Calibration ends.


### Calibration execution lease

A Controller-local lease timeout protects against an Edge outage leaving a Reader indefinitely in Calibration execution mode. The default lease is 30 seconds and is refreshed by successful Calibration pulls. Lease expiry clears only the execution context and returns Readers to normal acquisition.

### Applied Reader configuration

The UI no longer treats desired/effective settings as confirmed hardware state. `Cfe718ReaderRuntime` records an applied snapshot only after the SDK configuration call succeeds. The snapshot contains:

- configuration source (`Edge Runtime`, `Calibration`, or `Default`)
- RF power
- effective Reader scan/read interval
- TID start address used by Inventory_G2
- TID length used by Inventory_G2
- configuration hash
- applied timestamp

The Readers tab displays the last confirmed applied values. Offline Readers retain the last confirmed snapshot and timestamp.

## Clean-code status

Boundary coupling is substantially reduced, but the source is not yet fully decomposed. `ReaderManager`, `CoreApiClient`, `Cfe718ReaderRuntime`, `LocalStore` and `MainForm` remain larger than desirable and should be split by responsibility in a later behavior-preserving refactor.
