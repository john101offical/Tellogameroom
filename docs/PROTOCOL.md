# TelloGameRoom protocol

**Version 1.** The constant lives in `TelloGameRoom.Core.Protocol.ProtocolInfo.Version`.

## Conventions

- Base path: `/api/v1`. JSON, camelCase properties, enums as strings, timestamps in UTC (ISO 8601).
- Money in the REST API is integer **cents** (hundredths of the currency unit) unless a field says otherwise.
- Clients send `X-Tello-Protocol: 1`. A different value gets `426 Upgrade Required` with code `protocol_mismatch`.
- Errors are RFC 9457 problem documents with a stable `code` field:

```json
{ "status": 401, "title": "Unauthorized", "detail": "Wrong username or password.", "code": "invalid_credentials" }
```

| Code | Meaning |
|---|---|
| `validation_failed` | 400. `errors` maps field names to messages. |
| `invalid_credentials` | 401. Wrong username or password. |
| `account_locked` | 423. Too many failures; `lockedUntilUtc` says when to retry. |
| `account_disabled` | 403. The account was deactivated. |
| `invalid_refresh_token` | 401. Sign in again. |
| `setup_already_completed` | 409. |
| `invalid_pairing_code` | 400. Wrong, expired or already used. |
| `not_found`, `conflict`, `forbidden` | 404, 409, 403. |
| `rate_limited` | 429 with `Retry-After`. |
| `protocol_mismatch` | 426. |

## Authentication

| Caller | Header |
|---|---|
| Staff | `Authorization: Bearer {accessToken}` |
| Machine | `Authorization: Machine {machineId}.{secret}` |

SignalR connections may pass the same value as the `access_token` query parameter.

## Endpoints (Phase 1)

| Method | Path | Auth | Purpose |
|---|---|---|---|
| GET | `/health` | none | Liveness and database check. |
| GET | `/api/v1/setup/status` | none | `{ setupRequired, protocolVersion, serverVersion }` |
| POST | `/api/v1/setup` | none, once | Creates the shop, first branch and owner. Returns `AuthResponse`. |
| POST | `/api/v1/auth/login` | none | `{ username, password, deviceName? }` → `AuthResponse` |
| POST | `/api/v1/auth/refresh` | none | `{ refreshToken }` → `AuthResponse` with a new refresh token |
| POST | `/api/v1/auth/logout` | none | `{ refreshToken }` → 204. Revokes the token family. |
| GET | `/api/v1/auth/me` | staff | `UserInfo` with effective permissions and branches |
| POST | `/api/v1/auth/change-password` | staff | `{ currentPassword, newPassword }` → 204. Signs out other devices. |
| GET | `/api/v1/branches/{branchId}/machines` | `MachinesView` | List of `MachineSummary` |
| POST | `/api/v1/branches/{branchId}/machines/pairing-codes` | `MachinesManage` | `{ machineName, groupId? }` → `{ code, expiresAtUtc, machineName }` |
| POST | `/api/v1/machines/pair` | none | `{ code, hostName?, osVersion?, appVersion? }` → machine identity and token |
| GET | `/api/v1/machines/me` | machine | `MachineIdentity` including `serverTimeUtc` |

A branch the caller cannot access answers `404`, the same as a branch that does not exist.

`SetupRequest.hourlyRate` is the one exception to the cents rule: it is a decimal amount in currency units.

## Pairing flow

1. The owner asks for a pairing code for a machine name (for example `PC-01`). The code is 8 characters, shown as
   `ABCD-EFGH`, valid for 15 minutes and usable once.
2. The Machine app posts the code to `/machines/pair` and receives its `machineId` and `token`.
3. The token is shown only in that response. The Machine app stores it protected (Windows DPAPI) and uses it from then on.

## Hubs

`/hubs/dashboard` and `/hubs/machine` are reserved. Their messages are specified in Phase 2.

## Changelog

- **1** (Phase 1): setup, auth, machine pairing.
