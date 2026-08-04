# NSP Gatekeeper Controller 1.4.8

Windows Controller for NSP Reader acquisition and execution.

## Responsibility

Controller is limited to technical acquisition and execution:

- authenticate to the Edge Core API;
- send heartbeat;
- pull Reader identity and technical runtime parameters;
- bind SDK SerialNumber to the current local COM/IP connection;
- connect, supervise and automatically reconnect Reader drivers;
- report technical Reader status;
- send every raw RFID detection with `serial_number`, `port_no`, TID, timestamp and RSSI when available;
- execute Lane Calibration power/interval changes and send raw calibration events.

Controller does not manage RFID Tag Whitelist, User, Vehicle, runtime assignments, Parking Layout, valid Lane Ports, Check-in/Check-out or Parking Transactions. Cloud and Edge own those business decisions.

## Reader and Port ownership

```text
SDK SerialNumber = physical Reader identity
COM/IP             = dynamic local Controller binding
port_no            = raw technical observation from the Reader
valid business Port = Server/Edge decision
```

Controller never disables a Reader because the Server `ports` list is empty. It does not filter detections by Server Port configuration. The CF-E718 driver inventories its hardware ports and forwards every raw detection. Edge accepts or ignores each `port_no` according to Parking Layout or Lane Calibration configuration.

The `ports` property may still appear in Server payloads for compatibility, but Controller intentionally ignores it when starting or restarting a Reader.

## Runtime flow

```text
Edge Reader identity/parameters
→ Controller connects Reader by SDK SerialNumber
→ Reader inventories hardware ports
→ Controller sends all raw detections
→ Edge filters and processes configured ports
```

During Lane Calibration:

```text
Edge selects Reader and temporary Power/Interval
→ Controller restarts that Reader with temporary technical settings
→ Controller sends raw events from every observed port_no
→ Edge ignores ports outside the Calibration configuration
```

## Dynamic COM binding

On every reconnect Controller tries the previous COM first, then enumerates current Windows COM ports. Each candidate is opened and verified with SDK `GetSeriaNo()`. When Windows changes the COM number, Controller updates `controller_reader.endpoint` locally after the SDK SerialNumber matches.

## Readers UI

The Readers tab is intentionally simple and read-only:

- Serial Number;
- actual COM/connection;
- Online/Connecting/Stopped state;
- latest technical detail/error;
- last update time;
- current Windows COM list.

There is no manual Port configuration and no manual COM binding form. COM rebinding is automatic.

## Local persistence

PostgreSQL stores:

- Reader identity, technical parameters and local COM/IP binding;
- current Reader runtime status;
- Parking raw-detection outbox;
- Lane Calibration raw-event outbox.

Reader business Port configuration is not stored in `controller_reader`.

## Build

- .NET Framework 4.8
- Windows Forms
- x86 and x64
- PostgreSQL/Npgsql
- CF-E718 / UHFReader288 SDK

Build and hardware integration tests must be run on Windows with the physical Reader.

## Reader UI observation model

The Readers tab displays only physical devices observed through `GetSeriaNo()` during the current Controller process. The configured Server identity remains an internal runtime key and is not shown in this observation table. A detected SDK serial is persisted with the actual COM endpoint even when the Server identity does not match, allowing operators to see the real device connected to the machine.

## Lane Calibration API acknowledgement

Lane Calibration routes and payload fields use one consistent domain name:

```text
controller/lane-calibrations/pull
controller/lane-calibrations/events
controller/lane-calibrations/status

lane_calibration_code
current_lane_calibration_code
lane_calibration_available
```

The event endpoint is transport acknowledgement only. HTTP 200 means the complete raw-event batch was received and Controller marks it sent. Controller does not parse per-item business results. HTTP 500 or a transport failure leaves the batch pending for retry. Edge owns duplicate handling, port filtering, acceptance and business processing.
