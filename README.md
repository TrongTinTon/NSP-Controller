# NSP Gatekeeper Controller 1.4.0

Windows Controller for NSP Reader acquisition and execution.

## Responsibility

The Controller is intentionally limited to technical acquisition and execution:

- authenticate to the Edge Core API;
- send Controller heartbeat;
- pull Reader runtime parameters;
- combine Cloud/Edge technical parameters with Controller-local physical connection settings;
- start and supervise Reader drivers;
- report Reader status and configured Reader Ports;
- durably queue and push raw Parking detections;
- execute Lane Calibration and durably push raw calibration events/status.

The Controller does not manage RFID Tag Whitelist, User, Vehicle, runtime assignment, Parking Layout classification, Check-in/Check-out, access decisions, or Parking Transactions. Those responsibilities belong to Cloud and Edge business runtime.

## Runtime identity

Every physical observation is addressed by:

```text
Reader serial_number + port_no
```

`antenna_code`, `antenna_no`, and Antenna mapping are not part of Controller APIs or local business models. Vendor SDK function names containing `Antenna` remain only inside the CF-E718 native adapter because those names are defined by the device manufacturer.

## Main flows

```text
Edge Reader config -> Controller local connection mapping -> Reader driver
Reader callback -> Parking durable outbox -> Edge parking/detections/push
Lane Calibration config -> temporary Reader/Port runtime -> calibration durable outbox -> Edge measurement/events
Reader status -> Edge devices/report
Controller heartbeat -> Edge heartbeat
```

## Local persistence

PostgreSQL stores only:

- Reader technical configuration and local connection mapping;
- current Reader runtime status;
- pending/sent/dead Parking detections;
- pending/sent/dead Lane Calibration events.

The schema is defined in `database/init_database.sql`. Version 1.4.0 is a clean Reader-Port schema and does not preserve the legacy Antenna-based local schema.

## Configuration

Use `App.config` or environment variables:

- `NSP_CONTROLLER_CODE`
- `NSP_CORE_API_BASE_URL`
- `NSP_CORE_API_CLIENT_ID`
- `NSP_CORE_API_CLIENT_SECRET`
- `NSP_CORE_API_DATABASE`
- `NSP_POSTGRES_CONNECTION`
- `NSP_POSTGRES_ADMIN_CONNECTION`

No database or API password is shipped in source.

## Build

- Target: .NET Framework 4.8
- Platforms: x86 and x64
- UI: Windows Forms
- Database: PostgreSQL through Npgsql
- Included Reader driver: CF-E718 / UHFReader288

Build with Visual Studio/MSBuild on Windows. Native SDK files are copied according to the selected platform.

See:

- `docs/API_CONTRACT.md`
- `docs/CONTROLLER_NSP_ALIGNMENT.md`
- `docs/CONTROLLER_SOURCE_ANALYSIS_VI.md`
- `docs/DATABASE_BOOTSTRAP.md`


## Reader COM visibility (1.4.1)

- The Readers tab enumerates Windows COM ports and exposes them in the Endpoint dropdown.
- Reader rows come from Edge-synchronized Reader configuration, even before runtime status exists.
- SDK Auto COM discovery reports the resolved port (for example `COM3`) in Reader status.
- A physical COM port is not promoted to an NSP Reader by itself; Edge remains the source of Reader identity/configuration.
