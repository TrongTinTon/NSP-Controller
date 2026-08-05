# Runtime routing fix — Controller 1.4.17

## Correct routing rule

A physical Reader being connected does not authorize a business push by itself.
Each raw detection is routed only when one runtime context is active:

- Lane Calibration active (`ready` or `running`) → Lane Calibration outbox.
- Published Parking Layout active → Parking outbox.
- No runtime context → keep the detection in the local live view only; do not queue or push it.

The parking push worker is also gated by the active Parking runtime. It cannot drain
the Parking outbox while Controller is Idle or in Lane Calibration mode.

The Core API response parser now accepts both direct business payloads and nested
T4 Core API `{ success, data }` envelopes. This prevents an available Lane Calibration
from being parsed as unavailable.
