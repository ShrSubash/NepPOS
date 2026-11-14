# ADR 0002: Registers sign in with an API key from a one-time activation code

Date: 2026-11-14 · Status: accepted (replaces the OAuth2 client-credentials idea in the first integration design)

## Decision

- The platform admin creates a **one-time activation code** for a store (12 characters, e.g. `K7QM-3XRT-9PWD`, valid 1–30 days, default 7).
- On the register, the code is swapped once for an **API key** (`npk_` + 256 random bits) that belongs to exactly one tenant, store and register.
- The register sends it as `Authorization: Bearer npk_…`. Head Office stores only a **SHA-256 hash** of keys and codes.
- A lost or replaced PC is **revoked** in Head Office; its key stops working on the next request, and the register code can be activated again on the new PC.
- Admin endpoints use a single platform admin key (`X-Admin-Key`, from configuration, 24+ characters) until the platform portal exists.

## Why

- A full OAuth2 server (OpenIddict or Duende) is a lot to run for machine-to-machine calls from a few hundred registers. A long random key per register gives the same protection: it identifies one register, can be revoked alone, and is never stored in readable form.
- Activation codes mean nobody types or emails a long key, and a store can be set up over the phone.
- SHA-256 is enough because the secrets are long and random; slow password hashes are only needed for secrets people choose.

## Consequences

- If the website later needs finer permissions (scopes), it gets its own key type then.
- Keys must be kept encrypted on the register (Windows DPAPI), like the email password.
