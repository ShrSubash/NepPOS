# Head Office on the server

Head Office runs on the same server as the website (set up with the HamroGrocery repo's `deploy/server-setup.sh`,
which already made the `neppos` user, `/opt/neppos-headoffice`, `/var/lib/neppos`, HTTPS for `ho.neppos.com.au` and
the nightly backup of `headoffice.db`).

## Database

Head Office uses a SQLite file on the server, `/var/lib/neppos/headoffice.db`, built and upgraded by EF Core
migrations (`src/NepPOS.HeadOffice.Api/Data/Migrations`). It's backed up every night with the website (`hamro-backup.sh`).
That's plenty for the first few dozen shops; moving to SQL Server later needs its own set of migrations.

When a change adds or alters a Head Office table, add a migration before releasing (NepPOS folder):

```bash
dotnet ef migrations add WhatChanged --project src/NepPOS.HeadOffice.Api --output-dir Data/Migrations
```

## Once

1. Copy the service and enable it (from your PC, in the NepPOS folder):

   ```bash
   scp deploy/headoffice/neppos-headoffice.service deploy/headoffice/appsettings.Production.example.json root@SERVER_IP:/root/
   ```
   ```bash
   ssh root@SERVER_IP "cp /root/neppos-headoffice.service /etc/systemd/system/ && systemctl daemon-reload && systemctl enable neppos-headoffice && cp -n /root/appsettings.Production.example.json /opt/neppos-headoffice/appsettings.Production.json"
   ```

2. Make the licence signing key (Git Bash, see `docs/licensing.md`), and keep `licence-signing.pem` somewhere safe:

   ```bash
   openssl ecparam -name prime256v1 -genkey -noout | openssl pkcs8 -topk8 -nocrypt -out licence-signing.pem
   ```
   ```bash
   awk 'NF {printf "%s\\n", $0}' licence-signing.pem
   ```

   The second command prints the key on one line with `\n` between lines: that's what goes in `LicenceSigningKey`.

3. Fill in the secrets on the server: `nano /opt/neppos-headoffice/appsettings.Production.json`
   (AdminKey: `openssl rand -hex 32`; LicenceSigningKey from step 2; the owner's email and a strong password). Then:

   ```bash
   ssh root@SERVER_IP "chown neppos:neppos /opt/neppos-headoffice/appsettings.Production.json && chmod 600 /opt/neppos-headoffice/appsettings.Production.json"
   ```

## Every release

```bash
dotnet publish src/NepPOS.HeadOffice.Api -c Release -r linux-x64 --self-contained true -o publish-ho
```
```bash
tar -czf ho.tgz -C publish-ho .
```
```bash
scp ho.tgz root@SERVER_IP:/tmp/ho.tgz
```
```bash
ssh root@SERVER_IP "hamro-backup.sh && systemctl stop neppos-headoffice && tar -xzf /tmp/ho.tgz -C /opt/neppos-headoffice && chown -R neppos:neppos /opt/neppos-headoffice && chmod +x /opt/neppos-headoffice/NepPOS.HeadOffice.Api && systemctl start neppos-headoffice && rm /tmp/ho.tgz"
```

Head Office applies its database migrations when it starts. Check it: `https://ho.neppos.com.au/health` (or the log: `journalctl -u neppos-headoffice -n 100`).

## Connect the shop

- Each register: Manage → Head Office → address `https://ho.neppos.com.au` and its activation code (from the Portal).
  Each register has its own key; a lost or stolen PC is cut off by revoking its key in the Portal / Head Office admin.
- The website: Admin → Connect to NepPOS → `https://ho.neppos.com.au` and the website key (from the Portal).
