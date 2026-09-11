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
import { CopyInline } from "../ui/copy";
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
  RowHeader,
  SplitList,
  SplitView,
  TableCard,
} from "../ui/layout";
import { useConnectorOptions, useDestinationOptions } from "../ui/options";
import { StatusBadge } from "../ui/status";
import { Day } from "../ui/time";

/// Destinations is the authoring pattern every other capability copies. Its parts, in the order they
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

type DestinationListItem = components["schemas"]["DestinationListItemDto"];
type Destination = components["schemas"]["DestinationDto"];

/// The fields each form renders, so a message the server attributes to one of them lands on that
/// control and everything else lands at form level.
const editFields = [
  "name",
  "config",
  "authentication_scheme",
  "authentication_config",
  "authentication_secret_refs",
  "environment",
  "description",
] as const;
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
  authentication_scheme: z.string(),
  authentication_config: jsonDocument,
  authentication_secret_refs: jsonDocument,
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
const authentication = (scheme: string, config: string, secretRefs: string) =>
  scheme.trim()
    ? { scheme: scheme.trim(), config: parseJson(config).value, secret_refs: parseJson(secretRefs).value }
    : null;

export function DestinationsScreen({
  tenantId,
  selectedDestinationId,
}: {
  tenantId: string;
  selectedDestinationId?: string;
}) {
  const [status, setStatus] = useFilterParam("status");
  const [environment, setEnvironment] = useFilterParam("environment");
  const [connector, setConnector] = useFilterParam("connector");
  const [name, setName] = useFilterParam("name");
  const connectors = useConnectorOptions();
  const destinationOptions = useDestinationOptions(tenantId);
  const applied = [status, environment, connector, name].filter(Boolean).length;
  // Environment is free text on a Destination, so there is no vocabulary to enumerate — the options
  // are the values this Tenant actually uses, read off the Destination list the screen already holds
  // for naming. ponytail: first hundred Destinations, which is what that read carries; a Tenant past
  // that needs the Admin API to answer "which environments" rather than the dashboard inferring it.
  const environments: string[] = [
    ...new Set(
      (destinationOptions.data?.items ?? []).map((item) => item.environment).filter((value) => value !== null),
    ),
  ].sort();
  const list = useInfiniteQuery({
    queryKey: ["destinations", tenantId, { status, environment, connector, name }],
    queryFn: ({ pageParam }) =>
      call(() =>
        api.GET("/admin/tenants/{tenantId}/destinations", {
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
    getNextPageParam: nextCursor<DestinationListItem>,
  });
  const destinations = list.data?.pages.flatMap((page) => page.items) ?? [];

  return (
    <Page>
      <PageHeader
        title="Destinations"
        action={
          <CreateSheet label="New Destination" description="Tenant-owned endpoint built from a Connector">
            {(close) => <CreateDestination tenantId={tenantId} onCreated={close} />}
          </CreateSheet>
        }
      >
        Tenant-owned endpoints built from a Connector. Subscriptions deliver to one of these.
      </PageHeader>

      <FilterBar applied={applied}>
        <FilterSearch id="destination-name" label="Find by name" value={name} onChange={setName} />
        <Filter id="destination-status" label="Status" value={status} onChange={setStatus}>
          <SelectItem value="active">Active</SelectItem>
          <SelectItem value="disabled">Disabled</SelectItem>
        </Filter>
        {/* The environments a Tenant actually uses, read off the rows it already has rather than
            from a fixed list: environment is free text on a Destination, so there is no vocabulary
            to enumerate. */}
        <Filter id="destination-environment" label="Environment" value={environment} onChange={setEnvironment}>
          {environments.map((option) => (
            <SelectItem key={option} value={option}>
              {option}
            </SelectItem>
          ))}
        </Filter>
        <Filter id="destination-connector" label="Connector" value={connector} onChange={setConnector}>
          {(connectors.data?.items ?? []).map((option) => (
            <SelectItem key={option.id} value={option.key}>
              {option.key}
            </SelectItem>
          ))}
        </Filter>
      </FilterBar>

      <SplitView>
        <SplitList>
          <ListStatus
            busy={list.isFetching}
            loaded={list.isSuccess}
            problem={asProblem(list.error)}
            empty={destinations.length === 0}
            emptyText="This Tenant has no Destinations matching this filter."
          />
          {destinations.length > 0 ? (
            <TableCard
              caption={`Destinations, newest first${appliedNote(applied)}`}
              footer={
                <LoadMore
                  noun="Destination"
                  hasMore={list.hasNextPage}
                  busy={list.isFetching}
                  loaded={destinations.length}
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
                {destinations.map((destination) => (
                  <TableRow key={destination.id} className="has-[a[aria-current=page]]:bg-selected-surface">
                    <RowHeader>
                      {/* The route is the selection, so `aria-current` follows the URL rather than a
                        separately tracked flag — the same contract the Event ledger already has. */}
                      <NavLink className="no-underline" to={`/tenants/${tenantId}/destinations/${destination.id}`} end>
                        {destination.name}
                      </NavLink>
                    </RowHeader>
                    <TableCell className="font-mono text-[13px]">{destination.connector_key}</TableCell>
                    <TableCell>{destination.environment ?? "—"}</TableCell>
                    <TableCell>
                      <StatusBadge status={destination.status} />
                    </TableCell>
                    <TableCell className="text-ink-secondary">{destination.description ?? "—"}</TableCell>
                    <TableCell className="text-ink-secondary">
                      <Day value={destination.updated_at} />
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </TableCard>
          ) : null}
        </SplitList>

        {/* Keyed by Destination id so switching rows is a distinct panel rather than the same one
              fed a new id, which is what keeps a stale name from being on screen when focus moves. */}
        {selectedDestinationId ? (
          <DestinationInspector key={selectedDestinationId} tenantId={tenantId} destinationId={selectedDestinationId} />
        ) : (
          <InspectorPlaceholder label="Destination detail">
            Select a Destination to read its configuration and authentication here.
          </InspectorPlaceholder>
        )}
      </SplitView>
    </Page>
  );
}

function CreateDestination({ tenantId, onCreated }: { tenantId: string; onCreated: () => void }) {
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const connectors = useQuery({
    queryKey: ["connector-options"],
    queryFn: () => call(() => api.GET("/admin/connectors", { params: { query: { limit: 100 } } })),
  });
  const connectorOptionsUnavailable = connectors.isPending || connectors.isError;

  const form = useForm<CreateValues>({
    resolver: zodResolver(createSchema),
    defaultValues: {
      connector_id: "",
      name: "",
      config: "{}",
      authentication_scheme: "",
      authentication_config: "{}",
      authentication_secret_refs: "{}",
      environment: "",
      description: "",
    },
  });

  const create = useMutation({
    mutationFn: (values: CreateValues) =>
      call(() =>
        api.POST("/admin/tenants/{tenantId}/destinations", {
          params: { path: { tenantId } },
          body: {
            connector_id: values.connector_id,
            name: values.name,
            configuration: parseJson(values.config).value,
            authentication: authentication(
              values.authentication_scheme,
              values.authentication_config,
              values.authentication_secret_refs,
            ),
            environment: optional(values.environment),
            description: optional(values.description),
          },
        }),
      ),
    onSuccess: (created) => {
      void queryClient.invalidateQueries({ queryKey: ["destinations", tenantId] });
      onCreated();
      if (created) navigate(`/tenants/${tenantId}/destinations/${created.id}`);
    },
  });

  const submit = form.handleSubmit((values) =>
    create.mutate(values, { onError: (failure) => applyProblem(form, failure, createFields) }),
  );

  return (
    <Form {...form}>
      <form className="flex flex-col gap-4" noValidate onSubmit={submit} aria-label="Create a Destination">
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
        <TextField control={form.control} name="authentication_scheme" label="Authentication scheme (optional)" />
        <TextAreaField
          control={form.control}
          name="authentication_config"
          label="Authentication configuration (JSON)"
          className="min-h-24 font-mono text-sm"
          required
        />
        <TextAreaField
          control={form.control}
          name="authentication_secret_refs"
          label="Authentication secret references (JSON)"
          hint="Reference names only; never enter secret values."
          className="min-h-24 font-mono text-sm"
          required
        />
        <TextField control={form.control} name="environment" label="Environment (optional)" />
        <TextField control={form.control} name="description" label="Description (optional)" />

        <Button type="submit" className="self-start" disabled={create.isPending || connectorOptionsUnavailable}>
          Create Destination
        </Button>
      </form>
    </Form>
  );
}

/// The Connector a Destination was built from, as an Operator names it: the manifest key and the
/// contract version it is pinned to. Falls back to the identifier when the list has not resolved it.
function connectorLabel(
  connectors: { id: string; key: string; contract_version: number | string }[] | undefined,
  id: string,
): string {
  const connector = connectors?.find((item) => item.id === id);
  return connector ? `${connector.key} v${connector.contract_version}` : id;
}

function DestinationInspector({ tenantId, destinationId }: { tenantId: string; destinationId: string }) {
  // A Destination is built from a Connector, and which one it was is the first thing an Operator
  // checks when its configuration looks wrong. The identifier answers a different question.
  const connectors = useConnectorOptions();
  const [notice, setNotice] = useState("");
  const destination = useQuery({
    queryKey: ["destination", tenantId, destinationId],
    queryFn: () =>
      call(() =>
        api.GET("/admin/tenants/{tenantId}/destinations/{id}", {
          params: { path: { tenantId, id: destinationId } },
        }),
      ),
  });

  const problem = asProblem(destination.error);
  if (problem)
    return (
      <Inspector label="Destination detail">
        <h2 className="m-0">Destination</h2>
        <p role="alert">{problem.detail ?? `This Destination could not be read (${problem.status}).`}</p>
      </Inspector>
    );
  if (!destination.data) return <Inspector label="Destination detail">Loading…</Inspector>;

  const current = destination.data;
  return (
    <Inspector label="Destination detail">
      {/* The identity on the left, the state that qualifies it on the right: the two things an
          Operator checks before reading anything else in the panel. */}
      <div className="flex items-start justify-between gap-3">
        <div className="group min-w-0">
          <h2 className="font-mono break-all">{current.name}</h2>
          <span className="block text-xs text-ink-secondary">
            <CopyInline label="Destination id" value={current.id} />
          </span>
        </div>
        <div className="flex shrink-0 items-center gap-1.5">
          <StatusBadge status={current.status} className="mt-0.5" />
          <CloseInspector to={`/tenants/${tenantId}/destinations`} label="Close the Destination detail" />
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
        <dt>Destination authentication</dt>
        <dd>{current.authentication ? current.authentication.scheme : "Not configured"}</dd>
      </Details>

      <section className="flex min-w-0 flex-col gap-2">
        <h4 className="eyebrow">Configuration</h4>
        <pre className="text-xs">{formatJson(current.configuration)}</pre>
        <p className="m-0 text-xs text-ink-secondary">
          An update replaces this object outright rather than merging fields.
        </p>
      </section>

      <WriteStatus done={notice !== ""}>{notice}</WriteStatus>
      <EditDestination
        key={current.updated_at}
        tenantId={tenantId}
        destination={current}
        onDone={() => setNotice("Destination deactivated.")}
      />
    </Inspector>
  );
}

function EditDestination({
  tenantId,
  destination,
  onDone,
}: {
  tenantId: string;
  destination: Destination;
  onDone: () => void;
}) {
  const queryClient = useQueryClient();
  /// Both reads that can now be wrong: this Destination, and any list it appears in.
  const reread = () => {
    void queryClient.invalidateQueries({ queryKey: ["destination", tenantId, destination.id] });
    void queryClient.invalidateQueries({ queryKey: ["destinations", tenantId] });
  };

  const form = useForm<EditValues>({
    resolver: zodResolver(editSchema),
    defaultValues: {
      name: destination.name,
      config: formatJson(destination.configuration),
      authentication_scheme: destination.authentication?.scheme ?? "",
      authentication_config: formatJson(destination.authentication?.config ?? {}),
      authentication_secret_refs: formatJson(destination.authentication?.secret_refs ?? {}),
      environment: destination.environment ?? "",
      description: destination.description ?? "",
    },
  });

  const save = useMutation({
    mutationFn: (values: EditValues) =>
      call(() =>
        api.PUT("/admin/tenants/{tenantId}/destinations/{id}", {
          params: { path: { tenantId, id: destination.id } },
          body: {
            name: values.name,
            configuration: parseJson(values.config).value,
            authentication: authentication(
              values.authentication_scheme,
              values.authentication_config,
              values.authentication_secret_refs,
            ),
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
        api.POST("/admin/tenants/{tenantId}/destinations/{id}/deactivate", {
          params: { path: { tenantId, id: destination.id } },
        }),
      ),
    onSuccess: () => {
      reread();
      onDone();
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
                aria-label={`Edit ${destination.name}`}
                noValidate
                onSubmit={form.handleSubmit((values) =>
                  save.mutate(values, {
                    onSuccess: close,
                    onError: (failure) => applyProblem(form, failure, editFields),
                  }),
                )}
              >
                <FormError message={formError(asProblem(save.error), editFields)} />

                <TextField control={form.control} name="name" label="Name" required />
                <TextAreaField
                  control={form.control}
                  name="config"
                  label="Configuration (JSON)"
                  className="min-h-40 font-mono text-sm"
                  required
                />
                <TextField
                  control={form.control}
                  name="authentication_scheme"
                  label="Authentication scheme (optional)"
                />
                <TextAreaField
                  control={form.control}
                  name="authentication_config"
                  label="Authentication configuration (JSON)"
                  className="min-h-24 font-mono text-sm"
                  required
                />
                <TextAreaField
                  control={form.control}
                  name="authentication_secret_refs"
                  label="Authentication secret references (JSON)"
                  hint="Reference names only; never enter secret values."
                  className="min-h-24 font-mono text-sm"
                  required
                />
                <TextField control={form.control} name="environment" label="Environment (optional)" />
                <TextField control={form.control} name="description" label="Description (optional)" />

                <Button type="submit" className="self-start" disabled={save.isPending}>
                  Save changes
                </Button>
                <WriteStatus done={save.isSuccess}>Changes saved.</WriteStatus>
              </form>
            </Form>
          )}
        </EditSheet>
        {destination.status === "active" ? (
          <ConfirmAction
            label="Deactivate"
            consequence={`Deactivating ${destination.name} is blocked while active Subscriptions deliver to it. Deliveries already queued are not cancelled.`}
            question={`Deactivate the Destination "${destination.name}"?`}
            confirmLabel={`Deactivate ${destination.name}`}
            busy={deactivate.isPending}
            onConfirm={() => deactivate.mutate()}
          />
        ) : null}
      </div>
      <FormError message={formError(asProblem(deactivate.error))} />
    </div>
  );
}
