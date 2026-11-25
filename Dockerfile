# Base image tags stay literal rather than build arguments: the release evidence step resolves
# external image digests by reading FROM lines out of this file and skips anything containing a
# variable reference, so parameterizing them would silently drop both bases from that record.
FROM node:22.23.5-bookworm-slim AS node
FROM mcr.microsoft.com/dotnet/sdk:10.0.404 AS build
WORKDIR /src
COPY global.json Directory.Build.props VERSION ./
COPY src/ src/
RUN dotnet restore src/Integrios/Integrios.csproj --locked-mode
RUN dotnet publish src/Integrios/Integrios.csproj \
    -c Release -o /app --no-self-contained --no-restore

# The dashboard is built where both toolchains are present, so the one npm script the repository
# already uses runs unchanged: it generates the typed client from the Admin OpenAPI document, which
# only the .NET build can emit. Copying Node in rather than shelling out to a package manager keeps
# its version pinned by the image tag above, and CI reads that same tag so the two cannot drift.
FROM build AS dashboard
COPY --from=node /usr/local/bin/node /usr/local/bin/node
COPY --from=node /usr/local/lib/node_modules /usr/local/lib/node_modules
RUN ln -s /usr/local/lib/node_modules/npm/bin/npm-cli.js /usr/local/bin/npm
WORKDIR /src/src/Integrios.Admin/frontend
RUN npm ci
# Assets only: generate the typed client, type-check against it, and bundle. The frontend checks
# run in CI, which is also where the browser they need is installed -- an image build has no reason
# to carry test infrastructure, and would fail for want of it.
RUN npm run build:assets && mv /src/src/Integrios.Admin/wwwroot /dashboard

FROM mcr.microsoft.com/dotnet/aspnet:10.0.15
# Npgsql probes for Kerberos/GSSAPI on connect; the slim base image omits it.
RUN apt-get update \
    && apt-get install -y --no-install-recommends libgssapi-krb5-2 \
    && rm -rf /var/lib/apt/lists/*

ARG HTTP_PORT=8080

WORKDIR /app
COPY --from=build /app .
COPY --from=dashboard /dashboard ./wwwroot
# Empty key-per-file configuration root. Ingestion and Worker read it at startup when an operator
# mounts one process's own directory over it; Admin never reads it. Admin's key ring directory is
# owned by the app user so a fresh named volume mounted over it inherits that ownership.
RUN mkdir -p /run/secrets/integrios /var/lib/integrios/data-protection \
    && chown $APP_UID:$APP_UID /run/secrets/integrios /var/lib/integrios/data-protection
USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=${HTTP_PORT} \
    OperationalPort=5299
EXPOSE ${HTTP_PORT} 5299
# Exec form, so the role is PID 1 and receives SIGTERM: the Worker drains in-flight deliveries and
# releases their ownership before it exits.
ENTRYPOINT ["/app/integrios"]
