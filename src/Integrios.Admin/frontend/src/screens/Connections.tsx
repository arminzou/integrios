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
import { Filter, Form, SelectField, TextAreaField, TextField } from "../ui/fields";
import { useFilterParam } from "../ui/filters";
import { applyProblem } from "../ui/formProblem";
import { formatJson, parseJson } from "../ui/json";
import { Details, Page, PageHeader, Panel, RowHeader, TableCard } from "../ui/layout";
import { StatusBadge } from "../ui/status";

/// Connections is the authoring pattern every other capability copies. Its parts, in the order they
/// appear below:
///
/// - A list screen is a heading, a create panel behind a disclosure so it never dominates the list,
///   a filter, and the rows in a bordered card. A list is a `useInfiniteQuery` whose key carries the
///   filters, because a cursor is only valid for the filters it was issued under; paging is an
///   explicit Load more over `next_cursor`, never a page number or a total.
/// - A form is a Zod schema plus `useForm`. The schema is the only place the form's rules live; the
///   submit handler does the conversion a form of strings always needs — text to a JSON document, an
///   untouched optional field to `null` — and names the request body the typed client sends.
/// - A write is a `useMutation`, and on success it invalidates the queries it affected so the screen
///   re-reads authoritative server state rather than patching a second local copy of it.
/// - A rejected write comes back as Problem Details, thrown by `call` and caught here.
///   `applyProblem` puts each field-keyed message on its own control and `formError` renders
///   whatever was attributed to no rendered field, so the Admin API stays the authority on what is
///   wrong with a document.
/// - A picker over another capability is a real `<select>`, and an irreversible action is
///   `ConfirmAction`, which names what it is about to change before it can be confirmed.
///
/// What is capability-specific — which fields exist, what they mean, which mutations the Admin API
/// offers — stays here rather than moving into a shared form abstraction.

type ConnectionListItem = components["schemas"]["ConnectionListItemDto"];
type Connection = components["schemas"]["ConnectionDto"];

/// The fields each form renders, so a message the server attributes to one of them lands on that
/// control and everything else lands at form level.
const editFields = ["name", "config", "environment", "description"] as const;
const createFields = ["connector_id", ...editFields] as const;

/// A domain JSON document, authored as text. Well-formedness is all the dashboard checks; the
/// Connector contract, and the server, remain the authority on whether the document is valid.
const jsonDocument = z.string().superRefine((text, ctx) => {
  const parsed = parseJson(text);
  if (parsed.error !== undefined) ctx.addIssue({ code: z.ZodIssueCode.custom, message: parsed.error });
});

const editSchema = z.object({
  name: z.string().trim().min(1, "Enter a name."),
  config: jsonDocument,
  environment: z.string(),
  description: z.string(),
});

const createSchema = editSchema.extend({
  connector_id: z.string().min(1, "Choose a Connector."),
});

type EditValues = z.infer<typeof editSchema>;
type CreateValues = z.infer<typeof createSchema>;

/// An optional field left untouched is absent, not empty.
const optional = (text: string) => text.trim() || null;

export function ConnectionsScreen({ tenantId }: { tenantId: string }) {
  const [status, setStatus] = useFilterParam("status");
  const create = useCreatePanel("new-connection");
  const list = useInfiniteQuery({
    queryKey: ["connections", tenantId, { status }],
    queryFn: ({ pageParam }) =>
      call(() =>
        api.GET("/admin/tenants/{tenantId}/connections", {
          params: {
            path: { tenantId },
            query: { status: status || undefined, after: pageParam ?? undefined, limit: 20 },
          },
        }),
      ),
    initialPageParam: null as string | null,
    getNextPageParam: nextCursor<ConnectionListItem>,
  });
  const connections = list.data?.pages.flatMap((page) => page.items) ?? [];

  return (
    <Page>
      <PageHeader title="Connections" action={<Button {...create.triggerProps}>New Connection</Button>}>
        In{" "}
        <Link className="underline" to={`/tenants/${tenantId}`}>
          this Tenant
        </Link>
        .
      </PageHeader>

      <Panel {...create.panelProps} className="max-w-none">
        <CreateConnection tenantId={tenantId} />
      </Panel>

      <section className="flex flex-col gap-4">
        <h2>All Connections</h2>
        <FilterBar applied={(status ? 1 : 0) as number}>
          <Filter id="connection-status" label="Status" value={status} onChange={setStatus}>
            <option value="">Any status</option>
            <option value="active">Active</option>
            <option value="disabled">Disabled</option>
          </Filter>
        </FilterBar>

        <ListStatus
          busy={list.isFetching}
          loaded={list.isSuccess}
          problem={asProblem(list.error)}
          empty={connections.length === 0}
          emptyText="This Tenant has no Connections matching this filter."
        />
        {connections.length > 0 ? (
          <TableCard
            caption="Connections, newest first"
            footer={
              <LoadMore
                hasMore={list.hasNextPage}
                busy={list.isFetching}
                loaded={connections.length}
                onLoadMore={() => void list.fetchNextPage()}
              />
            }
          >
            <TableHeader>
              <TableRow>
                <TableHead scope="col">Name</TableHead>
                <TableHead scope="col">Status</TableHead>
                <TableHead scope="col">Environment</TableHead>
                <TableHead scope="col">Description</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {connections.map((connection) => (
                <TableRow key={connection.id}>
                  <RowHeader>
                    <Link className="underline" to={`/tenants/${tenantId}/connections/${connection.id}`}>
                      {connection.name}
                    </Link>
                  </RowHeader>
                  <TableCell>
                    <StatusBadge status={connection.status} />
                  </TableCell>
                  <TableCell>{connection.environment ?? "—"}</TableCell>
                  <TableCell>{connection.description ?? "—"}</TableCell>
                </TableRow>
              ))}
            </TableBody>
          </TableCard>
        ) : null}
      </section>
    </Page>
  );
}

function CreateConnection({ tenantId }: { tenantId: string }) {
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const connectors = useQuery({
    queryKey: ["connector-options"],
    queryFn: () => call(() => api.GET("/admin/connectors", { params: { query: { limit: 100 } } })),
  });
  const connectorOptionsUnavailable = connectors.isPending || connectors.isError;

  const form = useForm<CreateValues>({
    resolver: zodResolver(createSchema),
    defaultValues: { connector_id: "", name: "", config: "{}", environment: "", description: "" },
  });

  const create = useMutation({
    mutationFn: (values: CreateValues) =>
      call(() =>
        api.POST("/admin/tenants/{tenantId}/connections", {
          params: { path: { tenantId } },
          body: {
            connector_id: values.connector_id,
            name: values.name,
            config: parseJson(values.config).value,
            // Verification and authentication schemes carry secret references, never secret
            // values, so they are configured on the Connection itself rather than typed here.
            source_verification: null,
            destination_authentication: null,
            environment: optional(values.environment),
            description: optional(values.description),
          },
        }),
      ),
    onSuccess: (created) => {
      void queryClient.invalidateQueries({ queryKey: ["connections", tenantId] });
      if (created) navigate(`/tenants/${tenantId}/connections/${created.id}`);
    },
  });

  const submit = form.handleSubmit((values) =>
    create.mutate(values, { onError: (failure) => applyProblem(form, failure, createFields) }),
  );

  return (
    <Form {...form}>
      <Panel asChild>
        <form className="flex flex-col gap-4" onSubmit={submit}>
          <h2>Create a Connection</h2>
          <FormError message={formError(asProblem(connectors.error))} />
          <FormError message={formError(asProblem(create.error), createFields)} />

          <SelectField
            control={form.control}
            name="connector_id"
            label="Connector"
            hint={connectors.data?.next_cursor ? "Showing the first 100 Connectors." : undefined}
            disabled={connectorOptionsUnavailable}
            required
          >
            <option value="">Choose a Connector</option>
            {(connectors.data?.items ?? []).map((connector) => (
              <option key={connector.id} value={connector.id}>
                {connector.name} (v{connector.contract_version}, {connector.direction})
              </option>
            ))}
          </SelectField>
          <TextField control={form.control} name="name" label="Name" required />
          <TextAreaField
            control={form.control}
            name="config"
            label="Configuration (JSON)"
            hint="The Connector's manifest defines what this document must contain."
            className="min-h-40 font-mono text-sm"
            required
          />
          <TextField control={form.control} name="environment" label="Environment (optional)" />
          <TextField control={form.control} name="description" label="Description (optional)" />

          <Button type="submit" className="self-start" disabled={create.isPending || connectorOptionsUnavailable}>
            Create Connection
          </Button>
        </form>
      </Panel>
    </Form>
  );
}

export function ConnectionScreen({ tenantId, connectionId }: { tenantId: string; connectionId: string }) {
  const [notice, setNotice] = useState("");
  const connection = useQuery({
    queryKey: ["connection", tenantId, connectionId],
    queryFn: () =>
      call(() =>
        api.GET("/admin/tenants/{tenantId}/connections/{id}", {
          params: { path: { tenantId, id: connectionId } },
        }),
      ),
  });

  const problem = asProblem(connection.error);
  if (problem)
    return (
      <>
        <h1>Connection</h1>
        <p role="alert">{problem.detail ?? `This Connection could not be read (${problem.status}).`}</p>
      </>
    );
  if (!connection.data) return <p>Loading…</p>;

  const current = connection.data;
  return (
    <Page>
      <PageHeader title={current.name}>
        In{" "}
        <Link className="underline" to={`/tenants/${tenantId}/connections`}>
          this Tenant's Connections
        </Link>
        .
      </PageHeader>

      <Panel>
        <Details>
          <dt>Status</dt>
          <dd>
            <StatusBadge status={current.status} />
          </dd>
          <dt>Connector</dt>
          <dd>
            <Link className="font-mono text-sm underline" to={`/connectors/${current.connector_id}`}>
              {current.connector_id}
            </Link>
          </dd>
          <dt>Environment</dt>
          <dd>{current.environment ?? "—"}</dd>
          <dt>Source verification</dt>
          <dd>{current.source_verification ? current.source_verification.scheme : "Not configured"}</dd>
          <dt>Destination authentication</dt>
          <dd>{current.destination_authentication ? current.destination_authentication.scheme : "Not configured"}</dd>
        </Details>
      </Panel>

      <section className="flex max-w-2xl flex-col gap-2">
        <h2>Configuration</h2>
        <pre className="max-w-2xl text-sm">{formatJson(current.config)}</pre>
      </section>

      <WriteStatus done={notice !== ""}>{notice}</WriteStatus>
      <EditConnection
        key={current.updated_at}
        tenantId={tenantId}
        connection={current}
        onDone={() => setNotice("Connection deactivated.")}
      />
    </Page>
  );
}

function EditConnection({
  tenantId,
  connection,
  onDone,
}: {
  tenantId: string;
  connection: Connection;
  onDone: () => void;
}) {
  const queryClient = useQueryClient();
  /// Both reads that can now be wrong: this Connection, and any list it appears in.
  const reread = () => {
    void queryClient.invalidateQueries({ queryKey: ["connection", tenantId, connection.id] });
    void queryClient.invalidateQueries({ queryKey: ["connections", tenantId] });
  };

  const form = useForm<EditValues>({
    resolver: zodResolver(editSchema),
    defaultValues: {
      name: connection.name,
      config: formatJson(connection.config),
      environment: connection.environment ?? "",
      description: connection.description ?? "",
    },
  });

  const save = useMutation({
    mutationFn: (values: EditValues) =>
      call(() =>
        api.PATCH("/admin/tenants/{tenantId}/connections/{id}", {
          params: { path: { tenantId, id: connection.id } },
          body: {
            name: values.name,
            config: parseJson(values.config).value,
            // Sending null leaves the stored scheme untouched: this form never round-trips a
            // scheme's secret references, so it must not claim to replace them either.
            source_verification: null,
            destination_authentication: null,
            environment: optional(values.environment),
            description: optional(values.description),
          },
        }),
      ),
    onSuccess: reread,
  });

  const deactivate = useMutation({
    mutationFn: () =>
      call(() =>
        api.POST("/admin/tenants/{tenantId}/connections/{id}/deactivate", {
          params: { path: { tenantId, id: connection.id } },
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
            <h2>Edit {connection.name}</h2>
            <FormError message={formError(asProblem(save.error), editFields)} />

            <TextField control={form.control} name="name" label="Name" required />
            <TextAreaField
              control={form.control}
              name="config"
              label="Configuration (JSON)"
              className="min-h-40 font-mono text-sm"
              required
            />
            <TextField control={form.control} name="environment" label="Environment (optional)" />
            <TextField control={form.control} name="description" label="Description (optional)" />

            <Button type="submit" className="self-start" disabled={save.isPending}>
              Save changes
            </Button>
            <WriteStatus done={save.isSuccess}>Changes saved.</WriteStatus>
          </form>
        </Panel>
      </Form>

      {connection.status === "active" ? (
        <div className="flex flex-col items-start gap-2">
          <ConfirmAction
            label="Deactivate Connection"
            question={`Deactivate the Connection "${connection.name}"? Sources and Subscriptions that use it stop working.`}
            confirmLabel={`Deactivate ${connection.name}`}
            busy={deactivate.isPending}
            onConfirm={() => deactivate.mutate()}
          />
          <FormError message={formError(asProblem(deactivate.error))} />
        </div>
      ) : null}
    </div>
  );
}
