# Reader Connection Troubleshooting

## Log sequence for a healthy Reader

Search the daily log for the Reader serial number. A successful connection contains these checkpoints in order:

1. `Reader worker starting`
2. `Reader connection attempt started`
3. `Reader transport opened`
4. `Reader SDK SerialNumber verified`
5. `Reader configuration applied`
6. `RFID callback registered`
7. `RFID inventory started`

If the sequence stops, the last checkpoint identifies the failing phase.

## Fields included in connection failures

- `phase`: open transport, read SDK identity, apply configuration, callback registration, or inventory port.
- `configured_serial`: Reader identity received from Edge.
- `endpoint`: fixed COM/TCP endpoint or `AUTO-COM`.
- `windows_com`: COM ports currently enumerated by Windows.
- `sdk_result`: decimal and hexadecimal SDK result.
- `handle`, `com_port`, `com_address`: values returned by the SDK.
- `process_arch` and SDK mode/path/size/version.
- exception type, HRESULT, inner exceptions, and stack trace.

## Frequent root causes

- Configured `COMx` is absent from `windows_com`: Windows driver, power, cable, or COM-number problem.
- `BadImageFormatException`: application architecture does not match the packaged SDK DLL.
- `DllNotFoundException` or `FileNotFoundException`: `UHFReader288.dll` or an x86 dependency is missing from the output folder.
- `TypeLoadException` or `MissingMethodException`: incompatible x64 managed SDK assembly.
- `Open ... failed`: inspect the raw `sdk_result`, selected COM, baud code, handle, and COM address in the same log entry.
- `Reader SerialNumber mismatch`: the COM endpoint belongs to a different physical Reader.
- Failure during `SetTIDParameter`, `SetInventoryScanTime`, port mask, or port power: transport opened but the Reader rejected a runtime setting.
- Failure during `Inventory_G2` on every hardware port: the Reader likely disconnected after initialization or the SDK transport/session is no longer valid. A failure on only one port is logged and does not stop detections from the remaining ports.

## Lane Calibration restart

Changing Lane Calibration stops the current Reader runtime and starts a new runtime with the effective Calibration settings. On x64, version 1.4.4 closes the actual managed SDK session before reopening the COM port. The log must contain `Reader transport closed` before the next `Reader connection attempt started`.

Temporary Reader connection failures keep the Lane Calibration session waiting/reconnecting. They do not report the session as failed to Cloud.


## Windows changes the Reader COM number

Version 1.4.8 treats the SDK SerialNumber as identity and COM as a mutable local binding. The runtime tests the previous COM first, then current Windows COM ports, reads `GetSeriaNo`, selects the matching Reader and persists the newly verified COM endpoint in `controller_reader`. Cloud and Edge do not own or overwrite this value.
