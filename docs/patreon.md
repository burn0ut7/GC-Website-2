# Patreon support connection

Patreon links to an existing Discord-backed website account; it is not a sign-in provider or an administrator role.

## Local setup

In appsettings.Local.json, fill the Patreon section with a v2 ClientId, ClientSecret and the numeric CampaignId for Global Conflicts (https://www.patreon.com/c/globalconflicts). Keep credentials local. PublicOrigin is https://localhost:5081 for this machine.

Register this exact redirect URI in the Patreon client: https://localhost:5081/auth/patreon/callback

Create/manage the client at https://www.patreon.com/portal/registration/register-clients . The campaign owner can retrieve the numeric ID from GET https://www.patreon.com/api/oauth2/v2/campaigns using their creator access token; use the ID for the Global Conflicts campaign. For campaign-wide donation totals, supply the creator access token through the server-only Patreon:CreatorAccessToken setting (environment variable Patreon__CreatorAccessToken or a private local configuration). It needs campaigns and campaigns.members scopes. Never put it in tracked files, browser code or logs. CampaignUrl never authorizes support.

Run Launch.cmd after configuring. Link Patreon from Account > Connected accounts. Missing configuration leaves linking disabled, clearly labelled Not configured.

## What is verified

OAuth requests identity and identity.memberships. Only the authenticated user's member resources matching the configured campaign ID count. Active supporter requires active_patron, a positive currently_entitled_amount_cents, and last_charge_status Paid. Free memberships and other campaigns do not count. Declined payments are shown as Payment issue; incomplete or failed checks are unavailable, not a definitive negative. This represents current confirmed paid support, not historical lifetime donations.

The account API exposes connection/support status and checkedAt, never tokens or pledge amounts. Memberships for other campaigns are not stored. No privileges are granted by this display feature; Discord roles continue controlling site access.

Tokens are encrypted with ASP.NET Data Protection and kept server-side. Preserve the existing private Auth:KeysPath when moving the server. Existing external identity uniqueness prevents linking the same Patreon identity to multiple website accounts. Callback challenges expire after five minutes and are consumed atomically with linking. Disconnect invalidates pending challenges and deletes stored tokens and supporter state; it does not cancel the pledge or revoke the app in Patreon settings.

The web service refreshes due connections approximately every 15 minutes while running, with bounded batches and one-second spacing. Failed checks become unverified and retry on the next interval. Status older than 30 minutes is displayed as unavailable. This is polling, not instant webhooks. A future benefits feature must enforce freshness server-side, not trust the browser badge.

## Deploy and verify

Apply migration 010_patreon.sql through the normal migration command, then apply the new runtime grants in scripts/Grant-Runtime-Access.sql. Only gc_web needs these tables. No new worker credentials are required.

The --steam integration test path also runs Patreon HTTP callback/database tests using a fake provider: CSRF, state expiry, replay, provider errors, disconnect races, replacement conflicts, protected tokens, campaign/payment classification and refreshed membership state. Live Patreon testing requires the owner-provided credentials and campaign ID.

Reference: https://docs.patreon.com/#get-api-oauth2-v2-identity and the Member resource in the same official API reference.
