# HTTP delivery

The Worker fans an accepted Event out to every active Subscription that selects its type, and
delivers it over HTTP to that Subscription's Destination. A failing Destination is retried and then
dead-lettered. An Operator can replay a dead-lettered delivery.

## Sub-features

- `delivery-success` delivers an accepted Event to the Destination and records the attempt as
  `succeeded`.
- `delivery-retry` records each failed attempt while the Destination answers `5xx`.
- `delivery-dead-letter` dead-letters the delivery after the retry budget is spent.
- `delivery-replay` replays a dead-lettered delivery from the Admin API as a new attempt.

## How to get to it (user POV)

- Send an Event to Ingestion (see [Event intake](./event-intake.md)) for a Topic with an active
  Subscription.
- Watch `GET $INGESTION/events/<event-id>` (`delivery_attempts`) as the Source, or
  `GET $ADMIN/admin/tenants/<tenant>/events/<event-id>/deliveries` as the Operator.
- Replay with `POST $ADMIN/admin/tenants/<tenant>/events/<event-id>/deliveries/<delivery-id>/replay`.

## Driving it with curl and mocksink

Preconditions:

- `doctor` passes, and `eval "$($V/seed-delivery.sh)"` has exported `SLUG`, `TENANT`, `SOURCE`,
  and `TOKEN`. The Destination points at `$MOCKSINK/sink/$SLUG`, which answers `200`.
- `E=$EVIDENCE/http-delivery && mkdir -p $E`.

- **Send.** Run `curl -sS -X POST "$INGESTION/events?source_id=$SOURCE" -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' -d '{"event_type":"payment.created","source_event_id":"delivery-001","payload":{"paymentId":"pay_001","amount":1200}}' -w '\nHTTP %{http_code}\n' | tee $E/accept.txt`, then `EVENT=$(head -1 $E/accept.txt | jq -er .event_id)`. Status is `202`.
- **Wait for delivery.** Once a second, run `curl -fsS $INGESTION/events/$EVENT -H "Authorization: Bearer $TOKEN" > $E/event.json` until `jq -e 'any(.delivery_attempts[]?; .status=="succeeded")' $E/event.json` passes. It took 3 seconds in the proving run. The Event status is `routed`.
- **Sink received it.** Run `curl -fsS -X POST $MOCKSINK/__admin/requests/find -H 'Content-Type: application/json' -d "{\"method\":\"POST\",\"urlPath\":\"/sink/$SLUG\"}" | tee $E/sink-journal.json`. One request carries the posted payload.
- **Operator view.** Run `curl -fsS $ADMIN/admin/tenants/$TENANT/events/$EVENT/deliveries -H "$AUTH" | tee $E/admin-deliveries.json`. One `event_deliveries` entry has `"status":"succeeded"`.
- **Fail and dead-letter.** Make the sink fail with `curl -fsS -X POST $MOCKSINK/__admin/mappings -H 'Content-Type: application/json' -d "{\"id\":\"11111111-1111-1111-1111-111111111111\",\"priority\":1,\"request\":{\"method\":\"POST\",\"urlPath\":\"/sink/$SLUG\"},\"response\":{\"status\":500}}"`. Send a new Event with another `source_event_id`, and poll its `delivery_attempts` until three are `failed`. The Admin deliveries list then shows `dead_lettered`.
- **Replay.** Remove the failing mapping with `curl -fsS -X DELETE $MOCKSINK/__admin/mappings/11111111-1111-1111-1111-111111111111`. Take the `event_delivery_id` whose status is `dead_lettered`, and `POST` its `/replay` path with `$AUTH`. Poll until an attempt with `attempt_number >= 4` is `succeeded`. The three failed attempts stay in the history.

## Gotchas

- The Worker runs on the host, so the Destination must use `$MOCKSINK` (`127.0.0.1`), not the
  Compose-network name `http://mocksink:8080` that `docs/setup.md` uses.
- The default retry policy backs off exponentially from 30 seconds, so dead-lettering takes about
  90 seconds. Poll; do not sleep a fixed time.
- `docs/setup.md` reads the Connector id with `jq -r .id`, but the `PUT` response nests it under
  `.connector.id`. `seed-delivery.sh` uses the working path.
- A delivery needs both the Source and the Subscription active. Nothing flows while either is
  Inactive, and that is not a Worker failure.
- mocksink's journal keeps every request of the run. Filter by the seed's own `/sink/$SLUG` path.
