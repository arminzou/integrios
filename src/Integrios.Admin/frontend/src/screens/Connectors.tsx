import { useInfiniteQuery, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { NavLink, useNavigate } from "react-router";
import { Button } from "@/components/ui/button";
import { Label } from "@/components/ui/label";
import { SelectItem } from "@/components/ui/select";
import { TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import { Textarea } from "@/components/ui/textarea";
import { api } from "../api/client";
import { formError, type Problem } from "../api/problem";
import { asProblem, call, nextCursor } from "../api/query";
import type { components } from "../api/schema";
import {
  appliedNote,
  CreateSheet,
  EditSheet,
  FilterBar,
  FormError,
  ListStatus,
  LoadMore,
  narrowable,
  ReadError,
  SheetButton,
  WriteStatus,
} from "../ui/controls";
import { Filter } from "../ui/fields";
import { useFilterParam } from "../ui/filters";
import { formatJson, parseJson } from "../ui/json";
import {
  CloseInspector,
  Details,
  Inspector,
  InspectorPlaceholder,
  openRow,
  Page,
  PageHeader,
  RowChevron,
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
  const [importing, setImporting] = useState(false);
  const create = <SheetButton label="New Connector" expanded={creating} onOpen={() => setCreating(true)} />;
  const actions = (
    <div className="flex flex-wrap gap-2">
      <SheetButton label="Import manifest" expanded={importing} onOpen={() => setImporting(true)} variant="outline" />
      {create}
    </div>
  );

  return (
    <Page>
      <PageHeader title="Connectors" action={narrowing ? actions : undefined}>
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
              action={actions}
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
                    <TableRow
                      key={connector.id}
                      className="group cursor-pointer has-[a[aria-current=page]]:bg-selected-surface"
                      onClick={openRow}
                    >
                      <RowHeader>
                        <NavLink
                          className="-mx-3 block px-3 py-2 font-mono text-[13px] no-underline"
                          to={`/connectors/${connector.id}`}
                          end
                        >
                          {connector.key}
                        </NavLink>
                      </RowHeader>
                      <TableCell>{connector.name}</TableCell>
                      <TableCell>{connector.direction}</TableCell>
                      <TableCell>v{connector.contract_version}</TableCell>
                      <TableCell>
                        <div className="flex items-center justify-between gap-3">
                          <StatusBadge status={connector.status} />
                          <RowChevron />
                        </div>
                      </TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </TableCard>
            ) : null}
          </SplitList>

          {selectedConnectorId ? (
            <ConnectorInspector key={selectedConnectorId} connectorId={selectedConnectorId} />
          ) : connectors.length > 0 ? (
            <InspectorPlaceholder label="Connector detail">
              Select a Connector to read its manifest and what it permits.
            </InspectorPlaceholder>
          ) : null}
        </SplitView>
      </section>

      <CreateSheet
        label="New Connector"
        description="Declare one external system's contract."
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

      <CreateSheet
        label="Import manifest"
        description="Apply a complete Connector manifest as written."
        open={importing}
        onOpenChange={setImporting}
      >
        {() => <ConnectorManifestImport />}
      </CreateSheet>
    </Page>
  );
}

const isObject = (value: unknown): value is Record<string, unknown> =>
  value !== null && typeof value === "object" && !Array.isArray(value);

function ConnectorManifestImport() {
  const queryClient = useQueryClient();
  const [text, setText] = useState("");
  const parsed = text.trim() ? parseJson(text) : undefined;
  const document = parsed && "value" in parsed && isObject(parsed.value) ? parsed.value : undefined;
  const key = typeof document?.key === "string" && document.key.trim() ? document.key.trim() : undefined;
  const contractVersion =
    typeof document?.contract_version === "number" &&
    Number.isInteger(document.contract_version) &&
    document.contract_version > 0
      ? document.contract_version
      : undefined;
  const schemaVersion = document?.manifest_schema_version;
  const identity = key && contractVersion ? { key, contractVersion } : undefined;
  const reason = !text.trim()
    ? "Paste a manifest to identify the Connector and contract version."
    : parsed && "error" in parsed
      ? parsed.error
      : !document
        ? "The manifest must be a JSON object."
        : !key
          ? "The manifest must carry a non-empty string key."
          : !contractVersion
            ? "The manifest must carry a positive integer contract_version."
            : `Ready to apply ${key} contract v${contractVersion}.`;

  const apply = useMutation({
    mutationFn: async () => {
      if (!identity || !document) throw new Error("No valid manifest is ready to apply.");
      const response = await api.PUT("/admin/connectors/{key}/versions/{contractVersion}", {
        params: { path: identity },
        body: document,
      });
      await call(() => Promise.resolve(response));
      const outcome = response.response.headers.get("X-Integrios-Connector-Manifest-Outcome");
      if (!outcome || !["Created", "Unchanged", "PresentationReconciled"].includes(outcome))
        throw {
          status: 500,
          detail: "Admin did not report the Connector manifest outcome.",
          errors: {},
        } satisfies Problem;
      return outcome;
    },
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ["connectors"] });
      void queryClient.invalidateQueries({ queryKey: ["connector"] });
      void queryClient.invalidateQueries({ queryKey: ["connector-options"] });
    },
  });

  return (
    <form
      className="flex flex-col gap-4"
      aria-label="Import manifest"
      onSubmit={(event) => {
        event.preventDefault();
        if (identity) apply.mutate();
      }}
    >
      <FormError message={formError(asProblem(apply.error))} />
      <div className="flex flex-col gap-1.5">
        <Label htmlFor="connector-manifest-import">Connector manifest</Label>
        <Textarea
          id="connector-manifest-import"
          value={text}
          disabled={apply.isPending}
          spellCheck={false}
          onChange={(event) => {
            if (apply.isPending) return;
            setText(event.target.value);
            apply.reset();
          }}
          className="min-h-72 font-mono text-sm"
          aria-describedby="connector-manifest-import-status"
        />
        <p id="connector-manifest-import-status" className="m-0 text-xs text-ink-secondary">
          {reason}
        </p>
      </div>

      {document ? (
        <p className="m-0 text-sm">
          Manifest schema version: {typeof schemaVersion === "number" ? schemaVersion : "not detected"}.
        </p>
      ) : null}
      <p className="m-0 text-xs text-ink-secondary">
        See the{" "}
        <a
          href="https://github.com/arminzou/integrios/blob/main/docs/connector-manifest.md"
          target="_blank"
          rel="noreferrer"
          className="inline-flex min-h-6 items-center"
        >
          Connector manifest reference
        </a>
        .
      </p>

      <Button type="submit" className="self-start" disabled={!identity || apply.isPending}>
        {apply.isPending ? "Applying manifest…" : "Apply manifest"}
      </Button>
      <WriteStatus done={apply.isSuccess}>
        {apply.data ? `${apply.data} — ${identity?.key} contract v${identity?.contractVersion}.` : null}
      </WriteStatus>
    </form>
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
  const [showRaw, setShowRaw] = useState(false);

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
  const manifest = isObject(current.manifest) ? current.manifest : {};
  const knownSchema = Number(current.manifest_schema_version) === 1;
  const sourceVerification = isObject(manifest.source_verification) ? manifest.source_verification : {};
  const destinationAuthentication = isObject(manifest.destination_authentication)
    ? manifest.destination_authentication
    : {};
  const sourceSchemes = schemeNames(sourceVerification.schemes);
  const destinationSchemes = schemeNames(destinationAuthentication.schemes);
  const sourceFields = requiredFields(manifest.source_configuration_schema);
  const destinationFields = requiredFields(manifest.destination_configuration_schema);
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
        {knownSchema ? (
          <>
            <dt>Source verification</dt>
            <dd>
              <span className="block">{sourceSchemes.join(", ") || "None"}</span>
              <span className="block text-xs text-ink-secondary">
                Selection {sourceVerification.allow_unverified === true ? "optional" : "required"}.
              </span>
              {sourceSchemes.length === 0 ? (
                <span className="block text-xs text-danger-ink">Sources on this Connector cannot verify Events.</span>
              ) : null}
            </dd>
            <dt>Destination authentication</dt>
            <dd>
              <span className="block">{destinationSchemes.join(", ") || "None"}</span>
              <span className="block text-xs text-ink-secondary">
                Selection {destinationAuthentication.allow_unauthenticated === true ? "optional" : "required"}.
              </span>
              {destinationSchemes.length === 0 ? (
                <span className="block text-xs text-danger-ink">
                  Destinations on this Connector cannot authenticate Deliveries.
                </span>
              ) : null}
            </dd>
            <dt>Source required configuration</dt>
            <dd>{sourceFields.join(", ") || "None"}</dd>
            <dt>Destination required configuration</dt>
            <dd>{destinationFields.join(", ") || "None"}</dd>
          </>
        ) : null}
      </Details>

      {current.description ? <p className="m-0 text-[13px] text-ink-secondary">{current.description}</p> : null}

      <section className="flex min-w-0 flex-col gap-2">
        <h3 className="eyebrow">Manifest</h3>
        {!knownSchema ? (
          <p className="m-0 text-[13px] text-ink-secondary">
            This dashboard does not explain manifest schema version {current.manifest_schema_version}. Review the raw
            JSON.
          </p>
        ) : (
          <Button
            type="button"
            variant="outline"
            size="sm"
            className="self-start"
            aria-expanded={showRaw}
            onClick={() => setShowRaw((visible) => !visible)}
          >
            {showRaw ? "Hide raw JSON" : "Show raw JSON"}
          </Button>
        )}
        {!knownSchema || showRaw ? <pre className="text-xs">{formatJson(current.manifest)}</pre> : null}
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

function schemeNames(value: unknown) {
  return Array.isArray(value)
    ? value.flatMap((scheme) => (isObject(scheme) && typeof scheme.scheme === "string" ? [scheme.scheme] : []))
    : [];
}

function requiredFields(value: unknown) {
  if (!isObject(value) || !Array.isArray(value.required)) return [];
  return value.required.filter((field): field is string => typeof field === "string");
}
