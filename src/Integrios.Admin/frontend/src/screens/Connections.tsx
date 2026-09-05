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
import { Filter, Form, SelectField, TextAreaField, TextField } from "../ui/fields";
import { applyProblem } from "../ui/formProblem";
import { formatJson, parseJson } from "../ui/json";
import { Details, Page, PageHeader, Panel, RowHeader, TableCard } from "../ui/layout";
import { useAction } from "../ui/useAction";
import { useCursorList } from "../ui/useCursorList";
import { useOptions } from "../ui/useOptions";
import { useResource } from "../ui/useResource";

/// Connections is the authoring pattern every other capability copies. Its parts, in the order they
/// appear below:
///
/// - A list screen is a heading, a create panel behind a disclosure so it never dominates the list,
///   a filter, and the rows in a bordered card. Paging stays on the cursor the list returns.
/// - A form is a Zod schema plus `useForm`. The schema is the only place the form's rules live; the
///   submit handler does the conversion a form of strings always needs — text to a JSON document, an
///   untouched optional field to `null` — and names the request body the typed client sends.
/// - A rejected write comes back as Problem Details. `applyProblem` puts each field-keyed message on
///   its own control and `formError` renders whatever was attributed to no rendered field, so the
///   Admin API stays the authority on what is wrong with a document.
/// - A picker over another capability is a real `<select>`, and an irreversible action is
///   `ConfirmAction`, which names what it is about to change before it can be confirmed.
///
/// What is capability-specific — which fields exist, what they mean, which mutations the Admin API
/// offers — stays here rather than moving into a shared form abstraction.

type ConnectionListItem = components["schemas"]["ConnectionListItemDto"];
type Connection = components["schemas"]["ConnectionDto"];
type ConnectorListItem = components["schemas"]["ConnectorListItemDto"];

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
  const [status, setStatus] = useState("");
  const list = useCursorList<ConnectionListItem>(
    (after) =>
      api.GET("/admin/tenants/{tenantId}/connections", {
        params: { path: { tenantId }, query: { status: status || undefined, after: after ?? undefined, limit: 20 } },
      }),
    `connections|${tenantId}|${status}`,
  );

  return (
    <Page>
      <PageHeader title="Connections">
        In{" "}
        <Link className="underline" to={`/tenants/${tenantId}`}>
          this Tenant
        </Link>
        .
      </PageHeader>

      <Disclosure label="New Connection">
        <CreateConnection tenantId={tenantId} onCreated={list.reload} />
      </Disclosure>

      <section className="flex flex-col gap-4">
        <h2>All Connections</h2>
        <Filter id="connection-status" label="Status" value={status} onChange={setStatus}>
          <option value="">Any status</option>
          <option value="active">Active</option>
          <option value="disabled">Disabled</option>
        </Filter>

        <ListStatus
          busy={list.busy}
          loaded={list.loaded}
          problem={list.problem}
          empty={list.items.length === 0}
          emptyText="This Tenant has no Connections matching this filter."
        />
        {list.items.length > 0 ? (
          <TableCard caption="Connections, newest first">
            <TableHeader>
              <TableRow>
                <TableHead scope="col">Name</TableHead>
                <TableHead scope="col">Status</TableHead>
                <TableHead scope="col">Environment</TableHead>
                <TableHead scope="col">Description</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {list.items.map((connection) => (
                <TableRow key={connection.id}>
                  <RowHeader>
                    <Link className="underline" to={`/tenants/${tenantId}/connections/${connection.id}`}>
                      {connection.name}
                    </Link>
                  </RowHeader>
                  <TableCell>{connection.status}</TableCell>
                  <TableCell>{connection.environment ?? "—"}</TableCell>
                  <TableCell>{connection.description ?? "—"}</TableCell>
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

function CreateConnection({ tenantId, onCreated }: { tenantId: string; onCreated: () => void }) {
  const navigate = useNavigate();
  const connectors = useOptions<ConnectorListItem>(
    () => api.GET("/admin/connectors", { params: { query: { limit: 100 } } }),
    "connector-options",
  );
  const { busy, problem, run } = useAction();
  const connectorOptionsUnavailable = connectors.busy || connectors.problem !== null;

  const form = useForm<CreateValues>({
    resolver: zodResolver(createSchema),
    defaultValues: { connector_id: "", name: "", config: "{}", environment: "", description: "" },
  });

  const submit = form.handleSubmit(async (values) => {
    const failure = await run(
      () =>
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
      (created) => {
        onCreated();
        if (created) navigate(`/tenants/${tenantId}/connections/${created.id}`);
      },
    );
    if (failure) applyProblem(form, failure, createFields);
  });

  return (
    <Form {...form}>
      <Panel asChild>
        <form className="flex flex-col gap-4" onSubmit={submit}>
          <h2>Create a Connection</h2>
          <FormError message={formError(connectors.problem)} />
          <FormError message={formError(problem, createFields)} />

          <SelectField
            control={form.control}
            name="connector_id"
            label="Connector"
            hint={connectors.truncated ? "Showing the first 100 Connectors." : undefined}
            disabled={connectorOptionsUnavailable}
            required
          >
            <option value="">Choose a Connector</option>
            {connectors.items.map((connector) => (
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

          <Button type="submit" className="self-start" disabled={busy || connectorOptionsUnavailable}>
            Create Connection
          </Button>
        </form>
      </Panel>
    </Form>
  );
}

export function ConnectionScreen({ tenantId, connectionId }: { tenantId: string; connectionId: string }) {
  const connection = useResource<Connection>(
    () =>
      api.GET("/admin/tenants/{tenantId}/connections/{id}", {
        params: { path: { tenantId, id: connectionId } },
      }),
    `${tenantId}|${connectionId}`,
  );

  if (connection.problem)
    return (
      <>
        <h1>Connection</h1>
        <p role="alert">
          {connection.problem.detail ?? `This Connection could not be read (${connection.problem.status}).`}
        </p>
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
          <dd>{current.status}</dd>
          <dt>Connector</dt>
          <dd>
            <Link className="underline" to={`/connectors/${current.connector_id}`}>
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

      <EditConnection key={current.updated_at} tenantId={tenantId} connection={current} onSaved={connection.reload} />
    </Page>
  );
}

function EditConnection({
  tenantId,
  connection,
  onSaved,
}: {
  tenantId: string;
  connection: Connection;
  onSaved: () => void;
}) {
  const { busy, problem, run } = useAction();
  const form = useForm<EditValues>({
    resolver: zodResolver(editSchema),
    defaultValues: {
      name: connection.name,
      config: formatJson(connection.config),
      environment: connection.environment ?? "",
      description: connection.description ?? "",
    },
  });

  const submit = form.handleSubmit(async (values) => {
    const failure = await run(
      () =>
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
      onSaved,
    );
    if (failure) applyProblem(form, failure, editFields);
  });

  return (
    <div className="flex flex-col gap-6">
      <Form {...form}>
        <Panel asChild>
          <form className="flex flex-col gap-4" onSubmit={submit}>
            <h2>Edit {connection.name}</h2>
            <FormError message={formError(problem, editFields)} />

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

            <Button type="submit" className="self-start" disabled={busy}>
              Save changes
            </Button>
          </form>
        </Panel>
      </Form>

      {connection.status === "active" ? (
        <ConfirmAction
          label="Deactivate Connection"
          question={`Deactivate the Connection "${connection.name}"? Sources and Subscriptions that use it stop working.`}
          confirmLabel={`Deactivate ${connection.name}`}
          busy={busy}
          onConfirm={() =>
            void run(
              () =>
                api.POST("/admin/tenants/{tenantId}/connections/{id}/deactivate", {
                  params: { path: { tenantId, id: connection.id } },
                }),
              onSaved,
            )
          }
        />
      ) : null}
    </div>
  );
}
