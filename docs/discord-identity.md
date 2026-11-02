# Discord identity and private data inspection

## Decision

Use one community-owned Discord application for website OAuth and, when needed,
the future bot. OAuth client credentials and a bot token are different credentials
with different purposes. A bot process is not required for website sign-in.
Do not register a second application merely to isolate features; use a separate
development registration only if needed to isolate credentials and callbacks.

The website owns its account IDs and permissions. Verified Discord IDs identify
external accounts; display names never identify or authorize users. First login
creates and links a GC account atomically; repeat login resolves that account.
This is not a migration/link to an existing old-site account. No accounts are
merged by username or email, and a different Discord account means a different
GC account. Additional providers require a separate ownership-proving flow.

The community Discord server's **Developer role** grants private inspector access.
Configure `Discord:GuildId`, `Discord:DeveloperRoleId`, and `Discord:BotToken`.
These IDs identify the server and role, never a list of privileged people. The
legacy `Auth:AdminDiscordIds` setting is ignored and may be removed from old
configuration. There is no automatic Admin-role, first-user or development bypass.

Every privileged request resolves the website account's verified Discord link,
then calls Discord's HTTPS Get Guild Member endpoint using the server-held bot
credential. Access requires that member's role-ID array to contain the configured
Developer role. Names are display labels, not identity: renaming a role preserves
access; deleting/recreating it requires configuring its new ID.

No positive grants are cached in cookies or the database. Role removal takes effect
on the next successfully checked request. Missing configuration, missing membership,
invalid responses, timeouts, rate limits and provider errors deny privileged access.
A 429 pauses lookups according to Retry-After (bounded to 1–3600 seconds, default 30).
Ordinary sign-in and account access remain independent of role lookup availability.
The existing internal `administrator` policy and `isAdmin` response field now mean
Developer-role access; they do not imply the Discord Administrator permission.

The website makes the REST lookup directly. The bot must be a member of the intended
guild, but no bot process, Gateway connection, webhook or bot rewrite is required.
Reuse the community-owned application where appropriate; do not reset a live bot's
token merely to configure this site. Store its token server-side alongside the OAuth
secret, never in JavaScript, cookies, logs or Git. The code only reads membership.
Do not grant Administrator or Manage Roles just for this read operation.

Basic login still requests only `identify` and does not retain user provider tokens.
Using `guilds.members.read` instead would require securely retaining and refreshing
per-user OAuth tokens for subsequent permission checks; a community bot credential
keeps this slice smaller and also fits the planned bot integration. A shared cached
membership service can follow when traffic justifies it, with explicit freshness
and revocation behavior. No long-lived stale-role fallback is implemented.

References: [Discord OAuth](https://docs.discord.com/developers/topics/oauth2),
[Get Guild Member](https://docs.discord.com/developers/resources/guild#get-guild-member),
[rate limits](https://docs.discord.com/developers/topics/rate-limits).

## Review of the old website

Read-only review of `Global-Conflicts-ArmA/global-conflicts-website` at
`f42986451fab1e4b88abc6f8bc7819452cb24f3b`. These findings describe repository
code, not a penetration test or confirmation of deployed configuration. No code
was copied into this implementation.

- [Authentication](https://github.com/Global-Conflicts-ArmA/global-conflicts-website/blob/f42986451fab1e4b88abc6f8bc7819452cb24f3b/pages/api/auth/%5B...nextauth%5D.ts):
  NextAuth Discord provider and MongoDB adapter establish persistent website
  identity. The profile/sign-in callbacks also query a bot `/users/{id}` endpoint
  for membership, nickname and roles. Bot failure prevents production login and
  is presented as a membership problem. Keep persistent provider identity;
  avoid conflating failed provider lookup with confirmed non-membership.
- The session callback refresh interval is 24 hours (despite a one-hour comment).
  Refresh failures preserve cached roles. The fallback bot URL is HTTP; actual
  deployment may override it. Privileged access needs an explicit freshness
  limit and authenticated service boundary, not an indefinitely stale role cache.
- That file includes a development branch substituting a fixed database user's
  identity. Do not carry this forward; automated tests use an isolated fake
  provider, never a bypass in the shipped website.
- [Permission middleware](https://github.com/Global-Conflicts-ArmA/global-conflicts-website/blob/f42986451fab1e4b88abc6f8bc7819452cb24f3b/middleware/check_auth_perms.ts):
  backend checks supplement UI checks, which is useful. Authority is expressed
  through human-readable role names and broad Admin override. Prefer stable
  provider IDs mapped to explicit website permissions. The list helper also
  uses JavaScript `in` for the ANY membership check, rather than value inclusion.
- [Profile](https://github.com/Global-Conflicts-ArmA/global-conflicts-website/blob/f42986451fab1e4b88abc6f8bc7819452cb24f3b/pages/user/index.tsx):
  presents Discord identity and separately linked Steam identity, a useful UX.
  The Steam callback verifies the provider through its helper and then writes
  the link to the currently signed-in user. We would additionally require an
  explicit, expiring link intent bound to that GC session and uniqueness rules.
- [Steam unlink](https://github.com/Global-Conflicts-ArmA/global-conflicts-website/blob/f42986451fab1e4b88abc6f8bc7819452cb24f3b/pages/api/steam/unlink/index.ts)
  mutates identity from a navigable link without an explicit method/anti-forgery
  check in the handler. New identity mutations must use protected POST actions.
- [Player mappings](https://github.com/Global-Conflicts-ArmA/global-conflicts-website/blob/f42986451fab1e4b88abc6f8bc7819452cb24f3b/pages/api/player-mappings.ts)
  allows staff-managed Discord/platform associations. These are administrative
  mappings, not equivalent to a user proving ownership of a second account.

## What ships in this slice

- `/account.html`: Discord sign-in, linked Discord identity, GC account ID and
  anti-forgery-protected sign-out. Admin inspector link appears only for admins.
- `/admin`: protected HTML outside the public static directory. Its read-only
  `/api/admin/*` endpoints independently enforce the administrator policy.
- Inspector: stream/status totals, latest 50 receipts, telemetry summary and
  bounded raw event windows (60 seconds, 100 records/page, existing chunk limits).
  Nothing is editable. No arbitrary SQL, filesystem paths, secrets or raw log
  viewer is exposed. Responses do not cache account/admin data.
- The inspector reads the **website's GC database**, not the old local `lobby2`
  proof database. It can legitimately be empty until data is sent to this host.
  All four streams now have projectors; see [mission pipeline](mission-pipeline.md).
- Eight-hour, secure HTTP-only cookie sessions; OAuth state/correlation and PKCE;
  no saved user OAuth tokens. Logout clears the browser cookie, not all copies of a
  stolen session. Global session revocation is future work; admin grant removal
  independently denies privileged reads.

## Local configuration

1. In the community-owned [Discord Developer Portal](https://discord.com/developers/applications),
   use the intended application and register this exact OAuth redirect:
   `https://localhost:5081/auth/discord/callback`. Website sign-in needs the
   OAuth client ID and client secret. Role checks additionally need the bot token.
   Do not change the old
   website's callback or reset its secret as part of this setup.
2. Use the dedicated GC database described in the main README, apply its migrations
   with the schema owner, and use the restricted web runtime connection afterward.
   Do not run website migrations against the existing Lobby2 proof database.
3. Edit the private root file `appsettings.Local.json`. Copy `appsettings.Local.example.json` if missing. Each field explains where to find its value. The file is ignored by Git, copied into local build output, and excluded from published builds. It is plaintext: keep it private. Restart after editing. Environment/command-line settings override it. No prompt script or personal user-ID list is used.
4. Run `dotnet dev-certs https --trust` once, then start with
   `dotnet run --project GC.Web.csproj --no-launch-profile -- --urls https://localhost:5081`
   with `ASPNETCORE_ENVIRONMENT=Development`. Open
   `https://localhost:5081/account.html` and complete Discord's consent yourself.
   The ordinary HTTP preview stays useful for the landing page but cannot sign in.
5. Production uses service-owned configuration/secrets and a real HTTPS origin.
   Add the exact production callback in Discord and set `AllowedHosts` accordingly.
   Trust forwarded headers only from a configured reverse proxy; key storage
   must remain outside the web root and readable only by the service identity.

Assign or remove the Developer role in Discord to manage inspector access.
Do not post OAuth secrets, bot tokens or database passwords in chat or commit them.
OAuth setup and live consent remain necessary
before claiming that a real community account has successfully signed in.

## Validation

The backend suite runs the real ASP.NET OAuth and cookie handlers against a fake
Discord HTTP backchannel in an isolated test host/database. It proves state
rejection, PKCE exchange, stable identity on repeated login, secure cookies,
anonymous/member/admin boundaries, role/link removal, ignored legacy user grants, provider failures and rate limits, and CSRF logout.
The fake provider is test-only; shipped endpoints cannot choose provider URLs
or impersonate an identity. Real Discord consent requires operator configuration.
