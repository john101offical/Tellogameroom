# User guide

TelloGameRoom has two programs you will see: the **Dashboard** at the counter, and the **lock screen** on each
gaming PC. This guide covers daily work first, then the owner's tasks.

The Dashboard is available in English and Spanish (Settings → This computer → Language).

## For everyone

### Signing in

Open the Dashboard and sign in with **your own** username and password. Everything you do is recorded under your
name, so never share an account. After five wrong passwords the account locks for 15 minutes; the owner can reset
your password.

The bottom-left corner shows the connection: green *Connected*, or *Reconnecting…*. If the connection drops, the
PCs keep counting down by themselves; the grid catches up when the connection returns.

### Starting your shift

Go to **Shifts** → **Start shift**. Count the cash in the drawer and type the amount. Employees cannot sell until
they have started a shift. The top bar shows who is on duty.

### The Machines page

Each PC is a card.

| Card shows | Meaning |
|---|---|
| Blue dot, "Free", a play symbol | Locked and ready for a customer. |
| Green ring with a time | In use. The ring empties as the time runs out and turns red near the end. |
| Yellow ring, "Paused" | The session is paused and the PC is locked; the time is frozen. |
| Grey dot, "Offline" | The PC is off or has no network. |
| "In use · offline" | A session is running but the PC lost its connection. It keeps counting down and will lock on its own. |

### Selling time (three clicks)

1. Click the PC's card.
2. Pick **30 min**, **1 h**, **2 h** or **3 h**, or type other minutes. **1 h is already selected.**
3. Read the large **amount to collect**, take the cash, and press **Start session** (or Enter).

The PC unlocks at once. With the keyboard: Tab to the card, Enter, Enter.

If the Dashboard says *the price has changed*, the owner updated the rate while your dialog was open. Check the new
amount and confirm again.

### During a session

The buttons under the ring are the quick actions; **right-click** the card or press **…** for everything.

| Action | What happens |
|---|---|
| **Add time** (+) | Same dialog as starting. Collect the amount shown. |
| **Pause / resume** | Locks the PC and freezes the time; resume continues from the same point. |
| **Send message** | Shows a notice on that PC. Quick texts are provided; you can type your own. |
| **Move to another PC** | For a PC that crashes: the remaining time moves to a free PC you choose. No money changes hands. |
| **Stop session** | Ends it now. The remaining time is lost and **no money is returned**. |
| **Lock** | Locks the screen immediately. A running session is paused so the customer does not lose paid time. |

When about five minutes are left the customer sees a warning on their screen. At zero the PC locks by itself.

**Message all** and **All PCs** (top right) send a message or a lock to every PC in the branch.

### Ending your shift

**Shifts** → **End shift**. Count all the cash in the drawer and type the total. The Dashboard shows what it
expected (opening cash + your sales − your refunds) and whether the drawer is balanced, short or over. Select the
shift and press **Shift report (PDF)** to print it.

### What the customer sees

- **Locked:** the shop's logo and name, the PC name, and "Pay at the counter to start".
- **Playing:** a small timer in the top-right corner. It can be dragged elsewhere and never steals focus from a game.
- **Messages and the low-time warning** appear at the top of the screen.

## For the owner

Owners see extra pages: **Overview**, **Reports**, **Employees**, **Audit log**, and the full **Settings**.

### First-time setup

The first time the Dashboard connects to a new server, a two-step wizard asks for the shop name, the first branch,
the price per hour, and your owner account. Then:

1. **Settings → This branch:** check the price, currency and rounding, and set the **admin PIN** for the PCs.
2. **Machines → Add PC** for each gaming PC (see the deployment guide for the PC side).
3. **Employees → Add employee** for each staff member.
4. Add a **second owner account** for yourself or a partner, in case one password is forgotten.

### Owner-only session actions

| Action | Notes |
|---|---|
| **Remove time** | Refunds the removed time. A reason is required. |
| **Cancel and refund** | Ends the session and refunds the unused time, or an amount you type. A reason is required. |
| **Free session** | Tick "Free session" in the start dialog and give a reason. |
| **Restart / Shut down** | One PC, or all of them from **All PCs**. You are asked to confirm. |
| **Take screenshot** | Shows the PC's screen. Every request is logged. Tell customers that screens may be monitored. |

A refund is booked to whoever gives the money back. If you refund a sale an employee made, it does not reduce the
employee's expected cash.

### Prices

**Settings → This branch → Price per hour.** A new price applies only to sessions started after you save; running
sessions, and time added to them, keep the price they started with.

**Rounding** decides how odd amounts are rounded (for example 50 minutes): nearest, always up, or always down, to
the step you choose (100 by default).

**PC groups** are optional. Create a group with its own price (Settings → PC groups), then assign PCs to it from
their menu on the Machines page → **Set group**. PCs without a group use the branch price.

### Employees and permissions

**Employees** lists every account. Select one to edit it, reset its password, or deactivate it. Deactivating signs
the person out immediately and keeps their history.

The panel on the right decides what **all employees** may do: add time, remove time, pause, stop, cancel a paid
session, move a session, send messages, lock PCs, restart and shut down PCs. Changes apply at once. Prices,
settings, reports, the audit log and free time are always owner-only.

### Shifts

The Shifts page lists the last 30 days for everyone. The **Difference** column is red when cash was missing and
yellow when there was too much. If an employee forgot to clock out, select the shift and press **Close selected
shift**. **Export CSV** saves the list.

### Overview and Reports

**Overview** shows the last seven days: income, sessions, hours sold, today's income, income per day and the
busiest hours.

**Reports** does the same for any period (Today, Last 7 days, This month, Last month, or two dates), with income
per employee and per PC. **Export CSV** and **Export PDF** save it.

Income is always sales minus refunds. Free sessions count as sessions with no income.

### Audit log

Every sensitive action is recorded with who did it, on which PC, the old and new values, the reason where one was
required, and the time: sessions, added and removed time, refunds, free time, transfers, remote commands,
screenshots, price and setting changes, employee changes, shifts, sign-ins and failed sign-ins.

Filter by text, type of action and dates, and export to CSV. Entries cannot be changed or deleted by anyone.

### Several branches

**Settings → This branch → Add branch.** A new branch starts with the same price and currency as your first one.
Choose the branch you are looking at from the picker at the top of the window. Each branch has its own PCs, prices,
shifts and reports. Assign employees to branches when you add or edit them; owners see every branch.

### Shop name, logo and language

**Settings → Shop.** The name and logo appear on every lock screen. The shop language is used on the gaming PCs and
as the default for exported files.

### Backup

The server makes a backup every day by itself. **Settings → Backup** lets you download one or restore one.
Restoring replaces everything on the server with the backup and signs everyone out, so it asks you to confirm.

### Closing the lock screen on a gaming PC

Press **Ctrl+Shift+F12** on that PC and enter the admin PIN. During a session, right-click the floating timer
instead. The lock screen returns at the next sign-in or reboot.
