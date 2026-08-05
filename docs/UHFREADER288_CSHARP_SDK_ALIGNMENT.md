# UHFReader288 C# SDK alignment

## Source reviewed

- Vendor package folder: `C#`
- Manual: `UHFReader288.DLL manual V2.1`
- Managed assemblies:
  - `C#/x86/UHFReader288.dll`
  - `C#/x64/UHFReader288.dll`

## Findings

1. Both architecture packages are managed .NET assemblies.
2. The documented C# API does not expose an unmanaged transport handle to application code.
3. One SDK Reader object owns one COM/TCP connection.
4. `CloseComPort()` and `CloseNetPort()` close the connection owned by that object.
5. `Inventory_G2` receives TID address, TID length, and TID flag per request.
6. `StartInventory` and `StopInventory` are documented as Ex10-only fast-inventory commands.
7. The four-port Reader antenna mask uses the two-argument `SetAntennaMultiplexing(ref byte, byte)` overload.

## Refactor decision

The Controller uses one managed adapter for x86 and x64. Architecture differences are limited to which vendor assembly is copied to the output directory.

Vendor SDK concerns are isolated under `Readers/CFE718/Sdk`. Reader lifecycle and NSP ownership remain outside the SDK adapter.

## Responsibility boundaries

```text
UhfReader288Sdk
= assembly loading and API type discovery

UhfReader288Session
= one vendor object and one technical connection

Cfe718ReaderDiscovery
= physical Reader discovery

Cfe718ReaderConfiguration
= supported technical settings

Cfe718Inventory
= synchronous Inventory_G2 requests

Cfe718ReaderRuntime
= reconnect loop, status, and raw detections

ReaderManager
= Controller observation lifecycle and cache
```

## Commands deliberately not used

- `SetTIDParameter`: redundant because the required values are passed to `Inventory_G2`.
- `StartInventory` / `StopInventory`: Ex10-only fast-inventory path.
- `StopImmediately`: unnecessary for the normal synchronous request loop.

## Deployment rule

Do not mix the old native x86 DLL with the C# managed x64 DLL. Deploy the matching managed C# assembly for the selected build platform.
