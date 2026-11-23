# Licensing

Every business has one licence in Head Office: a plan, its limits, and how long it's paid for.
NepPOS Portal sets it (Businesses → a business → Licence); registers and the website obey it.

## What the licence holds

| | |
|---|---|
| Plan | Starter (1 store, 1 register), Standard (1 store, 3 registers, website, e-invoices), Multi-store (5 stores, 15 registers, everything). Any limit can be changed for one business. |
| Status | Trial, Active, Suspended, Cancelled |
| Paid until | End of the trial or the paid period |
| Message | Shown on the shop's registers when suspended or cancelled |

A new business gets a 30-day trial on the Standard plan. A business created before licences existed gets the
same trial the first time anything checks it.

## The rules (`LicencePolicy`, shared by all three)

1. **7 days before** the end: an amber notice on every register.
2. **After the end**: 14 days' grace. Everything works; a red notice says when sales stop.
3. **After the grace**: new sales stop. A sale already being paid always finishes. Reports, end of day,
   exports and Manage keep working, so the shop always has its own data.
4. **Suspended or cancelled**: new sales stop straight away, with the Portal's message.
5. **Offline**: a register must reach Head Office every 30 days (notice from day 23).
6. **Clock put back** more than a day behind the latest time the register trusted: new sales stop until the
   clock is right or the register reaches Head Office.
7. **Not activated**: a register can run 14 days before it's connected to Head Office.

Head Office also refuses: activating a register beyond the plan's limit, adding a store beyond it, and any
website or app call when the licence has stopped or the plan doesn't include the website.
Sales events are always accepted: they are the owner's records.

## How a register knows

Head Office signs the licence (ECDSA P-256) and sends it at activation and with every heartbeat (each minute).
The register keeps it in `%LOCALAPPDATA%\NepPOS\licence.json` and checks the signature itself, so it works
offline and editing the file only makes it invalid. A change in the Portal reaches an online register within a minute.

## The signing key

- **Developer PCs**: nothing to do. Head Office makes a key and keeps it in `headoffice.db`.
- **Production**: Head Office won't start without `HeadOffice:LicenceSigningKey` (a PEM EC private key) in
  App Service settings or Key Vault. Make one once:

  ```bash
  openssl ecparam -name prime256v1 -genkey -noout | openssl pkcs8 -topk8 -nocrypt -out licence-signing.pem
  ```
  ```bash
  openssl ec -in licence-signing.pem -pubout -out licence-public.pem
  ```

  Put `licence-signing.pem`'s text in the setting (never in `appsettings.json` or git). Then paste
  `licence-public.pem`'s text into `LicenceTrust.PinnedPublicKeyPem` (NepPOS.Application/Licensing) before
  building registers for customers, so a register only trusts licences from your Head Office.
