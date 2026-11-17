# Event intake

A Source posts an Event to Ingestion with a TenantApiKey. Ingestion accepts it durably, answers with
the Event's id, recognizes a resubmission of the same `source_event_id`, and refuses a request
whose credential is wrong.

## Sub-features

- `intake-accept` accepts a valid Event with `202` and returns `event_id`.
- `intake-read` returns the accepted Event from `GET /events/{id}` for the same Tenant.
- `intake-idempotent` answers a resubmission of the same `source_event_id` with the original Event.
- `intake-reject-auth` refuses a missing or wrong TenantApiKey.

## How to get to it (user POV)

- `POST $INGESTION/events?source_id=<source-id>` with `Authorization: Bearer <tenant-api-key>` and
  the Event API body (`event_type`, `payload`, optional `source_event_id`).
- `GET $INGESTION/events/<event-id>` with the same bearer token.

## Driving it with curl

Preconditions:

- `doctor` passes, and `eval "$($V/seed-delivery.sh)"` has exported `SOURCE` and `TOKEN`.
- `mkdir -p $EVIDENCE/event-intake`.

- **Accept.** Run `curl -sS -X POST "$INGESTION/events?source_id=$SOURCE" -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' -d '{"event_type":"payment.created","source_event_id":"intake-001","payload":{"amount":1200}}' -w '\nHTTP %{http_code}\n' | tee $EVIDENCE/event-intake/accept.txt`. Status is `202`. The body has `"status":"accepted"`, `"already_accepted":false`, and an `event_id`.
- **Read back.** Run `curl -fsS $INGESTION/events/<event-id> -H "Authorization: Bearer $TOKEN" | tee $EVIDENCE/event-intake/event.json`. The Event has the same id and payload.
- **Resubmit.** Repeat the accept request unchanged. The response names the same `event_id` with `"already_accepted":true`, and no second Event is created.
- **Wrong credential.** Repeat the accept request with `Authorization: Bearer intg_wrong`. The request is refused with a 4xx (401 expected) and no `event_id`.

## Gotchas

- `source_id` is a query parameter, not part of the body.
- The TenantApiKey token is shown once, when it is created. `seed-delivery.sh` captures it. If you
  lose the token, seed again.
- The Source must be active and must declare the `event_type`. A new Source starts Inactive; the
  seed script activates it.
- An unknown `event_type` or an inactive Source is an acceptance failure, not a delivery failure.
  Do not wait on the Worker for it.
