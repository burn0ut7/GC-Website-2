# Account settings

The local website account page separates profile/preferences from connected accounts.
Preferences are saved per GC account in PostgreSQL: display name, time zone, clock
format, initial video volume and reduced motion. The landing page reads these
settings after sign-in. Device accessibility preferences always take precedence.
A display name changes only the website presentation, not Discord or game identity.

Discord remains the only sign-in method and cannot be disconnected. Arma Reforger,
Steam and Patreon are explicitly unavailable; no ownership or integration is implied.
Technical identifiers are available in an expandable section.

GET/PUT /api/account/settings require a valid linked identity. PUT also requires
antiforgery proof, validates all values and limits the request to 8 KiB. The account
is taken from the authenticated session, never from a supplied user ID. Responses
are not cached. Migration 006 and the runtime grant script add the preferences table.

Design references: GitHub's separation of account preferences and security settings
(https://docs.github.com/en/account-and-profile/how-tos/account-settings), and EA's
connected-account management and explicit unlink consequences
(https://help.ea.com/en/articles/ea-account/unlink-ea-account/).

Validation: integration tests cover persistence, account isolation, validation,
antiforgery and request limits; schedule tests cover clock formats and DST. Browser
checks use a separate loopback-only test fixture with a demo account, not an auth
bypass in the application.

Navigation uses a shared server-side page registry and current authorization checks. New pages are registered in SiteNavigation; restricted routes must use the matching policy. The Administration page uses the existing Developer-only inspector, and every data endpoint remains independently protected. Menu results are uncached and refreshed on opening.

The Developer-only Administration page includes a paginated account directory with identities and preferences, plus a read-only browser for the ten application tables. Rows over 16 KiB are explicitly truncated. Scheduler internals and archive file contents remain outside this view. No arbitrary SQL or editing is offered.
