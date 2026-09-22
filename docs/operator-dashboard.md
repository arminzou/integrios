# Operator dashboard access

The Admin service serves the Integrios Operator dashboard from the same origin as the Admin API.
Every signed-in OperatorUser has deployment-wide control-plane authority. Configure OpenID Connect,
Integrios-managed email and password, or both; when both are enabled, the dashboard presents OIDC
first. With neither method enabled, Admin is machine-only: the dashboard still loads and says so,
but nobody can sign in until you configure a method and restart Admin.

Serve Admin over HTTPS outside local development. Its browser session cookie is secure-only, so a
browser will not retain a production session over plain HTTP.

Admin requires `Integrios:Admin:DataProtection:KeyRingPath` to name a writable directory shared by
every Admin replica. Its session cookies, antiforgery tokens, and pagination cursors remain valid
only while that key ring is retained. The supplied Compose and Azure references configure durable
shared storage; custom deployments must mount an equivalent access-restricted, storage-encrypted
directory.

## Choose a method

| Situation | Enable |
| --- | --- |
| No identity provider yet | Email and password |
| Existing Entra ID, Okta, Auth0, Keycloak, or Google workspace | OpenID Connect |
| Production | Both |

Enable both in production. Password sign-in is the only way back into the dashboard when the
provider is unreachable, and it must be provisioned before the outage.

## OpenID Connect

Register one OpenID Connect client with your provider. Use the Admin origin plus
`/auth/callback` as its redirect URI, for example:

```text
https://integrios.example.com/auth/callback
```

Set these Compose environment variables:

```dotenv
INTEGRIOS_ADMIN_OIDC_AUTHORITY=https://issuer.example.com/
INTEGRIOS_ADMIN_OIDC_CLIENT_ID=integrios-admin
INTEGRIOS_ADMIN_OIDC_CLIENT_SECRET=replace-me
INTEGRIOS_ADMIN_OIDC_DISPLAY_NAME=Company SSO
```

The authority must expose standard OIDC discovery metadata. Integrios requests `openid profile
email` by default and identifies a provider identity only by its exact issuer and subject. Email is
descriptive: equal email claims never link or merge Users. Any identity the provider successfully
authenticates becomes an OperatorUser on first sign-in, so restrict client assignment at the
provider.

These common authorities are configuration examples, not claims that each provider has been
live-verified against the current Integrios release:

| Provider | Authority example |
| --- | --- |
| Microsoft Entra ID | `https://login.microsoftonline.com/<tenant-id>/v2.0` |
| Auth0 | `https://<tenant>.auth0.com/` |
| Okta | `https://<organization>.okta.com/oauth2/default` |
| Keycloak | `https://<host>/realms/<realm>` |
| Google | `https://accounts.google.com` |

Provider consoles use different names for client assignment and redirect URIs. In each one, create
an OIDC web application, register the exact callback above, issue a client secret, and allow only
the people who should administer the deployment. Integrios currently configures one OIDC provider
per deployment.

The equivalent .NET configuration section is `Integrios:Admin:Oidc`. Advanced deployments may
override `CallbackPath`, `SignedOutCallbackPath`, `Scopes`, or `RequireHttpsMetadata`; keep metadata
HTTPS validation enabled outside local development.

## Email and password

Password sign-in is disabled by default. First create a credential through the interactive Admin
CLI connected to the deployment database:

```bash
docker compose run --rm admin operator-user create \
  --display-name "Deployment operator" \
  --email "operator@example.com"
```

The CLI prompts for the password twice without echoing it. It refuses redirected input and never
accepts a password in arguments, environment variables, or configuration.

Then set and apply:

```dotenv
INTEGRIOS_ADMIN_PASSWORD_ENABLED=true
```

```bash
docker compose up -d admin
```

Password sign-in can enable the dashboard by itself. If OIDC is also configured, the dashboard
shows **Continue with _provider_** before the email-and-password form.

To run the same commands from a source checkout, replace `docker compose run --rm admin` with:

```bash
dotnet run --project src/Integrios.Admin --
```

## Manage credentials

List Users and their password state:

```bash
docker compose run --rm admin operator-user list
```

To attach a Password credential to an OperatorUser first created through OIDC, copy the exact
`USER_ID` from that list. Email does not select or link a User:

```bash
docker compose run --rm admin operator-user set-password \
  --user-id <user-id> \
  --email "operator@example.com"
```

Reset an existing password, change its sign-in email, or disable it:

```bash
docker compose run --rm admin operator-user set-password --user-id <user-id>
docker compose run --rm admin operator-user change-email --user-id <user-id> --email "new@example.com"
docker compose run --rm admin operator-user disable-password --user-id <user-id>
```

A password reset or disablement immediately invalidates sessions issued from that credential.
Changing its sign-in email preserves existing sessions. Setting a new password on a disabled
credential re-enables it.

## Recover access

Integrios sends no recovery email and the dashboard has no **Forgot password** flow. Recovery
requires access to the Admin CLI and the configured database:

1. Run `operator-user list` to find the exact User ID and credential state.
2. Run `operator-user set-password --user-id <user-id>` to reset an existing credential, or include
   `--email` when that User has no Password credential yet.
3. If password sign-in was disabled deployment-wide, set
   `INTEGRIOS_ADMIN_PASSWORD_ENABLED=true` and restart Admin.

Disabling password sign-in retains stored credentials for later re-enablement and does not affect
OIDC sessions or OperatorKey automation. An OIDC outage is recoverable through password sign-in
only when a Password credential was provisioned and the method is enabled before or during the
outage.

Password attempts are limited per Admin replica to five per normalized email and twenty per
directly connected peer IP each minute. Multi-replica deployments multiply those limits; enforce a
distributed client-IP limit at a trusted ingress when that ceiling is required.

OperatorKey remains the machine credential for automation and out-of-band control-plane recovery.
Do not place it in a browser or share it as a human password.
