import { zodResolver } from "@hookform/resolvers/zod";
import { useInfiniteQuery, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useEffect, useState } from "react";
import { type Control, type FieldValues, type Path, useForm } from "react-hook-form";
import { Link, NavLink, useLocation, useNavigate } from "react-router";
import { z } from "zod";
import { Button } from "@/components/ui/button";
import { FormField } from "@/components/ui/form";
import { SelectItem } from "@/components/ui/select";
import { TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import { Timestamp } from "@/ui/time";
import { api } from "../api/client";
import { formError } from "../api/problem";
import { asProblem, call, nextCursor } from "../api/query";
import type { components } from "../api/schema";
import {
  appliedNote,
  ConfirmAction,
  CreateSheet,
  Disclosure,
  EditSheet,
  FilterBar,
  FormError,
  ListStatus,
  LoadMore,
  narrowable,
  ReadError,
  Section,
  SheetButton,
  WriteStatus,
} from "../ui/controls";
import { CopyInline } from "../ui/copy";
import { Filter, Form, SelectField, TextAreaField, TextField } from "../ui/fields";
import { useFilterParam } from "../ui/filters";
import { applyProblem } from "../ui/formProblem";
import { formatJson, object, parseJson } from "../ui/json";
import {
  CloseInspector,
  Details,
  Inspector,
  InspectorPlaceholder,
  openRow,
  Page,
  PageHeader,
  RowChevron,
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
  { value: "queue", label: "Message broker" },
];

/// A scheme the manifest offers but this dashboard has no word for is shown as the Connector named it.
const verificationLabel = (scheme: string) => (scheme === "hmac_sha256" ? "HMAC SHA-256" : scheme);

/// `queue` names only one of the two broker entity forms, so no Operator-facing surface prints it.
const typeLabel = (value: string) => sourceTypes.find((option) => option.value === value)?.label ?? value;

/// Where a Source's Event identity is read from. A kind offers a selector only when it
/// needs one — a message carries its own id — and the offered set is per Source type because the
/// affordances differ: a broker message has no request headers, a webhook request no message id.
///
/// The label says "Message ID", not whose: Azure Service Bus and RabbitMQ leave it to the publisher
/// while SQS and Pub/Sub assign it, so naming either one is wrong for the other. Nor does a broker
/// necessarily have one at all — Kafka identifies a record by its coordinates — which is why the
/// offered set will key on the transport rather than the Source type once a second transport lands.
const identityKinds = [
  { value: "message_id", label: "Message ID", types: ["queue"], selector: undefined },
  {
    value: "header",
    label: "Request header",
    types: ["webhook"],
    selector: { label: "Header name", placeholder: "X-GitHub-Delivery" },
  },
  {
    value: "json_path",
    label: "JSON body field",
    types: ["webhook", "queue"],
    selector: { label: "JSON Pointer", placeholder: "/id" },
  },
] as const;

const identitySelector = (kind: string) => identityKinds.find((option) => option.value === kind)?.selector;

/// Form paths that are also Admin API field keys, so a rejection lands on the control it is about.
/// The guided fields are deliberately absent: the API rejects the documents this form composes —
/// `configuration`, `verification`, `event_identity_rule`, `mapping`, `input_requirements` — and each
/// of those covers several controls at once, so `formError` states them over the form rather than
/// guessing which box to point at. Two of them are not rendered at all on the normal path.
const createFields = ["name", "connector_id", "topic_id", "type"] as const;
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

const identityFieldSchema = {
  identity_kind: z.string(),
  identity_value: z.string(),
  identity_allow_missing: z.boolean(),
};

const brokerFieldSchema = {
  broker_transport: z.string(),
  broker_namespace: z.string(),
  broker_entity: z.string(),
  broker_queue_name: z.string(),
  broker_topic_name: z.string(),
  broker_subscription_name: z.string(),
  broker_authentication: z.string(),
  broker_secret_ref: z.string(),
};

type IdentityValues = {
  identity_kind: string;
  identity_value: string;
  identity_allow_missing: boolean;
};

type BrokerValues = {
  [Field in keyof typeof brokerFieldSchema]: string;
};

const requireIdentitySelector = (values: IdentityValues, ctx: z.RefinementCtx) => {
  if (values.identity_kind && values.identity_kind !== "message_id" && !values.identity_value.trim())
    ctx.addIssue({ code: z.ZodIssueCode.custom, path: ["identity_value"], message: "Enter an identity selector." });
};

const requireBrokerFields = (values: BrokerValues, ctx: z.RefinementCtx) => {
  const required = (field: keyof BrokerValues, message: string) => {
    if (!values[field].trim()) ctx.addIssue({ code: z.ZodIssueCode.custom, path: [field], message });
  };
  required("broker_namespace", "Enter a broker namespace.");
  required("broker_authentication", "Choose an authentication method.");
  if (values.broker_entity === "topic_subscription") {
    required("broker_topic_name", "Enter a topic name.");
    required("broker_subscription_name", "Enter a subscription name.");
  } else {
    required("broker_queue_name", "Enter a queue name.");
  }
  if (values.broker_authentication === "connection_string")
    required("broker_secret_ref", "Enter a connection string reference.");
};

const createSchema = z
  .object({
    name: z.string().trim().min(1, "Enter a name."),
    connector_id: z.string().min(1, "Choose a Connector."),
    topic_id: z.string().min(1, "Choose a Topic."),
    type: z.string().min(1, "Choose a type."),
    ...brokerFieldSchema,
    verification_scheme: z.string(),
    verification_secret_ref: z.string(),
    input_requirements: optionalJsonDocument,
    mapping: z.string().max(65_536, "Keep the mapping expression at or below 64 KiB."),
    ...identityFieldSchema,
  })
  .superRefine((values, ctx) => {
    // Only the text fields have an empty case worth reporting; the one boolean carries no such state.
    const required = (field: keyof typeof values, message: string) => {
      const value = values[field];
      if (typeof value === "string" && !value.trim())
        ctx.addIssue({ code: z.ZodIssueCode.custom, path: [field], message });
    };
    if (values.type === "webhook") {
      if (values.verification_scheme) required("verification_secret_ref", "Enter a secret reference.");
    }
    requireIdentitySelector(values, ctx);
    if (values.type !== "queue") return;
    requireBrokerFields(values, ctx);
  });

const editSchema = z
  .object({
    name: z.string().trim().min(1, "Enter a name."),
    configuration: jsonDocument,
    input_requirements: optionalJsonDocument,
    mapping: z.string().max(65_536, "Keep the mapping expression at or below 64 KiB."),
    ...brokerFieldSchema,
    ...identityFieldSchema,
  })
  .superRefine((values, ctx) => {
    requireIdentitySelector(values, ctx);
    if (values.broker_transport) requireBrokerFields(values, ctx);
  });

type CreateValues = z.infer<typeof createSchema>;
type EditValues = z.infer<typeof editSchema>;

const optionalJson = (value: string) => (value.trim() ? parseJson(value).value : null);
const mapping = (expression: string) =>
  expression.trim() ? { engine: "jsonata", version: "1", expression: expression.trim() } : null;
const eventIdentityRule = (values: IdentityValues) =>
  values.identity_kind.trim() && (values.identity_kind === "message_id" || values.identity_value.trim())
    ? {
        kind: values.identity_kind.trim(),
        value: values.identity_kind === "message_id" ? "message_id" : values.identity_value.trim(),
        allow_missing: values.identity_allow_missing,
      }
    : null;

function SourceIdentityFields<TValues extends FieldValues>({
  control,
  type,
  kind,
  onKindChange,
}: {
  control: Control<TValues>;
  type: string;
  kind: string;
  /// Clears the selector, which the caller owns because only it holds the form. A value left standing
  /// when the kind changes is submitted under the new one: a header name becomes a JSON Pointer the
  /// API refuses, and a JSON Pointer becomes a header name it accepts, leaving a Source that looks
  /// configured and matches no request.
  onKindChange: () => void;
}) {
  const selector = identitySelector(kind);
  return (
    <>
      <SelectField
        control={control}
        name={"identity_kind" as Path<TValues>}
        label="Event identity"
        hint="A request missing the value is rejected unless you allow it below."
        emptyLabel="No duplicate detection"
        onChange={onKindChange}
      >
        {identityKinds
          .filter((option) => (option.types as readonly string[]).includes(type))
          .map((option) => (
            <SelectItem key={option.value} value={option.value}>
              {option.label}
            </SelectItem>
          ))}
      </SelectField>
      {selector ? (
        <TextField
          control={control}
          name={"identity_value" as Path<TValues>}
          label={selector.label}
          placeholder={selector.placeholder}
          required
        />
      ) : null}
      {kind ? (
        /* Composed from `FormField` because no field wrapper covers a checkbox. Offered for every
           kind rather than gated on one: a message id is application-defined on Azure Service Bus and
           on RabbitMQ, so an absent value is a real state for the kind that looks least likely to
           need the permission. */
        <FormField
          control={control}
          name={"identity_allow_missing" as Path<TValues>}
          render={({ field }) => (
            <label className="flex items-start gap-2.5 text-sm">
              <input
                type="checkbox"
                className="mt-0.5 size-4 shrink-0"
                name={field.name}
                checked={Boolean(field.value)}
                onBlur={field.onBlur}
                onChange={(event) => field.onChange(event.target.checked)}
              />
              <span className="min-w-0">
                Accept a request that carries no value here
                <span className="mt-0.5 block text-xs text-ink-secondary">
                  Integrios then reads the identity this Source's Event mapping produces, if it produces one, rather
                  than rejecting the request. An Event with no identity at all is not deduplicated.
                </span>
              </span>
            </label>
          )}
        />
      ) : null}
    </>
  );
}

/// What the chosen Connector permits a Source to select. `source_configuration_schema` is read only
/// to refuse it: this form authors no control from a declared property, so it names them instead.
/// A manifest carrying no verification block offers no scheme — a real manifest always carries one,
/// so an unrecognised document must not appear to accept a verification it never declared.
function sourceCapabilities(manifest: unknown) {
  const verification = object(object(manifest).source_verification);
  const schemes = Array.isArray(verification.schemes) ? verification.schemes : [];
  const required = object(object(manifest).source_configuration_schema).required;
  return {
    schemes: schemes.map((scheme) => String(object(scheme).scheme ?? "")).filter(Boolean),
    allowUnverified: verification.allow_unverified !== false,
    requiredConfiguration: Array.isArray(required) ? required.map(String) : [],
  };
}

function sourceConfiguration(values: BrokerValues): Record<string, unknown> {
  if (values.broker_transport !== "azure_service_bus") return {};
  const transportConfig =
    values.broker_entity === "topic_subscription"
      ? {
          namespace: values.broker_namespace.trim(),
          topic_name: values.broker_topic_name.trim(),
          subscription_name: values.broker_subscription_name.trim(),
        }
      : { namespace: values.broker_namespace.trim(), queue_name: values.broker_queue_name.trim() };
  return {
    transport: values.broker_transport,
    authentication:
      values.broker_authentication === "connection_string"
        ? { scheme: "connection_string", secret_ref: values.broker_secret_ref.trim() }
        : { scheme: "azure_identity" },
    transport_config: transportConfig,
  };
}

const normalizedJson = (value: unknown): unknown =>
  Array.isArray(value)
    ? value.map(normalizedJson)
    : value !== null && typeof value === "object"
      ? Object.fromEntries(
          Object.entries(value)
            .sort(([left], [right]) => left.localeCompare(right))
            .map(([key, item]) => [key, normalizedJson(item)]),
        )
      : value;

function brokerFields(configuration: unknown): BrokerValues | null {
  const document = object(configuration);
  const authentication = object(document.authentication);
  const transport = object(document.transport_config);
  if (document.transport !== "azure_service_bus" || typeof transport.namespace !== "string") return null;

  const brokerAuthentication = authentication.scheme;
  if (brokerAuthentication !== "azure_identity" && brokerAuthentication !== "connection_string") return null;
  if (brokerAuthentication === "connection_string" && typeof authentication.secret_ref !== "string") return null;

  const queueName = typeof transport.queue_name === "string" ? transport.queue_name : "";
  const topicName = typeof transport.topic_name === "string" ? transport.topic_name : "";
  const subscriptionName = typeof transport.subscription_name === "string" ? transport.subscription_name : "";
  const brokerEntity = queueName ? "queue" : topicName && subscriptionName ? "topic_subscription" : "";
  if (!brokerEntity) return null;

  const fields: BrokerValues = {
    broker_transport: "azure_service_bus",
    broker_namespace: transport.namespace,
    broker_entity: brokerEntity,
    broker_queue_name: queueName,
    broker_topic_name: topicName,
    broker_subscription_name: subscriptionName,
    broker_authentication: brokerAuthentication,
    broker_secret_ref: brokerAuthentication === "connection_string" ? String(authentication.secret_ref) : "",
  };
  return JSON.stringify(normalizedJson(configuration)) === JSON.stringify(normalizedJson(sourceConfiguration(fields)))
    ? fields
    : null;
}

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
                  <TableRow
                    key={source.id}
                    className="group cursor-pointer has-[a[aria-current=page]]:bg-selected-surface"
                    onClick={openRow}
                  >
                    <RowHeader>
                      <NavLink
                        className="-mx-3 block px-3 py-2 no-underline"
                        to={`/tenants/${tenantId}/sources/${source.id}`}
                        end
                      >
                        {source.name}
                      </NavLink>
                    </RowHeader>
                    <TableCell>{nameIn(connectorOptions.data?.items, source.connector_id)}</TableCell>
                    <TableCell>
                      <Link to={`/tenants/${tenantId}/topics/${source.topic_id}`}>
                        {nameIn(topicOptions.data?.items, source.topic_id)}
                      </Link>
                    </TableCell>
                    <TableCell>{typeLabel(source.type)}</TableCell>
                    <TableCell className="font-mono text-[13px]">{source.input_requirements || "—"}</TableCell>
                    <TableCell>
                      <div className="flex items-center justify-between gap-3">
                        <StatusBadge status={source.status} />
                        <RowChevron />
                      </div>
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </TableCard>
          ) : null}
        </SplitList>

        {selectedSourceId ? (
          <SourceInspector key={selectedSourceId} tenantId={tenantId} sourceId={selectedSourceId} />
        ) : sources.length > 0 ? (
          <InspectorPlaceholder label="Source detail">
            Select a Source to read the Connector and Topic it binds together.
          </InspectorPlaceholder>
        ) : null}
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
  const optionsPending = connectors.isPending || topics.isPending || connectors.isError || topics.isError;
  // A Source binds one Connector to one Topic, so a Tenant that has authored no Topic yet cannot
  // have a Source at all. The picker says so where an Operator meets the gap: an empty menu states
  // that there is nothing to choose without saying that something has to be authored first.
  //
  // The control is disabled and the sentence left standing beside it. A disabled control is skipped
  // by the Tab key, so the reason has to live in the form's reading order rather than only in the
  // description of a field a keyboard never reaches.
  const noTopics = topics.isSuccess && activeOnly(topics.data?.items).length === 0;
  // Connectors are deployment-wide, so an Operator whose deployment has none cannot author here at
  // all — the way out leaves the Tenant rather than staying inside it.
  const noConnectors = connectors.isSuccess && activeOnly(connectors.data?.items).length === 0;
  // A form that cannot be completed does not look completable: no Topic blocks the write exactly as
  // an option read that has not answered does.
  const cannotAuthor = optionsPending || noTopics || noConnectors;

  const form = useForm<CreateValues>({
    resolver: zodResolver(createSchema),
    defaultValues: {
      name: "",
      connector_id: "",
      topic_id: defaultTopicId,
      type: "webhook",
      broker_transport: "azure_service_bus",
      broker_namespace: "",
      broker_entity: "queue",
      broker_queue_name: "",
      broker_topic_name: "",
      broker_subscription_name: "",
      broker_authentication: "azure_identity",
      broker_secret_ref: "",
      verification_scheme: "",
      verification_secret_ref: "",
      input_requirements: "",
      mapping: "",
      identity_kind: "",
      identity_value: "",
      identity_allow_missing: false,
    },
  });
  const sourceType = form.watch("type");
  const connectorId = form.watch("connector_id");
  const brokerEntity = form.watch("broker_entity");
  const brokerAuthentication = form.watch("broker_authentication");
  const verificationScheme = form.watch("verification_scheme");
  const identityKind = form.watch("identity_kind");
  // What the chosen Connector permits. Read for webhook verification and for whether the Connector
  // demands source configuration this form cannot author; a queue Source composes its own document.
  const connector = useQuery({
    queryKey: ["connector", connectorId],
    queryFn: () => call(() => api.GET("/admin/connectors/{id}", { params: { path: { id: connectorId } } })),
    enabled: connectorId !== "",
  });
  const capabilities = sourceCapabilities(connector.data?.manifest);
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
            configuration: values.type === "queue" ? sourceConfiguration(values) : {},
            verification:
              values.type === "webhook" && values.verification_scheme.trim()
                ? {
                    scheme: values.verification_scheme.trim(),
                    config: {},
                    secret_refs: { secret: values.verification_secret_ref.trim() },
                  }
                : null,
            input_requirements: values.type === "event_api" ? null : optionalJson(values.input_requirements),
            mapping: values.type === "event_api" ? null : mapping(values.mapping),
            event_identity_rule: values.type === "event_api" ? null : eventIdentityRule(values),
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
          hint={
            noConnectors ? (
              <>
                No active Connectors exist yet, and a Source is built from one.{" "}
                <Link to="/connectors">Create a Connector</Link> first.
              </>
            ) : capabilities.requiredConfiguration.length > 0 ? (
              `This Connector requires Source configuration this form cannot author yet: ${capabilities.requiredConfiguration.join(", ")}. Choose another Connector, or author the Source through the Admin API.`
            ) : connectors.data?.next_cursor ? (
              "Showing the first 100 active Connectors."
            ) : undefined
          }
          disabled={connectors.isPending || connectors.isError || noConnectors}
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
          hint={
            noTopics ? (
              <>
                No active Topics yet, and a Source needs one.{" "}
                <Link to={`/tenants/${tenantId}/topics`}>Create a Topic</Link> first.
              </>
            ) : topics.data?.next_cursor ? (
              "Showing the first 100 active Topics."
            ) : undefined
          }
          disabled={topics.isPending || topics.isError || noTopics}
          required
        >
          {activeOnly(topics.data?.items).map((topic) => (
            <SelectItem key={topic.id} value={topic.id}>
              {topic.name}
            </SelectItem>
          ))}
        </SelectField>
        {/* The offered identity kinds differ by Source type — a broker message id has no webhook
            meaning, a request header no broker meaning — so a kind chosen under one type cannot
            survive into another. */}
        <SelectField
          control={form.control}
          name="type"
          label="Type"
          onChange={() => {
            form.setValue("identity_kind", "");
            form.setValue("identity_allow_missing", false);
          }}
          required
        >
          {sourceTypes.map((option) => (
            <SelectItem key={option.value} value={option.value}>
              {option.label}
            </SelectItem>
          ))}
        </SelectField>
        {sourceType === "event_api" ? (
          <EventApiRequest tenantId={tenantId} ingestionEndpoint={overview.data?.ingestion_endpoint} />
        ) : null}
        {sourceType === "webhook" ? (
          <Section title="Request verification" hint="How Integrios checks that a request came from the provider.">
            <SelectField
              control={form.control}
              name="verification_scheme"
              label="Verification"
              hint={
                connectorId === ""
                  ? "Choose a Connector to see what it accepts."
                  : capabilities.schemes.length === 0
                    ? "This Connector declares no verification scheme."
                    : capabilities.allowUnverified
                      ? undefined
                      : "This Connector requires a verified Source."
              }
              emptyLabel={capabilities.allowUnverified ? "No verification" : undefined}
              disabled={connector.isPending || capabilities.schemes.length === 0}
              required={!capabilities.allowUnverified}
            >
              {capabilities.schemes.map((scheme) => (
                <SelectItem key={scheme} value={scheme}>
                  {verificationLabel(scheme)}
                </SelectItem>
              ))}
            </SelectField>
            {verificationScheme ? (
              <TextField
                control={form.control}
                name="verification_secret_ref"
                label="Secret reference"
                hint="Reference name only; never enter the secret value."
                required
              />
            ) : null}
          </Section>
        ) : null}
        {sourceType === "queue" ? (
          <MessageBrokerFields control={form.control} entity={brokerEntity} authentication={brokerAuthentication} />
        ) : null}
        {sourceType !== "event_api" ? (
          <>
            <Section
              title="Event shape"
              hint="What identifies an Event from this Source, and the type, payload, and requirements derived from a representative request."
            >
              <SourceIdentityFields
                control={form.control}
                type={sourceType}
                kind={identityKind}
                onKindChange={() => form.setValue("identity_value", "")}
              />
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
            </Section>
            <Disclosure label="Advanced configuration">
              <div className="flex flex-col gap-4">
                <TextAreaField
                  control={form.control}
                  name="input_requirements"
                  label="Generated input requirements"
                  className="min-h-32 font-mono text-sm"
                />
                <TextAreaField
                  control={form.control}
                  name="mapping"
                  label="Generated Event mapping (JSONata)"
                  className="min-h-32 font-mono text-sm"
                />
              </div>
            </Disclosure>
          </>
        ) : null}

        <Button type="submit" className="self-start" disabled={create.isPending || cannotAuthor}>
          Create Source
        </Button>
      </form>
    </Form>
  );
}

/// Azure Service Bus is the only transport, so its fields sit directly under the broker choice. A
/// second transport branches here on `broker_transport` and composes its own `transport_config`;
/// nothing outside this component, `sourceConfiguration`, and its inverse knows which broker was chosen.
function MessageBrokerFields<TValues extends FieldValues>({
  control,
  entity,
  authentication,
}: {
  control: Control<TValues>;
  entity: string;
  authentication: string;
}) {
  return (
    <Section title="Message broker" hint="The broker Integrios receives messages from.">
      <SelectField control={control} name={"broker_transport" as Path<TValues>} label="Broker type" required>
        <SelectItem value="azure_service_bus">Azure Service Bus</SelectItem>
      </SelectField>
      <TextField
        control={control}
        name={"broker_namespace" as Path<TValues>}
        label="Namespace"
        placeholder="acme-events.servicebus.windows.net"
        required
      />
      <SelectField control={control} name={"broker_entity" as Path<TValues>} label="Broker entity" required>
        <SelectItem value="queue">Queue</SelectItem>
        <SelectItem value="topic_subscription">Topic subscription</SelectItem>
      </SelectField>
      {entity === "topic_subscription" ? (
        <>
          <TextField control={control} name={"broker_topic_name" as Path<TValues>} label="Topic name" required />
          <TextField
            control={control}
            name={"broker_subscription_name" as Path<TValues>}
            label="Subscription name"
            required
          />
        </>
      ) : (
        <TextField control={control} name={"broker_queue_name" as Path<TValues>} label="Queue name" required />
      )}
      <SelectField control={control} name={"broker_authentication" as Path<TValues>} label="Authentication" required>
        <SelectItem value="azure_identity">Azure identity</SelectItem>
        <SelectItem value="connection_string">Connection string reference</SelectItem>
      </SelectField>
      {authentication === "connection_string" ? (
        <TextField
          control={control}
          name={"broker_secret_ref" as Path<TValues>}
          label="Connection string reference"
          hint="Reference name only; never enter the connection string."
          required
        />
      ) : null}
    </Section>
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
        {current.revoked_at ? (
          <>
            <dt>Revoked</dt>
            <dd>
              <Timestamp value={current.revoked_at} />
            </dd>
          </>
        ) : null}
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

/// The Admin API owns the mutable Source contract. Type, Connector, and Topic remain fixed.
function EditSource({ tenantId, source, onDone }: { tenantId: string; source: Source; onDone: () => void }) {
  const queryClient = useQueryClient();
  const reread = () => {
    void queryClient.invalidateQueries({ queryKey: ["source", tenantId, source.id] });
    void queryClient.invalidateQueries({ queryKey: ["sources", tenantId] });
  };
  const storedBrokerFields = source.type === "queue" ? brokerFields(source.configuration) : null;
  const form = useForm<EditValues>({
    resolver: zodResolver(editSchema),
    defaultValues: {
      name: source.name,
      configuration: formatJson(source.configuration),
      input_requirements: source.input_requirements ? formatJson(source.input_requirements) : "",
      mapping: source.mapping?.expression ?? "",
      broker_transport: storedBrokerFields?.broker_transport ?? "",
      broker_namespace: storedBrokerFields?.broker_namespace ?? "",
      broker_entity: storedBrokerFields?.broker_entity ?? "",
      broker_queue_name: storedBrokerFields?.broker_queue_name ?? "",
      broker_topic_name: storedBrokerFields?.broker_topic_name ?? "",
      broker_subscription_name: storedBrokerFields?.broker_subscription_name ?? "",
      broker_authentication: storedBrokerFields?.broker_authentication ?? "",
      broker_secret_ref: storedBrokerFields?.broker_secret_ref ?? "",
      identity_kind: source.event_identity_rule?.kind ?? "",
      identity_value: source.event_identity_rule?.value ?? "",
      identity_allow_missing: source.event_identity_rule?.allow_missing ?? false,
    },
  });
  const identityKind = form.watch("identity_kind");
  const brokerEntity = form.watch("broker_entity");
  const brokerAuthentication = form.watch("broker_authentication");

  const save = useMutation({
    mutationFn: (values: EditValues) =>
      call(() =>
        api.PUT("/admin/tenants/{tenantId}/sources/{id}", {
          params: { path: { tenantId, id: source.id } },
          body: {
            name: values.name,
            configuration:
              source.type === "event_api"
                ? source.configuration
                : storedBrokerFields
                  ? sourceConfiguration(values)
                  : parseJson(values.configuration).value,
            verification: source.verification
              ? {
                  scheme: source.verification.scheme,
                  config: source.verification.config ?? {},
                  secret_refs: source.verification.secret_refs ?? {},
                }
              : null,
            input_requirements: optionalJson(values.input_requirements),
            mapping: mapping(values.mapping),
            event_identity_rule: source.type === "event_api" ? null : eventIdentityRule(values),
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
                aria-label={`Edit ${typeLabel(source.type)} Source`}
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
                {storedBrokerFields ? (
                  <MessageBrokerFields
                    control={form.control}
                    entity={brokerEntity}
                    authentication={brokerAuthentication}
                  />
                ) : null}
                {source.type !== "event_api" ? (
                  <Section title="Event identity" hint="How Integrios detects duplicate Events from this Source.">
                    <SourceIdentityFields
                      control={form.control}
                      type={source.type}
                      kind={identityKind}
                      onKindChange={() => form.setValue("identity_value", "")}
                    />
                  </Section>
                ) : null}
                {source.type !== "event_api" && !storedBrokerFields ? (
                  <Disclosure label="Advanced configuration">
                    <TextAreaField
                      control={form.control}
                      name="configuration"
                      label="Configuration (JSON)"
                      className="min-h-56 font-mono text-sm"
                      required
                    />
                  </Disclosure>
                ) : null}
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
            question={`Revoke the ${typeLabel(source.type)} Source ${source.name}? It stops accepting Events and cannot be restored.`}
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
