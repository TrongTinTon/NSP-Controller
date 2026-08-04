# Adding a Reader driver

A Reader driver must:

- connect using the physical endpoint managed locally by Controller;
- expose the hardware SerialNumber when the SDK supports it;
- reconnect automatically after transient communication failure;
- apply technical Power, interval and TID settings;
- inventory the hardware ports supported by that driver;
- emit every raw `RfidDetection` with `SerialNumber`, `PortNo`, `Tid`, timestamp and optional RSSI;
- never filter detections using Edge business Port configuration;
- report clear technical status and errors.

Business Port validation belongs to Edge.
