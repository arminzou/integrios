# One file, one image per service.
#
# Each service keeps its own image on purpose: the image boundary is where host capability
# isolation stops being a composition detail and becomes a property of the artifact. Only the
# selected host is published into the runtime stage, so code a process must not run is absent
# rather than merely unconfigured. A single multi-entrypoint image would put every host's
# binaries in every container and reduce that guarantee to configuration.
#
# What must not be duplicated is the base images and the runtime workaround below, which drifted
# across four near-identical files. PROJECT selects the host; everything else is shared.

# Base image tags stay literal rather than build arguments: the release evidence step resolves
# external image digests by reading FROM lines out of this file and skips anything containing a
# variable reference, so parameterizing them would silently drop both bases from that record.
# Selects whether this image builds and carries the Operator dashboard. It is a separate argument
# from PROJECT because a stage name interpolated into FROM must be lowercase, and it is global
# because only a global argument is visible there. It defaults to none, so a build that forgets to
# ask for the dashboard produces an image without one rather than one that half has it; the
# packaged Acceptance run is what proves Admin actually got it.
ARG DASHBOARD=none

FROM node:22.22.0-bookworm-slim AS node
FROM mcr.microsoft.com/dotnet/sdk:10.0.102 AS build
ARG PROJECT
WORKDIR /src
COPY Integrios.slnx .
COPY global.json Directory.Build.props VERSION ./
COPY src/ src/
RUN dotnet restore "src/${PROJECT}/${PROJECT}.csproj" --locked-mode
RUN dotnet publish "src/${PROJECT}/${PROJECT}.csproj" \
    -c Release -o /app --no-self-contained --no-restore
# Exec-form ENTRYPOINT does not expand build arguments, and a shell-form entrypoint would leave
# the shell as PID 1 and swallow SIGTERM, breaking the Worker's drain-before-exit and delivery
# ownership release. A fixed-name symlink to the published apphost keeps the entrypoint exec-form,
# so the service is PID 1 and still receives any command arguments (`bootstrap`, secret
# validation) appended by Compose.
RUN ln -s "/app/${PROJECT}" /app/service

# The dashboard is built where both toolchains are present, so the one npm script the repository
# already uses runs unchanged: it generates the typed client from the Admin OpenAPI document, which
# only the .NET build can emit. Copying Node in rather than shelling out to a package manager keeps
# its version pinned by the image tag above, and CI reads that same tag so the two cannot drift.
FROM build AS dashboard-build
COPY --from=node /usr/local/bin/node /usr/local/bin/node
COPY --from=node /usr/local/lib/node_modules /usr/local/lib/node_modules
RUN ln -s /usr/local/lib/node_modules/npm/bin/npm-cli.js /usr/local/bin/npm
WORKDIR /src/src/Integrios.Admin/frontend
RUN npm ci
# Assets only: generate the typed client, type-check against it, and bundle. The frontend checks
# run in CI, which is also where the browser they need is installed -- an image build has no reason
# to carry test infrastructure, and would fail for want of it.
RUN npm run build:assets && mv /src/src/Integrios.Admin/wwwroot /dashboard

# Only the Admin image builds or carries dashboard assets. BuildKit builds a stage only when the
# selected target depends on it, so the aliases below mean the Node stage above is never built for
# Ingestion or Worker -- those hosts stay independent of Node, and their `/dashboard` is empty.
FROM mcr.microsoft.com/dotnet/aspnet:10.0.10 AS dashboard-none
RUN mkdir -p /dashboard
FROM dashboard-build AS dashboard-admin
FROM dashboard-${DASHBOARD} AS dashboard

FROM mcr.microsoft.com/dotnet/aspnet:10.0.10
# Npgsql probes for Kerberos/GSSAPI on connect; the slim base image omits it.
RUN apt-get update \
    && apt-get install -y --no-install-recommends libgssapi-krb5-2 \
    && rm -rf /var/lib/apt/lists/*

# Directory an operator mounts credential material into, empty when the service resolves none.
# Admin validates references without resolving them, and the Ingestion and Worker boundaries stay
# separate, so each image creates only the mount root its own host reads. Named without the word
# "secret" so the BuildKit SecretsUsedInArgOrEnv check does not flag this path as a credential.
ARG MOUNT_ROOT=
# Every host is an ASP.NET application. Admin and Ingestion use HTTP_PORT for product traffic;
# all three hosts use 5299 for probes and Prometheus scraping.
ARG HTTP_PORT=8080

WORKDIR /app
COPY --from=build /app .
# Admin serves the dashboard from its own static root; the other hosts receive an empty directory
# and so have no shell to serve. Admin's own command-line verbs never read it either -- they branch
# before the web host is constructed.
COPY --from=dashboard /dashboard ./wwwroot
RUN if [ -n "${MOUNT_ROOT}" ]; then mkdir -p "${MOUNT_ROOT}"; fi
ENV ASPNETCORE_HTTP_PORTS=${HTTP_PORT}
EXPOSE ${HTTP_PORT} 5299
ENTRYPOINT ["/app/service"]
