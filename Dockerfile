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
FROM mcr.microsoft.com/dotnet/sdk:10.0.102 AS build
ARG PROJECT
WORKDIR /src
COPY Integrios.slnx .
COPY global.json Directory.Build.props ./
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
# Every host is an ASP.NET application; the Worker serves only Prometheus scraping and binds this
# port through UseUrls, so EXPOSE documents the port each image actually listens on.
ARG HTTP_PORT=8080

WORKDIR /app
COPY --from=build /app .
RUN if [ -n "${MOUNT_ROOT}" ]; then mkdir -p "${MOUNT_ROOT}"; fi
ENV ASPNETCORE_HTTP_PORTS=${HTTP_PORT}
EXPOSE ${HTTP_PORT}
ENTRYPOINT ["/app/service"]
