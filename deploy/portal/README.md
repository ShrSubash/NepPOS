# Portal on the server

The Portal is how you run NepPOS in production: create a business and its stores, issue a register's
activation code, create the website key for HamroGrocery, see which registers are online, and revoke a lost PC's key.
It has no database; everything goes through Head Office's admin API. It runs on the same server as Head Office,
at `https://portal.<domain>`, and only your own internet connection can open it.

## Once

1. DNS: add an **A record** `portal` → the server's IP (next to `ho` and `updates`).

2. Find your office/home internet address: open https://ifconfig.me on that PC. (If your internet provider changes it,
   update the Caddy line below.)

3. On the server, add the Portal to Caddy (`nano /etc/caddy/Caddyfile`), with your address in place of `203.0.113.7`:

   ```
   portal.hamrogrocery.com.au {
   	@outside not remote_ip 203.0.113.7
   	respond @outside "Not available" 403
   	encode zstd gzip
   	reverse_proxy 127.0.0.1:5090
   }
   ```

   ```bash
   systemctl reload caddy
   ```

4. Copy the service and settings (from your PC, in the NepPOS folder):

   ```bash
   scp deploy/portal/neppos-portal.service deploy/portal/appsettings.Production.example.json root@SERVER_IP:/root/
   ```
   ```bash
   ssh root@SERVER_IP "mkdir -p /opt/neppos-portal && cp /root/neppos-portal.service /etc/systemd/system/ && systemctl daemon-reload && systemctl enable neppos-portal && cp -n /root/appsettings.Production.example.json /opt/neppos-portal/appsettings.Production.json"
   ```

5. Put Head Office's AdminKey in it: `nano /opt/neppos-portal/appsettings.Production.json`, then
   `chown -R neppos:neppos /opt/neppos-portal && chmod 600 /opt/neppos-portal/appsettings.Production.json`.

## Every release

```bash
dotnet publish src/NepPOS.Portal -c Release -r linux-x64 --self-contained true -o publish-portal
```
```bash
tar -czf portal.tgz -C publish-portal .
```
```bash
scp portal.tgz root@SERVER_IP:/tmp/portal.tgz
```
```bash
ssh root@SERVER_IP "systemctl stop neppos-portal && tar -xzf /tmp/portal.tgz -C /opt/neppos-portal && chown -R neppos:neppos /opt/neppos-portal && chmod +x /opt/neppos-portal/NepPOS.Portal && systemctl start neppos-portal && rm /tmp/portal.tgz"
```

## First sign-in

Open `https://portal.<domain>` and sign in with the owner email and password from Head Office's settings
(`HeadOffice:Owner`). It makes you set up the authenticator app straight away. Then:

1. **New business**: your business, the first store, an activation code for the shop PC, and a website key.
2. Shop PC: Manage → Head Office → enter the activation code.
3. Website: Admin → Connect to NepPOS → `https://ho.<domain>` and the website key.
