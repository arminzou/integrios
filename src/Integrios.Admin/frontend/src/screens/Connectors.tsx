import { useInfiniteQuery, useQuery } from "@tanstack/react-query";
import { useState } from "react";
import { NavLink, useNavigate } from "react-router";
import { SelectItem } from "@/components/ui/select";
import { TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import { api } from "../api/client";
import { asProblem, call, nextCursor } from "../api/query";
import type { components } from "../api/schema";
import {
  appliedNote,
  CreateSheet,
  EditSheet,
  FilterBar,
  ListStatus,
  LoadMore,
  narrowable,
  ReadError,
  SheetButton,
} from "../ui/controls";
import { Filter } from "../ui/fields";
import { useFilterParam } from "../ui/filters";
import { formatJson } from "../ui/json";
import {
  CloseInspector,
  Details,
  Inspector,
  InspectorPlaceholder,
  Page,
  PageHeader,
  RowHeader,
  SplitList,
  SplitView,
  TableCard,
} from "../ui/layout";
import { StatusBadge } from "../ui/status";
import { ConnectorAuthoring } from "./ConnectorAuthoring";

type ConnectorListItem = components["schemas"]["ConnectorListItemDto"];

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
  // Whether there is a list to narrow yet. Until the read answers, neither the filter bar nor the
  // header's create action is rendered: an empty scope answers with the card that replaces the
  // table, carrying the action itself, and a screen that guessed first would retract them.
  const narrowing = narrowable(list.isSuccess, connectors.length, direction ? 1 : 0);

  const [creating, setCreating] = useState(false);
  const create = <SheetButton label="New Connector" expanded={creating} onOpen={() => setCreating(true)} />;

  return (
    <Page>
      <PageHeader title="Connectors" action={narrowing ? create : undefined}>
        Deployment-wide capability definitions. Sources and Destinations are built from these, per Tenant.
      </PageHeader>

      <section className="flex flex-col gap-4">
        {narrowing ? (
          <FilterBar applied={(direction ? 1 : 0) as number}>
            <Filter id="connector-direction" label="Direction" value={direction} onChange={setDirection}>
              <SelectItem value="source">Source</SelectItem>
              <SelectItem value="destination">Destination</SelectItem>
              <SelectItem value="both">Both</SelectItem>
            </Filter>
          </FilterBar>
        ) : null}

        <SplitView>
          <SplitList>
            <ListStatus
              busy={list.isFetching}
              loaded={list.isSuccess}
              problem={asProblem(list.error)}
              empty={connectors.length === 0}
              applied={direction ? 1 : 0}
              noun="Connectors"
              emptyText="A deployment-wide capability definition. Sources and Destinations are built from these, per Tenant."
              action={create}
            />
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
                    {/* The key is what an Operator writes in a manifest and what a Source or Destination is built
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
                      <RowHeader>
                        <NavLink className="font-mono text-[13px] no-underline" to={`/connectors/${connector.id}`} end>
                          {connector.key}
                        </NavLink>
                      </RowHeader>
                      <TableCell>{connector.name}</TableCell>
                      <TableCell>{connector.direction}</TableCell>
                      <TableCell>v{connector.contract_version}</TableCell>
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

      <CreateSheet
        label="New Connector"
        description="Build a reusable capability definition without writing its manifest."
        open={creating}
        onOpenChange={setCreating}
      >
        {(close) => (
          <ConnectorAuthoring
            onApplied={(installed) => {
              close();
              if (installed) navigate(`/connectors/${installed.id}`);
            }}
          />
        )}
      </CreateSheet>
    </Page>
  );
}

/// The selected Connector beside the list. A Connector is deployment-wide and read far more often
/// than it is applied — a Source or Destination configuration is validated against this manifest — so the
/// manifest is what the panel is mostly for.
function ConnectorInspector({ connectorId }: { connectorId: string }) {
  const navigate = useNavigate();
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
        <ReadError problem={problem} what="This Connector" back={{ to: "/connectors", label: "Back to Connectors" }} />
      </Inspector>
    );
  if (!connector.data) return <Inspector label="Connector detail">Loading…</Inspector>;

  const current = connector.data;
  return (
    <Inspector label="Connector detail">
      <div className="flex items-start justify-between gap-3">
        <h2 className="min-w-0">
          {current.name}
          <span className="block font-mono text-xs font-normal break-all text-ink-secondary">
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
        <h3 className="eyebrow">Manifest</h3>
        <pre className="text-xs">{formatJson(current.manifest)}</pre>
        <p className="m-0 text-xs text-ink-secondary">
          Applied by an Operator. A Connector is deployment-wide and shared by every Tenant.
        </p>
      </section>

      {/* An applied version is read here, never edited: authoring a change produces the next
          version through the same guided form the first one came from. */}
      <EditSheet
        label="Create new version"
        title="New Connector version"
        description={`Copied from ${current.key} v${current.contract_version}. Review it, then apply it as a later version.`}
      >
        {(close) => (
          <ConnectorAuthoring
            key={current.updated_at}
            from={current}
            onApplied={(next) => {
              close();
              if (next) navigate(`/connectors/${next.id}`);
            }}
          />
        )}
      </EditSheet>
    </Inspector>
  );
}
