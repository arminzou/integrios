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
  Disclosure,
  FilterBar,
  FormError,
  ListStatus,
  LoadMore,
  WriteStatus,
} from "../ui/controls";
import { Filter, FilterSearch, Form, SelectField, TextAreaField, TextField } from "../ui/fields";
import { useFilterParam } from "../ui/filters";
import { applyProblem } from "../ui/formProblem";
import { formatJson, parseJson } from "../ui/json";
import {
  CloseInspector,
  Details,
  Inspector,
  InspectorPlaceholder,
  Page,
  PageHeader,
  Panel,
  RowHeader,
  SplitList,
  SplitView,
  TableCard,
} from "../ui/layout";
import { useConnectionOptions, useConnectorOptions } from "../ui/options";
import { StatusBadge } from "../ui/status";
import { Day } from "../ui/time";

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

export function ConnectionsScreen({
  tenantId,
  selectedConnectionId,
}: {
  tenantId: string;
  selectedConnectionId?: string;
}) {
  const [status, setStatus] = useFilterParam("status");
  const [environment, setEnvironment] = useFilterParam("environment");
  const [connector, setConnector] = useFilterParam("connector");
  const [name, setName] = useFilterParam("name");
  const connectors = useConnectorOptions();
  const connectionOptions = useConnectionOptions(tenantId);
  const applied = [status, environment, connector, name].filter(Boolean).length;
  // Environment is free text on a Connection, so there is no vocabulary to enumerate — the options
  // are the values this Tenant actually uses, read off the Connection list the screen already holds
  // for naming. ponytail: first hundred Connections, which is what that read carries; a Tenant past
  // that needs the Admin API to answer "which environments" rather than the dashboard inferring it.
  const environments: string[] = [
    ...new Set((connectionOptions.data?.items ?? []).map((item) => item.environment).filter((value) => value !== null)),
  ].sort();
  const list = useInfiniteQuery({
    queryKey: ["connections", tenantId, { status, environment, connector, name }],
    queryFn: ({ pageParam }) =>
      call(() =>
        api.GET("/admin/tenants/{tenantId}/connections", {
          params: {
            path: { tenantId },
            query: {
              status: status || undefined,
              environment: environment || undefined,
              connector: connector || undefined,
              name: name || undefined,
              after: pageParam ?? undefined,
              limit: 20,
            },
          },
        }),
      ),
    initialPageParam: null as string | null,
    getNextPageParam: nextCursor<ConnectionListItem>,
  });
  const connections = list.data?.pages.flatMap((page) => page.items) ?? [];

  return (
    <Page>
      <PageHeader
        title="Connections"
        action={
          <CreateSheet label="New Connection" description="Tenant-owned endpoint built from a Connector">
            {(close) => <CreateConnection tenantId={tenantId} onCreated={close} />}
          </CreateSheet>
        }
      >
        Tenant-owned endpoints built from a Connector. A Subscription delivers to one of these.
      </PageHeader>

      <FilterBar applied={applied}>
        <FilterSearch id="connection-name" label="Find by name" value={name} onChange={setName} />
        <Filter id="connection-status" label="Status" value={status} onChange={setStatus}>
          <SelectItem value="active">Active</SelectItem>
          <SelectItem value="disabled">Disabled</SelectItem>
        </Filter>
        {/* The environments a Tenant actually uses, read off the rows it already has rather than
            from a fixed list: environment is free text on a Connection, so there is no vocabulary
            to enumerate. */}
        <Filter id="connection-environment" label="Environment" value={environment} onChange={setEnvironment}>
          {environments.map((option) => (
            <SelectItem key={option} value={option}>
              {option}
            </SelectItem>
          ))}
        </Filter>
        <Filter id="connection-connector" label="Connector" value={connector} onChange={setConnector}>
          {(connectors.data?.items ?? []).map((option) => (
            <SelectItem key={option.id} value={option.key}>
              {option.key}
            </SelectItem>
          ))}
        </Filter>
      </FilterBar>

      <ListStatus
        busy={list.isFetching}
        loaded={list.isSuccess}
        problem={asProblem(list.error)}
        empty={connections.length === 0}
        emptyText="This Tenant has no Connections matching this filter."
      />

      <SplitView>
        <SplitList>
          {connections.length > 0 ? (
            <TableCard
              caption={`Connections, newest first${appliedNote(applied)}`}
              footer={
                <LoadMore
                  noun="Connection"
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
                  <TableHead scope="col">Connector</TableHead>
                  <TableHead scope="col">Environment</TableHead>
                  <TableHead scope="col">Status</TableHead>
                  <TableHead scope="col">Description</TableHead>
                  <TableHead scope="col">Updated</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {connections.map((connection) => (
                  <TableRow key={connection.id} className="has-[a[aria-current=page]]:bg-selected-surface">
                    <RowHeader className="whitespace-nowrap">
                      {/* The route is the selection, so `aria-current` follows the URL rather than a
                        separately tracked flag — the same contract the Event ledger already has. */}
                      <NavLink
                        className="font-mono no-underline"
                        to={`/tenants/${tenantId}/connections/${connection.id}`}
                        end
                      >
                        {connection.name}
                      </NavLink>
                    </RowHeader>
                    <TableCell className="font-mono text-[13px]">{connection.connector_key}</TableCell>
                    <TableCell>{connection.environment ?? "—"}</TableCell>
                    <TableCell>
                      <StatusBadge status={connection.status} />
                    </TableCell>
                    <TableCell className="text-ink-secondary">{connection.description ?? "—"}</TableCell>
                    <TableCell className="text-ink-secondary">
                      <Day value={connection.updated_at} />
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </TableCard>
          ) : null}
        </SplitList>

        {/* Keyed by Connection id so switching rows is a distinct panel rather than the same one
              fed a new id, which is what keeps a stale name from being on screen when focus moves. */}
        {selectedConnectionId ? (
          <ConnectionInspector key={selectedConnectionId} tenantId={tenantId} connectionId={selectedConnectionId} />
        ) : (
          <InspectorPlaceholder label="Connection detail">
            Select a Connection to read its configuration and authentication here.
          </InspectorPlaceholder>
        )}
      </SplitView>
    </Page>
  );
}

function CreateConnection({ tenantId, onCreated }: { tenantId: string; onCreated: () => void }) {
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
      onCreated();
      if (created) navigate(`/tenants/${tenantId}/connections/${created.id}`);
    },
  });

  const submit = form.handleSubmit((values) =>
    create.mutate(values, { onError: (failure) => applyProblem(form, failure, createFields) }),
  );

  return (
    <Form {...form}>
      <form className="flex flex-col gap-4" onSubmit={submit} aria-label="Create a Connection">
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
          {(connectors.data?.items ?? []).map((connector) => (
            <SelectItem key={connector.id} value={connector.id}>
              {connector.name} (v{connector.contract_version}, {connector.direction})
            </SelectItem>
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
    </Form>
  );
}

/// The Connector a Connection was built from, as an Operator names it: the manifest key and the
/// contract version it is pinned to. Falls back to the identifier when the list has not resolved it.
function connectorLabel(
  connectors: { id: string; key: string; contract_version: number | string }[] | undefined,
  id: string,
): string {
  const connector = connectors?.find((item) => item.id === id);
  return connector ? `${connector.key} v${connector.contract_version}` : id;
}

function ConnectionInspector({ tenantId, connectionId }: { tenantId: string; connectionId: string }) {
  // A Connection is built from a Connector, and which one it was is the first thing an Operator
  // checks when its configuration looks wrong. The identifier answers a different question.
  const connectors = useConnectorOptions();
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
      <Inspector label="Connection detail">
        <h2 className="m-0">Connection</h2>
        <p role="alert">{problem.detail ?? `This Connection could not be read (${problem.status}).`}</p>
      </Inspector>
    );
  if (!connection.data) return <Inspector label="Connection detail">Loading…</Inspector>;

  const current = connection.data;
  return (
    <Inspector label="Connection detail">
      {/* The identity on the left, the state that qualifies it on the right: the two things an
          Operator checks before reading anything else in the panel. */}
      <div className="flex items-start justify-between gap-3">
        <div className="min-w-0">
          <h2 className="font-mono break-all">{current.name}</h2>
          <span className="block font-mono text-xs break-all text-ink-secondary">{current.id}</span>
        </div>
        <div className="flex shrink-0 items-center gap-1.5">
          <StatusBadge status={current.status} className="mt-0.5" />
          <CloseInspector to={`/tenants/${tenantId}/connections`} label="Close the Connection detail" />
        </div>
      </div>

      <Details className="border-b pb-3.5">
        <dt>Connector</dt>
        <dd>
          <Link className="font-mono" to={`/connectors/${current.connector_id}`}>
            {connectorLabel(connectors.data?.items, current.connector_id)}
          </Link>
        </dd>
        <dt>Environment</dt>
        <dd>{current.environment ?? "—"}</dd>
        <dt>Source verification</dt>
        <dd>{current.source_verification ? current.source_verification.scheme : "Not configured"}</dd>
        <dt>Destination authentication</dt>
        <dd>{current.destination_authentication ? current.destination_authentication.scheme : "Not configured"}</dd>
      </Details>

      <section className="flex min-w-0 flex-col gap-2">
        <h4 className="eyebrow">Configuration</h4>
        <pre className="text-xs">{formatJson(current.config)}</pre>
        <p className="m-0 text-xs text-ink-secondary">
          An update replaces this object outright rather than merging fields.
        </p>
      </section>

      <WriteStatus done={notice !== ""}>{notice}</WriteStatus>
      <EditConnection
        key={current.updated_at}
        tenantId={tenantId}
        connection={current}
        onDone={() => setNotice("Connection deactivated.")}
      />
    </Inspector>
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
      <Disclosure label="Edit this Connection">
        <Form {...form}>
          <Panel asChild>
            <form className="flex flex-col gap-4" aria-label={`Edit ${connection.name}`} onSubmit={submit}>
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
      </Disclosure>

      {connection.status === "active" ? (
        <div className="flex flex-col items-start gap-2">
          <ConfirmAction
            label="Deactivate Connection"
            consequence={`Deactivating ${connection.name} stops every Subscription that delivers to it. Deliveries already queued are not cancelled.`}
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
