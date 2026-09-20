import { zodResolver } from "@hookform/resolvers/zod";
import { useInfiniteQuery, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
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
import { CodeBlock } from "../ui/codeHighlight";
import {
  appliedNote,
  ConfirmAction,
  CreateSheet,
  FilterBar,
  FormError,
  ListStatus,
  LoadMore,
  narrowable,
  ReadError,
  SheetButton,
  WriteStatus,
} from "../ui/controls";
import { BodyPanel } from "../ui/copy";
import { Filter, Form, TextField } from "../ui/fields";
import { useListFilters } from "../ui/filters";
import { applyProblem } from "../ui/formProblem";
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
import { Timestamp } from "../ui/time";

type TenantApiKeyListItem = components["schemas"]["TenantApiKeyListItemDto"];
type CreatedKey = components["schemas"]["CreateTenantApiKeyResult"];

const createFields = ["name", "description"] as const;

const createSchema = z.object({
  name: z.string().trim().min(1, "Enter a name."),
  description: z.string(),
});

type CreateValues = z.infer<typeof createSchema>;

const apiKeyFilters = ["state"] as const;

export function TenantApiKeysScreen({
  tenantId,
  selectedTenantApiKeyId,
}: {
  tenantId: string;
  selectedTenantApiKeyId?: string;
}) {
  const [notice, setNotice] = useState("");
  const filters = useListFilters(apiKeyFilters);
  const { state } = filters.values;
  const list = useInfiniteQuery({
    queryKey: ["tenant-api-keys", tenantId, { state }],
    queryFn: ({ pageParam }) =>
      call(() =>
        api.GET("/admin/tenants/{tenantId}/tenant-api-keys", {
          params: {
            path: { tenantId },
            query: { state: state || undefined, after: pageParam ?? undefined, limit: 20 },
          },
        }),
      ),
    initialPageParam: null as string | null,
    getNextPageParam: nextCursor<TenantApiKeyListItem>,
  });
  const keys = list.data?.pages.flatMap((page) => page.items) ?? [];
  // Whether there is a list to narrow yet. Until the read answers, neither the filter bar nor the
  // header's create action is rendered: an empty scope answers with the card that replaces the
  // table, carrying the action itself, and a screen that guessed first would retract them.
  const narrowing = narrowable(list.isSuccess, keys.length, filters.applied);

  const [creating, setCreating] = useState(false);
  const create = <SheetButton label="New API key" expanded={creating} onOpen={() => setCreating(true)} />;

  return (
    <Page>
      <PageHeader title="API keys" action={narrowing ? create : undefined}>
        Tenant credentials for the intake endpoint. The token itself is shown once, at creation.
      </PageHeader>

      {narrowing ? (
        <FilterBar applied={filters.applied} onClear={filters.clear}>
          <Filter
            id="tenant-api-key-state"
            label="State"
            value={state}
            onChange={(value) => filters.set("state", value)}
          >
            <SelectItem value="active">Active</SelectItem>
            <SelectItem value="revoked">Revoked</SelectItem>
          </Filter>
        </FilterBar>
      ) : null}

      <WriteStatus done={notice !== ""}>{notice}</WriteStatus>
      <SplitView>
        <SplitList>
          <ListStatus
            busy={list.isFetching}
            loaded={list.isSuccess}
            problem={asProblem(list.error)}
            empty={keys.length === 0}
            applied={filters.applied}
            noun="API keys"
            emptyText="Tenant credentials for the intake endpoint. Without one, nothing can post an Event to this Tenant."
            action={create}
          />
          {keys.length > 0 ? (
            <TableCard
              caption={`API keys, newest first${appliedNote(filters.applied)}`}
              footer={
                <LoadMore
                  noun="API key"
                  hasMore={list.hasNextPage}
                  busy={list.isFetching}
                  loaded={keys.length}
                  onLoadMore={() => void list.fetchNextPage()}
                />
              }
            >
              <TableHeader>
                <TableRow>
                  <TableHead scope="col">Name</TableHead>
                  <TableHead scope="col">Prefix</TableHead>
                  <TableHead scope="col">State</TableHead>
                  <TableHead scope="col">Last used</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {keys.map((key) => (
                  <TableRow
                    key={key.id}
                    className="group cursor-pointer has-[a[aria-current=page]]:bg-selected-surface"
                    onClick={openRow}
                  >
                    <RowHeader>
                      <NavLink
                        className="-mx-3 block px-3 py-2 no-underline"
                        to={`/tenants/${tenantId}/tenant-api-keys/${key.id}`}
                        end
                      >
                        {key.name}
                      </NavLink>
                    </RowHeader>
                    {/* Only the prefix is ever stored or shown. The key itself exists once, at creation. */}
                    <TableCell className="font-mono">{key.key_prefix}</TableCell>
                    <TableCell>
                      <StatusBadge status={key.state} />
                    </TableCell>
                    <TableCell className="text-ink-secondary">
                      <div className="flex items-center justify-between gap-3">
                        {key.last_used_at ? <Timestamp value={key.last_used_at} /> : "Never used"}
                        <RowChevron />
                      </div>
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </TableCard>
          ) : null}
        </SplitList>

        {selectedTenantApiKeyId ? (
          <TenantApiKeyInspector
            key={selectedTenantApiKeyId}
            tenantId={tenantId}
            tenantApiKeyId={selectedTenantApiKeyId}
            onRevoked={(name) => setNotice(`${name} revoked.`)}
          />
        ) : keys.length > 0 ? (
          <InspectorPlaceholder label="Tenant API key detail">
            Select a key to read when it was last used, or to revoke it.
          </InspectorPlaceholder>
        ) : null}
      </SplitView>

      <CreateSheet
        label="New API key"
        description="Lets one system send Events to this Tenant"
        open={creating}
        onOpenChange={setCreating}
      >
        {(close) => <CreateTenantApiKey tenantId={tenantId} onCreated={close} />}
      </CreateSheet>
    </Page>
  );
}

/// The selected key beside the list. It reads the key by its own id rather than off the loaded page,
/// so a copied link resolves the same detail whether or not that row is in the list's current page.
function TenantApiKeyInspector({
  tenantId,
  tenantApiKeyId,
  onRevoked,
}: {
  tenantId: string;
  tenantApiKeyId: string;
  onRevoked: (name: string) => void;
}) {
  const apiKey = useQuery({
    queryKey: ["tenant-api-key", tenantId, tenantApiKeyId],
    queryFn: () =>
      call(() =>
        api.GET("/admin/tenants/{tenantId}/tenant-api-keys/{id}", {
          params: { path: { tenantId, id: tenantApiKeyId } },
        }),
      ),
  });

  const closed = `/tenants/${tenantId}/tenant-api-keys`;
  const problem = asProblem(apiKey.error);
  if (problem)
    return (
      <Inspector label="Tenant API key detail">
        <div className="flex items-start justify-between gap-3">
          <h2 className="m-0">Tenant API key</h2>
          <CloseInspector to={closed} label="Close the Tenant API key detail" />
        </div>
        <ReadError problem={problem} what="This key" back={{ to: closed, label: "Back to API keys" }} />
      </Inspector>
    );
  if (!apiKey.data) return <Inspector label="Tenant API key detail">Loading…</Inspector>;

  const current = apiKey.data;
  return (
    <Inspector label="Tenant API key detail">
      <div className="flex items-start justify-between gap-3">
        <h2 className="min-w-0">
          {current.name}
          {/* Only the prefix exists to show. The token itself is hashed at rest and was displayed
              once, at creation. */}
          <span className="block font-mono text-xs font-normal break-all text-ink-secondary">
            {current.key_prefix}…
          </span>
        </h2>
        <div className="flex shrink-0 items-center gap-1.5">
          <StatusBadge status={current.state} className="mt-0.5" />
          <CloseInspector to={closed} label="Close the Tenant API key detail" />
        </div>
      </div>

      <Details className="border-b pb-3.5">
        <dt>Created</dt>
        <dd>
          <Timestamp value={current.created_at} />
        </dd>
        <dt>Last used</dt>
        <dd>{current.last_used_at ? <Timestamp value={current.last_used_at} /> : "Never used"}</dd>
        {current.revoked_at ? (
          <>
            <dt>Revoked</dt>
            <dd>
              <Timestamp value={current.revoked_at} />
            </dd>
          </>
        ) : null}
      </Details>

      {current.description ? <p className="m-0 text-ink-secondary">{current.description}</p> : null}

      {/* Revocation is terminal, so a revoked key has nothing left to offer. */}
      {current.revoked_at ? null : (
        <RevokeTenantApiKey
          tenantId={tenantId}
          apiKey={{
            id: current.id,
            name: current.name,
            keyPrefix: current.key_prefix,
          }}
          onDone={() => onRevoked(current.name)}
        />
      )}
    </Inspector>
  );
}

function RevokeTenantApiKey({
  tenantId,
  apiKey,
  onDone,
}: {
  tenantId: string;
  apiKey: { id: string; name: string; keyPrefix: string };
  onDone: () => void;
}) {
  const queryClient = useQueryClient();
  const revoke = useMutation({
    mutationFn: () =>
      call(() =>
        api.POST("/admin/tenants/{tenantId}/tenant-api-keys/{id}/revoke", {
          params: { path: { tenantId, id: apiKey.id } },
        }),
      ),
    onSuccess: () => {
      onDone();
      // Re-read the key so its detail and the list show it as revoked rather than active.
      void queryClient.invalidateQueries({ queryKey: ["tenant-api-key", tenantId, apiKey.id] });
      return queryClient.invalidateQueries({ queryKey: ["tenant-api-keys", tenantId] });
    },
  });

  return (
    <div className="flex flex-col items-start gap-2">
      <ConfirmAction
        label="Revoke"
        consequence={`Revoking ${apiKey.name} rejects every request carrying it, immediately and permanently.`}
        question={`Revoke the Tenant API key "${apiKey.name}" (${apiKey.keyPrefix})? Callers using it stop being authenticated immediately.`}
        confirmLabel={`Revoke ${apiKey.name}`}
        busy={revoke.isPending}
        onConfirm={() => revoke.mutate()}
      />
      <FormError message={formError(asProblem(revoke.error))} />
    </div>
  );
}

function CreateTenantApiKey({ tenantId, onCreated }: { tenantId: string; onCreated: () => void }) {
  const [created, setCreated] = useState<CreatedKey | null>(null);
  const queryClient = useQueryClient();
  const navigate = useNavigate();
  const overview = useQuery({
    queryKey: ["tenant-overview", tenantId],
    queryFn: () => call(() => api.GET("/admin/tenants/{id}/overview", { params: { path: { id: tenantId } } })),
  });
  const form = useForm<CreateValues>({
    resolver: zodResolver(createSchema),
    defaultValues: { name: "", description: "" },
  });

  const create = useMutation({
    mutationFn: (values: CreateValues) =>
      call(() =>
        api.POST("/admin/tenants/{tenantId}/tenant-api-keys", {
          params: { path: { tenantId } },
          body: {
            name: values.name,
            description: values.description.trim() || null,
          },
        }),
      ),
    onSuccess: (result) => {
      // The list re-reads and will only ever carry the key's prefix.
      setCreated(result ?? null);
      void queryClient.invalidateQueries({ queryKey: ["tenant-api-keys", tenantId] });
    },
  });

  const submit = form.handleSubmit((values) =>
    create.mutate(values, { onError: (failure) => applyProblem(form, failure, createFields) }),
  );

  // The token exists in that one response and nowhere else — the server stores only its hash — so
  // once it arrives the form is gone: nothing is left to submit twice, and the sheet is the hand-off.
  if (created) {
    const base = overview.data?.ingestion_endpoint?.replace(/\/$/, "") ?? "<ingestion URL>";
    return (
      <section className="flex flex-col gap-4" aria-label={`New Tenant API key ${created.tenant_api_key.name}`}>
        <h3 role="status" className="m-0">
          {created.tenant_api_key.name} is active
        </h3>
        <p className="m-0 rounded-md bg-warning-surface p-3 text-sm text-warning-ink">
          This is the only time the key is shown. Store it in your secret manager before you close this.
        </p>
        <BodyPanel label="Key" value={created.token} language="text" unbounded />
        <section className="flex flex-col gap-2">
          <h4 className="m-0 text-sm font-semibold">Use it</h4>
          <CodeBlock
            value={`POST ${base}/events?source_id=<Source id>\nAuthorization: Bearer <this key>`}
            language="http"
          />
        </section>
        <Button
          type="button"
          className="self-start"
          onClick={() => {
            onCreated();
            navigate(`/tenants/${tenantId}/tenant-api-keys/${created.tenant_api_key.id}`);
          }}
        >
          Done
        </Button>
      </section>
    );
  }

  return (
    <Form {...form}>
      <form className="flex flex-col gap-4" noValidate onSubmit={submit} aria-label="Create a Tenant API key">
        <FormError message={formError(asProblem(create.error), createFields)} />

        <TextField
          control={form.control}
          name="name"
          label="Name"
          hint="Name the system that will use it. You'll find the key by this name when you revoke it."
          required
        />
        <TextField control={form.control} name="description" label="Description (optional)" />
        <Button type="submit" className="self-start" disabled={create.isPending}>
          Create API key
        </Button>
      </form>
    </Form>
  );
}
