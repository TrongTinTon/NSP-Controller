# NSP Gatekeeper Controller 1.4.18

Windows Controller for NSP Reader acquisition and execution.

## Official ownership

```text
Cloud
└── Source of truth

Edge
├── Business runtime
├── Receives Controller observations
├── Matches observations with runtime projection
├── Determines expected Reader online/offline
└── Sends edge/status to Cloud

Controller
├── Discovers physical Readers
├── Reads SDK SerialNumber
├── Caches physical observations
├── Detects raw RFID TID
└── Reports observations to Edge
```

Controller does not decide whether a Reader is valid, assigned, managed, in the correct Parking Layout, or in the correct Lane Calibration. It reports what the SDK actually observes. Edge owns all matching and business validation.

## CF-E718 SDK implementation

Version 1.4.18 is aligned with `UHFReader288.DLL manual V2.1` supplied with the vendor C# package.

The package states that:

- `UHFReader288.dll` is a .NET SDK assembly;
- separate x86 and x64 assemblies are supplied;
- the matching assembly must be copied beside the executable;
- the C# API owns COM/TCP connection state internally.

Therefore Controller now uses the managed C# SDK for both x86 and x64. The previous mixed design—x86 P/Invoke plus x64 reflection—has been removed.

```text
Build x86 → Readers/CFE718/Vendor/x86/UHFReader288.dll → output/UHFReader288.dll
Build x64 → Readers/CFE718/Vendor/x64/UHFReader288.dll → output/UHFReader288.dll
```

`dmdll.dll` is no longer part of the C# SDK runtime package.

## Reader source layout

```text
Readers/CFE718/
├── Cfe718ReaderFactory.cs          factory and registry entry only
├── Cfe718ReaderDiscovery.cs        physical COM discovery only
├── Cfe718ReaderRuntime.cs          reconnect and inventory lifecycle
├── Cfe718ReaderConfiguration.cs    technical Reader settings
├── Cfe718Inventory.cs              Inventory_G2 request construction
├── Cfe718ReaderIdentity.cs         SerialNumber and firmware reading
├── Cfe718Options.cs                configuration parsing and normalization
└── Sdk/
    ├── UhfReader288Sdk.cs          managed assembly loading
    ├── UhfReader288Session.cs      one vendor Reader object per connection
    ├── UhfReader288Types.cs        SDK-neutral DTOs
    └── UhfReader288Result.cs       SDK result interpretation
```

No vendor SDK reflection, transport state, RFID callback conversion, or SDK method lookup remains in the Reader runtime file.

## SDK command usage

The synchronous inventory path uses:

```text
OpenComPort / OpenNetPort
GetSeriaNo
GetModuleVersion
SetInventoryScanTime
SetRfPower
InitRFIDCallBack
Inventory_G2
CloseComPort / CloseNetPort
```

TID address and length are passed directly on every `Inventory_G2` request. `SetTIDParameter` is not required for this runtime path.

Controller applies `power_dbm` once as Reader-wide RF power through `SetRfPower`. Controller does not configure antenna topology or routing. Hardware ports are polled only to acquire RFID observations, and `port_no` is taken from the SDK callback and forwarded unchanged.

`StartInventory` and `StopInventory` are documented for Ex10-series fast inventory and are not used by the CF-E718 synchronous inventory loop. `StopImmediately` is also not required during normal close because each `Inventory_G2` request returns before the next request begins.

## Physical discovery

Discovery enumerates Windows COM ports. For each candidate:

```text
create managed SDK Reader object
→ OpenComPort
→ GetSeriaNo
→ optionally GetModuleVersion
→ cache observation
→ CloseComPort
```

A COM port that is not a CF-E718 Reader is ignored without business classification.

## Runtime behavior

For every discovered Reader observation:

```text
open observed COM/TCP endpoint
→ read SDK identity
→ apply technical settings
→ register callback
→ inventory hardware ports 1–4
→ emit raw TID detections
→ reconnect after technical failure
```

Controller never compares the observed SDK SerialNumber with a Cloud business assignment and never rejects a physical Reader as unmanaged or mismatched.

## Raw detection

Controller sends raw observations containing:

```text
serial_number
port_no
tid
detected_at
rssi_dbm when available
```

Controller does not resolve User, Vehicle, Parking Lane, Check-in, Check-out, or transaction type.

## Build

- .NET Framework 4.8
- Windows Forms
- x86 and x64
- PostgreSQL/Npgsql
- vendor C# `UHFReader288.dll`

Build and hardware integration tests must be run on Windows with the physical CF-E718 Reader.


## Controller Runtime Context UI

The Controller tab shows the actual runtime context supplied by Edge: `Idle`, `Parking Layout`, or `Lane Calibration`. Parking Layout rows include code, name, state, published revision and the lanes assigned to this Controller. Lane Calibration shows code, status, revision and Reader count. The Controller does not evaluate or own this business context.

## Lane Calibration Ready Runtime

A Lane Calibration session is active for Controller UI and acquisition when `status` is `ready` or `running`. Terminal statuses (`draft`, `completed`, `failed`, `cancelled`) are authoritative. `desired_state=running` is used only as a compatibility fallback when an older Edge response omits `status`.

Lane Calibration event pushes now log the Edge acknowledgement counters (`received`, `stored`, `duplicates`, `ignored`, `rejected`). This distinguishes transport delivery from Edge business acceptance and prevents an HTTP 200 response from being reported as “stored” without evidence.

## Runtime routing invariant

A connected Reader is only a technical acquisition source. It does not imply that
a business runtime is active. Routing is exclusive:

- `Lane Calibration` → Lane Calibration events only.
- `Parking Layout` → Parking detections only.
- `Idle` → detections remain visible locally and are not queued or pushed.

The Parking push worker is disabled outside Parking runtime. Core API response parsing
supports both direct payloads and nested T4 Core API envelopes.


## Lane Calibration acquisition continuity (1.4.18)

Lane Calibration Reader-wide power and scan interval changes are applied on the existing SDK session between inventory cycles. The physical Reader worker and RFID callback are not replaced for these changes. Logs now distinguish `lane-calibration-route`, durable local persistence (`lane-calibration-outbox`), and Edge delivery (`lane-calibration-push`).
