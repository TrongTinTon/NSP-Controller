# NSP Controller alignment

## Official ownership

- Cloud: source of truth.
- Edge: business runtime.
- Controller: acquisition and execution.
- `nsp_sync`: reliable Cloud↔Edge transport; it is not part of the Controller process.

## Controller owns

- physical Reader connection;
- dynamic COM/IP binding;
- SDK SerialNumber verification;
- Power, read interval and TID technical settings;
- automatic reconnect;
- raw TID acquisition;
- durable local outbox;
- technical status.

## Controller does not own

- valid Parking Ports;
- valid Lane Calibration Ports;
- RFID assignments;
- User/Vehicle resolution;
- Parking sequence matching;
- Check-in/Check-out;
- Parking Transactions.

## Port rule

Controller sends all raw detections produced by the Reader. `port_no` is evidence, not a Controller-side business filter. Edge decides whether that `port_no` belongs to a configured Parking or Lane Calibration flow.
