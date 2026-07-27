# Core API connection troubleshooting

## Test Connection flow

1. Validate Controller Code / Client ID / Client Secret.
2. If Server URL is configured, call `POST <server>/auth/token` with only `client_id` and `client_secret`.
3. If the request fails at network level only, start `_nsp._tcp.local` Zeroconf fallback.
4. If exactly one NSP Core API service is discovered, authenticate that candidate with `/auth/token`.
5. Save the discovered Server URL only after authentication succeeds.
6. Cache `access_token`, rotating `refresh_token` and expiry timestamps in memory.
7. Refresh via `POST /auth/refresh`; do not resend client credentials while refresh remains valid.

## Typical errors

- `Cannot connect to ...`: Edge IP/port/firewall/service problem.
- `HTTP 401`: Client ID/Secret or refresh token is invalid/revoked.
- `HTTP 403`: application/IP/route permission issue; Zeroconf is not retried.
- `HTTP 400`: payload/contract problem; do not retry blindly.
- `Zeroconf found no NSP Core API service`: Edge Zeroconf is not running, UDP/5353 multicast is blocked, or Controller and Edge are not on the same LAN.
- `Zeroconf found multiple NSP servers`: configure the intended Server URL explicitly.

The log file under `logs/` keeps the nested transport error.


## v1.1.2 - Zeroconf `raw_candidates=0`

`raw_candidates=0` means the Controller did not resolve any DNS-SD SRV record for the Discovery Service. This happens before TXT metadata filtering.

Controller v1.1.1 used a single UDP socket bound to an ephemeral source port and requested QU (unicast mDNS) replies. On Windows this is unreliable with some mDNS responders and multi-NIC hosts because:

- multicast may leave through VPN/Hyper-V/VMware instead of the physical LAN;
- some responders are more reliable with standard mDNS QM queries sourced from/listened on UDP/5353;
- a PTR-only response may require explicit SRV/TXT/A follow-up queries.

Controller v1.1.2 therefore:

1. enumerates every active private IPv4 LAN interface;
2. first uses standard mDNS QM mode on UDP/5353 and joins `224.0.0.251` on each interface;
3. explicitly sends the query through every active LAN interface;
4. resolves PTR -> SRV/TXT -> A when necessary;
5. falls back to QU from an ephemeral port only if standard mDNS does not resolve a service;
6. logs interface, query mode and received packets for diagnosis.

Expected logs include:

```
[zeroconf] mDNS discovery starting | service=_nsp._tcp.local interfaces=192.168.1.x
[zeroconf] mDNS listener ready | mode=QM local_port=5353
[zeroconf] mDNS query sent | mode=QM interface=192.168.1.x qtype=PTR name=_nsp._tcp.local
[zeroconf] mDNS packet received | mode=QM remote=192.168.1.189:5353 bytes=...
[zeroconf] NSP Core API discovery completed | count=1 raw_candidates=1 ptr_candidates=1
```

On the Zeroconf publisher, the same discovery should produce:

```
mDNS CLIENT_QUERY client=<controller-ip>:5353 service=_nsp._tcp.local. qtype=PTR ... response_mode=QM
```

If the publisher never logs `mDNS CLIENT_QUERY`, UDP multicast `224.0.0.251:5353` is not reaching the publisher. Check the selected NIC, Windows Firewall and VLAN/AP multicast policy.


## v1.1.3 - simplified Zeroconf TXT contract

Expected service:

```text
SRV port: 9000
TXT port: 8069
TXT auth_path: /auth/token
```

`9000` is the Discovery Service port. `8069` is the Core API port. Controller must never authenticate against port `9000`.
