import { useState } from "react";
import { api } from "../api/client";
import { fieldError, formError } from "../api/problem";
import type { components } from "../api/schema";
import { ConfirmAction, Field, FormError, Link, ListStatus, LoadMore, fieldProps } from "../ui/controls";
import { navigate } from "../routes";
import { useAction } from "../ui/useAction";
import { useCursorList } from "../ui/useCursorList";
import { useResource } from "../ui/useResource";

type Tenant = components["schemas"]["TenantDto"];

const createFields = ["slug", "name", "environment", "description"];
const updateFields = ["name", "environment", "description"];

export function TenantsScreen() {
  const [status, setStatus] = useState("");
  const list = useCursorList<Tenant>(
    (after) =>
      api.GET("/admin/tenants", {
        params: { query: { status: status || undefined, after: after ?? undefined, limit: 20 } },
      }),
    `tenants|${status}`,
  );

  return (
    <>
      <h1>Tenants</h1>
      <CreateTenant onCreated={list.reload} />

      <h2>All Tenants</h2>
      <Field id="tenant-status" label="Status">
        <select {...fieldProps("tenant-status")} value={status} onChange={(event) => setStatus(event.target.value)}>
          <option value="">Any status</option>
          <option value="active">Active</option>
          <option value="disabled">Disabled</option>
        </select>
      </Field>

      <ListStatus
        busy={list.busy}
        loaded={list.loaded}
        problem={list.problem}
        empty={list.items.length === 0}
        emptyText="No Tenants match this filter."
      />
      {list.items.length > 0 ? (
        <table>
          <caption>Tenants, newest first</caption>
          <thead>
            <tr>
              <th scope="col">Name</th>
              <th scope="col">Slug</th>
              <th scope="col">Status</th>
              <th scope="col">Environment</th>
            </tr>
          </thead>
          <tbody>
            {list.items.map((tenant) => (
              <tr key={tenant.id}>
                <th scope="row">
                  <Link to={`/tenants/${tenant.id}`}>{tenant.name}</Link>
                </th>
                <td>{tenant.slug}</td>
                <td>{tenant.status}</td>
                <td>{tenant.environment ?? "—"}</td>
              </tr>
            ))}
          </tbody>
        </table>
      ) : null}
      <LoadMore cursor={list.cursor} busy={list.busy} onLoadMore={list.loadMore} />
    </>
  );
}

function CreateTenant({ onCreated }: { onCreated: () => void }) {
  const [slug, setSlug] = useState("");
  const [name, setName] = useState("");
  const [environment, setEnvironment] = useState("");
  const [description, setDescription] = useState("");
  const { busy, problem, run } = useAction();

  return (
    <form
      onSubmit={(event) => {
        event.preventDefault();
        void run(
          () =>
            api.POST("/admin/tenants", {
              body: { slug, name, environment: environment || null, description: description || null },
            }),
          (created) => {
            setSlug("");
            setName("");
            setEnvironment("");
            setDescription("");
            onCreated();
            if (created) navigate(`/tenants/${created.id}`);
          },
        );
      }}
    >
      <h2>Create a Tenant</h2>
      <FormError message={formError(problem, createFields)} />
      <Field id="create-tenant-slug" label="Slug" error={fieldError(problem, "slug")}>
        <input
          {...fieldProps("create-tenant-slug", fieldError(problem, "slug"))}
          value={slug}
          onChange={(event) => setSlug(event.target.value)}
          required
        />
      </Field>
      <Field id="create-tenant-name" label="Name" error={fieldError(problem, "name")}>
        <input
          {...fieldProps("create-tenant-name", fieldError(problem, "name"))}
          value={name}
          onChange={(event) => setName(event.target.value)}
          required
        />
      </Field>
      <Field id="create-tenant-environment" label="Environment (optional)" error={fieldError(problem, "environment")}>
        <input
          {...fieldProps("create-tenant-environment", fieldError(problem, "environment"))}
          value={environment}
          onChange={(event) => setEnvironment(event.target.value)}
        />
      </Field>
      <Field id="create-tenant-description" label="Description (optional)" error={fieldError(problem, "description")}>
        <input
          {...fieldProps("create-tenant-description", fieldError(problem, "description"))}
          value={description}
          onChange={(event) => setDescription(event.target.value)}
        />
      </Field>
      <button type="submit" disabled={busy}>
        Create Tenant
      </button>
    </form>
  );
}

export function TenantScreen({ tenantId }: { tenantId: string }) {
  const tenant = useResource<Tenant>(
    () => api.GET("/admin/tenants/{id}", { params: { path: { id: tenantId } } }),
    tenantId,
  );

  if (tenant.problem)
    return (
      <>
        <h1>Tenant</h1>
        <p role="alert">{tenant.problem.detail ?? `This Tenant could not be read (${tenant.problem.status}).`}</p>
      </>
    );
  if (!tenant.data) return <p>Loading…</p>;

  const current = tenant.data;
  return (
    <>
      <h1>{current.name}</h1>
      <dl>
        <dt>Slug</dt>
        <dd>{current.slug}</dd>
        <dt>Status</dt>
        <dd>{current.status}</dd>
        <dt>Environment</dt>
        <dd>{current.environment ?? "—"}</dd>
        <dt>Description</dt>
        <dd>{current.description ?? "—"}</dd>
      </dl>

      <h2>Capabilities in {current.name}</h2>
      <ul>
        <li>
          <Link to={`/tenants/${tenantId}/connections`}>Connections</Link>
        </li>
        <li>
          <Link to={`/tenants/${tenantId}/topics`}>Topics and Subscriptions</Link>
        </li>
        <li>
          <Link to={`/tenants/${tenantId}/sources`}>Sources</Link>
        </li>
        <li>
          <Link to={`/tenants/${tenantId}/tenant-api-keys`}>Tenant API keys</Link>
        </li>
        <li>
          <Link to={`/tenants/${tenantId}/events`}>Events and Deliveries</Link>
        </li>
      </ul>

      <EditTenant key={current.updated_at} tenant={current} onSaved={tenant.reload} />
    </>
  );
}

function EditTenant({ tenant, onSaved }: { tenant: Tenant; onSaved: () => void }) {
  const [name, setName] = useState(tenant.name);
  const [environment, setEnvironment] = useState(tenant.environment ?? "");
  const [description, setDescription] = useState(tenant.description ?? "");
  const { busy, problem, run } = useAction();

  return (
    <>
      <form
        onSubmit={(event) => {
          event.preventDefault();
          void run(
            () =>
              api.PATCH("/admin/tenants/{id}", {
                params: { path: { id: tenant.id } },
                body: { name, environment: environment || null, description: description || null },
              }),
            onSaved,
          );
        }}
      >
        <h2>Edit {tenant.name}</h2>
        <FormError message={formError(problem, updateFields)} />
        <Field id="tenant-name" label="Name" error={fieldError(problem, "name")}>
          <input
            {...fieldProps("tenant-name", fieldError(problem, "name"))}
            value={name}
            onChange={(event) => setName(event.target.value)}
            required
          />
        </Field>
        <Field id="tenant-environment" label="Environment (optional)" error={fieldError(problem, "environment")}>
          <input
            {...fieldProps("tenant-environment", fieldError(problem, "environment"))}
            value={environment}
            onChange={(event) => setEnvironment(event.target.value)}
          />
        </Field>
        <Field id="tenant-description" label="Description (optional)" error={fieldError(problem, "description")}>
          <input
            {...fieldProps("tenant-description", fieldError(problem, "description"))}
            value={description}
            onChange={(event) => setDescription(event.target.value)}
          />
        </Field>
        <button type="submit" disabled={busy}>
          Save changes
        </button>
      </form>

      {/* Deactivation is offered only where the API actually owns it; there is no invented
          reactivation to make the pair look symmetrical. */}
      {tenant.status === "active" ? (
        <ConfirmAction
          label="Deactivate Tenant"
          question={`Deactivate the Tenant "${tenant.name}" (${tenant.slug})? Its Sources stop accepting Events.`}
          confirmLabel={`Deactivate ${tenant.name}`}
          busy={busy}
          onConfirm={() =>
            void run(() => api.POST("/admin/tenants/{id}/deactivate", { params: { path: { id: tenant.id } } }), onSaved)
          }
        />
      ) : null}
    </>
  );
}
