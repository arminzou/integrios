import { zodResolver } from "@hookform/resolvers/zod";
import { useInfiniteQuery, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { useForm } from "react-hook-form";
import { Link, NavLink, useNavigate } from "react-router";
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
  EditSheet,
  FilterBar,
  FormError,
  ListStatus,
  LoadMore,
  WriteStatus,
} from "../ui/controls";
import { Filter, FilterSearch, Form, TextField } from "../ui/fields";
import { useFilterParam } from "../ui/filters";
import { applyProblem } from "../ui/formProblem";
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
import { nameIn, useConnectionOptions } from "../ui/options";
import { StatusBadge } from "../ui/status";
import { SubscriptionsSection } from "./Subscriptions";

type Topic = components["schemas"]["AdminTopicResponse"];

const writeFields = ["name", "description"] as const;

const topicSchema = z.object({
  name: z.string().trim().min(1, "Enter a name."),
  description: z.string(),
});

type TopicValues = z.infer<typeof topicSchema>;

const optional = (text: string) => text.trim() || null;

export function TopicsScreen({ tenantId, selectedTopicId }: { tenantId: string; selectedTopicId?: string }) {
  const [status, setStatus] = useFilterParam("status");
  const [name, setName] = useFilterParam("name");
  const applied = [status, name].filter(Boolean).length;
  const list = useInfiniteQuery({
    queryKey: ["topics", tenantId, { status, name }],
    queryFn: ({ pageParam }) =>
      call(() =>
        api.GET("/admin/tenants/{tenantId}/topics", {
          params: {
            path: { tenantId },
            query: {
              status: status || undefined,
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

  return (
    <Page>
      <PageHeader
        title="Topics"
        action={
          <CreateSheet label="New Topic" description="The Tenant-scoped stream Subscriptions match against">
            {(close) => <CreateTopic tenantId={tenantId} onCreated={close} />}
          </CreateSheet>
        }
      >
        A Topic is the Tenant-scoped stream Subscriptions match against. Its name is immutable.
      </PageHeader>

      <section className="flex flex-col gap-4">
        <FilterBar applied={applied}>
          <FilterSearch id="topic-name" label="Find by name" value={name} onChange={setName} />
          <Filter id="topic-status" label="Status" value={status} onChange={setStatus}>
            <SelectItem value="active">Active</SelectItem>
            <SelectItem value="disabled">Disabled</SelectItem>
          </Filter>
        </FilterBar>

        <ListStatus
          busy={list.isFetching}
          loaded={list.isSuccess}
          problem={asProblem(list.error)}
          empty={topics.length === 0}
          emptyText="This Tenant has no Topics matching this filter."
        />
        <SplitView>
          <SplitList>
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
                    <TableHead scope="col">Description</TableHead>
                    <TableHead scope="col">Subscriptions</TableHead>
                    <TableHead scope="col">Status</TableHead>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {topics.map((topic) => (
                    <TableRow key={topic.id} className="has-[a[aria-current=page]]:bg-selected-surface">
                      <RowHeader className="whitespace-nowrap">
                        {/* The route is the selection, so `aria-current` follows the URL rather than a
                        separately tracked flag — the same contract every other ledger has. */}
                        <NavLink className="font-mono no-underline" to={`/tenants/${tenantId}/topics/${topic.id}`} end>
                          {topic.name}
                        </NavLink>
                      </RowHeader>
                      <TableCell className="text-ink-secondary">{topic.description ?? "—"}</TableCell>
                      {/* A Topic nothing subscribes to accepts Events and routes none of them, so
                          the count is what the list is scanned for rather than a detail. */}
                      <TableCell className="tabular-nums">{topic.subscription_count}</TableCell>
                      <TableCell>
                        <StatusBadge status={topic.status} />
                      </TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </TableCard>
            ) : null}
          </SplitList>

          {selectedTopicId ? (
            <TopicInspector key={selectedTopicId} tenantId={tenantId} topicId={selectedTopicId} />
          ) : (
            <InspectorPlaceholder label="Topic detail">
              Select a Topic to read its Subscriptions and what they match.
            </InspectorPlaceholder>
          )}
        </SplitView>
      </section>
    </Page>
  );
}

/// The selected Topic beside the list. Its Subscriptions are summarised here rather than authored
/// here: what an Operator reads off a Topic is what matches it and where that goes, and a create
/// panel, a filter and a cursor-paged table inside a 400-pixel panel would be a list-with-detail
/// nested inside a detail. Authoring keeps its own route, which this panel links to.
function TopicInspector({ tenantId, topicId }: { tenantId: string; topicId: string }) {
  const [notice, setNotice] = useState("");
  const connections = useConnectionOptions(tenantId);
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
          <CloseInspector to={`/tenants/${tenantId}/topics`} label="Close the Topic detail" />
        </div>
        <p role="alert">{problem.detail ?? `This Topic could not be read (${problem.status}).`}</p>
      </Inspector>
    );
  if (!topic.data) return <Inspector label="Topic detail">Loading…</Inspector>;

  const current = topic.data;
  const matched = subscriptions.data?.items ?? [];

  return (
    <Inspector label="Topic detail">
      <div className="flex items-start justify-between gap-3">
        <div className="min-w-0">
          <h2 className="font-mono break-all">{current.name}</h2>
          <span className="block font-mono text-xs break-all text-ink-secondary">{current.id}</span>
        </div>
        <div className="flex shrink-0 items-center gap-1.5">
          <StatusBadge status={current.status} className="mt-0.5" />
          <CloseInspector to={`/tenants/${tenantId}/topics`} label="Close the Topic detail" />
        </div>
      </div>

      <Details className="border-b pb-3.5">
        <dt>Description</dt>
        <dd>{current.description ?? "—"}</dd>
        <dt>Subscriptions</dt>
        <dd className="tabular-nums">{current.subscription_count}</dd>
      </Details>

      <section className="flex flex-col gap-2">
        <h3 className="eyebrow">Subscriptions</h3>
        {subscriptions.isPending ? <p className="m-0 text-[13px] text-ink-secondary">Loading…</p> : null}
        {/* A Topic nothing matches accepts Events and routes none of them, which is the established
            signal for a missing Subscription rather than an empty section. */}
        {subscriptions.isSuccess && matched.length === 0 ? (
          <p className="m-0 text-[13px] text-ink-secondary">
            No Subscription matches this Topic, so nothing accepted here is ever routed.
          </p>
        ) : null}
        {matched.map((subscription) => (
          <div
            key={subscription.id}
            className="flex items-center justify-between gap-2 rounded-md border bg-surface-quiet px-2.5 py-2"
          >
            <div className="min-w-0 text-[13px]">
              <Link
                className="block truncate no-underline"
                to={`/tenants/${tenantId}/topics/${topicId}/subscriptions/${subscription.id}`}
              >
                {subscription.name}
              </Link>
              <span className="block truncate font-mono text-xs text-ink-secondary">
                → {nameIn(connections.data?.items, subscription.destination_connection_id)}
              </span>
            </div>
            <StatusBadge status={subscription.status} className="shrink-0" />
          </div>
        ))}
        <Button asChild variant="outline" size="sm" className="self-start">
          <Link className="no-underline" to={`/tenants/${tenantId}/topics/${topicId}/subscriptions`}>
            Manage Subscriptions
          </Link>
        </Button>
      </section>

      <WriteStatus done={notice !== ""}>{notice}</WriteStatus>
      <EditTopic
        key={current.updated_at}
        tenantId={tenantId}
        topic={current}
        onDone={() => setNotice("Topic deactivated.")}
      />
    </Inspector>
  );
}

function CreateTopic({ tenantId, onCreated }: { tenantId: string; onCreated: () => void }) {
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const form = useForm<TopicValues>({
    resolver: zodResolver(topicSchema),
    defaultValues: { name: "", description: "" },
  });

  const create = useMutation({
    mutationFn: (values: TopicValues) =>
      call(() =>
        api.POST("/admin/tenants/{tenantId}/topics", {
          params: { path: { tenantId } },
          body: { name: values.name, description: optional(values.description) },
        }),
      ),
    onSuccess: (created) => {
      void queryClient.invalidateQueries({ queryKey: ["topics", tenantId] });
      onCreated();
      if (created) navigate(`/tenants/${tenantId}/topics/${created.id}`);
    },
  });

  const submit = form.handleSubmit((values) =>
    create.mutate(values, { onError: (failure) => applyProblem(form, failure, writeFields) }),
  );

  return (
    <Form {...form}>
      <form className="flex flex-col gap-4" onSubmit={submit} aria-label="Create a Topic">
        <FormError message={formError(asProblem(create.error), writeFields)} />

        <TextField control={form.control} name="name" label="Name" required />
        <TextField control={form.control} name="description" label="Description (optional)" />

        <Button type="submit" className="self-start" disabled={create.isPending}>
          Create Topic
        </Button>
      </form>
    </Form>
  );
}

/// The Subscription authoring workspace: a create panel, a filter and a cursor-paged list, which is
/// what would not fit in the inspector beside the Topics list. Reached from that panel.
export function TopicScreen({ tenantId, topicId }: { tenantId: string; topicId: string }) {
  const topic = useQuery({
    queryKey: ["topic", tenantId, topicId],
    queryFn: () =>
      call(() => api.GET("/admin/tenants/{tenantId}/topics/{id}", { params: { path: { tenantId, id: topicId } } })),
  });

  const problem = asProblem(topic.error);
  if (problem)
    return (
      <>
        <h1>Topic</h1>
        <p role="alert">{problem.detail ?? `This Topic could not be read (${problem.status}).`}</p>
      </>
    );
  if (!topic.data) return <p>Loading…</p>;

  const current = topic.data;
  return (
    <Page>
      <PageHeader
        title={`Subscriptions on ${current.name}`}
        action={
          <Button asChild variant="outline">
            <Link className="no-underline" to={`/tenants/${tenantId}/topics/${topicId}`}>
              Back to the Topic
            </Link>
          </Button>
        }
      >
        A Subscription matches Events on this Topic and delivers them to one Connection.
      </PageHeader>

      {/* Subscriptions are owned by the Topic in the API, so they are authored where they live
          rather than from a separate Tenant-level list that would have to reintroduce the Topic. */}
      <SubscriptionsSection tenantId={tenantId} topicId={topicId} topicName={current.name} />
    </Page>
  );
}

function EditTopic({ tenantId, topic, onDone }: { tenantId: string; topic: Topic; onDone: () => void }) {
  const queryClient = useQueryClient();
  const reread = () => {
    void queryClient.invalidateQueries({ queryKey: ["topic", tenantId, topic.id] });
    void queryClient.invalidateQueries({ queryKey: ["topics", tenantId] });
  };
  const form = useForm<TopicValues>({
    resolver: zodResolver(topicSchema),
    defaultValues: { name: topic.name, description: topic.description ?? "" },
  });

  const save = useMutation({
    mutationFn: (values: TopicValues) =>
      call(() =>
        api.PATCH("/admin/tenants/{tenantId}/topics/{id}", {
          params: { path: { tenantId, id: topic.id } },
          body: { name: values.name, description: optional(values.description) },
        }),
      ),
    onSuccess: reread,
  });

  const deactivate = useMutation({
    mutationFn: () =>
      call(() =>
        api.POST("/admin/tenants/{tenantId}/topics/{id}/deactivate", {
          params: { path: { tenantId, id: topic.id } },
        }),
      ),
    onSuccess: () => {
      reread();
      onDone();
    },
  });

  return (
    <div className="flex flex-col gap-6">
      <EditSheet label="Edit this Topic">
        {(close) => (
          <Form {...form}>
            <form
              className="flex flex-col gap-4"
              aria-label={`Edit ${topic.name}`}
              onSubmit={form.handleSubmit((values) =>
                save.mutate(values, {
                  onSuccess: close,
                  onError: (failure) => applyProblem(form, failure, writeFields),
                }),
              )}
            >
              <FormError message={formError(asProblem(save.error), writeFields)} />

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

      {topic.status === "active" ? (
        <div className="flex flex-col items-start gap-2">
          <ConfirmAction
            label="Deactivate Topic"
            question={`Deactivate the Topic "${topic.name}"? Its Subscriptions stop receiving Events.`}
            confirmLabel={`Deactivate ${topic.name}`}
            busy={deactivate.isPending}
            onConfirm={() => deactivate.mutate()}
          />
          <FormError message={formError(asProblem(deactivate.error))} />
        </div>
      ) : null}
    </div>
  );
}
