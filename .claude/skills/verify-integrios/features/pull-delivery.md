# Pull delivery

A Subscriber service claims deliveries for a pull Destination from the `delivery-api` role,
authenticated by a SubscriberKey scoped to that Destination, and reports each outcome. The
`Integrios.Subscriber` SDK runs that loop inside a .NET generic host.

## Sub-features

- `pull-sdk-ack` acks a handled delivery, which the Admin API then reports as `succeeded`.
- `pull-sdk-retry` asks for a retry after a delay; the next attempt is claimed again.
- `pull-sdk-fail` dead-letters a delivery when the handler throws
  `PermanentDeliveryFailureException`.
- `pull-sdk-nack` retries under the platform's backoff when the handler throws anything else.
- `pull-sdk-renew` renews the lease while a handler runs past a third of it.
- `pull-protocol` covers the raw HTTP protocol: rejected keys, empty claims, Destination isolation.

## How to get to it (user POV)

- Operator: create a Destination on the built-in `subscriber` Connector, then a SubscriberKey on it
  (`$ADMIN/admin/tenants/<tenant>/destinations/<destination>/subscriber-keys`, shown once), then an
  active Subscription to that Destination.
- Subscriber: `POST $DELIVERY_API/delivery/v1/claims` with `Authorization: Bearer <SubscriberKey>`,
  then `.../deliveries/<delivery>/attempts/<attempt>/renew|ack|nack|fail`. With the SDK, call
  `AddIntegriosSubscriber<THandler>` with `BaseAddress` and `SubscriberKey`.

## Driving it with prove-pull.sh

Preconditions: `up` and `doctor` pass, and the run's env is loaded.

- **Prove the SDK.** `bash $V/prove-pull.sh` seeds a Tenant with `seed-pull.sh`, builds a consumer
  under `artifacts/verify/<run>/subscriber` that references `src/Integrios.Subscriber`, accepts one
  Event per outcome, and checks each through the Admin deliveries list. It takes about a minute
  because the renewal case holds its handler for 45 seconds. Evidence lands in
  `$EVIDENCE/pull-delivery/`.
- **Try your own consumer.** `eval "$($V/seed-pull.sh)"` exports `SUBSCRIBER_KEY`,
  `PULL_DESTINATION`, `SOURCE`, and `TOKEN`; the run env already has `DELIVERY_API`. Run a generic host that calls
  `AddIntegriosSubscriber<THandler>` with `BaseAddress = $DELIVERY_API` and that key, then post a
  `payment.created` Event (see [Event intake](./event-intake.md)). The generated
  `artifacts/verify/<run>/subscriber` project is a working starting point.
- **Drive the protocol by hand.** Claim with
  `curl -sS -w '%{http_code}' -X POST "$DELIVERY_API/delivery/v1/claims" -H "Authorization: Bearer $SUBSCRIBER_KEY" -H 'Content-Type: application/json' -d '{"max":1,"wait_seconds":0}'`.
  `204` means nothing is ready; `200` returns `deliveries[]` with `delivery_id` and `attempt_id`.
  Settling with another Destination's key answers `409` with code `ownership_lost`.

## Gotchas

- The seed's Topic also has an HTTP Subscription, so every Event delivers twice: once to mocksink
  and once to the pull Destination. Filter the deliveries list by `destination_id`.
- A consumer project needs `Microsoft.Extensions.Hosting` itself; the SDK depends only on the
  hosting abstractions.
- The SDK package is unpublished. Reference `src/Integrios.Subscriber/Integrios.Subscriber.csproj`
  from a project, or `dotnet pack` it to a local feed.
- Pull leases use `Integrios:Delivery:LeaseDuration` (2 minutes by default) and the SDK renews at a
  third of the remaining lease, so a renewal shows up about 40 seconds into a handler.
