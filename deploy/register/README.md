# Register: installer, automatic updates and backups

## Installer and updates (Velopack)

A release is built on your PC with one command and uploaded to `https://updates.neppos.com.au/neppos`
(the server from the HamroGrocery repo's `deploy/README.md`).

- **New shop PC:** download and run `https://updates.neppos.com.au/neppos/NepPOS.Register-win-Setup.exe`.
  It installs NepPOS for that Windows user with a desktop and Start menu shortcut. Nothing else is needed (.NET is included).
  Windows may show "Windows protected your PC" until the installer is code-signed: **More info → Run anyway**.
- **Updates:** every register checks every 4 hours, downloads a new version in the background, and installs it the
  next time NepPOS starts (usually the next morning), so a sale is never interrupted. A manager can install it straight
  away on **Manage → Backups & updates**.
- **Data is safe:** sales, products and settings live in `%LOCALAPPDATA%\NepPOS`, outside the install folder, so
  installing, updating and reinstalling never touch them.

### Release a new version

In PowerShell, in the NepPOS folder (the first time, `Set-ExecutionPolicy -Scope CurrentUser RemoteSigned`):

```powershell
.\deploy\register\publish-release.ps1 -Version 1.0.0 -Server root@SERVER_IP
```

Use a higher version every time (1.0.1, 1.0.2, 1.1.0 …). Builds from Visual Studio (F5) don't update themselves;
only installed copies do.

## Staff and PINs

**Manage → Staff & PINs.** A new register starts with two sign-ins: owner `123456` and cashier `1111`.
Change both straight away (the page shows a warning until you do), then add a sign-in for each person.

- Every person has their own 4–6 digit PIN; the PIN alone says who signed in, so two people can't share one.
- Managers add and change cashiers and supervisors. Only an owner adds or changes managers and owners.
  Everyone can change their own PIN.
- When someone leaves, untick **Can sign in**. Their past sales stay in reports. Every change is in the Journal.
- Phone, email, start date and (owners only) hourly rate are optional details on the same page.

## Timesheets and staff activity

- Staff tap **Clock in** on the register's top bar when their shift starts and **Clock out** when it ends
  (separate from signing in, which happens between customers). The button turns green while they're on shift.
- **Manage → Timesheets:** hours per person for this week, last week or any dates; estimated pay for owners
  (hours × hourly rate, no penalty rates). Fix a forgotten clock-out with **Edit**; every change is in the Journal.
  **Export CSV** saves a file for payroll in the report folder.
- **Manage → Staff activity:** per person: first sign-in, last sign-out, sales and average sale, refunds, voids,
  lines removed, no-sales and discounts. Unusually many refunds, voids or no-sales are highlighted.

## Backups

**Manage → Backups & updates.** NepPOS backs up the register once a day by itself while it is running, and on
**Back up now**: the database (sales, products, customers, staff) and the register's settings, zipped, in
`%LOCALAPPDATA%\NepPOS\Backups`. Backups older than 30 days are deleted (the newest 3 are always kept).

Set **Also copy backups to** a USB drive left plugged in, or a OneDrive folder, so a dead PC doesn't take the
backups with it. If the drive is unplugged the backup still saves on the PC and the page says so.

Not in backups, on purpose: the Head Office key, licence and email password (encrypted for that PC; reconnect after a
restore). The restore steps are on the same page.
