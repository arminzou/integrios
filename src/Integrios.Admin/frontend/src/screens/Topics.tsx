import { zodResolver } from "@hookform/resolvers/zod";
import { useInfiniteQuery, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { useForm } from "react-hook-form";
import { Link, useNavigate } from "react-router";
import { z } from "zod";
import { Button } from "@/components/ui/button";
import { TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import { api } from "../api/client";
import { formError } from "../api/problem";
import { asProblem, call, nextCursor } from "../api/query";
import type { components } from "../api/schema";
import { ConfirmAction, FormError, ListStatus, LoadMore, useCreatePanel, WriteStatus } from "../ui/controls";
import { Filter, Form, TextField } from "../ui/fields";
import { useFilterParam } from "../ui/filters";
import { applyProblem } from "../ui/formProblem";
import { Details, Page, PageHeader, Panel, RowHeader, TableCard } from "../ui/layout";
import { SubscriptionsSection } from "./Subscriptions";

type Topic = components["schemas"]["AdminTopicResponse"];

const writeFields = ["name", "description"] as const;

const topicSchema = z.object({
  name: z.string().trim().min(1, "Enter a name."),
  description: z.string(),
});

type TopicValues = z.infer<typeof topicSchema>;

const optional = (text: string) => text.trim() || null;

export function TopicsScreen({ tenantId }: { tenantId: string }) {
  const [status, setStatus] = useFilterParam("status");
  const create = useCreatePanel("new-topic");
  const list = useInfiniteQuery({
    queryKey: ["topics", tenantId, { status }],
    queryFn: ({ pageParam }) =>
      call(() =>
        api.GET("/admin/tenants/{tenantId}/topics", {
          params: {
            path: { tenantId },
            query: { status: status || undefined, after: pageParam ?? undefined, limit: 20 },
          },
        }),
      ),
    initialPageParam: null as string | null,
    getNextPageParam: nextCursor<Topic>,
  });
  const topics = list.data?.pages.flatMap((page) => page.items) ?? [];

  return (
    <Page>
      <PageHeader title="Topics" action={<Button {...create.triggerProps}>New Topic</Button>}>
        In{" "}
        <Link className="underline" to={`/tenants/${tenantId}`}>
          this Tenant
        </Link>
        .
      </PageHeader>

      <Panel {...create.panelProps} className="max-w-none">
        <CreateTopic tenantId={tenantId} />
      </Panel>

      <section className="flex flex-col gap-4">
        <h2>All Topics</h2>
        <Filter id="topic-status" label="Status" value={status} onChange={setStatus}>
          <option value="">Any status</option>
          <option value="active">Active</option>
          <option value="disabled">Disabled</option>
        </Filter>

        <ListStatus
          busy={list.isFetching}
          loaded={list.isSuccess}
          problem={asProblem(list.error)}
          empty={topics.length === 0}
          emptyText="This Tenant has no Topics matching this filter."
        />
        {topics.length > 0 ? (
          <TableCard caption="Topics, newest first">
            <TableHeader>
              <TableRow>
                <TableHead scope="col">Name</TableHead>
                <TableHead scope="col">Status</TableHead>
                <TableHead scope="col">Description</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {topics.map((topic) => (
                <TableRow key={topic.id}>
                  <RowHeader>
                    <Link className="underline" to={`/tenants/${tenantId}/topics/${topic.id}`}>
                      {topic.name}
                    </Link>
                  </RowHeader>
                  <TableCell>{topic.status}</TableCell>
                  <TableCell>{topic.description ?? "—"}</TableCell>
                </TableRow>
              ))}
            </TableBody>
          </TableCard>
        ) : null}
        <LoadMore hasMore={list.hasNextPage} busy={list.isFetching} onLoadMore={() => void list.fetchNextPage()} />
      </section>
    </Page>
  );
}

function CreateTopic({ tenantId }: { tenantId: string }) {
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
      if (created) navigate(`/tenants/${tenantId}/topics/${created.id}`);
    },
  });

  const submit = form.handleSubmit((values) =>
    create.mutate(values, { onError: (failure) => applyProblem(form, failure, writeFields) }),
  );

  return (
    <Form {...form}>
      <Panel asChild>
        <form className="flex flex-col gap-4" onSubmit={submit}>
          <h2>Create a Topic</h2>
          <FormError message={formError(asProblem(create.error), writeFields)} />

          <TextField control={form.control} name="name" label="Name" required />
          <TextField control={form.control} name="description" label="Description (optional)" />

          <Button type="submit" className="self-start" disabled={create.isPending}>
            Create Topic
          </Button>
        </form>
      </Panel>
    </Form>
  );
}

export function TopicScreen({ tenantId, topicId }: { tenantId: string; topicId: string }) {
  const [notice, setNotice] = useState("");
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
      <PageHeader title={current.name}>
        In{" "}
        <Link className="underline" to={`/tenants/${tenantId}/topics`}>
          this Tenant's Topics
        </Link>
        .
      </PageHeader>

      <Panel>
        <Details>
          <dt>Status</dt>
          <dd>{current.status}</dd>
          <dt>Description</dt>
          <dd>{current.description ?? "—"}</dd>
        </Details>
      </Panel>

      <WriteStatus done={notice !== ""}>{notice}</WriteStatus>
      <EditTopic
        key={current.updated_at}
        tenantId={tenantId}
        topic={current}
        onDone={() => setNotice("Topic deactivated.")}
      />

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

  const submit = form.handleSubmit((values) =>
    save.mutate(values, { onError: (failure) => applyProblem(form, failure, writeFields) }),
  );

  return (
    <div className="flex flex-col gap-6">
      <Form {...form}>
        <Panel asChild>
          <form className="flex flex-col gap-4" onSubmit={submit}>
            <h2>Edit {topic.name}</h2>
            <FormError message={formError(asProblem(save.error), writeFields)} />

            <TextField control={form.control} name="name" label="Name" required />
            <TextField control={form.control} name="description" label="Description (optional)" />

            <Button type="submit" className="self-start" disabled={save.isPending}>
              Save changes
            </Button>
            <WriteStatus done={save.isSuccess}>Changes saved.</WriteStatus>
          </form>
        </Panel>
      </Form>

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
