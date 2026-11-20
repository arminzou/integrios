#!/usr/bin/env bash
# Proves the Integrios.Client SDK against this run's subscriber: seeds a pull Destination,
# builds a generic-host consumer from src/Integrios.Client, accepts one Event per handler
# outcome, and checks each delivery's recorded result through the Admin API.
# Requires a run from control.sh up and its env (source artifacts/verify/<run>/state/env).
set -euo pipefail

: "${DELIVERY_API:?source the run env first}"
V=$(dirname "$0")
ROOT=$(git rev-parse --show-toplevel)
eval "$("$V/seed-pull.sh")"
E="$EVIDENCE/pull-delivery"; mkdir -p "$E"
C="$EVIDENCE/../subscriber"; mkdir -p "$C"

cat >"$C/Subscriber.csproj" <<XML
<Project Sdk="Microsoft.NET.Sdk.Worker">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <RestorePackagesWithLockFile>false</RestorePackagesWithLockFile>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.Hosting" Version="10.0.15" />
    <ProjectReference Include="$ROOT/src/Integrios.Client/Integrios.Client.csproj" />
  </ItemGroup>
</Project>
XML
cat >"$C/Program.cs" <<'CS'
using Integrios.Client;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddIntegriosSubscriber<OrderHandler>(options =>
{
    options.BaseAddress = new Uri(Environment.GetEnvironmentVariable("DELIVERY_API")!);
    options.SubscriberKey = Environment.GetEnvironmentVariable("SUBSCRIBER_KEY")!;
    options.Concurrency = 2;
});
builder.Build().Run();

sealed class OrderHandler(ILogger<OrderHandler> logger) : IDeliveryHandler
{
    public async Task<DeliveryResult> HandleAsync(Delivery delivery, CancellationToken cancellationToken)
    {
        string outcome = delivery.Payload.GetProperty("outcome").GetString()!;
        logger.LogInformation("HANDLED {EventId} attempt {Attempt} outcome {Outcome}", delivery.EventId, delivery.AttemptNumber, outcome);
        switch (outcome)
        {
            case "slow": await Task.Delay(TimeSpan.FromSeconds(45), cancellationToken); return DeliveryResult.Ack;
            case "retry": return delivery.AttemptNumber == 1 ? DeliveryResult.Retry(TimeSpan.FromSeconds(2), "not yet") : DeliveryResult.Ack;
            case "reject": throw new PermanentDeliveryFailureException("order rejected");
            case "throw": throw new InvalidOperationException("transient boom");
            default: return DeliveryResult.Ack;
        }
    }
}
CS
dotnet build "$C" -nologo -v q >"$E/build.log"

DELIVERY_API=$DELIVERY_API SUBSCRIBER_KEY=$SUBSCRIBER_KEY dotnet run --no-build --project "$C" >"$E/subscriber.log" 2>&1 &
pid=$!
trap 'kill "$pid" 2>/dev/null || true' EXIT
declare -A events
for outcome in ack retry reject throw slow; do
  events[$outcome]=$(curl -fsS --max-time 10 -X POST "$INGESTION/events?source_id=$SOURCE" -H "Authorization: Bearer $TOKEN" \
    -H 'Content-Type: application/json' \
    -d "{\"event_type\":\"payment.created\",\"source_event_id\":\"sdk-$outcome-$SLUG\",\"payload\":{\"outcome\":\"$outcome\"}}" | jq -er .event_id)
done

pulled() {
  curl -fsS --max-time 10 "$ADMIN/admin/tenants/$TENANT/events/${events[$1]}/deliveries" -H "$AUTH" | tee "$E/$1.json" |
    jq -c --arg d "$PULL_DESTINATION" '[.event_deliveries[] | select(.destination_id == $d)][0]'
}
expect() {
  local i
  for i in {1..90}; do
    pulled "$1" | jq -e "$2" >/dev/null && { echo "ok   $1: $3" | tee -a "$E/result.txt"; return; }
    sleep 1
  done
  echo "FAIL $1: $3; last: $(pulled "$1")" | tee -a "$E/result.txt"
  exit 1
}
expect ack '.status == "succeeded" and .lifetime_attempt_count == 1' "Ack succeeds on the first attempt"
expect retry '.status == "succeeded" and .lifetime_attempt_count == 2' "Retry(2s) is claimed again and then acked"
expect reject '.status == "dead_lettered"' "PermanentDeliveryFailureException dead-letters"
expect throw '.lifetime_attempt_count >= 1 and .status != "succeeded"' "another exception nacks under platform backoff"
expect slow '.status == "succeeded"' "a handler past a third of the lease renews and then acks"
grep -q 'POST .*/renew' "$E/subscriber.log" || { echo "FAIL slow: no renew request in subscriber.log" | tee -a "$E/result.txt"; exit 1; }
echo "PASS evidence in $E" | tee -a "$E/result.txt"
