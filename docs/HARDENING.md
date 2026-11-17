# Hardening the gaming PCs

The Machine app and MachineGuard make it hard for a customer to use a PC without paying. They are software running
on a computer the customer physically sits at, so they raise the effort required; they cannot make cheating
impossible. This page says plainly what is covered, what is not, and what to configure in Windows and the BIOS to
close the remaining gaps.

## What the software does

| Measure | Effect |
|---|---|
| Fullscreen, top-most lock screen on every monitor | The desktop is not reachable while the PC is locked. |
| Keyboard hook | Blocks the Windows keys, Alt+Tab, Alt+Esc, Alt+F4, Ctrl+Esc and Ctrl+Shift+Esc while locked. |
| Focus guard | The lock screen takes focus back every second and refuses to close. |
| Task Manager policy | MachineGuard disables Task Manager for the customer account while locked, and re-enables it during a session and on uninstall. |
| MachineGuard service | Restarts the Machine app within about two seconds if it is killed, and starts it after every sign-in. |
| Admin PIN | Closing the app needs the PIN set in the Dashboard (Ctrl+Shift+F12). Five wrong attempts cost a one-minute wait. With no PIN set, the app cannot be closed at all. |
| Offline behaviour | Unplugging the network cable does not stop the countdown, and a PC that locked while offline stays locked. |
| Clock tampering | The countdown uses a monotonic clock; changing the Windows date or time has no effect. |
| Restart during a session | The remaining time is saved to disk and capped on restore, so killing the app or rebooting never adds time. |

## What it cannot prevent

| Gap | Why | What to do |
|---|---|---|
| **A local administrator** | An administrator can stop the service, edit the registry or uninstall the software. | Customers must use a **standard** Windows account. This is the single most important setting. |
| **Ctrl+Alt+Del** | Windows reserves this key combination; no program can block it. | With Task Manager disabled and a standard account, the screen it opens offers nothing useful. Also remove "Switch user" and "Sign out" (below). |
| **Booting another operating system** | A USB stick or a second disk bypasses Windows entirely. | BIOS/UEFI password, disable USB and network boot, enable Secure Boot. |
| **Safe Mode** | Services such as MachineGuard do not run in Safe Mode. | The BIOS password and a standard account make this much harder; see also the `bcdedit` step below. |
| **Exclusive-fullscreen games hiding the timer** | A game in exclusive fullscreen draws over every window. | The session still ends on time and the lock screen appears. Prefer borderless-window mode where the timer should stay visible. |
| **Programs left running** | When a session ends, the lock screen covers the game; it does not close it. | Use the Dashboard's remote **Restart** between customers when needed. |
| **The power button and the plug** | Hardware. | A reboot returns to the lock screen, so this gains the customer nothing. |
| **A second keyboard driver or hardware key injector** | Out of scope for a user-mode hook. | Physical supervision. |

## Windows checklist

Do this once per PC, signed in as an administrator.

1. **Create a standard account for customers** (for example `Player`) and keep the administrator password to yourself.
   Settings → Accounts → Other users → Add account, then make sure its type is *Standard user*.
2. **Sign in automatically as that account** so a power cycle lands on the lock screen: run `netplwiz`, select the
   account, and clear "Users must enter a user name and password".
3. **Install the Machine app** (see [DEPLOYMENT.md](DEPLOYMENT.md)), pair the PC and set an **admin PIN** in the
   Dashboard under Settings.
4. **Keep User Account Control on** (the default). A standard user then cannot install drivers or services.
5. **Remove "Switch user" and "Sign out" from Ctrl+Alt+Del** for the customer account. On Windows Pro use
   `gpedit.msc`: User Configuration → Administrative Templates → System → Ctrl+Alt+Del Options: enable
   "Remove Lock Computer", "Remove Change Password" and "Remove Logoff"; and Computer Configuration →
   Administrative Templates → System → Logon: enable "Hide entry points for Fast User Switching". On Windows Home,
   Group Policy is not available; the standard account and Task Manager policy still apply.
6. **Block the command prompt and registry tools** for the customer account (Windows Pro): User Configuration →
   Administrative Templates → System: enable "Prevent access to the command prompt" and "Prevent access to registry
   editing tools". Some game launchers need neither.
7. **Make Safe Mode harder to reach:** in an administrator command prompt, `bcdedit /set {default} bootmenupolicy standard`
   keeps the legacy F8 menu off, and `bcdedit /set {default} recoveryenabled No` disables automatic recovery mode.
   Keep a recovery USB stick for yourself, because this also removes your own easy route into recovery.
8. **Leave Windows Update on**, scheduled outside opening hours.

## BIOS / UEFI checklist

1. Set a **supervisor (setup) password**.
2. Set the internal disk as the **only boot device**; disable USB, optical and network (PXE) boot.
3. Keep **Secure Boot** enabled.
4. Disable the one-time **boot menu key** (often F12) if the firmware allows it.

## Physical

- Keep the PC cases closed or locked: resetting the BIOS password usually needs access to the motherboard.
- A counter with a view of the screens deters most attempts better than any software.

## What the owner can see afterwards

Every sensitive action is in the audit log, which cannot be edited: sessions started and stopped, time added or
removed, free time and its reason, refunds, remote commands, screenshots, sign-ins and failures. Each shift ends
with a cash count compared against what was sold. Wrong-PIN attempts and Machine app restarts are written to the
log files on the PC (`C:\ProgramData\TelloGameRoom`).

## Privacy note

Remote screenshots show whatever is on a customer's screen. They are restricted to the owner by default and every
request is logged. Tell customers that screens may be monitored, and check what your local law requires.
