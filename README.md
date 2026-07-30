# NSP Gatekeeper Controller

Current architecture:

`RFID Reader -> Controller -> durable local outbox -> Edge -> nsp.parking.detection.event -> Edge business processing -> nsp.parking.transaction`

## Controller responsibilities

- Core API Service authentication using `/auth/token` and rotating `/auth/refresh`.
- Zeroconf discovery only after the saved Server URL fails at network level.
- Controller heartbeat.
- Pull server-managed Reader/Antenna technical configuration.
- Preserve Controller-local physical Reader profile when Edge does not provide endpoint/driver.
- Run one isolated runtime per physical Reader.
- Enforce configured antenna inventory; RSSI remains Measurement telemetry only.
- Continuously collect raw RFID TID detections.
- Persist Parking detections before network delivery.
- Batch/retry Parking detections with stable `event_uid` idempotency.
- Report Reader runtime health using `serial_number`, `antennas`, `device_status`, `last_seen_at`, `firmware_version`, `power_dbm`, and `read_interval_ms`.
- Pull Measurement runtime configuration with multiple selected Readers, temporary Reader Power and Read Interval per Reader, an explicit antenna subset per Reader, and one shared revision.
- Isolate every selected Reader from Parking during Measurement and queue every TID detected on the explicitly selected antennas; Edge owns target matching and Measurement validation.
- Persist Measurement revision, Reader Power and Read Interval with each observation so retry cannot cross revisions after Measure Again.
- Push Measurement observations (including RSSI) and Measurement status.
- Operational logging and sent-outbox cleanup.

## Controller intentionally does NOT do

- Vehicle/User Card resolution.
- Owner/Borrow/Friendship validation.
- Lane direction or Outside/Inside transition inference.
- Check-in/check-out decision.
- Vehicle/User pairing.
- Allowed/denied decision.
- Parking Transaction creation.
- Parking occupancy, Notification or Mobile business logic.

## Runtime modes

Parking is the default mode. A `ready` Measurement Session waits until `planned_start_at` when a future start is configured. Only Readers participating in the active session are isolated from Parking delivery. Only the antenna subset selected for each Reader creates Measurement events; other Readers continue Parking. When `planned_end_at` is reached the Controller reports `completed`; when the server stops/finalizes the session, the affected Readers return to Parking automatically.

## Burst handling

Reader callbacks never wait for HTTP. Parking and Measurement have independent bounded in-memory ingest queues followed by PostgreSQL durable outboxes. Default Parking push interval is 200 ms with a maximum batch size of 250 (Edge supports up to 1000). Measurement uses max 100 events/request.

## Reader extension

The common pipeline depends on `IReaderDriverFactory`, `IReaderRuntime`, `RfidDetection` and `ReaderStatus`. New physical reader types are registered in `Bootstrap/Program.cs`; ReaderManager/Core API/Outbox business boundaries do not change.

The source includes the CHAFON CF-E718 / UHFReader288 native SDK adapter for x86 and x64. Antenna callbacks are accepted only when the antenna is present in the current server configuration; there is no automatic fallback to Antenna 1.

## Physical Reader connection

Current Edge `/v1/controller/device-config/pull` sends Serial Number, Reader operation parameters and physical antenna declarations, but not IP/COM endpoint. The Controller therefore preserves a local physical connection profile keyed by Serial Number. Use the Readers tab to configure Driver, Endpoint and Port. For CF-E718, an empty endpoint defaults to SDK Auto COM discovery.

## Build

- Windows + Visual Studio 2022
- .NET Framework 4.8 targeting pack
- Build platform must match vendor SDK (`x86` or `x64`)
- NuGet: Npgsql 6.0.11, Newtonsoft.Json 13.0.3

## Local PostgreSQL

The Controller creates four technical tables:

- `controller_reader`
- `controller_reader_runtime_status`
- `controller_parking_outbox`
- `controller_measurement_outbox`

No User, Vehicle, Borrow, Parking Transaction, Parking History or Notification master/business tables are stored locally.

See `docs/API_CONTRACT.md` for the exact current API contract and `docs/CONTROLLER_NSP_ALIGNMENT.md` for the implementation alignment summary.

## First-start PostgreSQL bootstrap

Database provisioning and schema provisioning are intentionally separated:

1. `DatabaseBootstrapper` checks the application connection from `PostgreSqlConnectionString`.
2. If the target database can already be opened, no administrative bootstrap is performed.
3. If the database/role is not ready, the Controller connects to PostgreSQL maintenance database `postgres` using `PostgreSqlAdminConnectionString` (or, as a limited fallback, the application credentials).
4. It creates the application role only when missing, creates the database only when missing, and grants CONNECT.
5. The Controller reconnects using the normal application credentials.
6. `LocalStore.EnsureSchema()` executes only `database/init_database.sql`, which owns technical table/index creation.

`00_create_database.sql` is retained only as a manual deployment/bootstrap reference. Runtime never launches `psql.exe` and never executes `\\gexec`.

No production PostgreSQL password is committed in `App.config`. Set the deployment-specific application password in `PostgreSqlConnectionString`. For unattended first-start on a fresh PostgreSQL instance, also provide a privileged `PostgreSqlAdminConnectionString`; it is used only when role/database bootstrap is required.


### Controller 1.3.1 Multi-target Measurement sessions
Measurement target lists are owned by Cloud/Edge and are not sent to Controller. Each Controller receives only its Reader subset and explicit antenna scope, then durably reports every detected TID. Edge filters the configured Measurement targets. Temporary Reader Power, Read Interval, revision, and selected antenna scope remain isolated from the operational Reader profile.

### Controller 1.2.0 Measurement Reader runtime settings
Reader declaration no longer exposes editable Power or Read Interval parameters in server setup. Measurement supplies temporary Reader Power and Read Interval per Reader, runtime status reports the actual applied values, and every durable Measurement observation retains revision, power and interval snapshots.

### Controller 1.1.9 antenna runtime cleanup
Operational antenna configuration now contains only the physical antenna number. The legacy per-antenna minimum RSSI threshold has been removed from the API parser, runtime configuration, and CF-E718 detection filter. RSSI is still captured as observation data and remains available for Measurement.

### Controller 1.1.8 Multi-Reader Measurement runtime
Measurement supports multiple Readers in one session. Each Reader gets temporary Reader Power and Read Interval for the shared revision; only its selected antennas participate. Every observed TID is durably queued and Edge performs target filtering. All affected Readers restore operational configuration when Measurement stops; revision/Reader Power/Read Interval snapshots remain attached to durable outbox observations.

### Controller 1.1.6 database bootstrap hardening
Invalid/incomplete optional `PostgreSqlAdminConnectionString` values are now ignored with a warning; bootstrap falls back to the validated application connection against database `postgres`.
