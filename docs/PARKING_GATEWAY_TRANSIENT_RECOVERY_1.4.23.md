# Controller 1.4.23 — Parking Gateway transient recovery

A Core API response containing `no Server Action configured` is an Edge Gateway
configuration/deployment failure, not a malformed RFID detection payload.

Changes:

- do not classify this specific HTTP 400 as permanent;
- keep/retry the durable Parking outbox batch instead of moving it to `dead`;
- on startup, requeue only historical dead rows whose `last_error` contains
  `no Server Action configured`;
- leave genuine payload-validation 4xx records in `dead` for operator review.
