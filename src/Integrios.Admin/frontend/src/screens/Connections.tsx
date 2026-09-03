import { useState } from "react";
import { api } from "../api/client";
import { fieldError, formError } from "../api/problem";
import type { components } from "../api/schema";
import { navigate } from "../routes";
import { ConfirmAction, Field, FormError, Link, ListStatus, LoadMore, fieldProps } from "../ui/controls";
import { formatJson, parseJson } from "../ui/json";
import { useAction } from "../ui/useAction";
import { useCursorList } from "../ui/useCursorList";
import { useOptions } from "../ui/useOptions";
import { useResource } from "../ui/useResource";

type ConnectionListItem = components["schemas"]["ConnectionListItemDto"];
type Connection = components["schemas"]["ConnectionDto"];
type ConnectorListItem = components["schemas"]["ConnectorListItemDto"];

const writeFields = ["connector_id", "name", "config", "environment", "description"];

export function ConnectionsScreen({ tenantId }: { tenantId: string }) {
  const [status, setStatus] = useState("");
  const list = useCursorList<ConnectionListItem>(
    (after) =>
      api.GET("/admin/tenants/{tenantId}/connections", {
        params: { path: { tenantId }, query: { status: status || undefined, after: after ?? undefined, limit: 20 } },
      }),
    `connections|${tenantId}|${status}`,
  );

  return (
    <>
      <h1>Connections</h1>
      <p>
        In <Link to={`/tenants/${tenantId}`}>this Tenant</Link>.
      </p>

      <CreateConnection tenantId={tenantId} onCreated={list.reload} />

      <h2>All Connections</h2>
      <Field id="connection-status" label="Status">
        <select
          {...fieldProps("connection-status")}
          value={status}
          onChange={(event) => setStatus(event.target.value)}
        >
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
        emptyText="This Tenant has no Connections matching this filter."
      />
      {list.items.length > 0 ? (
        <table>
          <caption>Connections, newest first</caption>
          <thead>
            <tr>
              <th scope="col">Name</th>
              <th scope="col">Status</th>
              <th scope="col">Environment</th>
              <th scope="col">Description</th>
            </tr>
          </thead>
          <tbody>
            {list.items.map((connection) => (
              <tr key={connection.id}>
                <th scope="row">
                  <Link to={`/tenants/${tenantId}/connections/${connection.id}`}>{connection.name}</Link>
                </th>
                <td>{connection.status}</td>
                <td>{connection.environment ?? "—"}</td>
                <td>{connection.description ?? "—"}</td>
              </tr>
            ))}
          </tbody>
        </table>
      ) : null}
      <LoadMore cursor={list.cursor} busy={list.busy} onLoadMore={list.loadMore} />
    </>
  );
}

function CreateConnection({ tenantId, onCreated }: { tenantId: string; onCreated: () => void }) {
  const connectors = useOptions<ConnectorListItem>(
    () => api.GET("/admin/connectors", { params: { query: { limit: 100 } } }),
    "connector-options",
  );
  const [connectorId, setConnectorId] = useState("");
  const [name, setName] = useState("");
  const [config, setConfig] = useState("{}");
  const [configError, setConfigError] = useState<string | undefined>(undefined);
  const [environment, setEnvironment] = useState("");
  const [description, setDescription] = useState("");
  const { busy, problem, run } = useAction();
  const connectorOptionsUnavailable = connectors.busy || connectors.problem !== null;

  return (
    <form
      onSubmit={(event) => {
        event.preventDefault();
        const parsed = parseJson(config);
        setConfigError(parsed.error);
        if (parsed.error !== undefined) return;

        void run(
          () =>
            api.POST("/admin/tenants/{tenantId}/connections", {
              params: { path: { tenantId } },
              body: {
                connector_id: connectorId,
                name,
                config: parsed.value,
                // Verification and authentication schemes carry secret references, never secret
                // values, so they are configured on the Connection itself rather than typed here.
                source_verification: null,
                destination_authentication: null,
                environment: environment || null,
                description: description || null,
              },
            }),
          (created) => {
            onCreated();
            if (created) navigate(`/tenants/${tenantId}/connections/${created.id}`);
          },
        );
      }}
    >
      <h2>Create a Connection</h2>
      <FormError message={formError(connectors.problem)} />
      <FormError message={formError(problem, writeFields)} />
      <Field
        id="create-connection-connector"
        label="Connector"
        error={fieldError(problem, "connector_id")}
        hint={connectors.truncated ? "Showing the first 100 Connectors." : undefined}
      >
        <select
          {...fieldProps("create-connection-connector", fieldError(problem, "connector_id"), connectors.truncated)}
          value={connectorId}
          onChange={(event) => setConnectorId(event.target.value)}
          disabled={connectorOptionsUnavailable}
          required
        >
          <option value="">Choose a Connector</option>
          {connectors.items.map((connector) => (
            <option key={connector.id} value={connector.id}>
              {connector.name} (v{connector.contract_version}, {connector.direction})
            </option>
          ))}
        </select>
      </Field>
      <Field id="create-connection-name" label="Name" error={fieldError(problem, "name")}>
        <input
          {...fieldProps("create-connection-name", fieldError(problem, "name"))}
          value={name}
          onChange={(event) => setName(event.target.value)}
          required
        />
      </Field>
      <Field
        id="create-connection-config"
        label="Configuration (JSON)"
        error={configError ?? fieldError(problem, "config")}
        hint="The Connector's manifest defines what this document must contain."
      >
        <textarea
          {...fieldProps("create-connection-config", configError ?? fieldError(problem, "config"), true)}
          rows={8}
          value={config}
          onChange={(event) => setConfig(event.target.value)}
          required
        />
      </Field>
      <Field id="create-connection-environment" label="Environment (optional)" error={fieldError(problem, "environment")}>
        <input
          {...fieldProps("create-connection-environment", fieldError(problem, "environment"))}
          value={environment}
          onChange={(event) => setEnvironment(event.target.value)}
        />
      </Field>
      <Field id="create-connection-description" label="Description (optional)" error={fieldError(problem, "description")}>
        <input
          {...fieldProps("create-connection-description", fieldError(problem, "description"))}
          value={description}
          onChange={(event) => setDescription(event.target.value)}
        />
      </Field>
      <button type="submit" disabled={busy || connectorOptionsUnavailable}>
        Create Connection
      </button>
    </form>
  );
}

export function ConnectionScreen({ tenantId, connectionId }: { tenantId: string; connectionId: string }) {
  const connection = useResource<Connection>(
    () =>
      api.GET("/admin/tenants/{tenantId}/connections/{id}", {
        params: { path: { tenantId, id: connectionId } },
      }),
    `${tenantId}|${connectionId}`,
  );

  if (connection.problem)
    return (
      <>
        <h1>Connection</h1>
        <p role="alert">
          {connection.problem.detail ?? `This Connection could not be read (${connection.problem.status}).`}
        </p>
      </>
    );
  if (!connection.data) return <p>Loading…</p>;

  const current = connection.data;
  return (
    <>
      <h1>{current.name}</h1>
      <p>
        In <Link to={`/tenants/${tenantId}/connections`}>this Tenant's Connections</Link>.
      </p>
      <dl>
        <dt>Status</dt>
        <dd>{current.status}</dd>
        <dt>Connector</dt>
        <dd>
          <Link to={`/connectors/${current.connector_id}`}>{current.connector_id}</Link>
        </dd>
        <dt>Environment</dt>
        <dd>{current.environment ?? "—"}</dd>
        <dt>Source verification</dt>
        <dd>{current.source_verification ? current.source_verification.scheme : "Not configured"}</dd>
        <dt>Destination authentication</dt>
        <dd>{current.destination_authentication ? current.destination_authentication.scheme : "Not configured"}</dd>
      </dl>

      <h2>Configuration</h2>
      <pre>{formatJson(current.config)}</pre>

      <EditConnection key={current.updated_at} tenantId={tenantId} connection={current} onSaved={connection.reload} />
    </>
  );
}

function EditConnection({
  tenantId,
  connection,
  onSaved,
}: {
  tenantId: string;
  connection: Connection;
  onSaved: () => void;
}) {
  const [name, setName] = useState(connection.name);
  const [config, setConfig] = useState(() => formatJson(connection.config));
  const [configError, setConfigError] = useState<string | undefined>(undefined);
  const [environment, setEnvironment] = useState(connection.environment ?? "");
  const [description, setDescription] = useState(connection.description ?? "");
  const { busy, problem, run } = useAction();

  return (
    <>
      <form
        onSubmit={(event) => {
          event.preventDefault();
          const parsed = parseJson(config);
          setConfigError(parsed.error);
          if (parsed.error !== undefined) return;

          void run(
            () =>
              api.PATCH("/admin/tenants/{tenantId}/connections/{id}", {
                params: { path: { tenantId, id: connection.id } },
                body: {
                  name,
                  config: parsed.value,
                  // Sending null leaves the stored scheme untouched: this form never round-trips a
                  // scheme's secret references, so it must not claim to replace them either.
                  source_verification: null,
                  destination_authentication: null,
                  environment: environment || null,
                  description: description || null,
                },
              }),
            onSaved,
          );
        }}
      >
        <h2>Edit {connection.name}</h2>
        <FormError message={formError(problem, writeFields)} />
        <Field id="connection-name" label="Name" error={fieldError(problem, "name")}>
          <input
            {...fieldProps("connection-name", fieldError(problem, "name"))}
            value={name}
            onChange={(event) => setName(event.target.value)}
            required
          />
        </Field>
        <Field id="connection-config" label="Configuration (JSON)" error={configError ?? fieldError(problem, "config")}>
          <textarea
            {...fieldProps("connection-config", configError ?? fieldError(problem, "config"))}
            rows={10}
            value={config}
            onChange={(event) => setConfig(event.target.value)}
            required
          />
        </Field>
        <Field id="connection-environment" label="Environment (optional)" error={fieldError(problem, "environment")}>
          <input
            {...fieldProps("connection-environment", fieldError(problem, "environment"))}
            value={environment}
            onChange={(event) => setEnvironment(event.target.value)}
          />
        </Field>
        <Field id="connection-description" label="Description (optional)" error={fieldError(problem, "description")}>
          <input
            {...fieldProps("connection-description", fieldError(problem, "description"))}
            value={description}
            onChange={(event) => setDescription(event.target.value)}
          />
        </Field>
        <button type="submit" disabled={busy}>
          Save changes
        </button>
      </form>

      {connection.status === "active" ? (
        <ConfirmAction
          label="Deactivate Connection"
          question={`Deactivate the Connection "${connection.name}"? Sources and Subscriptions that use it stop working.`}
          confirmLabel={`Deactivate ${connection.name}`}
          busy={busy}
          onConfirm={() =>
            void run(
              () =>
                api.POST("/admin/tenants/{tenantId}/connections/{id}/deactivate", {
                  params: { path: { tenantId, id: connection.id } },
                }),
              onSaved,
            )
          }
        />
      ) : null}
    </>
  );
}
