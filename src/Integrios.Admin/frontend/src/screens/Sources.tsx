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
import { ConfirmAction, FilterBar, FormError, ListStatus, LoadMore, useCreatePanel, WriteStatus } from "../ui/controls";
import { Filter, Form, SelectField, TextAreaField } from "../ui/fields";
import { useFilterParam } from "../ui/filters";
import { applyProblem } from "../ui/formProblem";
import { formatJson, parseJson } from "../ui/json";
import { Details, Page, PageHeader, Panel, RowHeader, TableCard } from "../ui/layout";
import { StatusBadge } from "../ui/status";

type SourceListItem = components["schemas"]["SourceListItemDto"];
type Source = components["schemas"]["SourceDto"];

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
  const [status, setStatus] = useFilterParam("status");
  const [type, setType] = useFilterParam("type");
  const create = useCreatePanel("new-source");
  const list = useInfiniteQuery({
    queryKey: ["sources", tenantId, { status, type }],
    queryFn: ({ pageParam }) =>
      call(() =>
        api.GET("/admin/tenants/{tenantId}/sources", {
          params: {
            path: { tenantId },
            query: { status: status || undefined, type: type || undefined, after: pageParam ?? undefined, limit: 20 },
          },
        }),
      ),
    initialPageParam: null as string | null,
    getNextPageParam: nextCursor<SourceListItem>,
  });
  const sources = list.data?.pages.flatMap((page) => page.items) ?? [];

  return (
    <Page>
      <PageHeader title="Sources" action={<Button {...create.triggerProps}>New Source</Button>}>
        In{" "}
        <Link className="underline" to={`/tenants/${tenantId}`}>
          this Tenant
        </Link>
        .
      </PageHeader>

      <Panel {...create.panelProps} className="max-w-none">
        <CreateSource tenantId={tenantId} />
      </Panel>

      <section className="flex flex-col gap-4">
        <h2>All Sources</h2>
        <div className="flex flex-wrap gap-4">
          <FilterBar applied={((status ? 1 : 0) as number) + ((type ? 1 : 0) as number)}>
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
          </FilterBar>
        </div>

        <ListStatus
          busy={list.isFetching}
          loaded={list.isSuccess}
          problem={asProblem(list.error)}
          empty={sources.length === 0}
          emptyText="This Tenant has no Sources matching these filters."
        />
        {sources.length > 0 ? (
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
              {sources.map((source) => (
                <TableRow key={source.id}>
                  <RowHeader>
                    <Link className="font-mono text-sm underline" to={`/tenants/${tenantId}/sources/${source.id}`}>
                      {source.id}
                    </Link>
                  </RowHeader>
                  <TableCell>{source.type}</TableCell>
                  <TableCell>
                    <StatusBadge status={source.status} />
                  </TableCell>
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
        <LoadMore hasMore={list.hasNextPage} busy={list.isFetching} onLoadMore={() => void list.fetchNextPage()} />
      </section>
    </Page>
  );
}

function CreateSource({ tenantId }: { tenantId: string }) {
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const connections = useQuery({
    queryKey: ["connection-options", tenantId],
    queryFn: () =>
      call(() =>
        api.GET("/admin/tenants/{tenantId}/connections", {
          params: { path: { tenantId }, query: { status: "active", limit: 100 } },
        }),
      ),
  });
  const topics = useQuery({
    queryKey: ["topic-options", tenantId],
    queryFn: () =>
      call(() =>
        api.GET("/admin/tenants/{tenantId}/topics", {
          params: { path: { tenantId }, query: { status: "active", limit: 100 } },
        }),
      ),
  });
  const optionsUnavailable = connections.isPending || topics.isPending || connections.isError || topics.isError;

  const form = useForm<CreateValues>({
    resolver: zodResolver(createSchema),
    defaultValues: { connection_id: "", topic_id: "", type: "webhook", configuration: "{}" },
  });

  const create = useMutation({
    mutationFn: (values: CreateValues) =>
      call(() =>
        api.POST("/admin/tenants/{tenantId}/sources", {
          params: { path: { tenantId } },
          body: {
            connection_id: values.connection_id,
            topic_id: values.topic_id,
            type: values.type,
            configuration: parseJson(values.configuration).value,
          },
        }),
      ),
    onSuccess: (created) => {
      void queryClient.invalidateQueries({ queryKey: ["sources", tenantId] });
      if (created) navigate(`/tenants/${tenantId}/sources/${created.id}`);
    },
  });

  const submit = form.handleSubmit((values) =>
    create.mutate(values, { onError: (failure) => applyProblem(form, failure, createFields) }),
  );

  return (
    <Form {...form}>
      <Panel asChild>
        <form className="flex flex-col gap-4" onSubmit={submit}>
          <h2>Create a Source</h2>
          <FormError message={formError(asProblem(connections.error ?? topics.error))} />
          <FormError message={formError(asProblem(create.error), createFields)} />

          <SelectField
            control={form.control}
            name="connection_id"
            label="Connection"
            hint={connections.data?.next_cursor ? "Showing the first 100 active Connections." : undefined}
            disabled={connections.isPending || connections.isError}
            required
          >
            <option value="">Choose a Connection</option>
            {(connections.data?.items ?? []).map((connection) => (
              <option key={connection.id} value={connection.id}>
                {connection.name}
              </option>
            ))}
          </SelectField>
          <SelectField
            control={form.control}
            name="topic_id"
            label="Topic"
            hint={topics.data?.next_cursor ? "Showing the first 100 active Topics." : undefined}
            disabled={topics.isPending || topics.isError}
            required
          >
            <option value="">Choose a Topic</option>
            {(topics.data?.items ?? []).map((topic) => (
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

          <Button type="submit" className="self-start" disabled={create.isPending || optionsUnavailable}>
            Create Source
          </Button>
        </form>
      </Panel>
    </Form>
  );
}

export function SourceScreen({ tenantId, sourceId }: { tenantId: string; sourceId: string }) {
  const [notice, setNotice] = useState("");
  const source = useQuery({
    queryKey: ["source", tenantId, sourceId],
    queryFn: () =>
      call(() => api.GET("/admin/tenants/{tenantId}/sources/{id}", { params: { path: { tenantId, id: sourceId } } })),
  });

  const problem = asProblem(source.error);
  if (problem)
    return (
      <>
        <h1>Source</h1>
        <p role="alert">{problem.detail ?? `This Source could not be read (${problem.status}).`}</p>
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
          <dd>
            <StatusBadge status={current.status} />
          </dd>
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

      <WriteStatus done={notice !== ""}>{notice}</WriteStatus>
      <EditSource
        key={current.updated_at}
        tenantId={tenantId}
        source={current}
        onDone={() => setNotice("Source revoked.")}
      />
    </Page>
  );
}

/// The Admin API owns exactly one Source update — its configuration. Type, Connection, and Topic are
/// fixed at creation, so they are shown rather than offered as editable fields.
function EditSource({ tenantId, source, onDone }: { tenantId: string; source: Source; onDone: () => void }) {
  const queryClient = useQueryClient();
  const reread = () => {
    void queryClient.invalidateQueries({ queryKey: ["source", tenantId, source.id] });
    void queryClient.invalidateQueries({ queryKey: ["sources", tenantId] });
  };
  const form = useForm<EditValues>({
    resolver: zodResolver(editSchema),
    defaultValues: { configuration: formatJson(source.configuration) },
  });

  const save = useMutation({
    mutationFn: (values: EditValues) =>
      call(() =>
        api.PATCH("/admin/tenants/{tenantId}/sources/{id}", {
          params: { path: { tenantId, id: source.id } },
          body: { configuration: parseJson(values.configuration).value },
        }),
      ),
    onSuccess: reread,
  });

  const revoke = useMutation({
    mutationFn: () =>
      call(() =>
        api.DELETE("/admin/tenants/{tenantId}/sources/{id}", {
          params: { path: { tenantId, id: source.id } },
        }),
      ),
    onSuccess: () => {
      reread();
      onDone();
    },
  });

  const submit = form.handleSubmit((values) =>
    save.mutate(values, { onError: (failure) => applyProblem(form, failure, editFields) }),
  );

  return (
    <div className="flex flex-col gap-6">
      <Form {...form}>
        <Panel asChild>
          <form className="flex flex-col gap-4" onSubmit={submit}>
            <h2>Edit configuration</h2>
            <FormError message={formError(asProblem(save.error), editFields)} />

            <TextAreaField
              control={form.control}
              name="configuration"
              label="Configuration (JSON)"
              className="min-h-56 font-mono text-sm"
              required
            />

            <Button type="submit" className="self-start" disabled={save.isPending}>
              Save configuration
            </Button>
            <WriteStatus done={save.isSuccess}>Configuration saved.</WriteStatus>
          </form>
        </Panel>
      </Form>

      {source.status === "active" ? (
        <div className="flex flex-col items-start gap-2">
          <ConfirmAction
            label="Revoke Source"
            question={`Revoke the ${source.type} Source ${source.id}? It stops accepting Events and cannot be restored.`}
            confirmLabel={`Revoke ${source.id}`}
            busy={revoke.isPending}
            onConfirm={() => revoke.mutate()}
          />
          <FormError message={formError(asProblem(revoke.error))} />
        </div>
      ) : null}
    </div>
  );
}
