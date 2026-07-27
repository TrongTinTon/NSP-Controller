# Adding another RFID reader

1. Create a folder under `Readers/<VendorOrModel>/`.
2. Implement `IReaderDriverFactory` with a unique `DriverKey`.
3. Return one isolated `IReaderRuntime` per physical reader.
4. The runtime emits only:
   - `RfidDetection` for physical RFID reads.
   - `ReaderStatus` for technical connection state.
5. Register the factory once in `Bootstrap/Program.cs`.

A driver must not call Core API, perform parking decisions, resolve users/vehicles, validate borrowing, or write Parking Transactions.

Example:

```csharp
registry.Register(new Cfe718ReaderFactory(logger));
registry.Register(new OtherReaderFactory(logger));
```

Current Edge Reader config identifies Readers by `serial_number` and does not expose a driver key or physical endpoint. Therefore Controller preserves the local physical profile (driver/endpoint/port) by Serial Number. A later Edge payload may provide an optional `connection` object without changing the common pipeline.
