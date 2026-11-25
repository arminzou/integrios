import { zodResolver } from "@hookform/resolvers/zod";
import { useInfiniteQuery, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useEffect, useState } from "react";
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
import { SecretReveal } from "../ui/copy";
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

type SubscriberKeyListItem = components["schemas"]["SubscriberKeyListItemDto"];
type CreatedKey = components["schemas"]["CreateSubscriberKeyResult"];

const createFields = ["name", "description"] as const;

const createSchema = z.object({
  name: z.string().trim().min(1, "Enter a name."),
  description: z.string(),
});

type CreateValues = z.infer<typeof createSchema>;

const apiKeyFilters = ["state"] as const;

export function SubscriberKeysScreen({
  tenantId,
  destinationId,
  selectedSubscriberKeyId,
}: {
  tenantId: string;
  destinationId: string;
  selectedSubscriberKeyId?: string;
}) {
  const [notice, setNotice] = useState("");
  const filters = useListFilters(apiKeyFilters);
  const { state } = filters.values;
  const list = useInfiniteQuery({
    queryKey: ["subscriber-keys", tenantId, destinationId, { state }],
    queryFn: ({ pageParam }) =>
      call(() =>
        api.GET("/admin/tenants/{tenantId}/destinations/{destinationId}/subscriber-keys", {
          params: {
            path: { tenantId, destinationId },
            query: { state: state || undefined, after: pageParam ?? undefined, limit: 20 },
          },
        }),
      ),
    initialPageParam: null as string | null,
    getNextPageParam: nextCursor<SubscriberKeyListItem>,
  });
  const keys = list.data?.pages.flatMap((page) => page.items) ?? [];
  const narrowing = narrowable(list.isSuccess, keys.length, filters.applied);

  const [creating, setCreating] = useState(false);
  const create = <SheetButton label="New Subscriber key" expanded={creating} onOpen={() => setCreating(true)} />;

  return (
    <Page>
      <PageHeader title="Subscriber keys" action={narrowing ? create : undefined}>
        Credentials for this pull Destination. The token itself is shown once, at creation.
      </PageHeader>

      {narrowing ? (
        <FilterBar applied={filters.applied} onClear={filters.clear}>
          <Filter
            id="subscriber-key-state"
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
            noun="Subscriber keys"
            emptyText="Credentials for this pull Destination. Without one, no Subscriber can claim its deliveries."
            action={create}
          />
          {keys.length > 0 ? (
            <TableCard
              caption={`Subscriber keys, newest first${appliedNote(filters.applied)}`}
              footer={
                <LoadMore
                  noun="Subscriber key"
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
                        to={`/tenants/${tenantId}/destinations/${destinationId}/subscriber-keys/${key.id}`}
                        end
                      >
                        {key.name}
                      </NavLink>
                    </RowHeader>
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

        {selectedSubscriberKeyId ? (
          <SubscriberKeyInspector
            key={selectedSubscriberKeyId}
            tenantId={tenantId}
            destinationId={destinationId}
            subscriberKeyId={selectedSubscriberKeyId}
            onRevoked={(name) => setNotice(`${name} revoked.`)}
            onDeleted={(name) => setNotice(`${name} deleted.`)}
          />
        ) : keys.length > 0 ? (
          <InspectorPlaceholder label="Subscriber key detail">
            Select a key to read when it was last used, or to manage its lifecycle.
          </InspectorPlaceholder>
        ) : null}
      </SplitView>

      <CreateSheet
        label="New Subscriber key"
        description="Lets one Subscriber consume deliveries for this Destination"
        open={creating}
        onOpenChange={setCreating}
      >
        {(close) => <CreateSubscriberKey tenantId={tenantId} destinationId={destinationId} onCreated={close} />}
      </CreateSheet>
    </Page>
  );
}

function SubscriberKeyInspector({
  tenantId,
  destinationId,
  subscriberKeyId,
  onRevoked,
  onDeleted,
}: {
  tenantId: string;
  destinationId: string;
  subscriberKeyId: string;
  onRevoked: (name: string) => void;
  onDeleted: (name: string) => void;
}) {
  const navigate = useNavigate();
  const apiKey = useQuery({
    queryKey: ["subscriber-key", tenantId, destinationId, subscriberKeyId],
    queryFn: () =>
      call(() =>
        api.GET("/admin/tenants/{tenantId}/destinations/{destinationId}/subscriber-keys/{id}", {
          params: { path: { tenantId, destinationId, id: subscriberKeyId } },
        }),
      ),
  });

  const closed = `/tenants/${tenantId}/destinations/${destinationId}/subscriber-keys`;
  const problem = asProblem(apiKey.error);
  if (problem)
    return (
      <Inspector label="Subscriber key detail">
        <div className="flex items-start justify-between gap-3">
          <h2 className="m-0">Subscriber key</h2>
          <div className="flex h-5 items-center">
            <CloseInspector to={closed} label="Close the Subscriber key detail" />
          </div>
        </div>
        <ReadError problem={problem} what="This key" back={{ to: closed, label: "Back to Subscriber keys" }} />
      </Inspector>
    );
  if (!apiKey.data) return <Inspector label="Subscriber key detail">Loading…</Inspector>;

  const current = apiKey.data;
  return (
    <Inspector label="Subscriber key detail">
      <div className="flex items-start justify-between gap-3">
        <h2 className="min-w-0">
          {current.name}
          <span className="block font-mono font-normal break-all text-ink-secondary">{current.key_prefix}…</span>
        </h2>
        <div className="flex h-5 shrink-0 items-center gap-1.5">
          <StatusBadge status={current.state} />
          <CloseInspector to={closed} label="Close the Subscriber key detail" />
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

      {current.description ? (
        <p className="m-0 border-b pb-3.5 text-[13px] text-ink-secondary">{current.description}</p>
      ) : null}

      {current.revoked_at ? (
        <DeleteSubscriberKey
          tenantId={tenantId}
          destinationId={destinationId}
          apiKey={{ id: current.id, name: current.name, keyPrefix: current.key_prefix }}
          onDone={() => {
            onDeleted(current.name);
            navigate(closed);
          }}
        />
      ) : (
        <RevokeSubscriberKey
          tenantId={tenantId}
          destinationId={destinationId}
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

function DeleteSubscriberKey({
  tenantId,
  destinationId,
  apiKey,
  onDone,
}: {
  tenantId: string;
  destinationId: string;
  apiKey: { id: string; name: string; keyPrefix: string };
  onDone: () => void;
}) {
  const queryClient = useQueryClient();
  const remove = useMutation({
    mutationFn: () =>
      call(() =>
        api.DELETE("/admin/tenants/{tenantId}/destinations/{destinationId}/subscriber-keys/{id}", {
          params: { path: { tenantId, destinationId, id: apiKey.id } },
        }),
      ),
    onSuccess: () => {
      onDone();
      return queryClient.invalidateQueries({ queryKey: ["subscriber-keys", tenantId, destinationId] });
    },
  });

  return (
    <div className="flex flex-col items-start gap-2">
      <ConfirmAction
        label="Delete"
        consequence={`Deleting ${apiKey.name} removes it from normal views. It cannot be restored.`}
        question={`Delete the revoked Subscriber key "${apiKey.name}" (${apiKey.keyPrefix})? It will disappear from normal views and cannot be restored.`}
        confirmLabel={`Delete ${apiKey.name}`}
        busy={remove.isPending}
        onConfirm={() => remove.mutate()}
      />
      <FormError message={formError(asProblem(remove.error))} />
    </div>
  );
}

function RevokeSubscriberKey({
  tenantId,
  destinationId,
  apiKey,
  onDone,
}: {
  tenantId: string;
  destinationId: string;
  apiKey: { id: string; name: string; keyPrefix: string };
  onDone: () => void;
}) {
  const queryClient = useQueryClient();
  const revoke = useMutation({
    mutationFn: () =>
      call(() =>
        api.POST("/admin/tenants/{tenantId}/destinations/{destinationId}/subscriber-keys/{id}/revoke", {
          params: { path: { tenantId, destinationId, id: apiKey.id } },
        }),
      ),
    onSuccess: () => {
      onDone();
      void queryClient.invalidateQueries({ queryKey: ["subscriber-key", tenantId, destinationId, apiKey.id] });
      return queryClient.invalidateQueries({ queryKey: ["subscriber-keys", tenantId, destinationId] });
    },
  });

  return (
    <div className="flex flex-col items-start gap-2">
      <ConfirmAction
        label="Revoke"
        consequence={`Revoking ${apiKey.name} rejects every request carrying it, immediately and permanently.`}
        question={`Revoke the Subscriber key "${apiKey.name}" (${apiKey.keyPrefix})? Callers using it stop being authenticated immediately.`}
        confirmLabel={`Revoke ${apiKey.name}`}
        busy={revoke.isPending}
        onConfirm={() => revoke.mutate()}
      />
      <FormError message={formError(asProblem(revoke.error))} />
    </div>
  );
}

function CreateSubscriberKey({
  tenantId,
  destinationId,
  onCreated,
}: {
  tenantId: string;
  destinationId: string;
  onCreated: () => void;
}) {
  const [created, setCreated] = useState<CreatedKey | null>(null);
  const queryClient = useQueryClient();
  const navigate = useNavigate();
  const form = useForm<CreateValues>({
    resolver: zodResolver(createSchema),
    defaultValues: { name: "", description: "" },
  });

  const create = useMutation({
    meta: { holdsSheet: true },
    mutationFn: (values: CreateValues) =>
      call(() =>
        api.POST("/admin/tenants/{tenantId}/destinations/{destinationId}/subscriber-keys", {
          params: { path: { tenantId, destinationId } },
          body: {
            name: values.name,
            description: values.description.trim() || null,
          },
        }),
      ),
    onSuccess: (result) => setCreated(result ?? null),
  });

  // The list re-reads, carrying only the key's prefix, once the reveal closes. Re-reading while it is
  // open could answer 401 and move to sign-in, unmounting the only copy of the key.
  useEffect(() => {
    if (created)
      return () => void queryClient.invalidateQueries({ queryKey: ["subscriber-keys", tenantId, destinationId] });
  }, [created, queryClient, tenantId, destinationId]);

  const submit = form.handleSubmit((values) =>
    create.mutate(values, { onError: (failure) => applyProblem(form, failure, createFields) }),
  );

  if (created) {
    return (
      <section className="flex flex-col gap-4" aria-label={`New Subscriber key ${created.subscriber_key.name}`}>
        <h3 role="status" className="m-0">
          {created.subscriber_key.name} is active
        </h3>
        <SecretReveal value={created.token} />
        <Button
          type="button"
          className="self-start"
          onClick={() => {
            onCreated();
            navigate(`/tenants/${tenantId}/destinations/${destinationId}/subscriber-keys/${created.subscriber_key.id}`);
          }}
        >
          Done
        </Button>
      </section>
    );
  }

  return (
    <Form {...form}>
      <form className="flex flex-col gap-4" noValidate onSubmit={submit} aria-label="Create a Subscriber key">
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
          Create Subscriber key
        </Button>
      </form>
    </Form>
  );
}
