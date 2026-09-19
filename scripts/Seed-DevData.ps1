#!/usr/bin/env pwsh
#Requires -Version 7.0
<#
.SYNOPSIS
    Replaces the local dev database's tenant-scoped data with the demo dashboard dataset.

.DESCRIPTION
    The PowerShell twin of scripts/seed-dev-data.sh. Everything is created through the public Admin
    and Ingestion APIs -- no direct inserts -- so the resulting rows are exactly what the platform
    itself would have written. Only the wipe touches SQL.

    Requires the Compose stack (`make up`) and docker compose. The Service Bus queue demo is opt-in
    via -QueueDemo (or INTEGRIOS_SEED_QUEUE_DEMO=1) and additionally needs the .NET SDK.

.EXAMPLE
    ./scripts/Seed-DevData.ps1
.EXAMPLE
    ./scripts/Seed-DevData.ps1 -QueueDemo
#>
[CmdletBinding()]
param(
    [switch]$QueueDemo,
    # 127.0.0.1, not localhost: .NET tries ::1 first without racing IPv4, and Docker Desktop's
    # published ports leave that attempt hanging ~21s before the fallback, on every request.
    [string]$Admin = 'http://127.0.0.1:5150',
    [string]$Ingestion = 'http://127.0.0.1:5231',
    [string]$MockSink = 'http://127.0.0.1:5054'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$script:repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $script:repoRoot

$operatorKey = if ($env:INTEGRIOS_OPERATOR_KEY) { $env:INTEGRIOS_OPERATOR_KEY } else { 'global_operator_key:operator_bootstrap_secret' }
$script:auth = @{ Authorization = "OperatorKey $operatorKey" }

$serviceBusHttp = if ($env:INTEGRIOS_SERVICEBUS_HTTP_PORT) { "http://127.0.0.1:$($env:INTEGRIOS_SERVICEBUS_HTTP_PORT)" } else { 'http://127.0.0.1:5300' }
$serviceBusPort = if ($env:INTEGRIOS_SERVICEBUS_PORT) { $env:INTEGRIOS_SERVICEBUS_PORT } else { '5672' }
$serviceBusQueue = 'ui-demo'
$serviceBusSecret = 'dev_service_bus'
$serviceBusKey = 'SAS_KEY_VALUE'
$serviceBusContainerConnection = "Endpoint=sb://servicebus-emulator;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=$serviceBusKey;UseDevelopmentEmulator=true;"
$serviceBusHostConnection = "Endpoint=sb://localhost:$serviceBusPort;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=$serviceBusKey;UseDevelopmentEmulator=true;"

# The Service Bus queue demo is opt-in, mirroring INTEGRIOS_SEED_QUEUE_DEMO in the bash script.
$includeQueueDemo = [bool]($QueueDemo.IsPresent -or ($env:INTEGRIOS_SEED_QUEUE_DEMO -and $env:INTEGRIOS_SEED_QUEUE_DEMO -ne '0'))

function Say([string]$Message) { Write-Host "`n=== $Message" }

function Invoke-Json {
    param(
        [Parameter(Mandatory)][string]$Method,
        [Parameter(Mandatory)][string]$Uri,
        [hashtable]$Headers,
        [AllowNull()]$Body
    )
    $params = @{ Method = $Method; Uri = $Uri }
    if ($Headers) { $params.Headers = $Headers }
    if ($null -ne $Body) {
        $params.ContentType = 'application/json'
        $params.Body = $Body | ConvertTo-Json -Depth 20 -Compress
    }
    Invoke-RestMethod @params
}

function Invoke-Admin {
    param(
        [Parameter(Mandatory)][string]$Method,
        [Parameter(Mandatory)][string]$Path,
        [AllowNull()]$Body
    )
    Invoke-Json -Method $Method -Uri "$Admin$Path" -Headers $script:auth -Body $Body
}

function Get-SqlScalar {
    param([Parameter(Mandatory)][string]$Sql)
    $out = docker compose exec -T postgres psql -qtAX -U integrios -d integrios -c $Sql
    ($out | Out-String).Trim()
}

function Show-Sql {
    param([Parameter(Mandatory)][string]$Sql)
    docker compose exec -T postgres psql -U integrios -d integrios -c $Sql
}

function New-Tenant {
    param([string]$Slug, [string]$Name, [string]$Environment, [string]$Description)
    (Invoke-Admin -Method Post -Path '/admin/tenants' -Body @{
        slug = $Slug; name = $Name; environment = $Environment; description = $Description
    }).id
}

function New-Destination {
    param([string]$Tenant, [string]$Name, [string]$BaseUri, [string]$Environment, [string]$Description)
    (Invoke-Admin -Method Post -Path "/admin/tenants/$Tenant/destinations" -Body @{
        connector_id    = $script:connector
        name            = $Name
        configuration   = @{ base_uri = $BaseUri }
        authentication  = $null
        environment     = $Environment
        description     = $Description
    }).id
}

function New-Topic {
    param([string]$Tenant, [string]$Key, [string]$Name, [string]$Description)
    (Invoke-Admin -Method Post -Path "/admin/tenants/$Tenant/topics" -Body @{
        key = $Key; name = $Name; description = $Description
    }).id
}

function New-Source {
    param(
        [string]$Tenant,
        [string]$Connector,
        [string]$Topic,
        [string]$Name,
        [string[]]$EventTypes,
        [string]$Type = 'event_api',
        [AllowNull()]$Configuration = $null
    )
    # Sources are authored Inactive; the demo activates each so traffic flows.
    if ($null -eq $Configuration) { $Configuration = @{ source_contract = 'event_json' } }
    $id = (Invoke-Admin -Method Post -Path "/admin/tenants/$Tenant/sources" -Body @{
        connector_id        = $Connector
        topic_id            = $Topic
        name                = $Name
        type                = $Type
        event_types         = $EventTypes
        configuration       = $Configuration
        verification        = $null
        input_requirements  = $null
        mapping             = $null
        event_identity_rule = $null
    }).id
    Invoke-Admin -Method Post -Path "/admin/tenants/$Tenant/sources/$id/activate" | Out-Null
    $id
}

function New-Subscription {
    param(
        [string]$Tenant,
        [string]$Topic,
        [string]$Name,
        [string]$EventType,
        [string]$Destination,
        [int]$OrderIndex,
        [string]$Description,
        [AllowNull()]$Mapping = $null
    )
    # Subscriptions are authored Inactive; the demo activates each so fanout routes to it.
    $id = (Invoke-Admin -Method Post -Path "/admin/tenants/$Tenant/topics/$Topic/subscriptions" -Body @{
        name          = $Name
        event_types   = @($EventType)
        destination_id = $Destination
        mapping       = $Mapping
        http_delivery = $null
        http_success  = $null
        order_index   = $OrderIndex
        description   = $Description
    }).id
    Invoke-Admin -Method Post -Path "/admin/tenants/$Tenant/topics/$Topic/subscriptions/$id/activate" | Out-Null
    $id
}

function New-Key {
    param([string]$Tenant, [string]$Name, [string]$Description)
    (Invoke-Admin -Method Post -Path "/admin/tenants/$Tenant/tenant-api-keys" -Body @{
        name = $Name; description = $Description; expires_at = $null
    }).token
}

function Send-Event {
    param([string]$Source, [string]$Token, [string]$EventType, [string]$SourceEventId, $Payload)
    $response = Invoke-Json -Method Post -Uri "$Ingestion/events?source_id=$Source" `
        -Headers @{ Authorization = "Bearer $Token" } `
        -Body @{ event_type = $EventType; source_event_id = $SourceEventId; payload = $Payload }
    $response.event_id
}

function Send-Webhook {
    param([string]$CallbackId, [string]$EventType, [string]$SourceEventId, $Payload)
    $response = Invoke-Json -Method Post -Uri "$Ingestion/webhooks/$CallbackId" `
        -Headers @{} `
        -Body @{ event_type = $EventType; source_event_id = $SourceEventId; payload = $Payload }
    $response.event_id
}

# Start the optional Service Bus emulator before the destructive wipe so a broker startup failure
# leaves the existing dev dataset untouched.
if ($includeQueueDemo) {
    Say 'starting the Service Bus queue demo'
    docker compose --profile queue-demo up -d servicebus-emulator
    $healthy = $false
    for ($i = 0; $i -lt 30; $i++) {
        try {
            Invoke-WebRequest -Uri "$serviceBusHttp/health" | Out-Null
            $healthy = $true
            break
        }
        catch {
            Start-Sleep -Seconds 2
        }
    }
    if (-not $healthy) { throw 'Service Bus emulator did not become healthy.' }
}

# --- 1. Wipe tenant-scoped data --------------------------------------------------------------
# Connectors and OperatorKeys are deployment-level and survive: they are what bootstrap and the
# manifest apply own, not demo data.
Say 'clearing existing tenant data'
Get-SqlScalar 'truncate table delivery_attempts, event_deliveries, outbox, events, subscriptions, sources, topics, destinations, tenant_api_keys, tenants cascade;' | Out-Null
Invoke-RestMethod -Method Delete -Uri "$MockSink/__admin/requests" | Out-Null
Invoke-RestMethod -Method Post -Uri "$MockSink/__admin/mappings/reset" | Out-Null

# --- 2. Connector -----------------------------------------------------------------------------
Say 'applying the http Connector manifest'
$manifest = Get-Content -LiteralPath (Join-Path $script:repoRoot 'examples/connectors/http.json') -Raw
$script:connector = (Invoke-RestMethod -Method Put -Uri "$Admin/admin/connectors/http/versions/1" `
    -Headers $script:auth -ContentType 'application/json' -Body $manifest).connector.id

# --- 3. Northwind Retail: the fully-configured Tenant ------------------------------------------
Say 'Northwind Retail'
$nw = New-Tenant -Slug 'northwind-retail' -Name 'Northwind Retail' -Environment 'production' `
    -Description 'Retail commerce platform. Orders, payments, and stock levels flow out to the ERP, the warehouse, and the analytics lake.'
$nwToken = New-Key -Tenant $nw -Name 'northwind-storefront' -Description 'Storefront checkout service. Rotated quarterly.'
$nwOldKey = (Invoke-Admin -Method Post -Path "/admin/tenants/$nw/tenant-api-keys" -Body @{
    name = 'northwind-legacy-pos'; description = 'Retired point-of-sale integration.'; expires_at = $null
}).tenant_api_key.id
Invoke-Admin -Method Post -Path "/admin/tenants/$nw/tenant-api-keys/$nwOldKey/revoke" | Out-Null

$nwErp = New-Destination -Tenant $nw -Name 'northwind-erp' -BaseUri 'http://mocksink:8080/sink/northwind-erp' -Environment 'production' `
    -Description 'Order and payment records into the ERP.'
$nwWms = New-Destination -Tenant $nw -Name 'northwind-wms' -BaseUri 'http://mocksink:8080/sink/northwind-wms' -Environment 'production' `
    -Description 'Warehouse management system, fulfilment side only.'
$nwLake = New-Destination -Tenant $nw -Name 'analytics-lake' -BaseUri 'http://mocksink:8080/sink/northwind-lake' -Environment 'production' `
    -Description 'Flattened order feed for the analytics lake.'
$nwBilling = New-Destination -Tenant $nw -Name 'legacy-billing' -BaseUri 'http://mocksink:8080/sink/northwind-billing' -Environment 'production' `
    -Description 'Decommissioned billing host. Kept until finance signs off on the cutover.'
$nwSandbox = New-Destination -Tenant $nw -Name 'erp-sandbox' -BaseUri 'http://mocksink:8080/sink/northwind-sandbox' -Environment 'staging' `
    -Description 'Vendor sandbox used during the last ERP upgrade.'
Invoke-Admin -Method Post -Path "/admin/tenants/$nw/destinations/$nwSandbox/deactivate" | Out-Null

$nwOrders = New-Topic -Tenant $nw -Key 'orders' -Name 'orders' -Description 'Order lifecycle from the storefront.'
$nwPay = New-Topic -Tenant $nw -Key 'payments' -Name 'payments' -Description 'Payment authorisation and capture.'
$nwStock = New-Topic -Tenant $nw -Key 'inventory' -Name 'inventory' -Description 'Stock level movements per warehouse.'
$nwWebhooks = New-Topic -Tenant $nw -Key 'storefront-webhooks' -Name 'storefront-webhooks' -Description 'Provider callbacks received from the storefront platform.'
$nwOld = New-Topic -Tenant $nw -Key 'pos-terminals' -Name 'pos-terminals' -Description 'Retired in-store terminal stream.'
if ($includeQueueDemo) {
    $nwQueue = New-Topic -Tenant $nw -Key 'warehouse-receipts' -Name 'warehouse-receipts' -Description 'Warehouse receipts consumed from the operations queue.'
}

$nwOrdersSrc = New-Source -Tenant $nw -Connector $script:connector -Topic $nwOrders -Name 'Northwind orders' -EventTypes @('order.placed', 'order.shipped', 'order.cancelled')
$nwPaySrc = New-Source -Tenant $nw -Connector $script:connector -Topic $nwPay -Name 'Northwind payments' -EventTypes @('payment.captured')
$nwStockSrc = New-Source -Tenant $nw -Connector $script:connector -Topic $nwStock -Name 'Northwind inventory' -EventTypes @('stock.adjusted')
$nwWebhookSrc = New-Source -Tenant $nw -Connector $script:connector -Topic $nwWebhooks -Name 'Northwind storefront webhooks' -EventTypes @('storefront.webhook.received') -Type 'webhook'
$nwWebhookCallback = (Invoke-Admin -Method Get -Path "/admin/tenants/$nw/sources/$nwWebhookSrc").configuration.callback_id
if ($includeQueueDemo) {
    $secretDir = Join-Path $script:repoRoot 'secrets/source/northwind-retail'
    New-Item -ItemType Directory -Force -Path $secretDir | Out-Null
    Set-Content -LiteralPath (Join-Path $secretDir $serviceBusSecret) -Value $serviceBusContainerConnection -NoNewline
    $nwQueueSrc = New-Source -Tenant $nw -Connector $script:connector -Topic $nwQueue -Name 'Northwind warehouse receipts' `
        -EventTypes @('warehouse.receipt.recorded') -Type 'broker' -Configuration @{
            source_contract  = 'event_json'
            transport        = 'azure_service_bus'
            authentication   = @{ scheme = 'connection_string'; secret_ref = $serviceBusSecret }
            transport_config = @{ namespace = 'servicebus-emulator'; queue_name = $serviceBusQueue }
        }
}

New-Subscription -Tenant $nw -Topic $nwOrders -Name 'erp-orders' -EventType 'order.placed' -Destination $nwErp -OrderIndex 0 `
    -Description 'Every placed order into the ERP.' | Out-Null
New-Subscription -Tenant $nw -Topic $nwOrders -Name 'wms-fulfilment' -EventType 'order.shipped' -Destination $nwWms -OrderIndex 1 `
    -Description 'Shipment confirmations back to the warehouse.' | Out-Null
New-Subscription -Tenant $nw -Topic $nwOrders -Name 'lake-orders' -EventType 'order.placed' -Destination $nwLake -OrderIndex 2 `
    -Description 'Flattened order feed for the analytics lake.' `
    -Mapping @{ engine = 'jsonata'; version = '1'; expression = '{ "order": orderId, "total": total, "placed_at": placedAt }' } | Out-Null
New-Subscription -Tenant $nw -Topic $nwPay -Name 'erp-payments' -EventType 'payment.captured' -Destination $nwErp -OrderIndex 0 `
    -Description 'Captured payments into the ERP ledger.' | Out-Null
New-Subscription -Tenant $nw -Topic $nwPay -Name 'legacy-billing-feed' -EventType 'payment.captured' -Destination $nwBilling -OrderIndex 1 `
    -Description 'Mirror of captured payments into the decommissioned billing host.' | Out-Null
New-Subscription -Tenant $nw -Topic $nwStock -Name 'wms-stock' -EventType 'stock.adjusted' -Destination $nwWms -OrderIndex 0 `
    -Description 'Stock adjustments back to the warehouse.' | Out-Null
New-Subscription -Tenant $nw -Topic $nwWebhooks -Name 'webhook-audit' -EventType 'storefront.webhook.received' -Destination $nwLake -OrderIndex 0 `
    -Description 'Storefront callbacks retained in the analytics lake.' | Out-Null
if ($includeQueueDemo) {
    New-Subscription -Tenant $nw -Topic $nwQueue -Name 'warehouse-receipts' -EventType 'warehouse.receipt.recorded' -Destination $nwWms -OrderIndex 0 `
        -Description 'Warehouse receipts delivered to the warehouse system.' | Out-Null
}
$nwPaused = New-Subscription -Tenant $nw -Topic $nwStock -Name 'lake-stock' -EventType 'stock.adjusted' -Destination $nwLake -OrderIndex 1 `
    -Description 'Paused while the lake schema migration runs.'
Invoke-Admin -Method Post -Path "/admin/tenants/$nw/topics/$nwStock/subscriptions/$nwPaused/deactivate" | Out-Null

# --- 4. Helios Energy: smaller, staging -------------------------------------------------------
Say 'Helios Energy'
$he = New-Tenant -Slug 'helios-energy' -Name 'Helios Energy' -Environment 'staging' `
    -Description 'Metering pilot. Half-hourly readings and device alarms from the field trial.'
$heToken = New-Key -Tenant $he -Name 'helios-field-gateway' -Description 'Field gateway in the pilot region.'
$heOps = New-Destination -Tenant $he -Name 'ops-console' -BaseUri 'http://mocksink:8080/sink/helios-ops' -Environment 'staging' `
    -Description 'Operations console alarm feed.'
$heMeters = New-Topic -Tenant $he -Key 'metering' -Name 'metering' -Description 'Half-hourly meter readings.'
$heDevices = New-Topic -Tenant $he -Key 'devices' -Name 'devices' -Description 'Device health and alarms.'
$heMetersSrc = New-Source -Tenant $he -Connector $script:connector -Topic $heMeters -Name 'Helios meter readings' -EventTypes @('meter.reading')
$heDevicesSrc = New-Source -Tenant $he -Connector $script:connector -Topic $heDevices -Name 'Helios device events' -EventTypes @('device.alarm.raised')
New-Subscription -Tenant $he -Topic $heDevices -Name 'ops-alarms' -EventType 'device.alarm.raised' -Destination $heOps -OrderIndex 0 `
    -Description 'Raised alarms to the operations console.' | Out-Null

# --- 5. Atlas Logistics: production, one unstable partner ---------------------------------------
Say 'Atlas Logistics'
$at = New-Tenant -Slug 'atlas-logistics' -Name 'Atlas Logistics' -Environment 'production' `
    -Description 'Freight tracking. Consignment scans out to the customer portal and the partner carrier API.'
$atToken = New-Key -Tenant $at -Name 'atlas-scanners' -Description 'Depot handheld scanners.'
$atPortal = New-Destination -Tenant $at -Name 'customer-portal' -BaseUri 'http://mocksink:8080/sink/atlas-portal' -Environment 'production' `
    -Description 'Tracking updates shown to customers.'
$atCarrier = New-Destination -Tenant $at -Name 'partner-carrier' -BaseUri 'http://mocksink:8080/sink/atlas-carrier' -Environment 'production' `
    -Description 'Partner carrier handover API. Their sandbox is unstable.'
$atFreight = New-Topic -Tenant $at -Key 'consignments' -Name 'consignments' -Description 'Consignment scan and status events.'
$atSrc = New-Source -Tenant $at -Connector $script:connector -Topic $atFreight -Name 'Atlas consignments' -EventTypes @('consignment.scanned', 'consignment.handover')
New-Subscription -Tenant $at -Topic $atFreight -Name 'portal-tracking' -EventType 'consignment.scanned' -Destination $atPortal -OrderIndex 0 `
    -Description 'Scan events to the customer tracking page.' | Out-Null
New-Subscription -Tenant $at -Topic $atFreight -Name 'carrier-handover' -EventType 'consignment.handover' -Destination $atCarrier -OrderIndex 1 `
    -Description 'Handover notifications to the partner carrier.' | Out-Null

# --- 6. Pilotworks: deactivated Tenant ---------------------------------------------------------
Say 'Pilotworks'
$pw = New-Tenant -Slug 'pilotworks' -Name 'Pilotworks' -Environment 'development' `
    -Description 'Evaluation Tenant from the Q1 proof of concept. Kept for its configuration; no longer sending.'
$pwTopic = New-Topic -Tenant $pw -Key 'trials' -Name 'trials' -Description 'Proof-of-concept event stream.'
New-Source -Tenant $pw -Connector $script:connector -Topic $pwTopic -Name 'Pilotworks trials' -EventTypes @('trial.started') | Out-Null
Invoke-Admin -Method Post -Path "/admin/tenants/$pw/deactivate" | Out-Null

# --- 7. Make one destination fail so a Delivery dead-letters ------------------------------------
Say 'pointing one destination at a failing sink'
Invoke-Json -Method Post -Uri "$MockSink/__admin/mappings" -Headers @{} -Body @{
    priority = 1
    request  = @{ method = 'POST'; urlPath = '/sink/northwind-billing' }
    response = @{ status = 503; jsonBody = @{ error = 'upstream unavailable' } }
} | Out-Null

# --- 8. Real traffic ----------------------------------------------------------------------------
Say 'sending events'
Send-Event -Source $nwOrdersSrc -Token $nwToken -EventType 'order.placed' -SourceEventId 'nw-order-1' `
    -Payload @{ orderId = 'SO-401'; customer = 'Contoso Ltd'; lines = 1; total = 119.5; currency = 'GBP'; placedAt = '2026-09-05T09:12:00Z' } | Out-Null
Send-Event -Source $nwOrdersSrc -Token $nwToken -EventType 'order.shipped' -SourceEventId 'nw-ship-1' `
    -Payload @{ orderId = 'SO-401'; carrier = 'Atlas Logistics'; tracking = 'ATL9911772'; warehouse = 'LEE-01' } | Out-Null
Send-Event -Source $nwPaySrc -Token $nwToken -EventType 'payment.captured' -SourceEventId 'nw-pay-1' `
    -Payload @{ paymentId = 'pay_8f21'; amount = 119.5; currency = 'GBP'; method = 'card'; processor = 'stripe' } | Out-Null
Send-Event -Source $nwStockSrc -Token $nwToken -EventType 'stock.adjusted' -SourceEventId 'nw-stock-1' `
    -Payload @{ sku = 'SKU-771'; warehouse = 'LEE-01'; delta = -1; reason = 'pick' } | Out-Null
Send-Webhook -CallbackId $nwWebhookCallback -EventType 'storefront.webhook.received' -SourceEventId 'nw-webhook-1' `
    -Payload @{ deliveryId = 'hook-1'; provider = 'storefront'; result = 'accepted' } | Out-Null
if ($includeQueueDemo) {
    $queueMessage = @{
        event_type      = 'warehouse.receipt.recorded'
        source_event_id = 'nw-queue-1'
        payload         = @{ receiptId = 'RCPT-901'; warehouse = 'LEE-01'; status = 'received' }
    } | ConvertTo-Json -Depth 10 -Compress
    $queueMessage | dotnet run scripts/send-service-bus.cs -- $serviceBusHostConnection $serviceBusQueue
}
# No Subscription matches these: they land as unrouted, the signal for a missing Subscription.
Send-Event -Source $nwOrdersSrc -Token $nwToken -EventType 'order.cancelled' -SourceEventId 'nw-cancel-1' `
    -Payload @{ orderId = 'SO-411'; reason = 'customer_request' } | Out-Null

Send-Event -Source $heMetersSrc -Token $heToken -EventType 'meter.reading' -SourceEventId 'he-read-1' `
    -Payload @{ meterId = 'MTR-33101'; kwh = 1.4; readAt = '2026-09-05T08:30:00Z'; tariff = 'economy7' } | Out-Null
Send-Event -Source $heDevicesSrc -Token $heToken -EventType 'device.alarm.raised' -SourceEventId 'he-alarm-1' `
    -Payload @{ meterId = 'MTR-33101'; alarm = 'tamper_detected'; severity = 'high' } | Out-Null

Send-Event -Source $atSrc -Token $atToken -EventType 'consignment.scanned' -SourceEventId 'at-scan-1' `
    -Payload @{ consignment = 'CN-55121'; depot = 'MAN-03'; scan = 'inbound'; scannedAt = '2026-09-05T06:44:00Z' } | Out-Null
Send-Event -Source $atSrc -Token $atToken -EventType 'consignment.handover' -SourceEventId 'at-hand-1' `
    -Payload @{ consignment = 'CN-55121'; partner = 'Meridian Freight'; manifest = 'MF-2209' } | Out-Null

if ($includeQueueDemo) {
    # Broker publishing and broker acceptance are separate boundaries; prove the receiver consumed the
    # one demo message before using the delivery state as the final completion signal.
    Say 'waiting for the broker Event'
    $brokerEvents = 0
    for ($i = 0; $i -lt 30; $i++) {
        $brokerEvents = [int](Get-SqlScalar "select count(*) from events where source_event_id = 'nw-queue-1';")
        if ($brokerEvents -ge 1) { break }
        Start-Sleep -Seconds 2
    }
    if ($brokerEvents -lt 1) { throw 'Broker Event was not accepted within one minute.' }
}

# --- 9. Wait for the failing Delivery to exhaust its retries ------------------------------------
# Three attempts on a 30s exponential base, so dead-lettering lands ~90s after the first attempt.
Say 'waiting for the failing Delivery to dead-letter (about two minutes)'
for ($i = 0; $i -lt 40; $i++) {
    $dead = [int](Get-SqlScalar "select count(*) from event_deliveries where status = 'dead_lettered';")
    Write-Host "  dead-lettered: $dead"
    if ($dead -ge 1) { break }
    Start-Sleep -Seconds 10
}

Say 'done'
Show-Sql 'select t.slug, e.status event_status, count(*) from events e join tenants t on t.id = e.tenant_id group by 1,2 order by 1,2;'
Show-Sql 'select status, count(*) from event_deliveries group by 1 order by 1;'
Write-Host "`nDashboard: $Admin   (operator key: global_operator_key:operator_bootstrap_secret)"
