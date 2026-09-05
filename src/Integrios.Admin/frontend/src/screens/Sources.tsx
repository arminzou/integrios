import { useState } from "react";
import { api } from "../api/client";
import { fieldError, formError } from "../api/problem";
import type { components } from "../api/schema";
import { navigate } from "../routes";
import { ConfirmAction, Disclosure, Field, FormError, Link, ListStatus, LoadMore, fieldProps } from "../ui/controls";
import { formatJson, parseJson } from "../ui/json";
import { useAction } from "../ui/useAction";
import { useCursorList } from "../ui/useCursorList";
import { useOptions } from "../ui/useOptions";
import { useResource } from "../ui/useResource";

type SourceListItem = components["schemas"]["SourceListItemDto"];
type Source = components["schemas"]["SourceDto"];
type ConnectionListItem = components["schemas"]["ConnectionListItemDto"];
type Topic = components["schemas"]["AdminTopicResponse"];

const sourceTypes = [
  { value: "event_api", label: "Event API" },
  { value: "webhook", label: "Webhook" },
  { value: "queue", label: "Queue" },
];

export function SourcesScreen({ tenantId }: { tenantId: string }) {
  const [status, setStatus] = useState("");
  const [type, setType] = useState("");
  const list = useCursorList<SourceListItem>(
    (after) =>
      api.GET("/admin/tenants/{tenantId}/sources", {
        params: {
          path: { tenantId },
          query: { status: status || undefined, type: type || undefined, after: after ?? undefined, limit: 20 },
        },
      }),
    `sources|${tenantId}|${status}|${type}`,
  );

  return (
    <>
      <h1>Sources</h1>
      <p>
        In <Link to={`/tenants/${tenantId}`}>this Tenant</Link>.
      </p>

      <Disclosure label="New Source">
        <CreateSource tenantId={tenantId} onCreated={list.reload} />
      </Disclosure>

      <h2>All Sources</h2>
      <Field id="source-status" label="Status">
        <select {...fieldProps("source-status")} value={status} onChange={(event) => setStatus(event.target.value)}>
          <option value="">Any status</option>
          <option value="active">Active</option>
          <option value="revoked">Revoked</option>
        </select>
      </Field>
      <Field id="source-type" label="Type">
        <select {...fieldProps("source-type")} value={type} onChange={(event) => setType(event.target.value)}>
          <option value="">Any type</option>
          {sourceTypes.map((option) => (
            <option key={option.value} value={option.value}>
              {option.label}
            </option>
          ))}
        </select>
      </Field>

      <ListStatus
        busy={list.busy}
        loaded={list.loaded}
        problem={list.problem}
        empty={list.items.length === 0}
        emptyText="This Tenant has no Sources matching these filters."
      />
      {list.items.length > 0 ? (
        <div className="table-card">
          <table>
            <caption>Sources, newest first</caption>
            <thead>
              <tr>
                <th scope="col">Source</th>
                <th scope="col">Type</th>
                <th scope="col">Status</th>
                <th scope="col">Topic</th>
              </tr>
            </thead>
            <tbody>
              {list.items.map((source) => (
                <tr key={source.id}>
                  <th scope="row">
                    <Link to={`/tenants/${tenantId}/sources/${source.id}`}>{source.id}</Link>
                  </th>
                  <td>{source.type}</td>
                  <td>{source.status}</td>
                  <td>
                    <Link to={`/tenants/${tenantId}/topics/${source.topic_id}`}>{source.topic_id}</Link>
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

function CreateSource({ tenantId, onCreated }: { tenantId: string; onCreated: () => void }) {
  const connections = useOptions<ConnectionListItem>(
    () =>
      api.GET("/admin/tenants/{tenantId}/connections", {
        params: { path: { tenantId }, query: { status: "active", limit: 100 } },
      }),
    `connection-options|${tenantId}`,
  );
  const topics = useOptions<Topic>(
    () =>
      api.GET("/admin/tenants/{tenantId}/topics", {
        params: { path: { tenantId }, query: { status: "active", limit: 100 } },
      }),
    `topic-options|${tenantId}`,
  );

  const [connectionId, setConnectionId] = useState("");
  const [topicId, setTopicId] = useState("");
  const [type, setType] = useState("webhook");
  const [configuration, setConfiguration] = useState("{}");
  const [configurationError, setConfigurationError] = useState<string | undefined>(undefined);
  const { busy, problem, run } = useAction();
  const optionsUnavailable =
    connections.busy || topics.busy || connections.problem !== null || topics.problem !== null;

  return (
    <form
      onSubmit={(event) => {
        event.preventDefault();
        const parsed = parseJson(configuration);
        setConfigurationError(parsed.error);
        if (parsed.error !== undefined) return;

        void run(
          () =>
            api.POST("/admin/tenants/{tenantId}/sources", {
              params: { path: { tenantId } },
              body: { connection_id: connectionId, topic_id: topicId, type, configuration: parsed.value },
            }),
          (created) => {
            onCreated();
            if (created) navigate(`/tenants/${tenantId}/sources/${created.id}`);
          },
        );
      }}
    >
      <h2>Create a Source</h2>
      <FormError message={formError(connections.problem ?? topics.problem)} />
      <FormError message={formError(problem, ["connection_id", "topic_id", "type", "configuration"])} />
      <Field
        id="create-source-connection"
        label="Connection"
        error={fieldError(problem, "connection_id")}
        hint={connections.truncated ? "Showing the first 100 active Connections." : undefined}
      >
        <select
          {...fieldProps("create-source-connection", fieldError(problem, "connection_id"), connections.truncated)}
          value={connectionId}
          onChange={(event) => setConnectionId(event.target.value)}
          disabled={connections.busy || connections.problem !== null}
          required
        >
          <option value="">Choose a Connection</option>
          {connections.items.map((connection) => (
            <option key={connection.id} value={connection.id}>
              {connection.name}
            </option>
          ))}
        </select>
      </Field>
      <Field
        id="create-source-topic"
        label="Topic"
        error={fieldError(problem, "topic_id")}
        hint={topics.truncated ? "Showing the first 100 active Topics." : undefined}
      >
        <select
          {...fieldProps("create-source-topic", fieldError(problem, "topic_id"), topics.truncated)}
          value={topicId}
          onChange={(event) => setTopicId(event.target.value)}
          disabled={topics.busy || topics.problem !== null}
          required
        >
          <option value="">Choose a Topic</option>
          {topics.items.map((topic) => (
            <option key={topic.id} value={topic.id}>
              {topic.name}
            </option>
          ))}
        </select>
      </Field>
      <Field id="create-source-type" label="Type" error={fieldError(problem, "type")}>
        <select
          {...fieldProps("create-source-type", fieldError(problem, "type"))}
          value={type}
          onChange={(event) => setType(event.target.value)}
          required
        >
          {sourceTypes.map((option) => (
            <option key={option.value} value={option.value}>
              {option.label}
            </option>
          ))}
        </select>
      </Field>
      <Field
        id="create-source-configuration"
        label="Configuration (JSON)"
        error={configurationError ?? fieldError(problem, "configuration")}
      >
        <textarea
          {...fieldProps("create-source-configuration", configurationError ?? fieldError(problem, "configuration"))}
          rows={8}
          value={configuration}
          onChange={(event) => setConfiguration(event.target.value)}
          required
        />
      </Field>
      <button type="submit" disabled={busy || optionsUnavailable}>
        Create Source
      </button>
    </form>
  );
}

export function SourceScreen({ tenantId, sourceId }: { tenantId: string; sourceId: string }) {
  const source = useResource<Source>(
    () => api.GET("/admin/tenants/{tenantId}/sources/{id}", { params: { path: { tenantId, id: sourceId } } }),
    `${tenantId}|${sourceId}`,
  );

  if (source.problem)
    return (
      <>
        <h1>Source</h1>
        <p role="alert">{source.problem.detail ?? `This Source could not be read (${source.problem.status}).`}</p>
      </>
    );
  if (!source.data) return <p>Loading…</p>;

  const current = source.data;
  return (
    <>
      <h1>Source {current.id}</h1>
      <p>
        In <Link to={`/tenants/${tenantId}/sources`}>this Tenant's Sources</Link>.
      </p>
      <dl>
        <dt>Type</dt>
        <dd>{current.type}</dd>
        <dt>Status</dt>
        <dd>{current.status}</dd>
        <dt>Connection</dt>
        <dd>
          <Link to={`/tenants/${tenantId}/connections/${current.connection_id}`}>{current.connection_id}</Link>
        </dd>
        <dt>Topic</dt>
        <dd>
          <Link to={`/tenants/${tenantId}/topics/${current.topic_id}`}>{current.topic_id}</Link>
        </dd>
        <dt>Revoked</dt>
        <dd>{current.revoked_at ?? "Not revoked"}</dd>
      </dl>

      <EditSource key={current.updated_at} tenantId={tenantId} source={current} onSaved={source.reload} />
    </>
  );
}

/// The Admin API owns exactly one Source update — its configuration. Type, Connection, and Topic are
/// fixed at creation, so they are shown rather than offered as editable fields.
function EditSource({ tenantId, source, onSaved }: { tenantId: string; source: Source; onSaved: () => void }) {
  const [configuration, setConfiguration] = useState(() => formatJson(source.configuration));
  const [configurationError, setConfigurationError] = useState<string | undefined>(undefined);
  const { busy, problem, run } = useAction();

  return (
    <>
      <form
        onSubmit={(event) => {
          event.preventDefault();
          const parsed = parseJson(configuration);
          setConfigurationError(parsed.error);
          if (parsed.error !== undefined) return;

          void run(
            () =>
              api.PATCH("/admin/tenants/{tenantId}/sources/{id}", {
                params: { path: { tenantId, id: source.id } },
                body: { configuration: parsed.value },
              }),
            onSaved,
          );
        }}
      >
        <h2>Edit configuration</h2>
        <FormError message={formError(problem, ["configuration"])} />
        <Field
          id="source-configuration"
          label="Configuration (JSON)"
          error={configurationError ?? fieldError(problem, "configuration")}
        >
          <textarea
            {...fieldProps("source-configuration", configurationError ?? fieldError(problem, "configuration"))}
            rows={12}
            value={configuration}
            onChange={(event) => setConfiguration(event.target.value)}
            required
          />
        </Field>
        <button type="submit" disabled={busy}>
          Save configuration
        </button>
      </form>

      {source.status === "active" ? (
        <ConfirmAction
          label="Revoke Source"
          question={`Revoke the ${source.type} Source ${source.id}? It stops accepting Events and cannot be restored.`}
          confirmLabel={`Revoke ${source.id}`}
          busy={busy}
          onConfirm={() =>
            void run(
              () =>
                api.DELETE("/admin/tenants/{tenantId}/sources/{id}", {
                  params: { path: { tenantId, id: source.id } },
                }),
              onSaved,
            )
          }
        />
      ) : null}
    </>
  );
}
