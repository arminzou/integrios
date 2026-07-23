FROM mcr.microsoft.com/dotnet/sdk:10.0.102 AS build
WORKDIR /src
COPY Integrios.slnx .
COPY global.json Directory.Build.props ./
COPY src/ src/
RUN dotnet restore src/Integrios.MockSink/Integrios.MockSink.csproj --locked-mode
RUN dotnet publish src/Integrios.MockSink/Integrios.MockSink.csproj \
    -c Release -o /app --no-self-contained --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0.10
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Integrios.MockSink.dll"]
