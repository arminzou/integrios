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
import { ConfirmAction, Disclosure, FormError, ListStatus, LoadMore } from "../ui/controls";
import { Filter, Form, TextField } from "../ui/fields";
import { applyProblem } from "../ui/formProblem";
import { Details, Page, PageHeader, Panel, RowHeader, TableCard } from "../ui/layout";
import { useAction } from "../ui/useAction";
import { useCursorList } from "../ui/useCursorList";
import { useResource } from "../ui/useResource";
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
  const [status, setStatus] = useState("");
  const list = useCursorList<Topic>(
    (after) =>
      api.GET("/admin/tenants/{tenantId}/topics", {
        params: { path: { tenantId }, query: { status: status || undefined, after: after ?? undefined, limit: 20 } },
      }),
    `topics|${tenantId}|${status}`,
  );

  return (
    <Page>
      <PageHeader title="Topics">
        In{" "}
        <Link className="underline" to={`/tenants/${tenantId}`}>
          this Tenant
        </Link>
        .
      </PageHeader>

      <Disclosure label="New Topic">
        <CreateTopic tenantId={tenantId} onCreated={list.reload} />
      </Disclosure>

      <section className="flex flex-col gap-4">
        <h2>All Topics</h2>
        <Filter id="topic-status" label="Status" value={status} onChange={setStatus}>
          <option value="">Any status</option>
          <option value="active">Active</option>
          <option value="disabled">Disabled</option>
        </Filter>

        <ListStatus
          busy={list.busy}
          loaded={list.loaded}
          problem={list.problem}
          empty={list.items.length === 0}
          emptyText="This Tenant has no Topics matching this filter."
        />
        {list.items.length > 0 ? (
          <TableCard caption="Topics, newest first">
            <TableHeader>
              <TableRow>
                <TableHead scope="col">Name</TableHead>
                <TableHead scope="col">Status</TableHead>
                <TableHead scope="col">Description</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {list.items.map((topic) => (
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
        <LoadMore cursor={list.cursor} busy={list.busy} onLoadMore={list.loadMore} />
      </section>
    </Page>
  );
}

function CreateTopic({ tenantId, onCreated }: { tenantId: string; onCreated: () => void }) {
  const navigate = useNavigate();
  const { busy, problem, run } = useAction();
  const form = useForm<TopicValues>({
    resolver: zodResolver(topicSchema),
    defaultValues: { name: "", description: "" },
  });

  const submit = form.handleSubmit(async (values) => {
    const failure = await run(
      () =>
        api.POST("/admin/tenants/{tenantId}/topics", {
          params: { path: { tenantId } },
          body: { name: values.name, description: optional(values.description) },
        }),
      (created) => {
        onCreated();
        if (created) navigate(`/tenants/${tenantId}/topics/${created.id}`);
      },
    );
    if (failure) applyProblem(form, failure, writeFields);
  });

  return (
    <Form {...form}>
      <Panel asChild>
        <form className="flex flex-col gap-4" onSubmit={submit}>
          <h2>Create a Topic</h2>
          <FormError message={formError(problem, writeFields)} />

          <TextField control={form.control} name="name" label="Name" required />
          <TextField control={form.control} name="description" label="Description (optional)" />

          <Button type="submit" className="self-start" disabled={busy}>
            Create Topic
          </Button>
        </form>
      </Panel>
    </Form>
  );
}

export function TopicScreen({ tenantId, topicId }: { tenantId: string; topicId: string }) {
  const topic = useResource<Topic>(
    () => api.GET("/admin/tenants/{tenantId}/topics/{id}", { params: { path: { tenantId, id: topicId } } }),
    `${tenantId}|${topicId}`,
  );

  if (topic.problem)
    return (
      <>
        <h1>Topic</h1>
        <p role="alert">{topic.problem.detail ?? `This Topic could not be read (${topic.problem.status}).`}</p>
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

      <EditTopic key={current.updated_at} tenantId={tenantId} topic={current} onSaved={topic.reload} />

      {/* Subscriptions are owned by the Topic in the API, so they are authored where they live
          rather than from a separate Tenant-level list that would have to reintroduce the Topic. */}
      <SubscriptionsSection tenantId={tenantId} topicId={topicId} topicName={current.name} />
    </Page>
  );
}

function EditTopic({ tenantId, topic, onSaved }: { tenantId: string; topic: Topic; onSaved: () => void }) {
  const { busy, problem, run } = useAction();
  const form = useForm<TopicValues>({
    resolver: zodResolver(topicSchema),
    defaultValues: { name: topic.name, description: topic.description ?? "" },
  });

  const submit = form.handleSubmit(async (values) => {
    const failure = await run(
      () =>
        api.PATCH("/admin/tenants/{tenantId}/topics/{id}", {
          params: { path: { tenantId, id: topic.id } },
          body: { name: values.name, description: optional(values.description) },
        }),
      onSaved,
    );
    if (failure) applyProblem(form, failure, writeFields);
  });

  return (
    <div className="flex flex-col gap-6">
      <Form {...form}>
        <Panel asChild>
          <form className="flex flex-col gap-4" onSubmit={submit}>
            <h2>Edit {topic.name}</h2>
            <FormError message={formError(problem, writeFields)} />

            <TextField control={form.control} name="name" label="Name" required />
            <TextField control={form.control} name="description" label="Description (optional)" />

            <Button type="submit" className="self-start" disabled={busy}>
              Save changes
            </Button>
          </form>
        </Panel>
      </Form>

      {topic.status === "active" ? (
        <ConfirmAction
          label="Deactivate Topic"
          question={`Deactivate the Topic "${topic.name}"? Its Subscriptions stop receiving Events.`}
          confirmLabel={`Deactivate ${topic.name}`}
          busy={busy}
          onConfirm={() =>
            void run(
              () =>
                api.POST("/admin/tenants/{tenantId}/topics/{id}/deactivate", {
                  params: { path: { tenantId, id: topic.id } },
                }),
              onSaved,
            )
          }
        />
      ) : null}
    </div>
  );
}
