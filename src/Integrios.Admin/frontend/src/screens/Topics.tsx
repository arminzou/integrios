import { zodResolver } from "@hookform/resolvers/zod";
import { useInfiniteQuery, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useEffect, useState } from "react";
import { useForm, useWatch } from "react-hook-form";
import { Link, NavLink, useNavigate } from "react-router";
import { z } from "zod";
import { Button } from "@/components/ui/button";
import { TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import { api } from "../api/client";
import { formError } from "../api/problem";
import { asProblem, call, nextCursor } from "../api/query";
import type { components } from "../api/schema";
import { dnsLabel } from "../identifiers";
import {
  appliedNote,
  ConfirmAction,
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
import { CopyInline } from "../ui/copy";
import { FilterSearch, Form, TextField } from "../ui/fields";
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
import { monoInput } from "../ui/mono";
import { StatusBadge } from "../ui/status";

type Topic = components["schemas"]["AdminTopicResponse"];

const createFields = ["key", "name", "description"] as const;
const writeFields = ["name", "description"] as const;

/// The grammar is syntax, which the dashboard may check; whether a Topic is otherwise valid stays
/// with the Admin API.
const createSchema = z.object({
  key: z
    .string()
    .trim()
    .min(1, "Enter a key.")
    .regex(/^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$/, "Use lowercase letters, digits, and hyphens."),
  name: z.string().trim().min(1, "Enter a name."),
  description: z.string(),
});

const editSchema = z.object({
  name: z.string().trim().min(1, "Enter a name."),
  description: z.string(),
});

type CreateValues = z.infer<typeof createSchema>;
type EditValues = z.infer<typeof editSchema>;

const optional = (text: string) => text.trim() || null;

const topicFilters = ["name"] as const;

export function TopicsScreen({ tenantId, selectedTopicId }: { tenantId: string; selectedTopicId?: string }) {
  const filters = useListFilters(topicFilters);
  const { name } = filters.values;
  const applied = filters.applied;
  const list = useInfiniteQuery({
    queryKey: ["topics", tenantId, { name }],
    queryFn: ({ pageParam }) =>
      call(() =>
        api.GET("/admin/tenants/{tenantId}/topics", {
          params: {
            path: { tenantId },
            query: {
              name: name || undefined,
              after: pageParam ?? undefined,
              limit: 20,
            },
          },
        }),
      ),
    initialPageParam: null as string | null,
    getNextPageParam: nextCursor<Topic>,
  });
  const topics = list.data?.pages.flatMap((page) => page.items) ?? [];
  // Whether there is a list to narrow yet. Until the read answers, neither the filter bar nor the
  // header's create action is rendered: an empty scope answers with the card that replaces the
  // table, carrying the action itself, and a screen that guessed first would retract them.
  const narrowing = narrowable(list.isSuccess, topics.length, applied);
  const [creating, setCreating] = useState(false);
  const create = <SheetButton label="New Topic" expanded={creating} onOpen={() => setCreating(true)} />;

  return (
    <Page>
      <PageHeader title="Topics" action={narrowing ? create : undefined}>
        A Topic is the Tenant-scoped stream Subscriptions match against. Its key is immutable and travels with every
        Event delivered from it; its name is a label you can correct.
      </PageHeader>

      {narrowing ? (
        <FilterBar applied={applied} onClear={filters.clear}>
          <FilterSearch
            id="topic-name"
            label="Name"
            placeholder="Name contains…"
            value={name}
            onChange={(value) => filters.set("name", value)}
          />
        </FilterBar>
      ) : null}

      <SplitView>
        <SplitList>
          <ListStatus
            busy={list.isFetching}
            loaded={list.isSuccess}
            problem={asProblem(list.error)}
            empty={topics.length === 0}
            applied={applied}
            noun="Topics"
            emptyText="The Tenant-scoped stream Subscriptions match against. Events reach a Destination by the Topic they are accepted into."
            action={create}
          />
          {topics.length > 0 ? (
            <TableCard
              caption={`Topics, newest first${appliedNote(applied)}`}
              footer={
                <LoadMore
                  noun="Topic"
                  hasMore={list.hasNextPage}
                  busy={list.isFetching}
                  loaded={topics.length}
                  onLoadMore={() => void list.fetchNextPage()}
                />
              }
            >
              <TableHeader>
                <TableRow>
                  <TableHead scope="col">Name</TableHead>
                  <TableHead scope="col">Key</TableHead>
                  <TableHead scope="col">Description</TableHead>
                  <TableHead scope="col" className="text-center">
                    Subscriptions
                  </TableHead>
                  <TableHead scope="col">
                    <span className="sr-only">Open</span>
                  </TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {topics.map((topic) => (
                  <TableRow
                    key={topic.id}
                    className="group cursor-pointer has-[a[aria-current=page]]:bg-selected-surface"
                    onClick={openRow}
                  >
                    <RowHeader>
                      {/* The route is the selection, so `aria-current` follows the URL rather than a
                      separately tracked flag — the same contract every other ledger has. */}
                      <NavLink
                        className="-mx-3 block px-3 py-2 no-underline"
                        to={`/tenants/${tenantId}/topics/${topic.id}`}
                        end
                      >
                        {topic.name}
                      </NavLink>
                    </RowHeader>
                    <TableCell className="font-mono">{topic.key}</TableCell>
                    <TableCell className="text-ink-secondary">{topic.description ?? "—"}</TableCell>
                    {/* A Topic nothing subscribes to accepts Events and routes none of them, so
                        the count is what the list is scanned for rather than a detail. */}
                    <TableCell className="text-center">{topic.subscription_count}</TableCell>
                    <TableCell>
                      <div className="flex items-center justify-end">
                        <RowChevron />
                      </div>
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </TableCard>
          ) : null}
        </SplitList>

        {selectedTopicId ? (
          <TopicInspector key={selectedTopicId} tenantId={tenantId} topicId={selectedTopicId} />
        ) : topics.length > 0 ? (
          <InspectorPlaceholder label="Topic detail">
            Select a Topic to read its Subscriptions and what they match.
          </InspectorPlaceholder>
        ) : null}
      </SplitView>

      <CreateSheet
        label="New Topic"
        description="The Tenant-scoped stream Subscriptions match against"
        open={creating}
        onOpenChange={setCreating}
      >
        {(close) => <CreateTopic tenantId={tenantId} onCreated={close} />}
      </CreateSheet>
    </Page>
  );
}

/// The selected Topic beside the list. Its Subscriptions are summarised here rather than authored
/// here: what an Operator reads off a Topic is what matches it and where that goes, and a create
/// panel, a filter and a cursor-paged table inside a 400-pixel panel would be a list-with-detail
/// nested inside a detail. Authoring keeps its own route, which this panel links to.
function TopicInspector({ tenantId, topicId }: { tenantId: string; topicId: string }) {
  const topic = useQuery({
    queryKey: ["topic", tenantId, topicId],
    queryFn: () =>
      call(() => api.GET("/admin/tenants/{tenantId}/topics/{id}", { params: { path: { tenantId, id: topicId } } })),
  });
  // A summary, not the list: enough Subscriptions to see the shape of the Topic, with the authoring
  // route carrying the rest. It has no cursor because it is not paging anything.
  const subscriptions = useQuery({
    queryKey: ["subscription-summary", tenantId, topicId],
    queryFn: () =>
      call(() =>
        api.GET("/admin/tenants/{tenantId}/topics/{topicId}/subscriptions", {
          params: { path: { tenantId, topicId }, query: { limit: 20 } },
        }),
      ),
  });

  const problem = asProblem(topic.error);
  if (problem)
    return (
      <Inspector label="Topic detail">
        <div className="flex items-start justify-between gap-3">
          <h2 className="m-0">Topic</h2>
          <div className="flex h-5 items-center">
            <CloseInspector to={`/tenants/${tenantId}/topics`} label="Close the Topic detail" />
          </div>
        </div>
        <ReadError
          problem={problem}
          what="This Topic"
          back={{ to: `/tenants/${tenantId}/topics`, label: "Back to Topics" }}
        />
      </Inspector>
    );
  if (!topic.data) return <Inspector label="Topic detail">Loading…</Inspector>;

  const current = topic.data;
  const matched = subscriptions.data?.items ?? [];

  return (
    <Inspector label="Topic detail">
      <div className="flex flex-col">
        <div className="flex items-start justify-between gap-3">
          <h2 className="min-w-0 break-all">{current.name}</h2>
          <div className="flex h-5 shrink-0 items-center">
            <CloseInspector to={`/tenants/${tenantId}/topics`} label="Close the Topic detail" />
          </div>
        </div>
        <span className="block text-xs text-ink-secondary">
          <CopyInline label="Topic id" value={current.id} />
        </span>
      </div>

      <Details className="border-b pb-3.5">
        {/* Immutable, and what travels with every Event, so it is stated with the facts rather than
            standing under the title as a second name. */}
        <dt>Key</dt>
        <dd>
          <code className="font-mono break-all">{current.key}</code>
        </dd>
        <dt>Description</dt>
        <dd>{current.description ?? "—"}</dd>
        <dt>Subscriptions</dt>
        <dd className="tabular-nums">{current.subscription_count}</dd>
        {/* Read-only: a Topic owns no Event types. These are what its Sources declare, and they are
            edited there. */}
        <dt>Event types</dt>
        <dd className="flex flex-wrap justify-end gap-x-2 gap-y-1">
          {current.event_types.length === 0
            ? "None declared by a Source yet"
            : current.event_types.map((eventType) => (
                <code key={eventType} className="font-mono break-all">
                  {eventType}
                </code>
              ))}
        </dd>
      </Details>

      <section className="flex flex-col gap-2 border-b pb-3.5">
        <h3 className="eyebrow">Subscriptions</h3>
        {subscriptions.isPending ? <p className="m-0 text-ink-secondary">Loading…</p> : null}
        {/* A Topic nothing matches accepts Events and routes none of them, which is the established
            signal for a missing Subscription rather than an empty section. */}
        {subscriptions.isSuccess && matched.length === 0 ? (
          <p className="m-0 text-ink-secondary">
            No Subscription matches this Topic, so nothing accepted here is ever routed.
          </p>
        ) : null}
        {matched.map((subscription) => (
          <div
            key={subscription.id}
            className="flex items-center justify-between gap-2 rounded-md bg-surface-quiet px-2.5 py-2"
          >
            <div className="min-w-0">
              <Link
                className="block truncate no-underline"
                to={`/tenants/${tenantId}/subscriptions/${topicId}/${subscription.id}`}
              >
                {subscription.name}
              </Link>
              <span className="block truncate font-mono text-ink-secondary">→ {subscription.destination_name}</span>
            </div>
            <StatusBadge status={subscription.status} className="shrink-0" />
          </div>
        ))}
        <Button asChild variant="outline" size="sm" className="self-start">
          <Link className="no-underline" to={`/tenants/${tenantId}/subscriptions?topic_id=${topicId}`}>
            Manage Subscriptions
          </Link>
        </Button>
      </section>

      <EditTopic key={current.updated_at} tenantId={tenantId} topic={current} />
    </Inspector>
  );
}

function CreateTopic({ tenantId, onCreated }: { tenantId: string; onCreated: () => void }) {
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const form = useForm<CreateValues>({
    resolver: zodResolver(createSchema),
    defaultValues: { key: "", name: "", description: "" },
  });

  // The key follows the name until an Operator writes one of their own, so the common case is not a
  // transliteration done by hand. Authorship is recorded from their own keystroke rather than read
  // off the form's dirty state, which is recomputed against the defaults whenever a field returns to
  // one — that would freeze a key this form wrote the moment the name was cleared.
  const [keyAuthored, setKeyAuthored] = useState(false);
  const authoredName = useWatch({ control: form.control, name: "name" });
  useEffect(() => {
    if (keyAuthored) return;
    form.setValue("key", dnsLabel(authoredName));
  }, [keyAuthored, authoredName, form]);

  const create = useMutation({
    mutationFn: (values: CreateValues) =>
      call(() =>
        api.POST("/admin/tenants/{tenantId}/topics", {
          params: { path: { tenantId } },
          body: {
            key: values.key,
            name: values.name,
            description: optional(values.description),
          },
        }),
      ),
    onSuccess: (created) => {
      void queryClient.invalidateQueries({ queryKey: ["topics", tenantId] });
      onCreated();
      if (created) navigate(`/tenants/${tenantId}/topics/${created.id}`);
    },
  });

  const submit = form.handleSubmit((values) =>
    create.mutate(values, { onError: (failure) => applyProblem(form, failure, createFields) }),
  );

  return (
    <Form {...form}>
      <form className="flex flex-col gap-4" noValidate onSubmit={submit} aria-label="Create a Topic">
        <FormError message={formError(asProblem(create.error), createFields)} />

        {/* The label first, the key second: an Operator knows what they are calling this Topic before
            they know what to call it on the wire, and the key is read off the name. */}
        <TextField control={form.control} name="name" label="Name" required />
        <TextField
          control={form.control}
          name="key"
          label="Key"
          hint="Immutable, and delivered with every Event from this Topic."
          onChange={() => setKeyAuthored(true)}
          className={monoInput}
          required
        />
        <TextField control={form.control} name="description" label="Description (optional)" />

        <Button type="submit" className="self-start" disabled={create.isPending}>
          Create Topic
        </Button>
      </form>
    </Form>
  );
}

/// A Topic has no status of its own: it groups its Sources and Subscriptions and exists until deleted,
/// so the only thing to change on it is its label.
function EditTopic({ tenantId, topic }: { tenantId: string; topic: Topic }) {
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const reread = () => {
    void queryClient.invalidateQueries({ queryKey: ["topic", tenantId, topic.id] });
    void queryClient.invalidateQueries({ queryKey: ["topics", tenantId] });
  };
  const form = useForm<EditValues>({
    resolver: zodResolver(editSchema),
    defaultValues: { name: topic.name, description: topic.description ?? "" },
  });

  const save = useMutation({
    mutationFn: (values: EditValues) =>
      call(() =>
        api.PUT("/admin/tenants/{tenantId}/topics/{id}", {
          params: { path: { tenantId, id: topic.id } },
          body: { name: values.name, description: optional(values.description) },
        }),
      ),
    onSuccess: reread,
  });
  const remove = useMutation({
    mutationFn: () =>
      call(() =>
        api.DELETE("/admin/tenants/{tenantId}/topics/{id}", {
          params: { path: { tenantId, id: topic.id } },
        }),
      ),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ["topics", tenantId] });
      navigate(`/tenants/${tenantId}/topics`);
    },
  });

  return (
    <div className="flex flex-col gap-4">
      <div className="flex flex-wrap items-start gap-2">
        <EditSheet label="Edit">
          {(close) => (
            <Form {...form}>
              <form
                className="flex flex-col gap-4"
                aria-label={`Edit ${topic.name}`}
                noValidate
                onSubmit={form.handleSubmit((values) =>
                  save.mutate(values, {
                    onSuccess: close,
                    onError: (failure) => applyProblem(form, failure, writeFields),
                  }),
                )}
              >
                <FormError message={formError(asProblem(save.error), writeFields)} />

                <div>
                  <p className="m-0 text-sm font-medium">Key</p>
                  <p className="m-0 font-mono text-ink-secondary">{topic.key}</p>
                </div>
                <TextField control={form.control} name="name" label="Name" required />
                <TextField control={form.control} name="description" label="Description (optional)" />

                <Button type="submit" className="self-start" disabled={save.isPending}>
                  Save changes
                </Button>
                <WriteStatus done={save.isSuccess}>Changes saved.</WriteStatus>
              </form>
            </Form>
          )}
        </EditSheet>
        <ConfirmAction
          label="Delete"
          consequence="This Topic cannot be restored. Deletion is refused while any Source or Subscription still references it."
          question={`Delete the Topic "${topic.name}"?`}
          confirmLabel={`Delete ${topic.name}`}
          busy={remove.isPending}
          onConfirm={() => remove.mutate()}
        />
      </div>
      <FormError message={formError(asProblem(remove.error))} />
    </div>
  );
}
