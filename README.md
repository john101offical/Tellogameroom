# TelloGameRoom

Game room / internet café management. Customers pay cash per hour at the counter; staff start a timer on a PC
from the dashboard; the PC locks when the time runs out.

| Project | What it is | Runs on |
|---|---|---|
| `TelloGameRoom.Server` | ASP.NET Core API + SignalR. The single source of truth. | Linux VPS |
| `TelloGameRoom.Dashboard` | WPF app for the owner and employees. | Windows 10/11 |
| `TelloGameRoom.Machine` | WPF lock screen and floating timer on each gaming PC. | Windows 10/11 |
| `TelloGameRoom.MachineGuard` | Windows Service that keeps the Machine app running. | Windows 10/11 |
| `TelloGameRoom.Core` | Shared contracts: DTOs, enums, permissions, billing maths. | everywhere |
| `TelloGameRoom.Tests` | xUnit unit and integration tests. | CI |

## Status

Built in phases. **Phase 1 is done**: solution skeleton, Core contracts, database schema and migrations, and the
server with authentication, permissions and machine pairing. The Windows apps are empty shells until Phases 3 and 4.

| Phase | Scope | State |
|---|---|---|
| 1 | Skeleton, contracts, schema, auth | done |
| 2 | Session and timer engine | next |
| 3 | Machine app MVP | |
| 4 | Dashboard MVP | |
| 5 | Remote control, messaging, audit log UI | |
| 6 | Employees, shifts, cash reconciliation | |
| 7 | Reports, charts, exports, settings | |
| 8 | Polish, translations, lock-down, installers, deployment | |

## Build and test

Requires the .NET 10 SDK.

```bash
dotnet build
```

```bash
dotnet test
```

The WPF projects and the Windows Service compile on Linux and macOS, but only run on Windows.

## Run the server locally

```bash
dotnet run --project src/TelloGameRoom.Server
```

The server creates `data/` (database, logs, generated signing key) in the working directory, applies migrations and
listens on the URL printed at startup. Check `GET /health`, then `GET /api/v1/setup/status`.

Configuration is read from `appsettings.json` and environment variables (`Storage__DataDirectory`,
`Jwt__SigningKey`, `Security__TrustedProxies__0`, ...). No secret is ever stored in this repository.

## Documentation

- [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md): how the pieces fit, the data model and the decisions taken.
- [docs/PROTOCOL.md](docs/PROTOCOL.md): the versioned REST and SignalR contract.

Deployment, hardening and user guides are written in Phase 8.
