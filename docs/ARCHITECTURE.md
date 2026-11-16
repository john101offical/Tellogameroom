# Architecture

## Overview

```
 Dashboard (WPF) ──HTTPS REST + SignalR /hubs/dashboard──┐
 Dashboard (WPF) ────────────────────────────────────────┤
                                                         ▼
                                              TelloGameRoom.Server ── SQLite (WAL)
                                                         ▲
 Machine (WPF) ───HTTPS REST + SignalR /hubs/machine─────┤
 Machine (WPF) ──────────────────────────────────────────┘
   ▲ supervised by MachineGuard (Windows Service)
```

The server is the only authority. Clients display state and enforce the lock; they never decide how much time is left.

## Principles

1. **Server-authoritative time.** A session stores timestamps, not a ticking counter. While active, remaining time is
   `EndsAtUtc - now`; while paused it is `RemainingSecondsAtPause`. A server restart loses nothing.
2. **Permissions are enforced on the server.** The permission catalogue lives in `Core` so clients can hide controls,
   but every endpoint and hub method checks on the server. The caller's role and active flag are re-read from the
   database on each request, so deactivating an employee takes effect immediately rather than when a token expires.
3. **Money is integer cents.** All amounts are `long` hundredths of the currency unit. Sums are exact and behave the
   same on SQLite and PostgreSQL.
4. **The ledger and the audit log are append-only.** `SessionEvents` and `AuditLog` reject updates and deletes both in
   the EF Core context and with database triggers. Income is always the sum of ledger rows.
5. **An action and its audit entry commit together**, in one transaction.

## Data model

| Table | Purpose |
|---|---|
| `ShopSettings` | One row: shop name, logo, default language. |
| `Branches` | A shop location, with its rate, currency, rounding rule, warning threshold and admin PIN hash. |
| `Users`, `UserBranches` | Staff accounts and the branches each employee works in. |
| `RefreshTokens` | Hashed, rotating refresh tokens grouped in families. |
| `EmployeePermissionOverrides` | Owner's changes to what employees may do. |
| `Machines`, `MachineGroups` | Gaming PCs and optional pricing groups. |
| `PairingCodes` | Hashed one-time codes for registering machines. |
| `Sessions` | One block of time on a machine. A unique filtered index allows only one open session per machine. |
| `SessionEvents` | Append-only ledger: start, add, remove, pause, resume, stop, expire, transfer, cancel. |
| `Shifts` | Clock-in and clock-out with opening, expected and counted cash. One open shift per employee. |
| `AuditLog` | Append-only record of sensitive actions. |

Times are UTC everywhere. Each branch has an IANA time zone used only to draw "day" boundaries in reports.

## Security

| Concern | Approach |
|---|---|
| Passwords | Argon2id (19 MiB, 2 passes by default), PHC-format hashes, transparent rehash when parameters are raised. |
| Staff tokens | 15-minute HS256 JWT plus a 30-day rotating refresh token. Reusing a rotated token revokes the family. |
| Machine tokens | 256-bit random secret, stored as SHA-256, sent as `Authorization: Machine {id}.{secret}`. Opaque rather than JWT so a machine can be disabled instantly. |
| Brute force | Account lockout (5 failures, 15 minutes) plus a per-IP rate limit on login, refresh, setup and pairing. |
| Enumeration | Unknown users and wrong passwords return the same error and cost the same time. |
| Signing key | From `Jwt__SigningKey`, or generated once into the data directory with owner-only permissions. Never in the repo. |
| Transport | TLS terminates at the reverse proxy; only proxies listed in `Security:TrustedProxies` are believed for client IPs. |

## Decisions

These were confirmed with the owner or chosen as defaults. Changing one later is possible but touches the listed area.

| Decision | Choice |
|---|---|
| Branches | **Multi-branch.** One server serves several locations; machines, sessions, shifts and pricing belong to a branch. Owners see all branches, employees only assigned ones. |
| Refunds | Removing time or cancelling a paid session is a cash refund: a negative ledger amount with a required reason, reducing the employee's expected cash. |
| Pause | Pausing locks the PC with a "Paused" screen. |
| VIP rates | Machine groups with their own rate exist in the schema but are optional and off by default. |
| Receipts | No printed receipts in v1. |
| Remote screenshot | Included (Phase 5), permission-restricted and audited. |
| Client OS | Windows 10 and 11. |
| Server address | No domain name, plain IPv4. TLS options for an IP-only server are covered in Phase 8's deployment guide. |
| Default rounding | Nearest COP 100. Default currency COP with no decimals. |
| Database | SQLite in WAL mode. The model uses no provider-specific types, so PostgreSQL needs only a provider switch and its own migrations. |
| UI toolkit | WPF-UI (Fluent). |
| Installer | Inno Setup. |

## Testing

`dotnet test` runs unit tests (billing, permissions, hashing, tokens) and integration tests that host the real server
in-process against a throwaway SQLite database with a controllable clock.
