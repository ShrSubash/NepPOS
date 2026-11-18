# NepPOS Portal

The NepPOS company's own console. NepPOS sells and supports the POS; its customers are the businesses that run it (a café, a grocery, a takeaway). The Portal is where NepPOS staff look after those customers: who they are, their stores and registers, their keys, and later their subscriptions, support tickets and software updates.

Customers never sign in here. A separate, optional **Business portal** for shop owners (their own stores, invoices, raising a ticket) is a later step.

## How it fits

```
 Registers (MAUI, in each shop)  ──sync/heartbeat──▶  Head Office API  ◀──admin API──  NepPOS Portal (this)
 Shop website (HamroGrocery)     ──website key────▶   (cloud, one DB)                   NepPOS staff only
```

- **Head Office** is the cloud API every register syncs to. It owns all data: businesses (tenants), stores, registers, keys, staff accounts and the audit log.
- **The Portal** is a Blazor web app with **no database**. Every screen reads and changes data through Head Office's admin API (`/api/v1/admin/*`) with the admin key, so all the rules live in one place and the Portal can be redeployed or replaced freely. See [ADR 0003](../adr/0003-portal-uses-head-office-admin-api.md).
- Every call carries `X-Portal-User` (the signed-in email) and Head Office writes it to the **audit log**. Calls made with the admin key directly (curl, the `.http` file) are logged as "admin key".

## Step 1: Platform console (this branch)

| Screen | What it does |
|---|---|
| **Sign in** | Email, password and a 6-digit authenticator code. Five wrong tries lock the account for 15 minutes. |
| **Two-step setup** | Forced on first sign-in: scan a QR code (drawn in the browser) or type the key, confirm a code. No other page opens until it's done. |
| **Overview** | Businesses, stores, registers online/offline (online = heartbeat in the last 10 minutes), activation codes waiting, registers needing attention, recent activity. |
| **Businesses** | Search; each customer's stores, registers, how many are online and when they were last heard from. |
| **New business** | One form: business, first store, activation code for the first register and (optionally) a website key. Codes and keys are shown once, with a Copy button. |
| **Business page** | Rename; add stores; issue activation codes; registers with status and **Revoke**; website keys (create, revoke); the business's activity. |
| **Registers** | Every register in the fleet, filtered All / Online / Offline / Revoked. |
| **Audit log** | Who did what, to which business, when. Read-only. |
| **Team** | Staff list. Owners add staff (Owner or Support) and disable them (never the last owner); everyone can change their own password. |

### Security

- Staff accounts live in Head Office (`PlatformUsers`): passwords hashed with ASP.NET Core's `PasswordHasher` (PBKDF2), 12+ characters; TOTP two-step sign-in (RFC 6238, 30-second codes, a code can't be used twice); lockout after 5 failures.
- The Portal keeps the signed-in person in an HTTP-only, SameSite=Strict cookie for 8 hours (renewed while in use). Sign-in and two-step setup are plain server-rendered forms with antiforgery; the rest is interactive Blazor Server.
- The admin key is only ever in user secrets (dev) or App Service settings (Azure), never in `appsettings.json`. Activation codes and website keys are shown once and never stored readable (Head Office keeps SHA-256 hashes).
- Run the Portal over HTTPS in production (HSTS is on outside Development). Keep it off the public internet if you can (Azure App Service access restrictions to your office IP).

## Running it on a developer PC

1. **Head Office**, once: the admin key and the first owner account.

   ```bash
   cd src/NepPOS.HeadOffice.Api
   dotnet user-secrets set "HeadOffice:AdminKey" "<24+ random characters>"
   dotnet user-secrets set "HeadOffice:Owner:Email" "you@yourcompany.com"
   dotnet user-secrets set "HeadOffice:Owner:Name" "Your Name"
   dotnet user-secrets set "HeadOffice:Owner:Password" "<12+ characters>"
   dotnet run --launch-profile http
   ```

   On start, Head Office creates that owner if there are no staff accounts yet (and logs it). After that the `Owner` settings are ignored; remove the password secret once you've signed in.

2. **Portal**: the same admin key, and where Head Office is.

   ```bash
   cd src/NepPOS.Portal
   dotnet user-secrets set "Portal:AdminKey" "<the same admin key>"
   dotnet user-secrets set "Portal:HeadOfficeUrl" "http://localhost:5180"
   dotnet run
   ```

3. Open `http://localhost:5190`, sign in with the owner email and password (leave the code empty), and set up two-step sign-in with your phone.

In Visual Studio, set both `NepPOS.HeadOffice.Api` and `NepPOS.Portal` as startup projects (Solution → Configure Startup Projects → Multiple).

### In Azure

Two App Services (or one plan, two apps). Portal settings: `Portal__HeadOfficeUrl` = Head Office's HTTPS address, `Portal__AdminKey` = the admin key (Key Vault reference). Head Office settings as in the integration guide, plus `HeadOffice__Owner__*` for the first deploy only.

## Roadmap

| Step | What | Notes |
|---|---|---|
| **1** | **Platform console**: sign-in with two-step, overview, businesses, stores, registers, keys, audit log, team | This branch |
| 2 | **Plans and subscriptions**: plan, register limit, start/expiry dates, status (trial, active, past due, suspended, cancelled); expiring-soon list | Head Office enforces limits on activation and shows a banner on the register; it **never stops a sale** |
| 3 | **Support tickets**: raised from the Portal, the register (Help → Report a problem, with logs attached) and the shop website; status, priority, assignee, replies, email notifications | |
| 4 | **Software updates**: upload a release, release notes, channels (stable/beta), staged rollout; registers check Head Office and show "A new version is available" with Update now / Tonight | Velopack on the register with Head Office as the update source; never mid-sale |
| 5 | **Fleet health**: version per register, last sync, outbox backlog, rejected events, alerts for registers offline > 24 h | |
| 6 | **Business portal** (optional) for shop owners: their stores, registers, invoices, tickets | Separate sign-in; same admin API with tenant scoping |
| 7 | **Billing**: invoices and card payments (Stripe), automatic suspend after a grace period | |
| 8 | **Announcements**: messages shown on registers (maintenance windows, new features) | |
| 9 | **Dashboard**: signups, churn, MRR, tickets per customer | |

## Code map

| Where | What |
|---|---|
| `src/NepPOS.Portal/Program.cs` | Cookie sign-in, policies (signed in + two-step done; owner), the Head Office client |
| `src/NepPOS.Portal/Services/HeadOfficeAdmin.cs` | Typed client for the admin API; never throws, returns a sentence to show |
| `src/NepPOS.Portal/Components/Account/` | Sign in, two-step setup, not allowed, error (server-rendered) |
| `src/NepPOS.Portal/Components/Pages/` | The console pages (interactive) |
| `src/NepPOS.Contracts/Admin/AdminContracts.cs` | The admin API's request and response shapes, shared by both |
| `src/NepPOS.HeadOffice.Api/Endpoints/PortalEndpoints.cs` | Overview, lists, rename, audit, staff and sign-in endpoints |
| `src/NepPOS.HeadOffice.Api/Security/Totp.cs`, `Audit.cs` | Two-step codes; writing the audit log |
| `tests/NepPOS.HeadOffice.Api.Tests/PortalApiTests.cs` | The Portal endpoints end to end |
