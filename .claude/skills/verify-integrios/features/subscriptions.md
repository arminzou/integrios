# Subscriptions

An Operator subscribes a Destination to Event types on a Topic. A new Subscription starts Inactive,
and only an active one receives deliveries.

## Sub-features

- `subscription-create` creates a Subscription from the `New Subscription` sheet. It chooses a
  Destination and the Event types that the Topic's Sources declare.
- `subscription-activate` activates and deactivates it.
- `subscription-delivers` proves that an active Subscription receives a matching Event and an
  inactive one does not.
- `subscription-api` performs the same create and lifecycle through the Admin API.

## How to get to it (user POV)

- Dashboard: `tenants/<tenant>/topics/<topic>`, `Manage Subscriptions`, then `New Subscription`
  and the form `Create a Subscription`. A Subscription's own page is
  `tenants/<tenant>/subscriptions/<topic>/<subscription>`.
- Admin API: `$ADMIN/admin/tenants/<tenant>/topics/<topic>/subscriptions` and its `/activate` and
  `/deactivate` actions.

## Driving it with browser.mjs

Preconditions:

- `up --dashboard` and `doctor` pass. `eval "$($V/seed-delivery.sh)"` has made a Tenant whose
  Topic has a Source that declares `payment.created` and a Destination named `$SLUG-sink`.

- **Open the sheet.** After `signIn`, go to `tenants/$TENANT/topics/$TOPIC`, choose `Manage Subscriptions`, then `New Subscription`.
- **Fill it.** In the form `Create a Subscription`, fill `Name` with `ui-sub`, choose `$SLUG-sink` from the `Destination` listbox, and check `payment.created` in the group `Event types`.
- **Create.** Choose `Create Subscription`. The URL becomes `tenants/$TENANT/subscriptions/$TOPIC/<id>`, and `GET .../subscriptions/<id>` reports `"status":"inactive"`.
- **Activate.** Choose `Activate`. The API reports `active`. Capture with `capture(page, "$EVIDENCE/subscriptions/active")`.
- **Delivers.** Send a `payment.created` Event (see [Event intake](./event-intake.md)). The mocksink journal for `/sink/$SLUG` gains one request for each active Subscription. The seed's own Subscription also delivers there, so expect two.
- **Deactivate.** Choose `Deactivate`, then `Deactivate ui-sub`. The API reports `inactive`, and a new Event adds only one request to the journal.

## Gotchas

- The `Event types` group lists only types that the Topic's Sources declare. An empty group means
  the Source declares nothing yet. It is not a dashboard fault.
- The seed's Subscription also targets `/sink/$SLUG`. Count journal entries per Event, or deactivate
  the seed's Subscription first.
- Delete the Subscription before you delete its Destination or the Source whose types it selects.
