import { zodResolver } from "@hookform/resolvers/zod";
import { useInfiniteQuery, useMutation, useQueries, useQuery, useQueryClient } from "@tanstack/react-query";
import { type ReactNode, useEffect, useState } from "react";
import { type Control, type FieldValues, type Path, useForm, useWatch } from "react-hook-form";
import { Link, NavLink, useLocation, useNavigate } from "react-router";
import { z } from "zod";
import { Button } from "@/components/ui/button";
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
import { formatJson, object, parseJson, sameJson } from "../ui/json";
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
import { EventBuilder, type EventIdentityRule } from "./EventBuilder";
import { SourceGuide } from "./SourceGuide";
import { guidedFrom } from "./sourceMapping";

type SourceListItem = components["schemas"]["SourceListItemDto"];
type Source = components["schemas"]["SourceDto"];

const sourceTypes = [
  { value: "event_api", label: "Event API" },
  { value: "webhook", label: "Webhook" },
  { value: "broker", label: "Message broker" },
];

/// A scheme the manifest offers but this dashboard has no word for is shown as the Connector named it.
const verificationLabel = (scheme: string) => (scheme === "hmac_sha256" ? "HMAC SHA-256" : scheme);

/// `broker` is the wire value; no Operator-facing surface prints it, only the "Message broker" label.
const typeLabel = (value: string) => sourceTypes.find((option) => option.value === value)?.label ?? value;

/// Form paths that are also Admin API field keys, so a rejection lands on the control it is about.
/// The guided fields are deliberately absent: the API rejects the documents this form composes —
/// `configuration`, `verification`, `event_identity_rule`, `mapping`, `input_requirements` — and each
/// of those covers several controls at once, so `formError` states them over the form rather than
/// guessing which box to point at. Two of them are not rendered at all on the normal path.
const createFields = ["name", "connector_id", "topic_id", "type"] as const;
const editFields = ["name"] as const;

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

const verificationFieldSchema = {
  verification_scheme: z.string(),
  verification_secret_ref: z.string(),
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

const requireVerificationSecret = (
  values: { verification_scheme: string; verification_secret_ref: string },
  ctx: z.RefinementCtx,
) => {
  if (values.verification_scheme && !values.verification_secret_ref.trim())
    ctx.addIssue({
      code: z.ZodIssueCode.custom,
      path: ["verification_secret_ref"],
      message: "Enter a secret reference.",
    });
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
    ...verificationFieldSchema,
    input_requirements: optionalJsonDocument,
    mapping: z.string().max(65_536, "Keep the mapping expression at or below 64 KiB."),
    ...identityFieldSchema,
  })
  .superRefine((values, ctx) => {
    if (values.type === "webhook") requireVerificationSecret(values, ctx);
    if (values.type !== "broker") return;
    requireBrokerFields(values, ctx);
  });

const editSchema = z
  .object({
    name: z.string().trim().min(1, "Enter a name."),
    configuration: jsonDocument,
    input_requirements: optionalJsonDocument,
    mapping: z.string().max(65_536, "Keep the mapping expression at or below 64 KiB."),
    ...brokerFieldSchema,
    ...verificationFieldSchema,
    ...identityFieldSchema,
  })
  .superRefine((values, ctx) => {
    requireVerificationSecret(values, ctx);
    if (values.broker_transport) requireBrokerFields(values, ctx);
  });

type CreateValues = z.infer<typeof createSchema>;
type EditValues = z.infer<typeof editSchema>;

const optionalJson = (value: string) => (value.trim() ? parseJson(value).value : null);
const mapping = (expression: string) =>
  expression.trim() ? { engine: "jsonata", version: "1", expression: expression.trim() } : null;
const identityDraft = (values: IdentityValues): EventIdentityRule | null =>
  values.identity_kind.trim() === ""
    ? null
    : { kind: values.identity_kind, value: values.identity_value, allowMissing: values.identity_allow_missing };

const eventIdentityRule = (values: IdentityValues) =>
  values.identity_kind.trim() && (values.identity_kind === "message_id" || values.identity_value.trim())
    ? {
        kind: values.identity_kind.trim(),
        value: values.identity_kind === "message_id" ? "message_id" : values.identity_value.trim(),
        allow_missing: values.identity_allow_missing,
      }
    : null;

/// Written into the configuration: the same for every input.
function Fixed({ children }: { children: ReactNode }) {
  return <code className="font-mono text-xs break-all text-ink">{children}</code>;
}

/// Read from each input rather than written here. Dashed, and led by where it is read from, so a
/// template like `github.`[header x-github-event] reads as text plus a slot at a glance.
function FromInput({ from, name }: { from: string; name?: string }) {
  return (
    <span className="inline-flex max-w-full flex-wrap items-baseline gap-x-1 rounded border border-dashed border-ink-secondary px-1 font-mono text-xs">
      <span className="text-ink-secondary">{from}</span>
      {name ? <span className="break-all text-ink">{name}</span> : null}
    </span>
  );
}

/// The identity rule stores a JSON Pointer; the Operator picked it by its field name.
const fieldName = (pointer: string) =>
  pointer
    .slice(1)
    .split("/")
    .map((segment) => segment.replaceAll("~1", "/").replaceAll("~0", "~"))
    .join(".");

function EventTypeTemplate({ expression }: { expression: string }) {
  if (expression.trim() === "") return <>Not configured</>;
  const rule = guidedFrom(expression);
  if (!rule) return <>Custom JSONata mapping</>;
  if (rule.source === "fixed") return <Fixed>{rule.value}</Fixed>;
  return (
    <span className="inline-flex max-w-full flex-wrap items-baseline gap-x-0.5">
      {rule.prefix ? <Fixed>{`${rule.prefix}.`}</Fixed> : null}
      {rule.source === "header" ? (
        <FromInput from="header" name={rule.header} />
      ) : (
        <FromInput from="body" name={rule.path} />
      )}
    </span>
  );
}

function IdentityTemplate({ identity }: { identity: EventIdentityRule | null }) {
  if (!identity) return <>No duplicate detection</>;
  const slot =
    identity.kind === "message_id" ? (
      <FromInput from="message ID" />
    ) : identity.kind === "header" ? (
      <FromInput from="header" name={identity.value} />
    ) : (
      <FromInput from="body" name={fieldName(identity.value)} />
    );
  return (
    <>
      {slot}
      {identity.allowMissing ? <span className="text-ink-secondary"> (may be absent)</span> : null}
    </>
  );
}

/// The three values the Event Builder owns, in one card with the control that edits them. The
/// Builder is a dialog, so what it settled and the button that reopens it are the only trace it
/// leaves on the form; sitting loose among the Source's own fields, the three read as three more
/// fields to fill in rather than one thing the Builder decides.
///
/// Captioned with what the three values are together, rather than with the tool that sets them: the
/// button inside already names the Builder, and the Event contract is the thing this Source keeps
/// once the dialog has closed. The raw editors below carry the same words.
function BuilderContract({ children }: { children: ReactNode }) {
  return (
    <div className="flex flex-col gap-3 rounded-lg border bg-surface-quiet p-3">
      <h4 className="m-0 text-sm font-semibold">Event contract</h4>
      {children}
    </div>
  );
}

/// What the Event Builder settled, stated where the Source is authored. The Builder is a dialog that
/// closes behind itself, so without this the only evidence that an Event contract exists is that a
/// button was pressed once — and the Event identity, which decides whether this Source deduplicates
/// at all, would not be visible until after the Source was created.
function SettledContract({
  mapping,
  identity,
  inputNoun,
  unsaved = false,
}: {
  mapping: string;
  identity: EventIdentityRule | null;
  inputNoun: string;
  /// Whether this differs from what the Source has saved. The summary reads like the Source's own
  /// configuration, so a draft has to say it is one.
  unsaved?: boolean;
}) {
  // A guided mapping always takes the payload from the body, so anything guided reads from the input.
  const reads = identity !== null || guidedFrom(mapping) !== undefined;
  return (
    <>
      {unsaved ? (
        <p className="m-0 text-xs text-warning-ink">Not saved yet. Save configuration to apply it to this Source.</p>
      ) : null}
      <Details>
        <dt>Event type</dt>
        <dd>
          <EventTypeTemplate expression={mapping} />
        </dd>
        <dt>Event identity</dt>
        <dd>
          <IdentityTemplate identity={identity} />
        </dd>
        <dt>Payload</dt>
        <dd>
          {mapping.trim() === "" ? (
            "Not configured"
          ) : guidedFrom(mapping) ? (
            <FromInput from="body" />
          ) : (
            "Custom JSONata mapping"
          )}
        </dd>
      </Details>
      {/* The dashed style carries a meaning, so it is said once rather than left to be guessed. */}
      {reads ? <p className="m-0 text-xs text-ink-secondary">Dashed values are read from each {inputNoun}.</p> : null}
    </>
  );
}

function SourceVerificationFields<TValues extends FieldValues>({
  control,
  connectorChosen,
  pending,
  capabilities,
}: {
  control: Control<TValues>;
  connectorChosen: boolean;
  pending: boolean;
  capabilities: ReturnType<typeof sourceCapabilities>;
}) {
  const scheme = String(useWatch({ control, name: "verification_scheme" as Path<TValues> }) ?? "");
  return (
    <Section title="Request verification" hint="How Integrios checks that a request came from the provider.">
      <SelectField
        control={control}
        name={"verification_scheme" as Path<TValues>}
        label="Verification"
        hint={
          !connectorChosen
            ? "Choose a Connector to see what it accepts."
            : capabilities.schemes.length === 0
              ? "This Connector declares no verification scheme."
              : capabilities.allowUnverified
                ? undefined
                : "This Connector requires a verified Source."
        }
        emptyLabel={capabilities.allowUnverified ? "No verification" : undefined}
        disabled={pending || capabilities.schemes.length === 0}
        required={!capabilities.allowUnverified}
      >
        {capabilities.schemes.map((value) => (
          <SelectItem key={value} value={value}>
            {verificationLabel(value)}
          </SelectItem>
        ))}
      </SelectField>
      {scheme ? (
        <TextField
          control={control}
          name={"verification_secret_ref" as Path<TValues>}
          label="Secret reference"
          hint="Reference name only; never enter the secret value."
          required
        />
      ) : null}
    </Section>
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
      type: "",
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
  // What the chosen Connector permits. Read for webhook verification and for whether the Connector
  // demands source configuration this form cannot author; a broker Source composes its own document.
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
    identity: identityDraft(form.watch()),
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
            configuration: values.type === "broker" ? sourceConfiguration(values) : {},
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
          placeholder="Choose a type"
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
        {sourceType === "webhook" || sourceType === "broker" ? <SourceInputDescription type={sourceType} /> : null}
        {sourceType === "webhook" ? (
          <SourceVerificationFields
            control={form.control}
            connectorChosen={connectorId !== ""}
            pending={connector.isPending}
            capabilities={capabilities}
          />
        ) : null}
        {sourceType === "broker" ? <MessageBrokerFields control={form.control} /> : null}
        {sourceType === "webhook" || sourceType === "broker" ? (
          <Section
            title="Event Normalization"
            hint="How this Source turns provider input into the Integrios Event accepted by the ingestion pipeline."
          >
            <BuilderContract>
              <SettledContract
                mapping={form.watch("mapping")}
                identity={identityDraft(form.watch())}
                inputNoun={sourceType === "webhook" ? "request" : "message"}
              />
              <EventBuilder
                key={sourceType}
                contractKey={`${sourceType} Source`}
                sourceType={sourceType === "webhook" ? "webhook" : "broker"}
                draft={sourceContractDraft}
                onUse={(draft) => {
                  form.setValue("mapping", draft.expression, { shouldDirty: true });
                  form.setValue("input_requirements", draft.schema ? formatJson(draft.schema) : "", {
                    shouldDirty: true,
                  });
                  form.setValue("identity_kind", draft.identity?.kind ?? "", { shouldDirty: true });
                  form.setValue("identity_value", draft.identity?.value ?? "", { shouldDirty: true });
                  form.setValue("identity_allow_missing", draft.identity?.allowMissing ?? false, { shouldDirty: true });
                }}
              />
            </BuilderContract>
          </Section>
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
function MessageBrokerFields<TValues extends FieldValues>({ control }: { control: Control<TValues> }) {
  const entity = String(useWatch({ control, name: "broker_entity" as Path<TValues> }) ?? "");
  const authentication = String(useWatch({ control, name: "broker_authentication" as Path<TValues> }) ?? "");
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

function SourceInputDescription({ type }: { type: "webhook" | "broker" }) {
  const webhook = type === "webhook";
  return (
    <section
      className="flex flex-col gap-2 rounded-md border bg-surface-quiet p-4"
      aria-labelledby={`${type}-source-input`}
    >
      <h3 id={`${type}-source-input`} className="m-0 text-sm font-medium">
        {webhook ? "Webhook request" : "Message broker input"}
      </h3>
      <p className="m-0 text-sm text-ink-secondary">
        {webhook
          ? "The provider sends a JSON HTTP request to the callback URL generated after this Source is created. Integrios verifies it, validates the input, and normalizes it into an Integrios Event."
          : "Integrios consumes JSON messages from the broker below, validates each message, and normalizes it into an Integrios Event."}
      </p>
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
      {current.status === "active" ? (
        <EditSource
          key={current.updated_at}
          tenantId={tenantId}
          source={current}
          onDone={() => setNotice("Source revoked.")}
        />
      ) : null}
    </Inspector>
  );
}

/// What routes on the Source's Topic, for the confirmation that precedes retyping its Events.
/// Subscriptions match the Event type exactly, so a Source whose mapping now types its Events
/// differently can silently stop reaching them. Which ones is not computable - a type read from
/// input is open-ended - so the Operator is shown what depends on the Topic and judges.
///
/// ponytail: one read per active Subscription, because the Topic list carries no match rules; add
/// the Event type to that list projection if Topics come to carry many Subscriptions.
function useTopicRouting(tenantId: string, topicId: string, enabled: boolean) {
  const list = useQuery({
    queryKey: ["topic-subscriptions", tenantId, topicId, "active"],
    queryFn: () =>
      call(() =>
        api.GET("/admin/tenants/{tenantId}/topics/{topicId}/subscriptions", {
          params: { path: { tenantId, topicId }, query: { status: "active", limit: 100 } },
        }),
      ),
    enabled,
  });
  const details = useQueries({
    queries: (list.data?.items ?? []).map((item) => ({
      queryKey: ["subscription", tenantId, topicId, item.id],
      queryFn: () =>
        call(() =>
          api.GET("/admin/tenants/{tenantId}/topics/{topicId}/subscriptions/{id}", {
            params: { path: { tenantId, topicId, id: item.id } },
          }),
        ),
      enabled,
    })),
  });
  const routes = details.flatMap((detail) => {
    const subscription = detail.data;
    if (!subscription) return [];
    const eventType = object(subscription.match_rules).event_type;
    return [`${subscription.name} (${typeof eventType === "string" ? eventType : "no Event type"})`];
  });
  const settled = list.isSuccess && details.every((detail) => !detail.isPending);
  return {
    routes,
    settled,
    partial: Boolean(list.data?.next_cursor) || list.isError || details.some((d) => d.isError),
  };
}

function retypeConsequence(routing: ReturnType<typeof useTopicRouting>): string {
  const kept = "Events already accepted keep their Event type.";
  if (!routing.settled) return `${kept} Reading the Subscriptions on this Topic…`;
  if (routing.routes.length === 0) return `${kept} No active Subscription on this Topic depends on it yet.`;
  return `${kept} Subscriptions match the Event type exactly, so these active Subscriptions on this Topic may stop receiving new Events: ${routing.routes.join(", ")}${routing.partial ? ", and possibly others not shown" : ""}.`;
}

/// The Admin API owns the mutable Source contract. Type, Connector, and Topic remain fixed.
function EditSource({ tenantId, source, onDone }: { tenantId: string; source: Source; onDone: () => void }) {
  const queryClient = useQueryClient();
  const reread = () => {
    void queryClient.invalidateQueries({ queryKey: ["source", tenantId, source.id] });
    void queryClient.invalidateQueries({ queryKey: ["sources", tenantId] });
  };
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
          {(close) => <EditSourceForm tenantId={tenantId} source={source} onSaved={reread} onClose={close} />}
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

/// The edit form lives inside the sheet, so it exists only while the sheet is open. Closing the sheet
/// without saving discards the draft, as every other edit sheet does: a draft that outlived Cancel
/// came back on the next Edit reading as the Source's configuration, and the Event Builder then took
/// it for what the Source had.
function EditSourceForm({
  tenantId,
  source,
  onSaved,
  onClose,
}: {
  tenantId: string;
  source: Source;
  onSaved: () => void;
  onClose: () => void;
}) {
  const storedBrokerFields = source.type === "broker" ? brokerFields(source.configuration) : null;
  const connector = useQuery({
    queryKey: ["connector", source.connector_id],
    queryFn: () => call(() => api.GET("/admin/connectors/{id}", { params: { path: { id: source.connector_id } } })),
    enabled: source.type === "webhook",
  });
  const capabilities = sourceCapabilities(connector.data?.manifest);
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
      verification_scheme: source.verification?.scheme ?? "",
      verification_secret_ref: String(object(source.verification?.secret_refs).secret ?? ""),
      identity_kind: source.event_identity_rule?.kind ?? "",
      identity_value: source.event_identity_rule?.value ?? "",
      identity_allow_missing: source.event_identity_rule?.allow_missing ?? false,
    },
  });
  const verificationScheme = form.watch("verification_scheme");
  const storedExpression = source.mapping?.expression ?? "";
  const mappingChanged = form.watch("mapping").trim() !== storedExpression.trim();
  const routing = useTopicRouting(tenantId, source.topic_id, mappingChanged);
  const sourceContractDraft = {
    expression: form.watch("mapping"),
    schema: optionalJson(form.watch("input_requirements")) as Record<string, unknown> | undefined,
    identity: identityDraft(form.watch()),
  };

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
            verification: values.verification_scheme.trim()
              ? {
                  scheme: values.verification_scheme.trim(),
                  config:
                    source.verification?.scheme === values.verification_scheme
                      ? (source.verification.config ?? {})
                      : {},
                  secret_refs:
                    source.verification?.scheme === values.verification_scheme
                      ? {
                          ...(source.verification.secret_refs ?? {}),
                          secret: values.verification_secret_ref.trim(),
                        }
                      : { secret: values.verification_secret_ref.trim() },
                }
              : null,
            input_requirements: optionalJson(values.input_requirements),
            mapping: mapping(values.mapping),
            event_identity_rule: source.type === "event_api" ? null : eventIdentityRule(values),
          },
        }),
      ),
    onSuccess: onSaved,
  });
  const persist = (values: EditValues, onSuccess: () => void) =>
    save.mutate(values, {
      onSuccess,
      onError: (failure) => applyProblem(form, failure, editFields),
    });

  const storedIdentity = source.type === "event_api" ? null : (source.event_identity_rule ?? null);
  const identityChanged = !sameJson(eventIdentityRule(form.watch()), storedIdentity);

  return (
    <Form {...form}>
      <form
        className="flex flex-col gap-4"
        aria-label={`Edit ${typeLabel(source.type)} Source`}
        noValidate
        onSubmit={form.handleSubmit((values) => {
          // Both of these are saved only through their confirmation, never by Enter.
          if (source.verification && !values.verification_scheme) return;
          if (values.mapping.trim() !== storedExpression.trim()) return;
          persist(values, onClose);
        })}
      >
        <FormError message={formError(asProblem(connector.error ?? save.error), editFields)} />

        <TextField control={form.control} name="name" label="Name" required />
        {storedBrokerFields ? <MessageBrokerFields control={form.control} /> : null}
        {source.type === "broker" && !storedBrokerFields ? (
          <Section
            title="Raw broker configuration"
            hint="This stored broker configuration is not supported by the guided fields. Edit its JSON directly."
          >
            <TextAreaField
              control={form.control}
              name="configuration"
              label="Broker configuration (JSON)"
              className="min-h-56 font-mono text-sm"
              required
            />
          </Section>
        ) : null}
        {source.type !== "event_api" ? (
          <Section
            title="Event Normalization"
            hint="How this Source turns provider input into the Integrios Event accepted by the ingestion pipeline."
          >
            <BuilderContract>
              <SettledContract
                mapping={form.watch("mapping")}
                identity={identityDraft(form.watch())}
                inputNoun={source.type === "webhook" ? "request" : "message"}
                unsaved={mappingChanged || identityChanged}
              />
              <EventBuilder
                contractKey={`${source.type} Source`}
                sourceType={source.type === "webhook" ? "webhook" : "broker"}
                draft={sourceContractDraft}
                onUse={(draft) => {
                  form.setValue("mapping", draft.expression, { shouldDirty: true });
                  form.setValue("input_requirements", draft.schema ? formatJson(draft.schema) : "", {
                    shouldDirty: true,
                  });
                  form.setValue("identity_kind", draft.identity?.kind ?? "", { shouldDirty: true });
                  form.setValue("identity_value", draft.identity?.value ?? "", { shouldDirty: true });
                  form.setValue("identity_allow_missing", draft.identity?.allowMissing ?? false, {
                    shouldDirty: true,
                  });
                }}
              />
            </BuilderContract>
          </Section>
        ) : null}
        {source.type === "webhook" ? (
          <SourceVerificationFields
            control={form.control}
            connectorChosen
            pending={connector.isPending}
            capabilities={capabilities}
          />
        ) : null}
        {source.type !== "event_api" ? (
          <Disclosure label="Raw event contract">
            <div className="flex flex-col gap-4">
              <p className="m-0 text-xs text-ink-secondary">
                Edit the input schema and JSONata mapping directly. Changes replace Event Builder output.
              </p>
              <TextAreaField
                control={form.control}
                name="input_requirements"
                label="Input schema (JSON, optional)"
                className="min-h-40 font-mono text-sm"
              />
              <TextAreaField
                control={form.control}
                name="mapping"
                label="Event mapping (JSONata, optional)"
                className="min-h-40 font-mono text-sm"
              />
            </div>
          </Disclosure>
        ) : null}

        {source.verification && !verificationScheme ? (
          <ConfirmAction
            label="Remove verification and save"
            question={`Remove request verification from ${source.name}?`}
            consequence={`This Source will start accepting unsigned requests.${
              mappingChanged ? ` ${retypeConsequence(routing)}` : ""
            }`}
            confirmLabel="Remove verification and save"
            busy={save.isPending}
            onConfirm={() => {
              void form.handleSubmit((values) => persist(values, onClose))();
            }}
          />
        ) : mappingChanged ? (
          <ConfirmAction
            label="Save configuration"
            variant="outline"
            question={`Change how Events from ${source.name} are typed?`}
            consequence={retypeConsequence(routing)}
            busy={save.isPending}
            onConfirm={() => {
              void form.handleSubmit((values) => persist(values, onClose))();
            }}
          />
        ) : (
          <Button type="submit" className="self-start" disabled={save.isPending}>
            Save configuration
          </Button>
        )}
        <WriteStatus done={save.isSuccess}>Configuration saved.</WriteStatus>
      </form>
    </Form>
  );
}
