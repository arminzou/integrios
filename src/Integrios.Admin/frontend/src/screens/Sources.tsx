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
import { Filter, Form, SelectField, TextAreaField } from "../ui/fields";
import { applyProblem } from "../ui/formProblem";
import { formatJson, parseJson } from "../ui/json";
import { Details, Page, PageHeader, Panel, RowHeader, TableCard } from "../ui/layout";
import { useAction } from "../ui/useAction";
import { useCursorList } from "../ui/useCursorList";
import { useOptions } from "../ui/useOptions";
import { useResource } from "../ui/useResource";

type SourceListItem = components["schemas"]["SourceListItemDto"];
type Source = components["schemas"]["SourceDto"];
type ConnectionListItem = components["schemas"]["ConnectionListItemDto"];
type Topic = components["schemas"]["AdminTopicResponse"];

const sourceTypes = [
  { value: "event_api", label: "Event API" },
  { value: "webhook", label: "Webhook" },
  { value: "queue", label: "Queue" },
];

const createFields = ["connection_id", "topic_id", "type", "configuration"] as const;
const editFields = ["configuration"] as const;

/// A domain JSON document, authored as text: well-formedness is all the dashboard checks, and the
/// server stays the authority on whether the document is valid for this Source type.
const jsonDocument = z.string().superRefine((text, ctx) => {
  const parsed = parseJson(text);
  if (parsed.error !== undefined) ctx.addIssue({ code: z.ZodIssueCode.custom, message: parsed.error });
});

const createSchema = z.object({
  connection_id: z.string().min(1, "Choose a Connection."),
  topic_id: z.string().min(1, "Choose a Topic."),
  type: z.string().min(1, "Choose a type."),
  configuration: jsonDocument,
});

const editSchema = z.object({ configuration: jsonDocument });

type CreateValues = z.infer<typeof createSchema>;
type EditValues = z.infer<typeof editSchema>;

export function SourcesScreen({ tenantId }: { tenantId: string }) {
  const [status, setStatus] = useState("");
  const [type, setType] = useState("");
  const list = useCursorList<SourceListItem>(
    (after) =>
      api.GET("/admin/tenants/{tenantId}/sources", {
        params: {
          path: { tenantId },
          query: { status: status || undefined, type: type || undefined, after: after ?? undefined, limit: 20 },
        },
      }),
    `sources|${tenantId}|${status}|${type}`,
  );

  return (
    <Page>
      <PageHeader title="Sources">
        In{" "}
        <Link className="underline" to={`/tenants/${tenantId}`}>
          this Tenant
        </Link>
        .
      </PageHeader>

      <Disclosure label="New Source">
        <CreateSource tenantId={tenantId} onCreated={list.reload} />
      </Disclosure>

      <section className="flex flex-col gap-4">
        <h2>All Sources</h2>
        <div className="flex flex-wrap gap-4">
          <Filter id="source-status" label="Status" value={status} onChange={setStatus}>
            <option value="">Any status</option>
            <option value="active">Active</option>
            <option value="revoked">Revoked</option>
          </Filter>
          <Filter id="source-type" label="Type" value={type} onChange={setType}>
            <option value="">Any type</option>
            {sourceTypes.map((option) => (
              <option key={option.value} value={option.value}>
                {option.label}
              </option>
            ))}
          </Filter>
        </div>

        <ListStatus
          busy={list.busy}
          loaded={list.loaded}
          problem={list.problem}
          empty={list.items.length === 0}
          emptyText="This Tenant has no Sources matching these filters."
        />
        {list.items.length > 0 ? (
          <TableCard caption="Sources, newest first">
            <TableHeader>
              <TableRow>
                <TableHead scope="col">Source</TableHead>
                <TableHead scope="col">Type</TableHead>
                <TableHead scope="col">Status</TableHead>
                <TableHead scope="col">Topic</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {list.items.map((source) => (
                <TableRow key={source.id}>
                  <RowHeader>
                    <Link className="font-mono text-sm underline" to={`/tenants/${tenantId}/sources/${source.id}`}>
                      {source.id}
                    </Link>
                  </RowHeader>
                  <TableCell>{source.type}</TableCell>
                  <TableCell>{source.status}</TableCell>
                  <TableCell>
                    <Link className="font-mono text-sm underline" to={`/tenants/${tenantId}/topics/${source.topic_id}`}>
                      {source.topic_id}
                    </Link>
                  </TableCell>
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

function CreateSource({ tenantId, onCreated }: { tenantId: string; onCreated: () => void }) {
  const navigate = useNavigate();
  const connections = useOptions<ConnectionListItem>(
    () =>
      api.GET("/admin/tenants/{tenantId}/connections", {
        params: { path: { tenantId }, query: { status: "active", limit: 100 } },
      }),
    `connection-options|${tenantId}`,
  );
  const topics = useOptions<Topic>(
    () =>
      api.GET("/admin/tenants/{tenantId}/topics", {
        params: { path: { tenantId }, query: { status: "active", limit: 100 } },
      }),
    `topic-options|${tenantId}`,
  );
  const { busy, problem, run } = useAction();
  const optionsUnavailable = connections.busy || topics.busy || connections.problem !== null || topics.problem !== null;

  const form = useForm<CreateValues>({
    resolver: zodResolver(createSchema),
    defaultValues: { connection_id: "", topic_id: "", type: "webhook", configuration: "{}" },
  });

  const submit = form.handleSubmit(async (values) => {
    const failure = await run(
      () =>
        api.POST("/admin/tenants/{tenantId}/sources", {
          params: { path: { tenantId } },
          body: {
            connection_id: values.connection_id,
            topic_id: values.topic_id,
            type: values.type,
            configuration: parseJson(values.configuration).value,
          },
        }),
      (created) => {
        onCreated();
        if (created) navigate(`/tenants/${tenantId}/sources/${created.id}`);
      },
    );
    if (failure) applyProblem(form, failure, createFields);
  });

  return (
    <Form {...form}>
      <Panel asChild>
        <form className="flex flex-col gap-4" onSubmit={submit}>
          <h2>Create a Source</h2>
          <FormError message={formError(connections.problem ?? topics.problem)} />
          <FormError message={formError(problem, createFields)} />

          <SelectField
            control={form.control}
            name="connection_id"
            label="Connection"
            hint={connections.truncated ? "Showing the first 100 active Connections." : undefined}
            disabled={connections.busy || connections.problem !== null}
            required
          >
            <option value="">Choose a Connection</option>
            {connections.items.map((connection) => (
              <option key={connection.id} value={connection.id}>
                {connection.name}
              </option>
            ))}
          </SelectField>
          <SelectField
            control={form.control}
            name="topic_id"
            label="Topic"
            hint={topics.truncated ? "Showing the first 100 active Topics." : undefined}
            disabled={topics.busy || topics.problem !== null}
            required
          >
            <option value="">Choose a Topic</option>
            {topics.items.map((topic) => (
              <option key={topic.id} value={topic.id}>
                {topic.name}
              </option>
            ))}
          </SelectField>
          <SelectField control={form.control} name="type" label="Type" required>
            {sourceTypes.map((option) => (
              <option key={option.value} value={option.value}>
                {option.label}
              </option>
            ))}
          </SelectField>
          <TextAreaField
            control={form.control}
            name="configuration"
            label="Configuration (JSON)"
            className="min-h-40 font-mono text-sm"
            required
          />

          <Button type="submit" className="self-start" disabled={busy || optionsUnavailable}>
            Create Source
          </Button>
        </form>
      </Panel>
    </Form>
  );
}

export function SourceScreen({ tenantId, sourceId }: { tenantId: string; sourceId: string }) {
  const source = useResource<Source>(
    () => api.GET("/admin/tenants/{tenantId}/sources/{id}", { params: { path: { tenantId, id: sourceId } } }),
    `${tenantId}|${sourceId}`,
  );

  if (source.problem)
    return (
      <>
        <h1>Source</h1>
        <p role="alert">{source.problem.detail ?? `This Source could not be read (${source.problem.status}).`}</p>
      </>
    );
  if (!source.data) return <p>Loading…</p>;

  const current = source.data;
  return (
    <Page>
      <PageHeader title={`Source ${current.id}`}>
        In{" "}
        <Link className="underline" to={`/tenants/${tenantId}/sources`}>
          this Tenant's Sources
        </Link>
        .
      </PageHeader>

      <Panel>
        <Details>
          <dt>Type</dt>
          <dd>{current.type}</dd>
          <dt>Status</dt>
          <dd>{current.status}</dd>
          <dt>Connection</dt>
          <dd>
            <Link
              className="font-mono text-sm underline"
              to={`/tenants/${tenantId}/connections/${current.connection_id}`}
            >
              {current.connection_id}
            </Link>
          </dd>
          <dt>Topic</dt>
          <dd>
            <Link className="font-mono text-sm underline" to={`/tenants/${tenantId}/topics/${current.topic_id}`}>
              {current.topic_id}
            </Link>
          </dd>
          <dt>Revoked</dt>
          <dd>{current.revoked_at ?? "Not revoked"}</dd>
        </Details>
      </Panel>

      <EditSource key={current.updated_at} tenantId={tenantId} source={current} onSaved={source.reload} />
    </Page>
  );
}

/// The Admin API owns exactly one Source update — its configuration. Type, Connection, and Topic are
/// fixed at creation, so they are shown rather than offered as editable fields.
function EditSource({ tenantId, source, onSaved }: { tenantId: string; source: Source; onSaved: () => void }) {
  const { busy, problem, run } = useAction();
  const form = useForm<EditValues>({
    resolver: zodResolver(editSchema),
    defaultValues: { configuration: formatJson(source.configuration) },
  });

  const submit = form.handleSubmit(async (values) => {
    const failure = await run(
      () =>
        api.PATCH("/admin/tenants/{tenantId}/sources/{id}", {
          params: { path: { tenantId, id: source.id } },
          body: { configuration: parseJson(values.configuration).value },
        }),
      onSaved,
    );
    if (failure) applyProblem(form, failure, editFields);
  });

  return (
    <div className="flex flex-col gap-6">
      <Form {...form}>
        <Panel asChild>
          <form className="flex flex-col gap-4" onSubmit={submit}>
            <h2>Edit configuration</h2>
            <FormError message={formError(problem, editFields)} />

            <TextAreaField
              control={form.control}
              name="configuration"
              label="Configuration (JSON)"
              className="min-h-56 font-mono text-sm"
              required
            />

            <Button type="submit" className="self-start" disabled={busy}>
              Save configuration
            </Button>
          </form>
        </Panel>
      </Form>

      {source.status === "active" ? (
        <ConfirmAction
          label="Revoke Source"
          question={`Revoke the ${source.type} Source ${source.id}? It stops accepting Events and cannot be restored.`}
          confirmLabel={`Revoke ${source.id}`}
          busy={busy}
          onConfirm={() =>
            void run(
              () =>
                api.DELETE("/admin/tenants/{tenantId}/sources/{id}", {
                  params: { path: { tenantId, id: source.id } },
                }),
              onSaved,
            )
          }
        />
      ) : null}
    </div>
  );
}
