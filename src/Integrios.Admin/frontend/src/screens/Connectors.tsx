import { zodResolver } from "@hookform/resolvers/zod";
import { useInfiniteQuery, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useForm } from "react-hook-form";
import { NavLink, useNavigate } from "react-router";
import { z } from "zod";
import { Button } from "@/components/ui/button";
import { SelectItem } from "@/components/ui/select";
import { TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import { api } from "../api/client";
import { formError } from "../api/problem";
import { asProblem, call, nextCursor } from "../api/query";
import type { components } from "../api/schema";
import { appliedNote, CreateSheet, Disclosure, FilterBar, FormError, ListStatus, LoadMore } from "../ui/controls";
import { Filter, Form, TextAreaField, TextField } from "../ui/fields";
import { useFilterParam } from "../ui/filters";
import { applyProblem } from "../ui/formProblem";
import { formatJson, parseJson } from "../ui/json";
import {
  CloseInspector,
  Details,
  Inspector,
  InspectorPlaceholder,
  Page,
  PageHeader,
  Panel,
  RowHeader,
  SplitList,
  SplitView,
  TableCard,
} from "../ui/layout";
import { StatusBadge } from "../ui/status";
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
export function ConnectorsScreen({ selectedConnectorId }: { selectedConnectorId?: string } = {}) {
  const navigate = useNavigate();
  const [direction, setDirection] = useFilterParam("direction");
  const list = useInfiniteQuery({
    queryKey: ["connectors", { direction }],
    queryFn: ({ pageParam }) =>
      call(() =>
        api.GET("/admin/connectors", {
          params: { query: { direction: direction || undefined, after: pageParam ?? undefined, limit: 20 } },
        }),
      ),
    initialPageParam: null as string | null,
    getNextPageParam: nextCursor<ConnectorListItem>,
  });
  const connectors = list.data?.pages.flatMap((page) => page.items) ?? [];

  return (
    <Page>
      <PageHeader
        title="Connectors"
        action={
          <CreateSheet label="Apply manifest" description="Install or update a deployment-wide Connector">
            {(close) => (
              <ApplyManifest
                onApplied={(installed) => {
                  close();
                  if (installed) navigate(`/connectors/${installed.id}`);
                }}
              />
            )}
          </CreateSheet>
        }
      >
        Deployment-wide capability definitions. Connections are built from these, per Tenant.
      </PageHeader>

      <section className="flex flex-col gap-4">
        <FilterBar applied={(direction ? 1 : 0) as number}>
          <Filter id="connector-direction" label="Direction" value={direction} onChange={setDirection}>
            <SelectItem value="source">Source</SelectItem>
            <SelectItem value="destination">Destination</SelectItem>
            <SelectItem value="both">Both</SelectItem>
          </Filter>
        </FilterBar>

        <ListStatus
          busy={list.isFetching}
          loaded={list.isSuccess}
          problem={asProblem(list.error)}
          empty={connectors.length === 0}
          emptyText={
            direction
              ? "No Connectors match this filter."
              : "No Connectors are installed. Use Apply manifest, above, to install the first one."
          }
        />
        <SplitView>
          <SplitList>
            {connectors.length > 0 ? (
              <TableCard
                caption={`Connectors, newest first${appliedNote(direction ? 1 : 0)}`}
                footer={
                  <LoadMore
                    noun="Connector"
                    hasMore={list.hasNextPage}
                    busy={list.isFetching}
                    loaded={connectors.length}
                    onLoadMore={() => void list.fetchNextPage()}
                  />
                }
              >
                <TableHeader>
                  <TableRow>
                    {/* The key is what an Operator writes in a manifest and what a Connection is built
                    from, so it names the row; the presentation name follows it. */}
                    <TableHead scope="col">Key</TableHead>
                    <TableHead scope="col">Name</TableHead>
                    <TableHead scope="col">Direction</TableHead>
                    <TableHead scope="col">Contract</TableHead>
                    <TableHead scope="col">Status</TableHead>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {connectors.map((connector) => (
                    <TableRow key={connector.id} className="has-[a[aria-current=page]]:bg-selected-surface">
                      <RowHeader className="whitespace-nowrap">
                        <NavLink className="font-mono no-underline" to={`/connectors/${connector.id}`} end>
                          {connector.key}
                        </NavLink>
                      </RowHeader>
                      <TableCell>{connector.name}</TableCell>
                      <TableCell>{connector.direction}</TableCell>
                      <TableCell className="tabular-nums">v{connector.contract_version}</TableCell>
                      <TableCell>
                        <StatusBadge status={connector.status} />
                      </TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </TableCard>
            ) : null}
          </SplitList>

          {selectedConnectorId ? (
            <ConnectorInspector key={selectedConnectorId} connectorId={selectedConnectorId} />
          ) : (
            <InspectorPlaceholder label="Connector detail">
              Select a Connector to read its manifest and what it permits.
            </InspectorPlaceholder>
          )}
        </SplitView>
      </section>

      {/* A dry run is a tool an Operator occasionally reaches for, not what this page is. Expanded by
          default it was taller than the list it sat under, so the screen read as a form with a list
          on top of it. */}
      <Disclosure label="Preview a Source contract">
        <SourceContractPreview />
      </Disclosure>
    </Page>
  );
}

/// The selected Connector beside the list. A Connector is deployment-wide and read far more often
/// than it is applied — a Connection's configuration is validated against this manifest — so the
/// manifest is what the panel is mostly for.
function ConnectorInspector({ connectorId }: { connectorId: string }) {
  const connector = useQuery({
    queryKey: ["connector", connectorId],
    queryFn: () => call(() => api.GET("/admin/connectors/{id}", { params: { path: { id: connectorId } } })),
  });

  const problem = asProblem(connector.error);
  if (problem)
    return (
      <Inspector label="Connector detail">
        <div className="flex items-start justify-between gap-3">
          <h2 className="m-0">Connector</h2>
          <CloseInspector to="/connectors" label="Close the Connector detail" />
        </div>
        <p role="alert">{problem.detail ?? `This Connector could not be read (${problem.status}).`}</p>
      </Inspector>
    );
  if (!connector.data) return <Inspector label="Connector detail">Loading…</Inspector>;

  const current = connector.data;
  return (
    <Inspector label="Connector detail">
      <div className="flex items-start justify-between gap-3">
        <h2 className="min-w-0">
          {current.name}
          <span className="block font-mono text-xs break-all text-ink-secondary">
            {current.key} · contract v{current.contract_version}
          </span>
        </h2>
        <div className="flex shrink-0 items-center gap-1.5">
          <StatusBadge status={current.status} className="mt-0.5" />
          <CloseInspector to="/connectors" label="Close the Connector detail" />
        </div>
      </div>

      <Details className="border-b pb-3.5">
        <dt>Direction</dt>
        <dd>{current.direction}</dd>
        <dt>Contract version</dt>
        <dd className="tabular-nums">{current.contract_version}</dd>
        <dt>Manifest schema</dt>
        <dd className="tabular-nums">{current.manifest_schema_version}</dd>
      </Details>

      {current.description ? <p className="m-0 text-[13px] text-ink-secondary">{current.description}</p> : null}

      <section className="flex min-w-0 flex-col gap-2">
        <h4 className="eyebrow">Manifest</h4>
        <pre className="text-xs">{formatJson(current.manifest)}</pre>
        <p className="m-0 text-xs text-ink-secondary">
          Applied by an Operator. A Connector is deployment-wide and shared by every Tenant.
        </p>
      </section>

      <ApplyManifest key={current.updated_at} connector={current} />
    </Inspector>
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
  onApplied?: (applied: Connector | undefined) => void;
}) {
  const queryClient = useQueryClient();
  const form = useForm<ApplyValues>({
    resolver: zodResolver(applySchema),
    defaultValues: {
      key: connector?.key ?? "",
      contract_version: String(connector?.contract_version ?? 1),
      manifest: formatJson(connector?.manifest),
    },
  });

  const apply = useMutation({
    mutationFn: (values: ApplyValues) =>
      call(() =>
        api.PUT("/admin/connectors/{key}/versions/{contractVersion}", {
          params: { path: { key: values.key, contractVersion: Number(values.contract_version) } },
          body: parseJson(values.manifest).value,
        }),
      ),
    onSuccess: (applied) => {
      void queryClient.invalidateQueries({ queryKey: ["connectors"] });
      void queryClient.invalidateQueries({ queryKey: ["connector-options"] });
      if (connector) void queryClient.invalidateQueries({ queryKey: ["connector", connector.id] });
      onApplied?.(applied);
    },
  });

  const submit = form.handleSubmit((values) =>
    apply.mutate(values, { onError: (failure) => applyProblem(form, failure, applyFields) }),
  );

  return (
    <Form {...form}>
      <Panel asChild>
        <form className="flex flex-col gap-4" onSubmit={submit}>
          <h2>{connector ? "Apply a manifest" : "Install a Connector"}</h2>
          <FormError message={formError(asProblem(apply.error), applyFields)} />

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

          <Button type="submit" className="self-start" disabled={apply.isPending}>
            {connector ? "Apply manifest" : "Install Connector"}
          </Button>
        </form>
      </Panel>
    </Form>
  );
}
