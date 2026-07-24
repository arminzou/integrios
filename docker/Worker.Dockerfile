FROM mcr.microsoft.com/dotnet/sdk:10.0.102 AS build
WORKDIR /src
COPY Integrios.slnx .
COPY global.json Directory.Build.props ./
COPY src/ src/
RUN dotnet restore src/Integrios.Worker/Integrios.Worker.csproj --locked-mode
RUN dotnet publish src/Integrios.Worker/Integrios.Worker.csproj \
    -c Release -o /app --no-self-contained --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0.10
# Npgsql probes for Kerberos/GSSAPI on connect; the slim base image omits it.
RUN apt-get update \
    && apt-get install -y --no-install-recommends libgssapi-krb5-2 \
    && mkdir -p /run/secrets/integrios \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "Integrios.Worker.dll"]
