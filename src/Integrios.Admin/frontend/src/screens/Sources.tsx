import { zodResolver } from "@hookform/resolvers/zod";
import { useInfiniteQuery, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useEffect, useState } from "react";
import { useForm } from "react-hook-form";
import { Link, NavLink, useLocation, useNavigate } from "react-router";
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
  narrowable,
  ReadError,
  SheetButton,
  WriteStatus,
} from "../ui/controls";
import { CopyInline } from "../ui/copy";
import { Filter, Form, SelectField, TextAreaField, TextField } from "../ui/fields";
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
import { activeOnly, nameIn, useConnectorOptions, useTopicOptions } from "../ui/options";
import { StatusBadge } from "../ui/status";
import { EventBuilder } from "./EventBuilder";
import { SourceGuide } from "./SourceGuide";

type SourceListItem = components["schemas"]["SourceListItemDto"];
type Source = components["schemas"]["SourceDto"];

const sourceTypes = [
  { value: "event_api", label: "Event API" },
  { value: "webhook", label: "Webhook" },
  { value: "queue", label: "Queue" },
];

const createFields = [
  "name",
  "connector_id",
  "topic_id",
  "type",
  "configuration",
  "verification_scheme",
  "verification_config",
  "verification_secret_refs",
  "input_requirements",
  "mapping",
  "identity_kind",
  "identity_value",
] as const;
const editFields = ["name", "configuration", "input_requirements", "mapping"] as const;

/// A domain JSON document, authored as text: well-formedness is all the dashboard checks, and the
/// server stays the authority on whether the document is valid for this Source type.
const jsonDocument = z.string().superRefine((text, ctx) => {
  const parsed = parseJson(text);
  if (parsed.error !== undefined) ctx.addIssue({ code: z.ZodIssueCode.custom, message: parsed.error });
});

const optionalJsonDocument = z.string().superRefine((text, ctx) => {
  if (!text.trim()) return;
  const parsed = parseJson(text);
  if (parsed.error !== undefined) ctx.addIssue({ code: z.ZodIssueCode.custom, message: parsed.error });
});

const createSchema = z.object({
  name: z.string().trim().min(1, "Enter a name."),
  connector_id: z.string().min(1, "Choose a Connector."),
  topic_id: z.string().min(1, "Choose a Topic."),
  type: z.string().min(1, "Choose a type."),
  configuration: jsonDocument,
  verification_scheme: z.string(),
  verification_config: optionalJsonDocument,
  verification_secret_refs: optionalJsonDocument,
  input_requirements: optionalJsonDocument,
  mapping: z.string().max(65_536, "Keep the mapping expression at or below 64 KiB."),
  identity_kind: z.string(),
  identity_value: z.string(),
});

const editSchema = z.object({
  name: z.string().trim().min(1, "Enter a name."),
  configuration: jsonDocument,
  input_requirements: optionalJsonDocument,
  mapping: z.string().max(65_536, "Keep the mapping expression at or below 64 KiB."),
});

type CreateValues = z.infer<typeof createSchema>;
type EditValues = z.infer<typeof editSchema>;

const optionalJson = (value: string) => (value.trim() ? parseJson(value).value : null);
const mapping = (expression: string) =>
  expression.trim() ? { engine: "jsonata", version: "1", expression: expression.trim() } : null;

export function SourcesScreen({ tenantId, selectedSourceId }: { tenantId: string; selectedSourceId?: string }) {
  const location = useLocation();
  const navigate = useNavigate();
  const openCreate = (location.state as { openSourceCreate?: boolean } | null)?.openSourceCreate === true;
  useEffect(() => {
    if (!openCreate) return;
    navigate(`${location.pathname}${location.search}`, { replace: true, state: null });
  }, [location.pathname, location.search, navigate, openCreate]);
  const connectorOptions = useConnectorOptions();
  const topicOptions = useTopicOptions(tenantId);
  const [status, setStatus] = useFilterParam("status");
  const [type, setType] = useFilterParam("type");
  const [topicId, setTopicId] = useFilterParam("topic_id");
  const applied = [status, type, topicId].filter(Boolean).length;
  const list = useInfiniteQuery({
    queryKey: ["sources", tenantId, { status, type, topicId }],
    queryFn: ({ pageParam }) =>
      call(() =>
        api.GET("/admin/tenants/{tenantId}/sources", {
          params: {
            path: { tenantId },
            query: {
              status: status || undefined,
              type: type || undefined,
              topic_id: topicId || undefined,
              after: pageParam ?? undefined,
              limit: 20,
            },
          },
        }),
      ),
    initialPageParam: null as string | null,
    getNextPageParam: nextCursor<SourceListItem>,
  });
  const sources = list.data?.pages.flatMap((page) => page.items) ?? [];
  // Whether there is a list to narrow yet. Until the read answers, neither the filter bar nor the
  // header's create action is rendered: an empty scope answers with the card that replaces the
  // table, carrying the action itself, and a screen that guessed first would retract them.
  const narrowing = narrowable(list.isSuccess, sources.length, applied);

  const [creating, setCreating] = useState(openCreate);
  const create = <SheetButton label="New Source" expanded={creating} onOpen={() => setCreating(true)} />;

  return (
    <Page>
      <PageHeader title="Sources" action={narrowing ? create : undefined}>
        A Source binds one Connector to one Topic and owns how its input is read.
      </PageHeader>

      {narrowing ? (
        <FilterBar applied={applied}>
          <Filter
            id="source-topic"
            label="Topic"
            value={topicId}
            onChange={setTopicId}
            hint={topicOptions.data?.next_cursor ? "Showing the first 100 Topics." : undefined}
          >
            {(topicOptions.data?.items ?? []).map((topic) => (
              <SelectItem key={topic.id} value={topic.id}>
                {topic.name}
              </SelectItem>
            ))}
          </Filter>
          <Filter id="source-status" label="Status" value={status} onChange={setStatus}>
            <SelectItem value="active">Active</SelectItem>
            <SelectItem value="revoked">Revoked</SelectItem>
          </Filter>
          <Filter id="source-type" label="Type" value={type} onChange={setType}>
            {sourceTypes.map((option) => (
              <SelectItem key={option.value} value={option.value}>
                {option.label}
              </SelectItem>
            ))}
          </Filter>
        </FilterBar>
      ) : null}

      <SplitView>
        <SplitList>
          <ListStatus
            busy={list.isFetching}
            loaded={list.isSuccess}
            problem={asProblem(list.error)}
            empty={sources.length === 0}
            applied={applied}
            noun="Sources"
            emptyText={
              <>
                A Source binds one <Link to="/connectors">Connector</Link> to one Topic and owns how its input is read.
              </>
            }
            action={create}
          />
          {sources.length > 0 ? (
            <TableCard
              caption={`Sources, newest first${appliedNote(applied)}`}
              footer={
                <LoadMore
                  noun="Source"
                  hasMore={list.hasNextPage}
                  busy={list.isFetching}
                  loaded={sources.length}
                  onLoadMore={() => void list.fetchNextPage()}
                />
              }
            >
              <TableHeader>
                <TableRow>
                  <TableHead scope="col">Name</TableHead>
                  <TableHead scope="col">Connector</TableHead>
                  <TableHead scope="col">Topic</TableHead>
                  <TableHead scope="col">Type</TableHead>
                  <TableHead scope="col">Input requirements</TableHead>
                  <TableHead scope="col">Status</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {sources.map((source) => (
                  <TableRow key={source.id} className="has-[a[aria-current=page]]:bg-selected-surface">
                    <RowHeader>
                      <NavLink className="no-underline" to={`/tenants/${tenantId}/sources/${source.id}`} end>
                        {source.name}
                      </NavLink>
                    </RowHeader>
                    <TableCell>{nameIn(connectorOptions.data?.items, source.connector_id)}</TableCell>
                    <TableCell>
                      <Link to={`/tenants/${tenantId}/topics/${source.topic_id}`}>
                        {nameIn(topicOptions.data?.items, source.topic_id)}
                      </Link>
                    </TableCell>
                    <TableCell>{source.type}</TableCell>
                    <TableCell className="font-mono text-[13px]">{source.input_requirements || "—"}</TableCell>
                    <TableCell>
                      <StatusBadge status={source.status} />
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </TableCard>
          ) : null}
        </SplitList>

        {selectedSourceId ? (
          <SourceInspector key={selectedSourceId} tenantId={tenantId} sourceId={selectedSourceId} />
        ) : (
          <InspectorPlaceholder label="Source detail">
            Select a Source to read the Connector and Topic it binds together.
          </InspectorPlaceholder>
        )}
      </SplitView>

      <CreateSheet
        label="New Source"
        description="A Source binds one Connector to one Topic"
        open={creating}
        onOpenChange={setCreating}
      >
        {(close) => <CreateSource tenantId={tenantId} defaultTopicId={topicId} onCreated={close} />}
      </CreateSheet>
    </Page>
  );
}

function CreateSource({
  tenantId,
  defaultTopicId = "",
  onCreated,
}: {
  tenantId: string;
  defaultTopicId?: string;
  onCreated: () => void;
}) {
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const connectors = useConnectorOptions();
  const topics = useTopicOptions(tenantId);
  const optionsUnavailable = connectors.isPending || topics.isPending || connectors.isError || topics.isError;

  const form = useForm<CreateValues>({
    resolver: zodResolver(createSchema),
    defaultValues: {
      name: "",
      connector_id: "",
      topic_id: defaultTopicId,
      type: "webhook",
      configuration: "{}",
      verification_scheme: "",
      verification_config: "{}",
      verification_secret_refs: "{}",
      input_requirements: "",
      mapping: "",
      identity_kind: "",
      identity_value: "",
    },
  });
  const sourceType = form.watch("type");
  const overview = useQuery({
    queryKey: ["tenant-overview", tenantId],
    queryFn: () => call(() => api.GET("/admin/tenants/{id}/overview", { params: { path: { id: tenantId } } })),
    enabled: sourceType === "event_api",
  });
  const sourceContractDraft = {
    expression: form.watch("mapping"),
    schema: optionalJson(form.watch("input_requirements")) as Record<string, unknown> | undefined,
  };

  const create = useMutation({
    mutationFn: (values: CreateValues) =>
      call(() =>
        api.POST("/admin/tenants/{tenantId}/sources", {
          params: { path: { tenantId } },
          body: {
            name: values.name,
            connector_id: values.connector_id,
            topic_id: values.topic_id,
            type: values.type,
            configuration: parseJson(values.configuration).value,
            verification:
              values.type === "webhook" && values.verification_scheme.trim()
                ? {
                    scheme: values.verification_scheme.trim(),
                    config: optionalJson(values.verification_config) ?? {},
                    secret_refs: optionalJson(values.verification_secret_refs) ?? {},
                  }
                : null,
            input_requirements: values.type === "event_api" ? null : optionalJson(values.input_requirements),
            mapping: values.type === "event_api" ? null : mapping(values.mapping),
            event_identity_rule:
              values.type !== "event_api" && values.identity_kind.trim() && values.identity_value.trim()
                ? { kind: values.identity_kind.trim(), value: values.identity_value.trim() }
                : null,
          },
        }),
      ),
    onSuccess: (created) => {
      void queryClient.invalidateQueries({ queryKey: ["sources", tenantId] });
      onCreated();
      if (created) navigate(`/tenants/${tenantId}/sources/${created.id}`, { state: { openSourceGuide: created.id } });
    },
  });

  const submit = form.handleSubmit((values) =>
    create.mutate(values, { onError: (failure) => applyProblem(form, failure, createFields) }),
  );

  return (
    <Form {...form}>
      <form className="flex flex-col gap-4" noValidate onSubmit={submit} aria-label="Create a Source">
        <FormError message={formError(asProblem(connectors.error ?? topics.error))} />
        <FormError message={formError(asProblem(create.error), createFields)} />

        <TextField control={form.control} name="name" label="Name" required />
        <SelectField
          control={form.control}
          name="connector_id"
          label="Connector"
          hint={connectors.data?.next_cursor ? "Showing the first 100 active Connectors." : undefined}
          disabled={connectors.isPending || connectors.isError}
          required
        >
          {activeOnly(connectors.data?.items).map((connector) => (
            <SelectItem key={connector.id} value={connector.id}>
              {connector.name}
            </SelectItem>
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
          {activeOnly(topics.data?.items).map((topic) => (
            <SelectItem key={topic.id} value={topic.id}>
              {topic.name}
            </SelectItem>
          ))}
        </SelectField>
        <SelectField control={form.control} name="type" label="Type" required>
          {sourceTypes.map((option) => (
            <SelectItem key={option.value} value={option.value}>
              {option.label}
            </SelectItem>
          ))}
        </SelectField>
        <TextAreaField
          control={form.control}
          name="configuration"
          label={sourceType === "queue" ? "Queue transport configuration (JSON)" : "Configuration (JSON)"}
          className="min-h-40 font-mono text-sm"
          required
        />
        {sourceType === "event_api" ? (
          <EventApiRequest tenantId={tenantId} ingestionEndpoint={overview.data?.ingestion_endpoint} />
        ) : null}
        {sourceType === "webhook" ? (
          <>
            <TextAreaField
              control={form.control}
              name="verification_scheme"
              label="Verification scheme (optional)"
              className="min-h-16 font-mono text-sm"
            />
            <TextAreaField
              control={form.control}
              name="verification_config"
              label="Verification configuration (JSON)"
              className="min-h-24 font-mono text-sm"
            />
            <TextAreaField
              control={form.control}
              name="verification_secret_refs"
              label="Verification secret references (JSON)"
              hint="Reference names only; never enter secret values."
              className="min-h-24 font-mono text-sm"
            />
          </>
        ) : null}
        {sourceType !== "event_api" ? (
          <>
            <EventBuilder
              key={sourceType}
              contractKey={`${sourceType} Source`}
              draft={sourceContractDraft}
              onUse={(draft) => {
                form.setValue("mapping", draft.expression, { shouldDirty: true });
                form.setValue("input_requirements", draft.schema ? formatJson(draft.schema) : "", {
                  shouldDirty: true,
                });
              }}
            />
            <TextAreaField
              control={form.control}
              name="input_requirements"
              label="Input requirements (JSON, optional)"
              className="min-h-32 font-mono text-sm"
            />
            <TextAreaField
              control={form.control}
              name="mapping"
              label="Event mapping (JSONata, optional)"
              className="min-h-32 font-mono text-sm"
            />
            <TextAreaField
              control={form.control}
              name="identity_kind"
              label={
                sourceType === "queue"
                  ? "Event identity kind (message_id or json_path)"
                  : "Event identity kind (header or json_path)"
              }
              className="min-h-16 font-mono text-sm"
            />
            <TextAreaField
              control={form.control}
              name="identity_value"
              label="Event identity selector"
              hint="A selected identity is fixed when this Source is created."
              className="min-h-16 font-mono text-sm"
            />
          </>
        ) : null}

        <Button type="submit" className="self-start" disabled={create.isPending || optionsUnavailable}>
          Create Source
        </Button>
      </form>
    </Form>
  );
}

function EventApiRequest({ tenantId, ingestionEndpoint }: { tenantId: string; ingestionEndpoint?: string | null }) {
  const endpoint = ingestionEndpoint
    ? `${ingestionEndpoint.replace(/\/$/, "")}/events?source_id=<Source id after create>`
    : "Loading the ingestion URL…";
  const request = JSON.stringify(
    {
      event_type: "<event-type>",
      payload: {},
      source_event_id: "<stable-source-event-id>",
      metadata: {},
    },
    null,
    2,
  );

  return (
    <section className="flex flex-col gap-2 rounded-md border bg-surface-quiet p-4" aria-labelledby="event-api-request">
      <h3 id="event-api-request" className="m-0 text-sm font-medium">
        Event API request
      </h3>
      <p className="m-0 text-sm text-ink-secondary">
        This Source accepts the fixed Integrios Event JSON. It has no Source verification, input requirements, or
        mapping; authenticate with a <Link to={`/tenants/${tenantId}/tenant-api-keys`}>Tenant API key</Link>.
      </p>
      <dl className="grid gap-1 text-sm sm:grid-cols-[auto_minmax(0,1fr)] sm:gap-x-3">
        <dt className="font-medium">Ingestion URL</dt>
        <dd className="m-0 min-w-0 break-all font-mono">{endpoint}</dd>
        <dt className="font-medium">Authorization</dt>
        <dd className="m-0 font-mono">Bearer &lt;TenantApiKey&gt;</dd>
      </dl>
      <pre className="m-0 overflow-x-auto rounded-md border bg-surface p-3 text-sm">{request}</pre>
    </section>
  );
}

function SourceInspector({ tenantId, sourceId }: { tenantId: string; sourceId: string }) {
  const connectorOptions = useConnectorOptions();
  const topicOptions = useTopicOptions(tenantId);
  const [notice, setNotice] = useState("");
  const source = useQuery({
    queryKey: ["source", tenantId, sourceId],
    queryFn: () =>
      call(() => api.GET("/admin/tenants/{tenantId}/sources/{id}", { params: { path: { tenantId, id: sourceId } } })),
  });

  const problem = asProblem(source.error);
  if (problem)
    return (
      <Inspector label="Source detail">
        <h2 className="m-0">Source</h2>
        <ReadError
          problem={problem}
          what="This Source"
          back={{ to: `/tenants/${tenantId}/sources`, label: "Back to Sources" }}
        />
      </Inspector>
    );
  if (!source.data) return <Inspector label="Source detail">Loading…</Inspector>;

  const current = source.data;
  return (
    <Inspector label="Source detail">
      <div className="flex items-start justify-between gap-3">
        <h2 className="group min-w-0">
          Source{" "}
          <span className="block text-xs font-normal text-ink-secondary">
            <CopyInline label="Source id" value={current.id} />
          </span>
        </h2>
        <div className="flex shrink-0 items-center gap-1.5">
          <StatusBadge status={current.status} className="mt-0.5" />
          <CloseInspector to={`/tenants/${tenantId}/sources`} label="Close the Source detail" />
        </div>
      </div>

      <Details className="border-b pb-3.5">
        <dt>Type</dt>
        <dd>{current.type}</dd>
        <dt>Connector</dt>
        <dd>
          <Link className="font-mono" to={`/connectors/${current.connector_id}`}>
            {nameIn(connectorOptions.data?.items, current.connector_id)}
          </Link>
        </dd>
        <dt>Topic</dt>
        <dd>
          <Link className="font-mono" to={`/tenants/${tenantId}/topics/${current.topic_id}`}>
            {nameIn(topicOptions.data?.items, current.topic_id)}
          </Link>
        </dd>
        <dt>Revoked</dt>
        <dd>{current.revoked_at ?? "Not revoked"}</dd>
      </Details>

      <SourceGuide tenantId={tenantId} source={current} />
      <WriteStatus done={notice !== ""}>{notice}</WriteStatus>
      <EditSource
        key={current.updated_at}
        tenantId={tenantId}
        source={current}
        onDone={() => setNotice("Source revoked.")}
      />
    </Inspector>
  );
}

/// The Admin API owns the mutable Source contract. Type, Connector, Topic, and identity rule are
/// fixed at creation, so they are shown rather than offered as editable fields.
function EditSource({ tenantId, source, onDone }: { tenantId: string; source: Source; onDone: () => void }) {
  const queryClient = useQueryClient();
  const reread = () => {
    void queryClient.invalidateQueries({ queryKey: ["source", tenantId, source.id] });
    void queryClient.invalidateQueries({ queryKey: ["sources", tenantId] });
  };
  const form = useForm<EditValues>({
    resolver: zodResolver(editSchema),
    defaultValues: {
      name: source.name,
      configuration: formatJson(source.configuration),
      input_requirements: source.input_requirements ? formatJson(source.input_requirements) : "",
      mapping: source.mapping?.expression ?? "",
    },
  });

  const save = useMutation({
    mutationFn: (values: EditValues) =>
      call(() =>
        api.PUT("/admin/tenants/{tenantId}/sources/{id}", {
          params: { path: { tenantId, id: source.id } },
          body: {
            name: values.name,
            configuration: parseJson(values.configuration).value,
            verification: source.verification
              ? {
                  scheme: source.verification.scheme,
                  config: source.verification.config ?? {},
                  secret_refs: source.verification.secret_refs ?? {},
                }
              : null,
            input_requirements: optionalJson(values.input_requirements),
            mapping: mapping(values.mapping),
          },
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

  return (
    <div className="flex flex-col gap-4">
      <div className="flex flex-wrap items-start gap-2">
        <EditSheet label="Edit" description="An update replaces the configuration outright">
          {(close) => (
            <Form {...form}>
              <form
                className="flex flex-col gap-4"
                aria-label={`Edit ${source.type} Source`}
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
                  name="configuration"
                  label="Configuration (JSON)"
                  className="min-h-56 font-mono text-sm"
                  required
                />
                {source.type !== "event_api" ? (
                  <>
                    <TextAreaField
                      control={form.control}
                      name="input_requirements"
                      label="Input requirements (JSON, optional)"
                      className="min-h-40 font-mono text-sm"
                    />
                    <TextAreaField
                      control={form.control}
                      name="mapping"
                      label="Event mapping (JSONata, optional)"
                      className="min-h-40 font-mono text-sm"
                    />
                  </>
                ) : null}

                <Button type="submit" className="self-start" disabled={save.isPending}>
                  Save configuration
                </Button>
                <WriteStatus done={save.isSuccess}>Configuration saved.</WriteStatus>
              </form>
            </Form>
          )}
        </EditSheet>
        {source.status === "active" ? (
          <ConfirmAction
            label="Revoke"
            consequence="Revoking a Source stops it accepting Events. It cannot be restored, and a replacement is a new Source with a new identifier."
            question={`Revoke the ${source.type} Source ${source.name}? It stops accepting Events and cannot be restored.`}
            confirmLabel={`Revoke ${source.name}`}
            busy={revoke.isPending}
            onConfirm={() => revoke.mutate()}
          />
        ) : null}
      </div>
      <FormError message={formError(asProblem(revoke.error))} />
    </div>
  );
}
