import { useState } from "react";
import { api } from "../api/client";
import { fieldError, formError } from "../api/problem";
import type { components } from "../api/schema";
import { ConfirmAction, Disclosure, Field, FormError, fieldProps, Link, ListStatus, LoadMore } from "../ui/controls";
import { useAction } from "../ui/useAction";
import { useCursorList } from "../ui/useCursorList";

type TenantApiKeyListItem = components["schemas"]["TenantApiKeyListItemDto"];
type CreatedKey = components["schemas"]["CreateTenantApiKeyResult"];

const createFields = ["name", "description", "expires_at"];

export function TenantApiKeysScreen({ tenantId }: { tenantId: string }) {
  const [state, setState] = useState("");
  const list = useCursorList<TenantApiKeyListItem>(
    (after) =>
      api.GET("/admin/tenants/{tenantId}/tenant-api-keys", {
        params: { path: { tenantId }, query: { state: state || undefined, after: after ?? undefined, limit: 20 } },
      }),
    `tenant-api-keys|${tenantId}|${state}`,
  );

  return (
    <>
      <h1>Tenant API keys</h1>
      <p>
        In <Link to={`/tenants/${tenantId}`}>this Tenant</Link>.
      </p>

      <Disclosure label="New Tenant API key">
        <CreateTenantApiKey tenantId={tenantId} onCreated={list.reload} />
      </Disclosure>

      <h2>All Tenant API keys</h2>
      <Field id="tenant-api-key-state" label="State">
        <select
          {...fieldProps("tenant-api-key-state")}
          value={state}
          onChange={(event) => setState(event.target.value)}
        >
          <option value="">Any state</option>
          <option value="active">Active</option>
          <option value="expired">Expired</option>
          <option value="revoked">Revoked</option>
        </select>
      </Field>

      <ListStatus
        busy={list.busy}
        loaded={list.loaded}
        problem={list.problem}
        empty={list.items.length === 0}
        emptyText="This Tenant has no API keys matching this filter."
      />
      {list.items.length > 0 ? (
        <div className="table-card">
          <table>
            <caption>Tenant API keys, newest first</caption>
            <thead>
              <tr>
                <th scope="col">Name</th>
                <th scope="col">Prefix</th>
                <th scope="col">State</th>
                <th scope="col">Expires</th>
                <th scope="col">Last used</th>
                <th scope="col">Action</th>
              </tr>
            </thead>
            <tbody>
              {list.items.map((key) => (
                <tr key={key.id}>
                  <th scope="row">{key.name}</th>
                  {/* Only the prefix is ever stored or shown. The key itself exists once, at creation. */}
                  <td>{key.key_prefix}</td>
                  <td>{key.state}</td>
                  <td>{key.expires_at ?? "Never"}</td>
                  <td>{key.last_used_at ?? "Never used"}</td>
                  <td>
                    <RevokeTenantApiKey tenantId={tenantId} apiKey={key} onRevoked={list.reload} />
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      ) : null}
      <LoadMore cursor={list.cursor} busy={list.busy} onLoadMore={list.loadMore} />
    </>
  );
}

function RevokeTenantApiKey({
  tenantId,
  apiKey,
  onRevoked,
}: {
  tenantId: string;
  apiKey: TenantApiKeyListItem;
  onRevoked: () => void;
}) {
  const { busy, problem, run } = useAction();

  if (apiKey.state === "revoked") return <span>Revoked</span>;

  return (
    <>
      <ConfirmAction
        label="Revoke"
        question={`Revoke the Tenant API key "${apiKey.name}" (${apiKey.key_prefix})? Callers using it stop being authenticated immediately.`}
        confirmLabel={`Revoke ${apiKey.name}`}
        busy={busy}
        onConfirm={() =>
          void run(
            () =>
              api.POST("/admin/tenants/{tenantId}/tenant-api-keys/{id}/revoke", {
                params: { path: { tenantId, id: apiKey.id } },
              }),
            onRevoked,
          )
        }
      />
      <FormError message={formError(problem)} />
    </>
  );
}

function CreateTenantApiKey({ tenantId, onCreated }: { tenantId: string; onCreated: () => void }) {
  const [name, setName] = useState("");
  const [description, setDescription] = useState("");
  const [expiresAt, setExpiresAt] = useState("");
  const [created, setCreated] = useState<CreatedKey | null>(null);
  const { busy, problem, run } = useAction();

  return (
    <>
      <form
        onSubmit={(event) => {
          event.preventDefault();
          void run(
            () =>
              api.POST("/admin/tenants/{tenantId}/tenant-api-keys", {
                params: { path: { tenantId } },
                body: {
                  name,
                  description: description || null,
                  // A local datetime-local value carries no offset, so it is sent as an instant the
                  // server can read unambiguously rather than as the browser's own wall clock.
                  expires_at: expiresAt ? new Date(expiresAt).toISOString() : null,
                },
              }),
            (result) => {
              setName("");
              setDescription("");
              setExpiresAt("");
              setCreated(result ?? null);
              onCreated();
            },
          );
        }}
      >
        <h2>Create a Tenant API key</h2>
        <FormError message={formError(problem, createFields)} />
        <Field id="create-key-name" label="Name" error={fieldError(problem, "name")}>
          <input
            {...fieldProps("create-key-name", fieldError(problem, "name"))}
            value={name}
            onChange={(event) => setName(event.target.value)}
            required
          />
        </Field>
        <Field id="create-key-description" label="Description (optional)" error={fieldError(problem, "description")}>
          <input
            {...fieldProps("create-key-description", fieldError(problem, "description"))}
            value={description}
            onChange={(event) => setDescription(event.target.value)}
          />
        </Field>
        <Field
          id="create-key-expires"
          label="Expires (optional)"
          error={fieldError(problem, "expires_at")}
          hint="Leave empty for a key that does not expire."
        >
          <input
            {...fieldProps("create-key-expires", fieldError(problem, "expires_at"), true)}
            type="datetime-local"
            value={expiresAt}
            onChange={(event) => setExpiresAt(event.target.value)}
          />
        </Field>
        <button type="submit" disabled={busy}>
          Create Tenant API key
        </button>
      </form>

      {/* The token exists in this response and nowhere else — the server stores only its hash, so it
          is shown once, here, and is gone as soon as this panel is dismissed. */}
      {created ? (
        <section aria-label={`New Tenant API key ${created.tenant_api_key.name}`}>
          <h3>Copy the key for {created.tenant_api_key.name} now</h3>
          <p role="status">This key is shown once. It cannot be read again after you dismiss this message.</p>
          <output>{created.token}</output>
          <button type="button" onClick={() => setCreated(null)}>
            I have copied the key
          </button>
        </section>
      ) : null}
    </>
  );
}
