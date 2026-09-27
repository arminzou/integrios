# Operator dashboard access

The Admin service serves the Integrios Operator dashboard from the same origin as the Admin API.
Every signed-in OperatorUser has deployment-wide control-plane authority. Configure OpenID Connect,
Integrios-managed email and password, or both; when both are enabled, the dashboard presents OIDC
first. With neither method enabled, Admin is machine-only: the dashboard still loads and says so,
but nobody can sign in until you configure a method and restart Admin.

Serve Admin over HTTPS outside local development. Its browser session cookie is secure-only, so a
browser will not retain a production session over plain HTTP. When a proxy terminates TLS in front
of Admin, set `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` on Admin so it builds `https` OpenID
Connect callbacks, and make sure that proxy is the only path to Admin.

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

Password sign-in is enabled by default. After database migration and runtime grants, provision the
first OperatorUser through the interactive Admin CLI connected to the deployment database (see
[Run the Operator CLI](#run-the-operator-cli)). The Azure reference runs this step inside its
deployment command instead:

```bash
operator-user bootstrap \
  --display-name "Deployment operator" \
  --email "operator@example.com"
```

On an empty deployment, the CLI prompts for the password twice using masked input. The User and
Password credential are created atomically. If any User already exists, including an OIDC User,
the command reports that setup is already complete and changes nothing. Repeating setup never
resets a password, changes an email, or re-enables a disabled credential.

To explicitly disable password login, set and apply:

```dotenv
INTEGRIOS_ADMIN_PASSWORD_ENABLED=false
```

```bash
docker compose up -d admin
```

Upgrades also use the enabled default when the setting was omitted. Set it to `false` explicitly
before upgrading an OIDC-only or API-only installation. Enabling the form grants nobody access
until a Password credential exists. If OIDC is also configured, the dashboard
shows **Continue with _provider_** before the email-and-password form.

For automated initial setup only, `operator-user bootstrap` accepts `--password-file <path>`
pointing to a protected UTF-8 secret file. Its contents are read exactly: do not append a newline.
Mount the file only into the setup process and remove it afterwards. Never supply the password
itself as an argument, environment variable, or ordinary configuration value. An existing User
makes bootstrap a no-op even when that file is absent. `operator-user bootstrap-status` reports
an `initialized` JSON boolean through the trusted CLI without exposing credentials.

Additional accounts use `operator-user create` with the same display-name and email arguments.
Ordinary credential-management commands retain masked interactive password confirmation and
refuse redirected password input. Configuring OIDC later does not automatically link the initial
password account by email; attaching a Password credential to an existing OIDC User uses User.Id.

## Run the Operator CLI

Every `operator-user` command runs in the Admin image or project, connected to the deployment
database with Admin's own database settings. Password commands prompt with masked input, so they
need an interactive terminal. How you reach one depends on the deployment:

| Deployment | Run a command |
| --- | --- |
| Docker Compose | `operator-user <command>` |
| Source checkout | `dotnet run --project src/Integrios.Admin -- operator-user <command>` |
| Azure Container Apps | `az containerapp exec -g <resource-group> -n <prefix>-admin --command "/bin/sh"`, then `/app/service operator-user <command>` in that shell |

On Azure Container Apps, the Admin app must have an active revision. Open a shell rather than
passing the command through `--command`: exec splits that value on spaces and ignores quotes, so an
argument such as a two-word display name breaks. Run all commands in one session, because Azure
limits how often exec sessions can be opened and answers with a 429 and a retry delay of several
minutes.

## Manage credentials

List Users and their password state:

```bash
operator-user list
```

To attach a Password credential to an OperatorUser first created through OIDC, copy the exact
`USER_ID` from that list. Email does not select or link a User:

```bash
operator-user set-password \
  --user-id <user-id> \
  --email "operator@example.com"
```

Reset an existing password, change its sign-in email, or disable it:

```bash
operator-user set-password --user-id <user-id>
operator-user change-email --user-id <user-id> --email "new@example.com"
operator-user disable-password --user-id <user-id>
```

`set-password --email` only attaches a first Password credential to a User that has none. For a
User that already has one, reset the password and change the email as two commands; the CLI
rejects the combination before asking for a password.

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

If sign-in fails, Admin's request log shows the cause. `POST /auth/password/login` answering 401
means an unknown sign-in email or a wrong password; check the stored email with `operator-user list`,
since it is not the display name. A 400 answered within milliseconds means the antiforgery check
rejected the request before credentials were read; reload the page and retry.

Disabling password sign-in retains stored credentials for later re-enablement and does not affect
OIDC sessions or OperatorKey automation. An OIDC outage is recoverable through password sign-in
only when a Password credential was provisioned and the method is enabled before or during the
outage.

Password attempts are limited per Admin replica to five per normalized email and twenty per
directly connected peer IP each minute. Multi-replica deployments multiply those limits; enforce a
distributed client-IP limit at a trusted ingress when that ceiling is required.

OperatorKey remains the machine credential for automation and out-of-band control-plane recovery.
Do not place it in a browser or share it as a human password.
