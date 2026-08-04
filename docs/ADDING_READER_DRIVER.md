# Adding a Reader driver

Implement:

```text
IReaderDriverFactory
IReaderRuntime
```

A runtime must:

- use `ReaderDeviceConfig.SerialNumber` as the Reader identity;
- apply only configured `Ports`;
- emit `RfidDetection` with `SerialNumber`, `PortNo`, `Tid`, timestamp and optional RSSI;
- emit `ReaderStatus` whenever connection state changes;
- isolate failures inside its own worker/thread;
- stop and dispose without blocking other Reader runtimes.

Register the factory in `Bootstrap/Program.cs`.

Physical connection settings belong to Controller local configuration. Cloud/Edge runtime payloads must not contain vendor endpoint credentials or driver-specific options.
