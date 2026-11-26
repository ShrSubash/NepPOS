# Register: installer, automatic updates and backups

## Installer and updates (Velopack)

A release is built on your PC with one command and uploaded to `https://updates.<domain>/neppos`
(the server from the HamroGrocery repo's `deploy/README.md`).

- **New shop PC:** download and run `https://updates.<domain>/neppos/NepPOS.Register-win-Setup.exe`.
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

## Backups

**Manage → Backups & updates.** NepPOS backs up the register once a day by itself while it is running, and on
**Back up now**: the database (sales, products, customers, staff) and the register's settings, zipped, in
`%LOCALAPPDATA%\NepPOS\Backups`. Backups older than 30 days are deleted (the newest 3 are always kept).

Set **Also copy backups to** a USB drive left plugged in, or a OneDrive folder, so a dead PC doesn't take the
backups with it. If the drive is unplugged the backup still saves on the PC and the page says so.

Not in backups, on purpose: the Head Office key, licence and email password (encrypted for that PC; reconnect after a
restore). The restore steps are on the same page.
