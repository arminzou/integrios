# Destinations

An Operator creates a Tenant-owned Destination from a destination-capable Connector, sees it in the
Tenant's Destinations list, and can deactivate, reactivate, or delete it.

## Sub-features

- `destination-create` creates a Destination from the `New Destination` sheet.
- `destination-list` lists the Destination with its Connector, environment, and status.
- `destination-status` deactivates and reactivates the Destination.
- `destination-delete` deletes a Destination that no Subscription references.
- `destination-api` performs the same create and read through the Admin API.

## How to get to it (user POV)

- Dashboard: `tenants/<tenant>/destinations`, button `New Destination`, form `Create a Destination`.
- Admin API: `POST` and `GET $ADMIN/admin/tenants/<tenant>/destinations` with `$AUTH`.

## Driving it with browser.mjs

Preconditions:

- `up --dashboard` and `doctor` pass. A Tenant and the `http` Connector exist (run
  `eval "$($V/seed-delivery.sh)"`).

- **Open the sheet.** After `signIn`, go to `tenants/$TENANT/destinations` and choose `New Destination`. A dialog named `New Destination` holds the form `Create a Destination`.
- **Fill it.** In that form, open the `Connector` listbox and choose the option `HTTP (v1)`. Fill `Name` with `ui-sink` and `Base URI` with `$MOCKSINK/sink/ui`. The Connector picker is a scripted listbox: click the trigger, then click the `option` by role.
- **Create.** Choose `Create Destination`. The URL becomes `tenants/$TENANT/destinations/<id>`.
- **List.** Capture with `capture(page, "$EVIDENCE/destinations/list")`. The table shows `ui-sink`, `HTTP`, and `Active`.
- **Read back.** Run `curl -fsS $ADMIN/admin/tenants/$TENANT/destinations -H "$AUTH" | tee $EVIDENCE/destinations/api.json`. An item named `ui-sink` has `configuration.base_uri` equal to `$MOCKSINK/sink/ui`.
- **Status.** Choose `Deactivate`, then the confirmation button `Deactivate ui-sink` in the dialog. The API reports `inactive`. Choose `Activate`, and the API reports `active` again.
- **Delete.** Choose `Delete`, then `Delete ui-sink`. `GET .../destinations/<id>` answers `404`.

## Gotchas

- Delete is refused while a Subscription references the Destination. Delete the Subscription first.
- An update replaces the whole `configuration` object. It does not merge fields.
- The `http` Connector requires an absolute `base_uri` with no query or fragment.
- `Activate` acts at once. `Deactivate` and `Delete` open a confirmation that names the resource.
