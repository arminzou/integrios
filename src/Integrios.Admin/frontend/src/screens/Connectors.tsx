import { zodResolver } from "@hookform/resolvers/zod";
import { useState } from "react";
import { useForm } from "react-hook-form";
import { Link, useNavigate } from "react-router";
import { z } from "zod";
import { Button } from "@/components/ui/button";
import { TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import { api } from "../api/client";
import { formError } from "../api/problem";
import type { components } from "../api/schema";
import { FormError, ListStatus, LoadMore } from "../ui/controls";
import { Filter, Form, TextAreaField, TextField } from "../ui/fields";
import { applyProblem } from "../ui/formProblem";
import { formatJson, parseJson } from "../ui/json";
import { Details, Page, PageHeader, Panel, RowHeader, TableCard } from "../ui/layout";
import { useAction } from "../ui/useAction";
import { useCursorList } from "../ui/useCursorList";
import { useResource } from "../ui/useResource";
import { SourceContractPreview } from "./Previews";

type ConnectorListItem = components["schemas"]["ConnectorListItemDto"];
type Connector = components["schemas"]["ConnectorDto"];

const applyFields = ["key"] as const;

const applySchema = z.object({
  key: z.string().trim().min(1, "Enter a key."),
  contract_version: z.string().regex(/^[1-9]\d*$/, "Enter a version of 1 or more."),
  manifest: z.string().superRefine((text, ctx) => {
    const parsed = parseJson(text);
    if (parsed.error !== undefined) ctx.addIssue({ code: z.ZodIssueCode.custom, message: parsed.error });
  }),
});

type ApplyValues = z.infer<typeof applySchema>;

/// Connectors are deployment-wide rather than Tenant-scoped, so this screen carries no Tenant.
export function ConnectorsScreen() {
  const navigate = useNavigate();
  const [direction, setDirection] = useState("");
  const list = useCursorList<ConnectorListItem>(
    (after) =>
      api.GET("/admin/connectors", {
        params: { query: { direction: direction || undefined, after: after ?? undefined, limit: 20 } },
      }),
    `connectors|${direction}`,
  );

  return (
    <Page>
      <PageHeader title="Connectors">Connectors are installed for the whole deployment, not for one Tenant.</PageHeader>

      {/* Unlike Connections' create form, this one is never collapsed behind a disclosure: a fresh
          deployment installs no Connectors, and this list is the only screen it can reach, so the
          form that gets it out of that state must stay immediately visible rather than
          discoverable-only. */}
      <ApplyManifest
        onApplied={(installed) => {
          list.reload();
          if (installed) navigate(`/connectors/${installed.id}`);
        }}
      />

      <section className="flex flex-col gap-4">
        <h2>All Connectors</h2>
        <Filter id="connector-direction" label="Direction" value={direction} onChange={setDirection}>
          <option value="">Any direction</option>
          <option value="source">Source</option>
          <option value="destination">Destination</option>
          <option value="both">Both</option>
        </Filter>

        <ListStatus
          busy={list.busy}
          loaded={list.loaded}
          problem={list.problem}
          empty={list.items.length === 0}
          emptyText="No Connectors match this filter."
        />
        {list.items.length > 0 ? (
          <TableCard caption="Connectors, newest first">
            <TableHeader>
              <TableRow>
                <TableHead scope="col">Name</TableHead>
                <TableHead scope="col">Key</TableHead>
                <TableHead scope="col">Contract version</TableHead>
                <TableHead scope="col">Direction</TableHead>
                <TableHead scope="col">Status</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {list.items.map((connector) => (
                <TableRow key={connector.id}>
                  <RowHeader>
                    <Link className="underline" to={`/connectors/${connector.id}`}>
                      {connector.name}
                    </Link>
                  </RowHeader>
                  <TableCell className="font-mono text-sm">{connector.key}</TableCell>
                  <TableCell>{connector.contract_version}</TableCell>
                  <TableCell>{connector.direction}</TableCell>
                  <TableCell>{connector.status}</TableCell>
                </TableRow>
              ))}
            </TableBody>
          </TableCard>
        ) : null}
        <LoadMore cursor={list.cursor} busy={list.busy} onLoadMore={list.loadMore} />
      </section>

      <SourceContractPreview />
    </Page>
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
    <Page>
      <PageHeader title={current.name} />

      <Panel>
        <Details>
          <dt>Key</dt>
          <dd className="font-mono text-sm">{current.key}</dd>
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
        </Details>
      </Panel>

      <section className="flex max-w-2xl flex-col gap-2">
        <h2>Manifest</h2>
        <pre className="text-sm">{formatJson(current.manifest)}</pre>
      </section>

      <ApplyManifest key={current.updated_at} connector={current} onApplied={connector.reload} />
    </Page>
  );
}

/// A Connector is authored by applying a manifest to one contract version, and that one call both
/// installs a key the deployment does not have yet and updates one it does. So this is one form,
/// not two: the only difference is whether the key is already decided. There is no field-level
/// Connector editor, because the manifest is the Connector's own contract and the API owns no
/// partial update of it.
function ApplyManifest({
  connector,
  onApplied,
}: {
  connector?: Connector;
  onApplied: (applied: Connector | undefined) => void;
}) {
  const { busy, problem, run } = useAction();
  const form = useForm<ApplyValues>({
    resolver: zodResolver(applySchema),
    defaultValues: {
      key: connector?.key ?? "",
      contract_version: String(connector?.contract_version ?? 1),
      manifest: formatJson(connector?.manifest),
    },
  });

  const submit = form.handleSubmit(async (values) => {
    const failure = await run(
      () =>
        api.PUT("/admin/connectors/{key}/versions/{contractVersion}", {
          params: { path: { key: values.key, contractVersion: Number(values.contract_version) } },
          body: parseJson(values.manifest).value,
        }),
      onApplied,
    );
    if (failure) applyProblem(form, failure, applyFields);
  });

  return (
    <Form {...form}>
      <Panel asChild>
        <form className="flex flex-col gap-4" onSubmit={submit}>
          <h2>{connector ? "Apply a manifest" : "Install a Connector"}</h2>
          <FormError message={formError(problem, applyFields)} />

          {/* An existing Connector's key is its identity, so it is read-only rather than offered for
              editing: changing it here would install a different Connector, not rename this one. */}
          <TextField
            control={form.control}
            name="key"
            label="Key"
            hint={connector ? undefined : "The Connector's stable identifier, such as http."}
            className="font-mono text-sm"
            readOnly={connector !== undefined}
            required
          />
          <TextField
            control={form.control}
            name="contract_version"
            label="Contract version"
            hint="Applying to a new version installs it; applying to an existing one updates that version."
            type="number"
            min={1}
            step={1}
            required
          />
          <TextAreaField
            control={form.control}
            name="manifest"
            label="Manifest (JSON)"
            className="min-h-64 font-mono text-sm"
            required
          />

          <Button type="submit" className="self-start" disabled={busy}>
            {connector ? "Apply manifest" : "Install Connector"}
          </Button>
        </form>
      </Panel>
    </Form>
  );
}
