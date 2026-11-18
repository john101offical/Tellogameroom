# Deployment

This guide takes you from an empty VPS to a working shop: server, TLS, firewall, backups, then the Windows apps.

Examples use `203.0.113.5` as the server's public IPv4 address. Replace it with yours.

## 1. What you need

| Item | Notes |
|---|---|
| A Linux VPS | Ubuntu 24.04 LTS, 1 vCPU and 1 GB RAM is enough for 100 PCs. A public IPv4 address. |
| Windows 10 or 11 (64-bit) | On the counter PC (Dashboard) and on every gaming PC (Machine app). |
| The installers | Built by CI (the "installers" artifact of the Windows job) or with `deploy\installer\build-installers.ps1`. |

## 2. Prepare the VPS

Sign in over SSH as a user with `sudo`.

Install Docker:

```bash
curl -fsSL https://get.docker.com | sudo sh
```

Open only what is needed. Ports 80 and 443 are for the clients; 22 is your SSH access:

```bash
sudo ufw allow 22/tcp
```

```bash
sudo ufw allow 80/tcp
```

```bash
sudo ufw allow 443/tcp
```

```bash
sudo ufw enable
```

The server's own port (8080) is never published; only the reverse proxy can reach it.

Recommended SSH hygiene: key-based login only (`PasswordAuthentication no` in `/etc/ssh/sshd_config`) and
automatic security updates (`sudo apt install unattended-upgrades`).

## 3. Choose how TLS will work

All traffic is encrypted either way. The difference is how the Windows apps know they are talking to *your* server.

| | A. IP address only | B. With a host name |
|---|---|---|
| You need | Nothing extra | A name that points to your IP. A free dynamic-DNS name works. |
| Certificate | Self-signed, made once by `make-cert.sh`, valid 10 years | Let's Encrypt, automatic |
| On each client | Enter the server's **fingerprint** once (or pass it to the installer) | Nothing: just the host name |
| Caddyfile | `Caddyfile.ip` | `Caddyfile.domain` |

Since you have a plain IPv4 address, **option A** works today. Option B is less work per PC in the long run; you can
switch later by changing two lines in `.env` and re-entering the address on the clients.

Why the fingerprint: public certificate authorities do not hand out ordinary certificates for bare IP addresses, so
in option A the server uses a self-signed certificate. The apps cannot verify that through the usual chain, so they
are told its SHA-256 fingerprint and accept only that certificate. Without the fingerprint the apps refuse to connect.

## 4. Start the server

```bash
git clone https://github.com/john101offical/tellogameroom.git
```

```bash
cd tellogameroom/deploy/docker
```

```bash
cp .env.example .env
```

Edit `.env`: set `TELLO_ADDRESS` to your IP address (option A) or host name (option B), and for option B set
`TELLO_CADDYFILE=Caddyfile.domain`.

For option A, create the certificate. This prints the fingerprint; keep it, you will type it on the clients:

```bash
sh ../caddy/make-cert.sh
```

```bash
sudo docker compose up -d --build
```

Check it:

```bash
sudo docker compose ps
```

```bash
curl -k https://203.0.113.5/health
```

The second command should print `{"status":"ok"}`.

To see the fingerprint again later:

```bash
sh ../caddy/fingerprint.sh
```

The certificate is in `deploy/caddy/certs` and is valid for ten years. It is not in git. If you delete that folder
(or move to a new VPS without copying it), `make-cert.sh` creates a new one and every client needs the new fingerprint.

### What is stored where

Everything lives in the Docker volume `tello-data` (`/data` in the container):

| Path | Contents |
|---|---|
| `tellogameroom.db` | The database. |
| `jwt-signing.key` | Generated on first start. Losing it only signs everyone out. |
| `logs/` | Daily log files, 30 days. |
| `backups/` | Automatic daily backups (`auto-*`, 14 kept), manual ones, and `pre-restore-*` safety copies. |

No secret is stored in the repository or in `.env`.

### Without Docker (systemd)

See the comments in `deploy/systemd/tellogameroom.service`. Install the .NET 10 ASP.NET runtime, publish the server
to `/opt/tellogameroom`, enable the unit, and put Caddy or nginx (`deploy/nginx/tellogameroom.conf`) in front of it.

## 5. Backups

**Automatic, on the server.** Once a day the server writes a consistent snapshot to `backups/` and keeps the last 14.
Nothing to configure.

**Off the server.** A backup on the same VPS does not survive losing the VPS. Copy the newest one elsewhere every
night with cron (`sudo crontab -e`):

```
30 4 * * * /home/youruser/tellogameroom/deploy/backup/offsite-backup.sh user@backup-host:/backups/tello >> /var/log/tello-backup.log 2>&1
```

The destination is anything `scp` accepts. Edit the last line of the script to use `rclone` or a cloud bucket instead.

**On demand.** In the Dashboard: Settings → Backup → Download backup.

**Restore.** Settings → Backup → Restore backup, and pick a `.db` file. The server checks the file, restarts, and
comes back with the restored data. The data it replaced is kept as `backups/pre-restore-*.db`. Everyone has to sign
in again. Test a restore once before you need it.

To copy a backup file out of the volume by hand:

```bash
sudo docker compose cp server:/data/backups ./backups
```

## 6. Updating the server

```bash
cd tellogameroom && git pull
```

```bash
cd deploy/docker && sudo docker compose up -d --build
```

Database migrations run automatically at start. Sessions keep running across the restart: time is computed from
stored timestamps, and each PC keeps counting down on its own while the server is away. Update the Windows apps to
the same release; apps built for a different protocol version are told to update.

## 7. Install the Dashboard

Run `TelloGameRoom-Dashboard-Setup-x.y.z.exe` on the counter PC. On first start, enter the server address (and the
fingerprint for option A). On a fresh server the setup wizard creates the owner account, the shop and its first branch.

Silent install with the address pre-filled:

```
TelloGameRoom-Dashboard-Setup-1.0.0.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SERVER=203.0.113.5 /FINGERPRINT=<fingerprint>
```

## 8. Install the Machine app on each gaming PC

Prepare the PC first as described in [HARDENING.md](HARDENING.md): a standard Windows account for customers that
signs in automatically. Then, as an administrator:

1. Run `TelloGameRoom-Machine-Setup-x.y.z.exe`. It installs the app and the MachineGuard service, which starts the
   lock screen for whoever is signed in.
2. In the Dashboard: Machines → **Add PC**, type a name such as `PC-01`, and read the pairing code.
3. On the gaming PC, type the server address, the code and (option A) the fingerprint. The PC appears in the
   Dashboard as *Free*.
4. Set an **admin PIN** in the Dashboard (Settings → This branch → Change admin PIN). Until one is set, the lock
   screen cannot be closed from the PC at all.

Many PCs at once, from a script or a deployment tool:

```
TelloGameRoom-Machine-Setup-1.0.0.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SERVER=203.0.113.5 /FINGERPRINT=<fingerprint>
```

After that, each PC shows the pairing screen with the address filled in and only needs its code.

To close the lock screen for maintenance: press **Ctrl+Shift+F12** and enter the admin PIN. The guard stands down
until the next sign-in or reboot. To remove the software, uninstall "TelloGameRoom Machine" from Windows Settings;
the uninstaller removes the service and gives Task Manager back.

Reinstalled Windows on a PC? In the Dashboard, open the PC's menu → **New pairing code**. The PC keeps its name and
history, and its old credentials stop working.

## 9. Building the installers yourself

On Windows with the .NET 10 SDK and Inno Setup 6:

```
powershell -ExecutionPolicy Bypass -File deploy\installer\build-installers.ps1 -Version 1.0.0
```

The installers are self-contained: the PCs do not need .NET installed. They are **not code-signed**, so Windows
SmartScreen will warn on first run; sign them with your own certificate if that matters to you.

## 10. First run on Windows: what to check

The Windows apps were written and compiled on Linux and have not been launched yet. Before opening the shop with
them, go through this list on one counter PC and one gaming PC and report anything that looks or behaves wrong:

1. Dashboard: connect, run the setup wizard, sign out and in.
2. Add a PC, pair it, and see it turn *Free* in the grid.
3. Start a 1-hour session (click the card, press Enter). The gaming PC should unlock and show the floating timer.
4. Add time, pause, resume, send a message, and stop. Each should show on the PC within a second.
5. Start a 2-minute session with the warning set to 1 minute: watch the warning, then the lock at zero.
6. During a session, unplug the PC's network cable: the timer keeps counting and locks at zero. Plug it back in.
7. While locked, try Alt+Tab, the Windows key, Alt+F4 and Ctrl+Shift+Esc.
8. Kill `TelloGameRoom.Machine.exe` from an administrator prompt: it should be back within a few seconds.
9. Ctrl+Shift+F12 with the admin PIN closes the lock screen.
10. Clock in as an employee, sell a session, clock out with a cash count, and open the shift report PDF.
11. Open Reports, export CSV and PDF, and download a backup.
12. Switch language and theme in Settings.

## 11. Troubleshooting

| Symptom | Check |
|---|---|
| The apps say the certificate is not trusted | Option A: the fingerprint is missing or mistyped. Run `fingerprint.sh` again and compare. |
| The apps cannot reach the server | `curl -k https://<address>/health` from another network; the firewall; `sudo docker compose logs caddy`. |
| A PC shows "This PC is no longer registered" | It was deleted, disabled or re-paired. Create a new pairing code from its menu. |
| Dashboard shows "Reconnecting…" | Network between the counter and the VPS. Timers keep running; the grid resyncs when the connection returns. |
| Logs | Server: `sudo docker compose logs server` and `/data/logs`. Dashboard: `%LocalAppData%\TelloGameRoom\Dashboard\logs`. Machine and guard: `C:\ProgramData\TelloGameRoom`. |
| Too many login attempts | An account locks for 15 minutes after 5 failures. The owner can reset the password, which also unlocks it. |
| The owner forgot their password | There is no back door. Another owner account can reset it; otherwise restore a backup taken when the password was known. Create a second owner account early. |
