import { zodResolver } from "@hookform/resolvers/zod";
import { useInfiniteQuery, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { useForm } from "react-hook-form";
import { NavLink } from "react-router";
import { z } from "zod";
import { Button } from "@/components/ui/button";
import { SelectItem } from "@/components/ui/select";
import { TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import { api } from "../api/client";
import { formError } from "../api/problem";
import { asProblem, call, nextCursor } from "../api/query";
import type { components } from "../api/schema";
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
import { Filter, Form, TextField } from "../ui/fields";
import { useFilterParam } from "../ui/filters";
import { applyProblem } from "../ui/formProblem";
import {
  CloseInspector,
  Details,
  Inspector,
  InspectorPlaceholder,
  openRow,
  Page,
  PageHeader,
  Panel,
  RowChevron,
  RowHeader,
  SplitList,
  SplitView,
  TableCard,
} from "../ui/layout";
import { StatusBadge } from "../ui/status";
import { Day, Timestamp } from "../ui/time";

type TenantApiKeyListItem = components["schemas"]["TenantApiKeyListItemDto"];
type CreatedKey = components["schemas"]["CreateTenantApiKeyResult"];

const createFields = ["name", "description", "expires_at"] as const;

const createSchema = z.object({
  name: z.string().trim().min(1, "Enter a name."),
  description: z.string(),
  expires_at: z.string(),
});

type CreateValues = z.infer<typeof createSchema>;

export function TenantApiKeysScreen({
  tenantId,
  selectedTenantApiKeyId,
}: {
  tenantId: string;
  selectedTenantApiKeyId?: string;
}) {
  const [notice, setNotice] = useState("");
  const [state, setState] = useFilterParam("state");
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
  const narrowing = narrowable(list.isSuccess, keys.length, state ? 1 : 0);

  const [creating, setCreating] = useState(false);
  const create = <SheetButton label="New API key" expanded={creating} onOpen={() => setCreating(true)} />;

  return (
    <Page>
      <PageHeader title="API keys" action={narrowing ? create : undefined}>
        Tenant credentials for the intake endpoint. The token itself is shown once, at creation.
      </PageHeader>

      <section className="flex flex-col gap-4">
        {narrowing ? (
          <FilterBar applied={state ? 1 : 0}>
            <Filter id="tenant-api-key-state" label="State" value={state} onChange={setState}>
              <SelectItem value="active">Active</SelectItem>
              <SelectItem value="expired">Expired</SelectItem>
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
              applied={state ? 1 : 0}
              noun="API keys"
              emptyText="Tenant credentials for the intake endpoint. Without one, nothing can post an Event to this Tenant."
              action={create}
            />
            {keys.length > 0 ? (
              <TableCard
                caption={`API keys, newest first${appliedNote(state ? 1 : 0)}`}
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
                    <TableHead scope="col">Expires</TableHead>
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
                      <TableCell className="font-mono text-[13px]">{key.key_prefix}</TableCell>
                      <TableCell>
                        <StatusBadge status={key.state} />
                      </TableCell>
                      <TableCell className="text-ink-secondary">
                        {key.expires_at ? <Day value={key.expires_at} /> : "Never"}
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
              Select a key to read when it was last used and to revoke it.
            </InspectorPlaceholder>
          ) : null}
        </SplitView>
      </section>

      <CreateSheet
        label="New API key"
        description="The token is shown once, at creation"
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
        <dt>Expires</dt>
        <dd>{current.expires_at ? <Timestamp value={current.expires_at} /> : "Never"}</dd>
        <dt>Last used</dt>
        <dd>{current.last_used_at ? <Timestamp value={current.last_used_at} /> : "Never used"}</dd>
      </Details>

      {current.description ? <p className="m-0 text-[13px] text-ink-secondary">{current.description}</p> : null}

      <RevokeTenantApiKey
        tenantId={tenantId}
        apiKey={{
          id: current.id,
          name: current.name,
          keyPrefix: current.key_prefix,
          revoked: current.state === "revoked",
        }}
        onDone={() => onRevoked(current.name)}
      />
    </Inspector>
  );
}

function RevokeTenantApiKey({
  tenantId,
  apiKey,
  onDone,
}: {
  tenantId: string;
  // The two reads spell the same field differently — the list says `state`, the detail says
  // `status` — so the control takes what it actually needs rather than either DTO.
  apiKey: { id: string; name: string; keyPrefix: string; revoked: boolean };
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
      return queryClient.invalidateQueries({ queryKey: ["tenant-api-keys", tenantId] });
    },
  });

  if (apiKey.revoked)
    return (
      <p className="m-0 text-[13px] text-ink-secondary">
        Revoked keys are kept so a request that still carries one can be recognised in the logs.
      </p>
    );

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
  const form = useForm<CreateValues>({
    resolver: zodResolver(createSchema),
    defaultValues: { name: "", description: "", expires_at: "" },
  });

  const create = useMutation({
    mutationFn: (values: CreateValues) =>
      call(() =>
        api.POST("/admin/tenants/{tenantId}/tenant-api-keys", {
          params: { path: { tenantId } },
          body: {
            name: values.name,
            description: values.description.trim() || null,
            // A local datetime-local value carries no offset, so it is sent as an instant the
            // server can read unambiguously rather than as the browser's own wall clock.
            expires_at: values.expires_at ? new Date(values.expires_at).toISOString() : null,
          },
        }),
      ),
    onSuccess: (result) => {
      form.reset();
      // The token is in this response and nowhere else; the list below re-reads and will only ever
      // carry the key's prefix.
      setCreated(result ?? null);
      void queryClient.invalidateQueries({ queryKey: ["tenant-api-keys", tenantId] });
    },
  });

  const submit = form.handleSubmit((values) =>
    create.mutate(values, { onError: (failure) => applyProblem(form, failure, createFields) }),
  );

  return (
    <div className="flex flex-col gap-4">
      <Form {...form}>
        <form className="flex flex-col gap-4" noValidate onSubmit={submit} aria-label="Create a Tenant API key">
          <FormError message={formError(asProblem(create.error), createFields)} />

          <TextField control={form.control} name="name" label="Name" required />
          <TextField control={form.control} name="description" label="Description (optional)" />
          <TextField
            control={form.control}
            name="expires_at"
            label="Expires (optional)"
            hint="Leave empty for a key that does not expire."
            type="datetime-local"
          />

          <Button type="submit" className="self-start" disabled={create.isPending}>
            Create Tenant API key
          </Button>
        </form>
      </Form>

      {/* The token exists in this response and nowhere else — the server stores only its hash, so it
          is shown once, here, and is gone as soon as this panel is dismissed. */}
      {created ? (
        <Panel asChild aria-label={`New Tenant API key ${created.tenant_api_key.name}`}>
          <section className="flex flex-col gap-3">
            <h3>Copy the key for {created.tenant_api_key.name} now</h3>
            <p role="status" className="m-0 text-ink-secondary">
              This key is shown once. It cannot be read again after you dismiss this message.
            </p>
            <output className="rounded-md bg-surface-quiet px-3 py-2 font-mono text-sm break-all">
              {created.token}
            </output>
            {/* The sheet stays open through the create: this token is in that one response and
                nowhere else, so closing on success would destroy the only copy of it. Dismissing the
                message is what says the Operator has it, and only then does the sheet close. */}
            <Button
              type="button"
              variant="outline"
              className="self-start"
              onClick={() => {
                setCreated(null);
                onCreated();
              }}
            >
              I have copied the key
            </Button>
          </section>
        </Panel>
      ) : null}
    </div>
  );
}
