# TelloGameRoom protocol

**Version 1.** The constant lives in `TelloGameRoom.Core.Protocol.ProtocolInfo.Version`. Bump it on any breaking
change to a REST payload or hub message, and add a line to the changelog at the end.

## Conventions

- Base path: `/api/v1`. JSON, camelCase properties, enums as strings, timestamps in UTC (ISO 8601).
- Money is integer **cents** (hundredths of the currency unit). COP 5,000 is `500000`. The single exception is
  `SetupRequest.hourlyRate`, a decimal in currency units.
- Durations are whole seconds.
- Clients send `X-Tello-Protocol: 1`. A different value gets `426 Upgrade Required` (`protocol_mismatch`).
- Errors are RFC 9457 problem documents with a stable `code`:

```json
{ "status": 409, "title": "Conflict", "detail": "This machine already has a session.", "code": "machine_busy" }
```

| Code | Status | Meaning |
|---|---|---|
| `validation_failed` | 400 | `errors` maps field names to messages. |
| `reason_required` | 400 | The action needs a reason. |
| `invalid_pairing_code` | 400 | Wrong, expired or already used. |
| `invalid_credentials` | 401 | Wrong username or password. |
| `invalid_refresh_token` | 401 | Sign in again. |
| `forbidden`, `account_disabled` | 403 | Not allowed, or the account was deactivated. |
| `not_found` | 404 | Also returned for a branch the caller may not access. |
| `conflict` | 409 | Duplicate name and similar. |
| `machine_busy`, `machine_offline`, `machine_disabled` | 409 | The machine cannot take the action now. |
| `no_open_session`, `invalid_session_state` | 409 | Nothing to act on, or not possible in this state. |
| `price_changed` | 409 | `expectedAmountCents` differs from the server's amount. Reload pricing and confirm again. |
| `shift_required`, `shift_already_open`, `no_open_shift` | 409 | Shift rules. |
| `last_owner`, `setup_already_completed` | 409 | |
| `account_locked` | 423 | `lockedUntilUtc` says when to retry. |
| `protocol_mismatch` | 426 | |
| `rate_limited` | 429 | With `Retry-After`. |
| `timeout` | 504 | A machine did not answer (screenshot). |

## Authentication

| Caller | Header |
|---|---|
| Staff | `Authorization: Bearer {accessToken}` (15 minutes; refresh with the refresh token) |
| Machine | `Authorization: Machine {machineId}.{secret}` |

The dashboard hub also accepts the access token as the `access_token` query parameter, and the machine hub accepts
the machine credential there.

## REST endpoints

"Permission" names are members of `TelloGameRoom.Core.Security.Permission`. *Staff* means any signed-in, active user.
`{b}` is `/api/v1/branches/{branchId}` and `{m}` is `{b}/machines/{machineId}`.

### Setup and sign-in

| Method | Path | Auth | Purpose |
|---|---|---|---|
| GET | `/health` | none | Liveness and database check. |
| GET | `/api/v1/setup/status` | none | `{ setupRequired, protocolVersion, serverVersion }` |
| POST | `/api/v1/setup` | none, once | Creates the shop, first branch and owner. Returns `AuthResponse`. |
| POST | `/api/v1/auth/login` | none | `{ username, password, deviceName? }` → `AuthResponse` |
| POST | `/api/v1/auth/refresh` | none | `{ refreshToken }` → `AuthResponse` with a new refresh token |
| POST | `/api/v1/auth/logout` | none | `{ refreshToken }` → 204. Revokes the token family. |
| GET | `/api/v1/auth/me` | staff | `UserInfo` with effective permissions and branches |
| POST | `/api/v1/auth/change-password` | staff | `{ currentPassword, newPassword }` → 204 |

### Machines and sessions

Session endpoints return the machine's new `MachineStateDto`.

| Method | Path | Permission | Body |
|---|---|---|---|
| GET | `{b}/state` | `MachinesView` | → `BranchStateDto` (pricing, machines, on-duty staff) |
| GET | `{b}/machines` | `MachinesView` | → list of `MachineSummary` |
| GET | `{m}/quote?seconds=` | `SessionsStart` | → `{ seconds, hourlyRateCents, amountCents }` |
| POST | `{m}/session/start` | `SessionsStart` | `{ durationSeconds, expectedAmountCents?, isFree?, reason?, allowOffline? }` |
| POST | `{m}/session/add-time` | `SessionsAddTime` | `{ seconds, expectedAmountCents? }` |
| POST | `{m}/session/remove-time` | `SessionsRemoveTime` | `{ seconds, reason }` |
| POST | `{m}/session/pause`, `/resume` | `SessionsPauseResume` | `{}` |
| POST | `{m}/session/stop` | `SessionsStop` | `{ reason? }` (no refund) |
| POST | `{m}/session/cancel` | `SessionsCancelPaid` | `{ reason, refundCents? }` (default: unused time) |
| POST | `{m}/session/transfer` | `SessionsTransfer` | `{ targetMachineId, reason?, allowOffline? }` |
| POST | `{b}/machines/pairing-codes` | `MachinesManage` | `{ machineName, groupId? }` → `{ code, expiresAtUtc, machineName }` |
| POST | `{m}/repair-code` | `MachinesManage` | → a pairing code that re-registers this machine with a new secret |
| PUT | `{m}` | `MachinesManage` | `{ name, groupId?, isEnabled }` |
| DELETE | `{m}` | `MachinesManage` | Only for a machine with no session history. |

Durations must be between 1 minute and 24 hours. Free sessions need `SessionsFreeTime` and a reason.
Employees must have an open shift for start, add-time, remove-time and cancel.

### Remote control

| Method | Path | Permission | Body |
|---|---|---|---|
| POST | `{m}/message` | `RemoteMessage` | `{ text }` (max 300 characters) |
| POST | `{b}/message-all` | `RemoteMessage` | `{ text }` → `{ sent, offline }` |
| POST | `{m}/command` | `RemoteLock` for `Lock`; `RemotePower` for `Restart`, `Shutdown` | `{ command }` |
| POST | `{b}/command-all` | same | `{ command }` → `{ sent, offline }` |
| POST | `{m}/screenshot` | `RemoteScreenshot` | → `image/jpeg`, or 504 after 15 seconds |

### Shifts

| Method | Path | Permission | Notes |
|---|---|---|---|
| POST | `{b}/shifts/open` | `ShiftsOwn` | `{ openingCashCents }` |
| POST | `{b}/shifts/close` | `ShiftsOwn` | `{ countedCashCents, notes? }`. Closes the caller's shift. |
| GET | `{b}/shifts/current` | `ShiftsOwn` | The caller's open shift, or 204. |
| GET | `{b}/shifts?fromUtc=&toUtc=&userId=` | `ShiftsOwn` | Own shifts only, unless the caller has `ShiftsViewAll`. |
| GET | `{b}/shifts/{id}` | `ShiftsOwn` | Same rule. |
| POST | `{b}/shifts/{id}/close` | `ShiftsViewAll` | Close someone else's shift. |
| GET | `{b}/shifts/{id}/report.pdf?lang=` | `ShiftsOwn` | Same rule. |
| GET | `{b}/shifts/export.csv?fromUtc=&toUtc=&lang=` | `ReportsExport` | |

`ShiftDto`: `expectedCashCents = openingCashCents + salesCents - refundsCents`; `differenceCents = counted - expected`.

### Reports, audit, employees

| Method | Path | Permission |
|---|---|---|
| GET | `{b}/reports/summary?from=YYYY-MM-DD&to=YYYY-MM-DD` | `ReportsView` |
| GET | `{b}/reports/summary.csv` / `.pdf` (same query, plus `lang`) | `ReportsExport` |
| GET | `/api/v1/audit?branchId=&fromUtc=&toUtc=&action=&userId=&machineId=&search=&page=&pageSize=` | `AuditView` |
| GET | `/api/v1/audit/export.csv` (same filters) | `AuditView` and `ReportsExport` |
| GET, POST | `/api/v1/employees` | `EmployeesManage` |
| PUT | `/api/v1/employees/{id}` | `EmployeesManage` |
| POST | `/api/v1/employees/{id}/reset-password` | `EmployeesManage` |
| GET, PUT | `/api/v1/settings/employee-permissions` | `EmployeesManage` |

Report dates are local days in the branch's time zone, at most 366 days. An `action` filter ending in a dot
(`session.`) matches every action with that prefix.

### Settings and administration

| Method | Path | Permission |
|---|---|---|
| GET | `/api/v1/settings/shop` | staff |
| PUT | `/api/v1/settings/shop` | `SettingsManage` |
| GET | `/api/v1/settings/shop/logo` | none (public) |
| PUT, DELETE | `/api/v1/settings/shop/logo` | `SettingsManage` (PNG or JPEG body, max 1 MB) |
| GET | `/api/v1/branches` | staff (the branches the caller can use) |
| POST | `/api/v1/branches` | `BranchesManage` |
| PUT | `/api/v1/branches/{id}/settings` | `SettingsManage` |
| GET | `{b}/groups` | `MachinesView` |
| POST, PUT, DELETE | `{b}/groups`, `{b}/groups/{id}` | `PricingManage` |
| GET | `/api/v1/admin/backup` | `BackupManage` (downloads a SQLite snapshot) |
| POST | `/api/v1/admin/restore` | `BackupManage` (raw backup body; 202, then the server restarts) |

### Machine side

| Method | Path | Auth | Purpose |
|---|---|---|---|
| POST | `/api/v1/machines/pair` | none | `{ code, hostName?, osVersion?, appVersion? }` → identity and token (shown once) |
| GET | `/api/v1/machines/me` | machine | `MachineIdentity` |
| POST | `/api/v1/machines/me/screenshots/{requestId}` | machine | JPEG body, max 4 MB |

## Hubs

Typed interfaces live in `TelloGameRoom.Core.Protocol` (`Hubs.cs`). JSON protocol, enums as strings.

### `/hubs/machine`

| Direction | Message | Payload |
|---|---|---|
| client → server | `Sync()` | returns `MachineSessionState` |
| client → server | `GetConfig()` | returns `MachineConfigDto` |
| server → client | `SessionStateChanged` | `MachineSessionState` |
| server → client | `ConfigChanged` | `MachineConfigDto` |
| server → client | `ShowMessage` | `{ text, sentAtUtc }` |
| server → client | `ExecuteCommand` | `Lock`, `Restart` or `Shutdown` |
| server → client | `CaptureScreenshot` | `requestId` to upload against |

`MachineSessionState` is `{ state: Locked|Active|Paused, sessionId, remainingSeconds, warningThresholdSeconds, version, serverTimeUtc }`.
The machine counts down from `remainingSeconds` on its own monotonic clock. For the same `sessionId`, a message with
a lower `version` than the last one applied must be ignored. A machine calls `Sync` after every (re)connect and
every 30 seconds.

### `/hubs/dashboard`

| Direction | Message | Payload |
|---|---|---|
| client → server | `Subscribe(branchId)` | returns `BranchStateDto`; needs `MachinesView` and access to the branch |
| client → server | `Unsubscribe(branchId)` | |
| server → client | `MachineChanged` | `MachineStateDto` |
| server → client | `MachineRemoved` | `branchId`, `machineId` |
| server → client | `BranchChanged` | `branchId`: pricing, settings or the on-duty list changed; fetch `{b}/state` again |

The server closes a dashboard connection when its access token expires; the client reconnects with a fresh token
and subscribes again.

## Pairing flow

1. The owner asks for a pairing code for a machine name. The code is 8 characters, shown as `ABCD-EFGH`, valid for
   15 minutes and usable once.
2. The Machine app posts the code to `/machines/pair` and receives its `machineId` and `token`.
3. The token is shown only in that response. The Machine app stores it encrypted (Windows DPAPI).

## Changelog

- **1**: initial protocol: setup, auth, pairing, sessions, hubs, remote control, shifts, reports, audit, employees, settings, backup.
