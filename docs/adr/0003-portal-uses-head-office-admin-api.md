# ADR 0003: The Portal works through Head Office's admin API; staff accounts live in Head Office

Date: 2026-11-18 · Status: accepted (replaces "the Portal shares Head Office's database" in the integration design)

## Decision

- `NepPOS.Portal` is a separate Blazor Web App in this repo with **no database of its own**. It reads and changes everything through Head Office's `/api/v1/admin/*` endpoints, sending the platform admin key (`X-Admin-Key`) and the signed-in person's email (`X-Portal-User`).
- **Staff accounts** (`PlatformUser`: email, role Owner or Support, password hash, TOTP secret, lockout) are stored in Head Office. The Portal checks a sign-in by calling `POST /api/v1/admin/staff/sign-in`, then keeps its own cookie.
- Every change made through the admin API is written to an append-only **audit log** in Head Office (`AuditEntries`), with the person from `X-Portal-User`, or "admin key" when there is none.
- Two-step sign-in (TOTP) is required for every staff account.

## Why

- **One owner of the rules.** Head Office already validates businesses, stores, codes and keys for the curl and `.http` workflow. Sharing the database would mean two apps writing the same tables with two copies of those rules.
- **Smaller blast radius.** The Portal holds no connection string; if it is compromised, the attacker gets what the admin API allows, all of it logged, rather than the whole database.
- **Freedom to change.** The Portal can be redeployed, moved or rewritten (or a Business portal added) without touching the database schema.
- Staff in Head Office keeps a single source of truth for "who did this" in the audit log, and lets later clients (a CLI, the Business portal) use the same accounts.

## Consequences

- The admin key is powerful: it lives only in user secrets / App Service settings (Key Vault in production) and should be rotated if anyone with access leaves. Head Office trusts `X-Portal-User` because only holders of the admin key can send it.
- Owner-only actions (managing the team) are enforced by the Portal's policies today. When a second client appears, move those checks into Head Office by passing a signed staff token instead of the email header.
- Every new Portal feature needs an admin endpoint first; that is intended.
