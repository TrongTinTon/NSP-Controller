# Reader connection troubleshooting

Controller records only technical acquisition state. It does not decide whether a Reader is valid, assigned, or part of a Parking Layout/Lane Calibration.

Useful technical fields:

- `serial_number`: SDK SerialNumber observed from the physical Reader.
- `endpoint`: local COM/TCP endpoint currently used.
- `status`: `online`, `offline`, or `degraded`.
- `last_seen_at`: latest physical observation time.

Technical failures may include missing SDK files, COM open failure, transport timeout, or inventory failure. Business errors such as Reader mismatch, unmanaged Reader, wrong assignment, or invalid Parking scope must never be produced by Controller.
