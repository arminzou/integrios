import { useState } from "react";
import { api } from "../api/client";
import { formError } from "../api/problem";
import type { components } from "../api/schema";
import { Field, FormError, Link, ListStatus, LoadMore, fieldProps } from "../ui/controls";
import { formatJson, parseJson } from "../ui/json";
import { SourceContractPreview } from "./Previews";
import { useAction } from "../ui/useAction";
import { useCursorList } from "../ui/useCursorList";
import { useResource } from "../ui/useResource";

type ConnectorListItem = components["schemas"]["ConnectorListItemDto"];
type Connector = components["schemas"]["ConnectorDto"];

/// Connectors are deployment-wide rather than Tenant-scoped, so this screen carries no Tenant.
export function ConnectorsScreen() {
  const [direction, setDirection] = useState("");
  const list = useCursorList<ConnectorListItem>(
    (after) =>
      api.GET("/admin/connectors", {
        params: { query: { direction: direction || undefined, after: after ?? undefined, limit: 20 } },
      }),
    `connectors|${direction}`,
  );

  return (
    <>
      <h1>Connectors</h1>
      <p>Connectors are installed for the whole deployment, not for one Tenant.</p>

      <Field id="connector-direction" label="Direction">
        <select
          {...fieldProps("connector-direction")}
          value={direction}
          onChange={(event) => setDirection(event.target.value)}
        >
          <option value="">Any direction</option>
          <option value="source">Source</option>
          <option value="destination">Destination</option>
          <option value="both">Both</option>
        </select>
      </Field>

      <ListStatus
        busy={list.busy}
        loaded={list.loaded}
        problem={list.problem}
        empty={list.items.length === 0}
        emptyText="No Connectors match this filter."
      />
      {list.items.length > 0 ? (
        <table>
          <caption>Connectors, newest first</caption>
          <thead>
            <tr>
              <th scope="col">Name</th>
              <th scope="col">Key</th>
              <th scope="col">Contract version</th>
              <th scope="col">Direction</th>
              <th scope="col">Status</th>
            </tr>
          </thead>
          <tbody>
            {list.items.map((connector) => (
              <tr key={connector.id}>
                <th scope="row">
                  <Link to={`/connectors/${connector.id}`}>{connector.name}</Link>
                </th>
                <td>{connector.key}</td>
                <td>{connector.contract_version}</td>
                <td>{connector.direction}</td>
                <td>{connector.status}</td>
              </tr>
            ))}
          </tbody>
        </table>
      ) : null}
      <LoadMore cursor={list.cursor} busy={list.busy} onLoadMore={list.loadMore} />

      <SourceContractPreview />
    </>
  );
}

export function ConnectorScreen({ connectorId }: { connectorId: string }) {
  const connector = useResource<Connector>(
    () => api.GET("/admin/connectors/{id}", { params: { path: { id: connectorId } } }),
    connectorId,
  );

  if (connector.problem)
    return (
      <>
        <h1>Connector</h1>
        <p role="alert">
          {connector.problem.detail ?? `This Connector could not be read (${connector.problem.status}).`}
        </p>
      </>
    );
  if (!connector.data) return <p>Loading…</p>;

  const current = connector.data;
  return (
    <>
      <h1>{current.name}</h1>
      <dl>
        <dt>Key</dt>
        <dd>{current.key}</dd>
        <dt>Contract version</dt>
        <dd>{current.contract_version}</dd>
        <dt>Manifest schema version</dt>
        <dd>{current.manifest_schema_version}</dd>
        <dt>Direction</dt>
        <dd>{current.direction}</dd>
        <dt>Status</dt>
        <dd>{current.status}</dd>
        <dt>Description</dt>
        <dd>{current.description ?? "—"}</dd>
      </dl>

      <h2>Manifest</h2>
      <pre>{formatJson(current.manifest)}</pre>

      <ApplyManifest key={current.updated_at} connector={current} onApplied={connector.reload} />
    </>
  );
}

/// A Connector is authored by applying a manifest to one contract version. There is no field-level
/// Connector editor, because the manifest is the Connector's own contract and the API owns no
/// partial update of it.
function ApplyManifest({ connector, onApplied }: { connector: Connector; onApplied: () => void }) {
  const [contractVersion, setContractVersion] = useState(String(connector.contract_version));
  const [manifest, setManifest] = useState(() => formatJson(connector.manifest));
  const [manifestError, setManifestError] = useState<string | undefined>(undefined);
  const { busy, problem, run } = useAction();

  return (
    <form
      onSubmit={(event) => {
        event.preventDefault();
        const parsed = parseJson(manifest);
        setManifestError(parsed.error);
        if (parsed.error !== undefined) return;

        void run(
          () =>
            api.PUT("/admin/connectors/{key}/versions/{contractVersion}", {
              params: { path: { key: connector.key, contractVersion: Number(contractVersion) } },
              body: parsed.value,
            }),
          onApplied,
        );
      }}
    >
      <h2>Apply a manifest</h2>
      <FormError message={formError(problem)} />
      <Field
        id="connector-contract-version"
        label="Contract version"
        hint="Applying to a new version installs it; applying to an existing one updates that version."
      >
        <input
          {...fieldProps("connector-contract-version")}
          type="number"
          min={1}
          step={1}
          value={contractVersion}
          onChange={(event) => setContractVersion(event.target.value)}
          required
        />
      </Field>
      <Field id="connector-manifest" label="Manifest (JSON)" error={manifestError}>
        <textarea
          {...fieldProps("connector-manifest", manifestError)}
          rows={16}
          value={manifest}
          onChange={(event) => setManifest(event.target.value)}
          required
        />
      </Field>
      <button type="submit" disabled={busy}>
        Apply manifest
      </button>
    </form>
  );
}
