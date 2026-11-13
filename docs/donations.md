# Donations

The public page is `/donations.html`, available in Explore Pages. Its order is monthly goal, supporters, costs. The default budget is a **$100/month USD game-server estimate**, not a confirmed bill. Patreon link: https://www.patreon.com/c/globalconflicts/.

## Configuration

Apply migration `013_donations.sql` as schema owner and run the new grants in `scripts/Grant-Runtime-Access.sql`. Restart the web service after building. Existing user settings, links and authentication are preserved.

Set `Patreon:CampaignId` and the server-only `Patreon:CreatorAccessToken` using private local settings or environment variables. The creator token requires `campaigns` and `campaigns.members` scopes. Keep it out of source control and the browser. Replace expired/revoked credentials server-side; failures retain the last complete snapshot and become visibly stale after one hour. Individual account linking still needs the existing OAuth client configuration documented in `patreon.md`.

Configure actual monthly USD costs with a `Donations:Costs` array of objects with `Name`, `MonthlyCents`, and `Estimated`. The goal is the sum, so costs and the progress target cannot drift. Omit the array to use the $100 server estimate. Example:

```json
{"Donations":{"Costs":[{"Name":"Game server","MonthlyCents":10000,"Estimated":true}]}}
```

No credentials are checked in. When campaign sync is unconfigured, the page shows unavailable totals and no fabricated donors, while the Patreon support link remains usable.

## Meaning of the numbers

The server polls Patreon API v2 every 15 minutes, fetches every page and publishes an atomic complete snapshot. Only active memberships with positive entitlement and a Paid last charge are shown. The monthly estimate uses `will_pay_amount_cents / pledge_cadence`; annual pledges are divided by their cadence. Cancelled renewals can contribute zero upcoming support even while their paid membership remains active. Unknown billing values make the overall total unavailable, never a misleading zero. Only monthly USD campaigns are supported; incompatible campaigns leave sync unavailable/stale.

These are **upcoming gross monthly pledges**, not payments received, net payouts or an accounting ledger. Fees reduce payouts. Support history is the current pledge chain's start date from Patreon, not a verified count of successful monthly payments or lifetime tenure. No transaction history is invented.

## Privacy

Account > Donation privacy lets each signed-in person hide their identity or opt into sharing the estimated monthly amount. Default: linked website/Discord name visible, amount hidden. Full anonymity overrides amount visibility and removes the name, avatar and tenure from public responses. Unlinked patrons are anonymous. Anonymous supporters are combined into one count; totals still include them. Aggregate totals can permit inference in small groups, so hidden amounts mean no per-person amount disclosure, not a mathematical secrecy guarantee.

The public API joins current profile choices on each request and sends no internal user/Patreon IDs, tokens, raw Patreon names or hidden per-person amounts. Responses are `no-store`; privacy changes take effect on the next page fetch. Names sort alphabetically, never by hidden donation amount. Unlinking Patreon removes the website identity from the list while retaining campaign totals. Discord avatars are captured on subsequent Discord sign-in; older accounts show initials until they sign in again. Image failures also show initials.

## Validation

`dotnet run --project tests/GC.Infrastructure.Tests -- --donations` with `GC_TEST_ADMIN_CONNECTION` set creates and drops only a random test database. It exercises migrations, provider pagination, partial refresh retention, monthly calculations, donor privacy serialization, protected settings routes, CSRF and account ownership. Live Patreon verification requires configured creator and OAuth credentials.

API references: https://docs.patreon.com/#get-api-oauth2-v2-campaigns-campaign_id-members and https://docs.patreon.com/#member .
