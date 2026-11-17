# TelloGameRoom

Game room / internet café management. Customers pay cash per hour at the counter; staff start a timer on a PC
from the Dashboard; the PC locks when the time runs out. One server can run several branches.

| Project | What it is | Runs on |
|---|---|---|
| `TelloGameRoom.Server` | ASP.NET Core API + SignalR. The single source of truth. | Linux VPS (Docker or systemd) |
| `TelloGameRoom.Dashboard` | WPF app for the owner and employees. | Windows 10/11 |
| `TelloGameRoom.Machine` | WPF lock screen and floating timer on each gaming PC. | Windows 10/11 |
| `TelloGameRoom.MachineGuard` | Windows Service that keeps the Machine app running. | Windows 10/11 |
| `TelloGameRoom.Core` | Shared contracts: DTOs, permissions, billing maths, the offline timer. | everywhere |
| `TelloGameRoom.Tests` | xUnit unit, integration and end-to-end tests. | CI |

## Status

All eight phases are implemented.

**Verified by automated tests (171 tests):** the server, its API and SignalR flow, billing, permissions, shifts,
reports, exports, backup and restore, restart recovery, the client-side offline timer logic, and a scale test with
100 simulated machines and 10 dashboards.

**Compiled but not yet run:** the three Windows programs (Dashboard, Machine, MachineGuard) were developed on Linux,
where WPF and Windows Services cannot be launched. They build without warnings, but their screens, the lock-down
behaviour and the installers must be tried on a real Windows PC before the shop depends on them. The Docker image
has likewise not been built here; CI builds it. See "First run on Windows" in [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md).

## Build and test

Requires the .NET 10 SDK.

```bash
dotnet build
```

```bash
dotnet test
```

## Try the server locally

```bash
dotnet run --project src/TelloGameRoom.Server
```

It creates `data/` (database, logs, signing key, backups) in the working directory and prints the URL it listens on.
Then start the Dashboard on Windows, enter that address (with `http://` for a local test), and follow the setup wizard.

## Documentation

| Document | Contents |
|---|---|
| [docs/USER_GUIDE.md](docs/USER_GUIDE.md) | How staff and the owner use the system day to day. |
| [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md) | VPS setup, TLS without a domain name, firewall, backups, installers. |
| [docs/HARDENING.md](docs/HARDENING.md) | What the lock-down does and does not prevent, and how to harden the PCs. |
| [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) | Design, data model, security, decisions. |
| [docs/PROTOCOL.md](docs/PROTOCOL.md) | The versioned REST and SignalR contract. |

## Repository layout

```
src/        the five projects
tests/      xUnit tests
docs/       documentation
deploy/     Dockerfile, docker-compose, Caddy, nginx, systemd, installer scripts, backup script
tools/      resx generator and translation checker
```

## Translations

UI strings live in `src/*/Resources/strings.json` as `"Key": ["English", "Español"]`. After editing:

```bash
python3 tools/resx.py src/TelloGameRoom.Dashboard/Resources
```

```bash
python3 tools/check_strings.py src/TelloGameRoom.Dashboard src/TelloGameRoom.Machine
```

The first regenerates the `.resx` files; the second fails if the code uses a key that has no text.
