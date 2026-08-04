# Controller alignment with NSP architecture

## Architectural boundary

```text
Cloud = source of truth
nsp_sync = reliable Cloud ↔ Edge transport
Edge = business runtime
Controller = acquisition and execution
```

The Controller communicates with Edge, not directly with Cloud business modules.

## Controller owns

- Controller identity and Core API credentials;
- physical Reader connection mapping;
- Reader driver lifecycle;
- Reader technical settings execution;
- Reader Port acquisition;
- raw TID, Reader serial, Port, timestamp and RSSI when available;
- durable local delivery queues;
- Lane Calibration execution and status.

## Controller does not own

- User or Vehicle identity;
- RFID Tag master, Whitelist, assignment or revocation;
- Parking Layout or Event Sequence interpretation;
- Check-in/Check-out classification;
- duplicate/business grouping policy;
- Parking decisions or transactions;
- Cloud/Edge synchronization state.

## Reader Port rule

The Controller API and local domain use `port_no`. Antenna identity and Antenna mapping are outside Controller scope. Native CF-E718 SDK methods keep vendor-defined names only at the hardware boundary.

## Failure behavior

- Reader callbacks do not wait for HTTP.
- Parking and Lane Calibration use separate bounded ingest queues and durable PostgreSQL outboxes.
- HTTP 401 triggers re-authentication.
- Network failures retain pending outbox rows.
- HTTP 429 applies server cooldown without treating throttling as delivery failure.
- Permanent request errors move rows to `dead`.
- Lane Calibration item-level rejection moves only rejected rows to `dead`; delivered/duplicate/ignored rows are finalized.
- Failure of one Reader runtime does not stop other Reader runtimes.
