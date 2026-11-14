# ADR 0001: Separate products, one shared Head Office

Date: 2026-11-14 · Status: accepted

## Decision

NepPOS (POS) and HamroGrocery (online shop) are **separate products**. A customer can buy either one or both.

- **HamroGrocery works fully on its own.** A customer with both turns on *Connect to NepPOS* in the website admin; with it off, the website owns its products and prices as it does today.
- **One Head Office serves every POS customer** (multi-tenant). Every Head Office row has a `TenantId`, and a caller's tenant, store and register come only from the key it signed in with, never from the request.
- **Each web customer gets their own copy of the website** (own deployment, own database, own Stripe account) until there are enough of them (roughly 10+) to justify making the website multi-tenant.

## Why

- A shared Head Office costs one column and a few checks now. Adding tenants later would mean changing every table and query, and running a separate Head Office per customer would not scale.
- A multi-tenant website, Stripe Connect and a platform portal are a lot of work that only pays off with many web customers. One copy each is simpler for the first few.
- Optional connection matches how Shopify, Square and similar products link to other systems.

## Consequences

- Head Office has tenants → stores → registers from its first version.
- The website never depends on Head Office being up unless the connection is switched on.
- Revisit the website decision when copies become painful to run.
