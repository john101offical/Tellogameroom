# Architecture

## Overview

```
 Dashboard (WPF) ──HTTPS REST + SignalR /hubs/dashboard──┐
 Dashboard (WPF) ────────────────────────────────────────┤
                                                         ▼
                                    Caddy (TLS) ─► TelloGameRoom.Server ── SQLite (WAL)
                                                         ▲
 Machine (WPF) ───HTTPS REST + SignalR /hubs/machine─────┤
 Machine (WPF) ──────────────────────────────────────────┘
   ▲ supervised by MachineGuard (Windows Service, named pipe)
```

The server is the only authority. Clients display state and enforce the lock; they never decide how much time is left.

## Principles

1. **Server-authoritative time.** A session stores timestamps, not a ticking counter. While active, remaining time is
   `EndsAtUtc - now`; while paused it is `RemainingSecondsAtPause` (`SessionClock` in Core). A restart loses nothing:
   a background worker asks the database once a second which sessions are overdue and ends them, recording the
   moment their time actually ran out.
2. **Machines keep working offline.** `MachineSessionTracker` (Core) counts down on a monotonic clock, so changing
   the Windows clock does nothing. It locks itself at zero and then stays locked until the *server* says otherwise.
   It persists its state to disk, so killing the app or rebooting does not hand out free time. Whatever the server
   sends replaces local state; stale messages (lower version, same session) are ignored.
3. **Permissions are enforced on the server.** The permission catalogue lives in Core so clients can hide controls,
   but every endpoint and hub method checks on the server. The caller's role and active flag are re-read from the
   database on each request, so deactivating an employee takes effect immediately.
4. **Money is integer cents.** All amounts are `long` hundredths of the currency unit: exact sums, identical on
   SQLite and PostgreSQL.
5. **The ledger and the audit log are append-only.** `SessionEvents` and `AuditLog` reject updates and deletes both
   in the EF Core context and with database triggers. Income is always the sum of ledger rows; a shift's expected
   cash is the sum of the rows booked to it.
6. **An action and its audit entry commit together**, in one transaction. State is pushed to clients after commit.
7. **The price staff see is the price charged.** The Dashboard computes the amount with the same `PriceCalculator`
   as the server and sends it along; the server rejects the request if its own result differs (`price_changed`).

## Server

| Area | Where |
|---|---|
| Sessions and timers | `Services/SessionService.cs`, `SessionExpiryWorker.cs`. One process-wide gate serializes session changes; a unique filtered index (one open session per machine) is the backstop. |
| Realtime | `Realtime/MachineHub.cs`, `DashboardHub.cs`, `RealtimeNotifier.cs`, `MachinePresence.cs` |
| Auth | `Services/AuthService.cs`, `Security/` |
| Shifts and cash | `Services/ShiftService.cs` |
| Reports and exports | `Reports/` (QuestPDF for PDF) |
| Backup and restore | `Services/BackupService.cs` (`VACUUM INTO`, daily worker, staged restore applied at next start) |

## Data model

| Table | Purpose |
|---|---|
| `ShopSettings` | One row: shop name, logo, default language. |
| `Branches` | A location, with its rate, currency, rounding rule, warning threshold, time zone and admin PIN hash. |
| `Users`, `UserBranches` | Staff accounts and the branches each employee works in. |
| `RefreshTokens` | Hashed, rotating refresh tokens grouped in families. |
| `EmployeePermissionOverrides` | The owner's changes to what employees may do. |
| `Machines`, `MachineGroups` | Gaming PCs and optional pricing groups. |
| `PairingCodes` | Hashed one-time codes for registering (or re-registering) machines. |
| `Sessions` | One block of time on a machine. |
| `SessionEvents` | Append-only ledger: start, add, remove, pause, resume, stop, expire, transfer, cancel. |
| `Shifts` | Clock-in and clock-out with opening, expected and counted cash. One open shift per employee. |
| `AuditLog` | Append-only record of sensitive actions. |

Times are UTC everywhere. Each branch has an IANA time zone used only to draw "day" boundaries in reports.

## Security

| Concern | Approach |
|---|---|
| Passwords | Argon2id (19 MiB, 2 passes by default), PHC-format hashes, transparent rehash when parameters are raised. |
| Staff tokens | 15-minute HS256 JWT plus a 30-day rotating refresh token. Reusing a rotated token revokes the family. Tokens live only in the Dashboard's memory. |
| Machine tokens | 256-bit random secret, stored as SHA-256 on the server and DPAPI-encrypted on the PC. Opaque rather than JWT so a machine can be disabled instantly. |
| Admin PIN | PBKDF2-SHA256, verified on the machine so it works offline; five wrong attempts cost a one-minute wait. |
| Brute force | Account lockout (5 failures, 15 minutes) plus a per-IP rate limit on login, refresh, setup and pairing. |
| Enumeration | Unknown users and wrong passwords return the same error and cost the same time. |
| Signing key | From `Jwt__SigningKey`, or generated once into the data directory with owner-only permissions. Never in the repo. |
| Transport | TLS at the reverse proxy. Without a domain name, clients pin the proxy's certificate fingerprint. |
| Uploads | Logos and screenshots are checked by content, size-limited, and never interpreted. CSV exports neutralize spreadsheet formulas. |

## Windows clients

Both apps use MVVM (CommunityToolkit.Mvvm). View models hold no WPF controls; dialogs are opened through small
static helpers. Each app has one theme file (`Resources/Theme.xaml`) holding its whole design system.

- **Dashboard.** WPF-UI supplies the Fluent controls and dark/light themes. `Session` owns tokens and refresh;
  `Realtime` owns the hub connection and resubscribes after every reconnect; `BranchContext` is the shared, live
  model of the selected branch that all pages read.
- **Machine.** Plain WPF with its own styles, to stay light on gaming PCs. `ServerLink` reconnects forever with
  backoff; `MainViewModel` ticks the tracker; `App` switches between the lock screen and the floating timer and
  arms the keyboard hook.
- **MachineGuard.** Checks every two seconds that the Machine app runs in the console session and starts it there
  (as the user, not as SYSTEM) if it does not. Disables Task Manager while locked. Accepts pipe messages only from
  the installed Machine executable.

## Decisions

| Decision | Choice and reason |
|---|---|
| Branches | **Multi-branch.** Machines, sessions, shifts and pricing belong to a branch. Owners see all branches, employees only assigned ones. |
| Refunds | Removing time or cancelling a paid session is a cash refund: a negative ledger amount with a required reason, booked to whoever gave the money back. |
| Stop vs cancel | *Stop* ends a session with no refund (the customer left). *Cancel* ends it and refunds the unused time, or an explicit amount. |
| Pause | Pausing locks the PC with a "Paused" screen. A remote *Lock* on a running session pauses it, so paid time is not lost. |
| Shift required | Employees must be clocked in to take or refund money, so every peso belongs to a shift. The owner may act without a shift. |
| Offline machines | Starting a session on a PC that is not connected needs an explicit confirmation. |
| VIP rates | Machine groups with their own rate exist but are optional and never required. |
| Receipts | No printed receipts in v1. |
| Remote screenshot | Included; owner-only by default and always audited. |
| Charts | A small custom WPF bar chart instead of LiveCharts2 or ScottPlot. Both pull .NET Framework-only OpenTK packages into a .NET 10 app (NuGet warning NU1701), and the charts needed here are simple. |
| Machine app UI | Hand-styled WPF rather than WPF-UI: fewer dependencies in the process that must never crash. |
| Database | SQLite in WAL mode. The model uses no provider-specific types; PostgreSQL needs the provider package, a `UseNpgsql` switch in `Program.cs` and its own migrations. That switch is not implemented yet. |
| Installer | Inno Setup: simple, scriptable silent installs, and it handles the service and the uninstall clean-up. |
| Default rounding | Nearest COP 100. Default currency COP with no decimals. |

## Known limitations

- The Windows apps have not been run yet (see the README).
- Lists built when a page opens (theme names, rounding options, audit categories) switch language at the next
  sign-in rather than instantly; everything else switches live.
- With monitors at different DPI scales, the lock screen covers all of them but its content is laid out for the primary one.
- A disabled machine's existing connection is not cut; it simply cannot be given sessions and cannot reconnect.
- SignalR runs on a single server process; scaling out would need a backplane. One process comfortably handles
  the 100-machine target.

## Testing

`dotnet test` runs unit tests (billing, permissions, hashing, the offline tracker, CSV) and integration tests that
host the real server in-process against a throwaway SQLite database with a controllable clock, connect simulated
machines and dashboards over SignalR, restart the server on the same data, and run a full working day end to end.
