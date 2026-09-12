import { zodResolver } from "@hookform/resolvers/zod";
import { useInfiniteQuery, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { X } from "lucide-react";
import { Dialog as DialogPrimitive } from "radix-ui";
import { useEffect, useRef, useState } from "react";
import { type UseFormReturn, useForm } from "react-hook-form";
import { Link, NavLink, useLocation, useNavigate } from "react-router";
import { z } from "zod";
import { Button } from "@/components/ui/button";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import { Textarea } from "@/components/ui/textarea";
import { api } from "../api/client";
import { fieldError, formError } from "../api/problem";
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
import {
  expressionFromFieldMappings,
  type FieldMapping,
  parseFieldMappings,
  payloadFieldPaths,
  payloadPlaceholder,
} from "../ui/fieldMapping";
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
import { activeOnly, useDestinationOptions, useTopicOptions } from "../ui/options";
import { StatusBadge } from "../ui/status";

type SubscriptionByTenantListItem = components["schemas"]["SubscriptionByTenantListItemDto"];
type Subscription = components["schemas"]["SubscriptionDto"];
type HttpDelivery = components["schemas"]["HttpDeliveryConfiguration"];
type HttpSuccessRule = components["schemas"]["HttpSuccessRule"];
type EventListItem = components["schemas"]["EventListItemDto"];
type EditableFieldMapping = FieldMapping & { id: string };

const writeFields = [
  "name",
  "match_rules",
  "destination_id",
  "mapping",
  "http_delivery",
  "http_success",
  "description",
] as const;

/// The rows the form itself renders. `http_delivery` is not one of them: the server names the whole
/// delivery configuration, which is spread across four controls here, so its message stays at form
/// level rather than being attached to an arbitrary one of them.
const formFields = ["name", "destination_id", "event_type", "mapping", "description"] as const;

/// The version the dashboard authors. The server owns the meaning of each version, so an existing
/// Subscription keeps whatever version it already carries rather than being silently upgraded.
const currentHttpDeliveryVersion = 1;

const jsonDocument = z.string().superRefine((text, ctx) => {
  const parsed = parseJson(text);
  if (parsed.error !== undefined) ctx.addIssue({ code: z.ZodIssueCode.custom, message: parsed.error });
});

const subscriptionSchema = z.object({
  name: z.string().trim().min(1, "Enter a name."),
  destination_id: z.string().min(1, "Choose a Destination."),
  event_type: z.string().trim().min(1, "Enter an Event type."),
  mapping: z.string().max(65_536, "Keep the mapping expression at or below 64 KiB."),
  method: z.string().min(1),
  path: z.string(),
  body: z.string().min(1, "Enter a body format."),
  headers: jsonDocument,
  http_success: z.string().superRefine((text, ctx) => {
    if (!text.trim()) return;
    const parsed = parseJson(text);
    if (parsed.error !== undefined) ctx.addIssue({ code: z.ZodIssueCode.custom, message: parsed.error });
  }),
  description: z.string(),
});

type SubscriptionValues = z.infer<typeof subscriptionSchema>;

const mappingEnvelope = (expression: string) =>
  expression.trim() === "" ? null : { engine: "jsonata", version: "1", expression };

function mappingExpression(mapping: unknown): string {
  if (typeof mapping !== "object" || mapping === null) return "";
  const expression = (mapping as { expression?: unknown }).expression;
  return typeof expression === "string" ? expression : "";
}

function subscriptionEventType(matchRules: unknown): string {
  if (typeof matchRules !== "object" || matchRules === null) return "";
  const eventType = (matchRules as { event_type?: unknown }).event_type;
  return typeof eventType === "string" ? eventType : "";
}

const editableFieldMapping = (row: FieldMapping = { output: "", source: "" }): EditableFieldMapping => ({
  ...row,
  id: crypto.randomUUID(),
});

function formatMappingExpression(expression: string): string {
  const parsed = parseJson(expression);
  if (!parsed.error) return formatJson(parsed.value);

  let formatted = "";
  let quote: '"' | "'" | "`" | undefined;
  let escaped = false;
  let depth = 0;
  let skipWhitespace = false;
  const delimiters: string[] = [];

  for (const character of expression.trim()) {
    if (quote) {
      formatted += character;
      if (escaped) escaped = false;
      else if (character === "\\") escaped = true;
      else if (character === quote) quote = undefined;
      continue;
    }

    if (/\s/.test(character) && skipWhitespace) continue;

    if (character === '"' || character === "'" || character === "`") {
      quote = character;
      formatted += character;
      skipWhitespace = false;
    } else if (character === "{" || character === "[") {
      delimiters.push(character);
      depth += 1;
      formatted += `${character}\n${"  ".repeat(depth)}`;
      skipWhitespace = true;
    } else if (character === "(") {
      delimiters.push(character);
      formatted += character;
      skipWhitespace = false;
    } else if (character === "}" || character === "]") {
      delimiters.pop();
      depth = Math.max(0, depth - 1);
      formatted = `${formatted.trimEnd()}\n${"  ".repeat(depth)}${character}`;
      skipWhitespace = false;
    } else if (character === ")") {
      delimiters.pop();
      formatted += character;
      skipWhitespace = false;
    } else if (character === ",") {
      if (delimiters.at(-1) === "{" || delimiters.at(-1) === "[") formatted += `,\n${"  ".repeat(depth)}`;
      else formatted += ", ";
      skipWhitespace = true;
    } else {
      formatted += character;
      skipWhitespace = false;
    }
  }

  return formatted;
}

function MappingValue({ expression }: { expression: string }) {
  const fieldMappings = parseFieldMappings(expression);
  if (fieldMappings)
    return (
      <dl className="m-0 grid max-h-40 grid-cols-[minmax(0,1fr)_auto_minmax(0,1fr)] gap-x-2 gap-y-1 overflow-auto text-xs">
        {fieldMappings.map((row) => (
          <div key={row.output} className="col-span-3 grid grid-cols-subgrid">
            <dt className="m-0 break-words font-medium">{row.output}</dt>
            <dd className="m-0 text-ink-secondary" aria-hidden="true">
              ←
            </dd>
            <dd className="m-0 break-words font-mono">{row.source}</dd>
          </div>
        ))}
      </dl>
    );

  return (
    <pre className="m-0 max-h-40 max-w-full overflow-auto whitespace-pre-wrap break-words text-xs">
      {formatMappingExpression(expression)}
    </pre>
  );
}

export function SubscriptionsScreen({
  tenantId,
  selectedTopicId,
  selectedSubscriptionId,
}: {
  tenantId: string;
  selectedTopicId?: string;
  selectedSubscriptionId?: string;
}) {
  const [name, setName] = useFilterParam("name");
  const [topicId, setTopicId] = useFilterParam("topic_id");
  const [destinationId, setDestinationId] = useFilterParam("destination_id");
  const [status, setStatus] = useFilterParam("status");
  const topics = useTopicOptions(tenantId);
  const destinations = useDestinationOptions(tenantId);
  const applied = [name, topicId, destinationId, status].filter(Boolean).length;
  const list = useInfiniteQuery({
    queryKey: ["tenant-subscriptions", tenantId, { name, topicId, destinationId, status }],
    queryFn: ({ pageParam }) =>
      call(() =>
        api.GET("/admin/tenants/{tenantId}/subscriptions", {
          params: {
            path: { tenantId },
            query: {
              name: name || undefined,
              topic_id: topicId || undefined,
              destination_id: destinationId || undefined,
              status: status || undefined,
              after: pageParam ?? undefined,
              limit: 20,
            },
          },
        }),
      ),
    initialPageParam: null as string | null,
    getNextPageParam: nextCursor<SubscriptionByTenantListItem>,
  });
  const subscriptions = list.data?.pages.flatMap((page) => page.items) ?? [];

  return (
    <Page>
      <PageHeader
        title="Subscriptions"
        action={
          <CreateSheet label="New Subscription" description="Routes matching Events from one Topic">
            {(close) => <CreateTenantSubscription tenantId={tenantId} defaultTopicId={topicId} onCreated={close} />}
          </CreateSheet>
        }
      >
        Tenant-wide routes from Topics to Destinations.
      </PageHeader>

      <FilterBar applied={applied}>
        <FilterSearch id="subscription-name" label="Find by name" value={name} onChange={setName} />
        <Filter
          id="subscription-topic"
          label="Topic"
          value={topicId}
          onChange={setTopicId}
          hint={topics.data?.next_cursor ? "Showing the first 100 Topics." : undefined}
        >
          {(topics.data?.items ?? []).map((topic) => (
            <SelectItem key={topic.id} value={topic.id}>
              {topic.name}
            </SelectItem>
          ))}
        </Filter>
        <Filter
          id="subscription-destination"
          label="Destination"
          value={destinationId}
          onChange={setDestinationId}
          hint={destinations.data?.next_cursor ? "Showing the first 100 Destinations." : undefined}
        >
          {(destinations.data?.items ?? []).map((destination) => (
            <SelectItem key={destination.id} value={destination.id}>
              {destination.name}
            </SelectItem>
          ))}
        </Filter>
        <Filter id="subscription-status" label="Status" value={status} onChange={setStatus}>
          <SelectItem value="active">Active</SelectItem>
          <SelectItem value="disabled">Disabled</SelectItem>
        </Filter>
      </FilterBar>

      <SplitView>
        <SplitList>
          <ListStatus
            busy={list.isFetching}
            loaded={list.isSuccess}
            problem={asProblem(list.error)}
            empty={subscriptions.length === 0}
            emptyText="This Tenant has no Subscriptions matching this filter."
          />
          {subscriptions.length > 0 ? (
            <TableCard
              caption={`Subscriptions, newest first${appliedNote(applied)}`}
              footer={
                <LoadMore
                  noun="Subscription"
                  hasMore={list.hasNextPage}
                  busy={list.isFetching}
                  loaded={subscriptions.length}
                  onLoadMore={() => void list.fetchNextPage()}
                />
              }
            >
              <TableHeader>
                <TableRow>
                  <TableHead scope="col">Name</TableHead>
                  <TableHead scope="col">Topic</TableHead>
                  <TableHead scope="col">Destination</TableHead>
                  <TableHead scope="col">Status</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {subscriptions.map((subscription) => (
                  <TableRow key={subscription.id} className="has-[a[aria-current=page]]:bg-selected-surface">
                    <RowHeader>
                      <NavLink
                        className="no-underline"
                        to={`/tenants/${tenantId}/subscriptions/${subscription.topic_id}/${subscription.id}`}
                        end
                      >
                        {subscription.name}
                      </NavLink>
                    </RowHeader>
                    <TableCell>
                      <Link className="no-underline" to={`/tenants/${tenantId}/topics/${subscription.topic_id}`}>
                        {subscription.topic_name}
                      </Link>
                    </TableCell>
                    <TableCell>
                      <Link
                        className="no-underline"
                        to={`/tenants/${tenantId}/destinations/${subscription.destination_id}`}
                      >
                        {subscription.destination_name}
                      </Link>
                    </TableCell>
                    <TableCell>
                      <StatusBadge status={subscription.status} />
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </TableCard>
          ) : null}
        </SplitList>

        {selectedTopicId && selectedSubscriptionId ? (
          <SubscriptionInspector
            key={selectedSubscriptionId}
            tenantId={tenantId}
            topicId={selectedTopicId}
            subscriptionId={selectedSubscriptionId}
          />
        ) : (
          <InspectorPlaceholder label="Subscription detail">
            Select a Subscription to inspect its Topic, destination and delivery order.
          </InspectorPlaceholder>
        )}
      </SplitView>
    </Page>
  );
}

function CreateTenantSubscription({
  tenantId,
  defaultTopicId,
  onCreated,
}: {
  tenantId: string;
  defaultTopicId: string;
  onCreated: () => void;
}) {
  const navigate = useNavigate();
  const topics = useTopicOptions(tenantId);
  const topicForm = useForm<{ topic_id: string }>({ defaultValues: { topic_id: defaultTopicId } });
  const topicId = topicForm.watch("topic_id");
  const topic = topics.data?.items.find((item) => item.id === topicId);

  return (
    <div className="flex flex-col gap-4">
      <Form {...topicForm}>
        <SelectField
          control={topicForm.control}
          name="topic_id"
          label="Topic"
          hint={topics.data?.next_cursor ? "Showing the first 100 active Topics." : undefined}
          disabled={topics.isPending || topics.isError}
          required
        >
          {activeOnly(topics.data?.items).map((option) => (
            <SelectItem key={option.id} value={option.id}>
              {option.name}
            </SelectItem>
          ))}
        </SelectField>
      </Form>
      <FormError message={formError(asProblem(topics.error))} />
      {topic ? (
        <SubscriptionForm
          tenantId={tenantId}
          topicId={topic.id}
          onSaved={(created) => {
            onCreated();
            if (created) navigate(`/tenants/${tenantId}/subscriptions/${topic.id}/${created.id}`);
          }}
        />
      ) : (
        <p className="m-0 text-sm text-ink-secondary">Choose the Topic this Subscription consumes.</p>
      )}
    </div>
  );
}

function SubscriptionInspector({
  tenantId,
  topicId,
  subscriptionId,
}: {
  tenantId: string;
  topicId: string;
  subscriptionId: string;
}) {
  const queryClient = useQueryClient();
  const location = useLocation();
  const navigate = useNavigate();
  const openPlayground =
    (location.state as { openSubscriptionPlayground?: string } | null)?.openSubscriptionPlayground === subscriptionId;
  useEffect(() => {
    if (!openPlayground) return;
    navigate(`${location.pathname}${location.search}`, { replace: true, state: null });
  }, [location.pathname, location.search, navigate, openPlayground]);
  const subscription = useQuery({
    queryKey: ["subscription", tenantId, topicId, subscriptionId],
    queryFn: () =>
      call(() =>
        api.GET("/admin/tenants/{tenantId}/topics/{topicId}/subscriptions/{id}", {
          params: { path: { tenantId, topicId, id: subscriptionId } },
        }),
      ),
  });
  const deactivate = useMutation({
    mutationFn: () =>
      call(() =>
        api.POST("/admin/tenants/{tenantId}/topics/{topicId}/subscriptions/{id}/deactivate", {
          params: { path: { tenantId, topicId, id: subscriptionId } },
        }),
      ),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ["subscription", tenantId, topicId, subscriptionId] });
      void queryClient.invalidateQueries({ queryKey: ["subscriptions", tenantId, topicId] });
      void queryClient.invalidateQueries({ queryKey: ["tenant-subscriptions", tenantId] });
    },
  });

  const problem = asProblem(subscription.error);
  if (problem)
    return (
      <Inspector label="Subscription detail">
        <div className="flex items-start justify-between gap-3">
          <h2>Subscription</h2>
          <CloseInspector to={`/tenants/${tenantId}/subscriptions`} label="Close the Subscription detail" />
        </div>
        <p role="alert">{problem.detail ?? `This Subscription could not be read (${problem.status}).`}</p>
      </Inspector>
    );
  if (!subscription.data) return <Inspector label="Subscription detail">Loading…</Inspector>;

  const current = subscription.data;
  const mapping = mappingExpression(current.mapping_config);
  return (
    <Inspector label="Subscription detail">
      <div className="flex items-start justify-between gap-3">
        <div className="group min-w-0">
          <h2>{current.name}</h2>
          <span className="block text-xs text-ink-secondary">
            <CopyInline label="Subscription id" value={current.id} />
          </span>
        </div>
        <div className="flex shrink-0 items-center gap-1.5">
          <StatusBadge status={current.status} className="mt-0.5" />
          <CloseInspector to={`/tenants/${tenantId}/subscriptions`} label="Close the Subscription detail" />
        </div>
      </div>

      <Details>
        <dt>Topic</dt>
        <dd>
          <Link to={`/tenants/${tenantId}/topics/${topicId}`}>Open Topic</Link>
        </dd>
        <dt>Destination</dt>
        <dd>
          <Link to={`/tenants/${tenantId}/destinations/${current.destination_id}`}>Open Destination</Link>
        </dd>
        <dt>Description</dt>
        <dd>{current.description ?? "—"}</dd>
      </Details>

      <SubscriptionSourcePath tenantId={tenantId} topicId={topicId} subscription={current} />

      {mapping ? (
        <section className="flex min-w-0 flex-col gap-2">
          <h3 className="eyebrow">Mapping</h3>
          <MappingValue expression={mapping} />
          <p className="m-0 text-xs text-ink-secondary">
            Evaluated per Event before delivery. An evaluation failure becomes an EventDelivery failure.
          </p>
        </section>
      ) : null}

      <div className="flex flex-wrap items-start gap-2">
        <EditSheet label="Edit" description="Routes matching Events from this Topic">
          {(close) => (
            <SubscriptionForm
              key={current.updated_at}
              tenantId={tenantId}
              topicId={topicId}
              subscription={current}
              onSaved={close}
            />
          )}
        </EditSheet>
        <EditSheet
          label="Playground"
          title="Edit"
          description="Routes matching Events from this Topic"
          initialOpen={openPlayground}
        >
          {(close) => (
            <SubscriptionForm
              key={`playground-${current.updated_at}`}
              tenantId={tenantId}
              topicId={topicId}
              subscription={current}
              initialPlaygroundOpen
              onSaved={close}
            />
          )}
        </EditSheet>
        {current.status === "active" ? (
          <ConfirmAction
            label="Deactivate"
            consequence={`Deactivating ${current.name} stops it receiving Events from this Topic. Deliveries already queued are not cancelled.`}
            question={`Deactivate the Subscription "${current.name}"? It stops receiving Events from this Topic.`}
            confirmLabel={`Deactivate ${current.name}`}
            busy={deactivate.isPending}
            onConfirm={() => deactivate.mutate()}
          />
        ) : null}
      </div>

      <WriteStatus done={deactivate.isSuccess}>Subscription deactivated.</WriteStatus>
      <FormError message={formError(asProblem(deactivate.error))} />
    </Inspector>
  );
}

function SubscriptionSourcePath({
  tenantId,
  topicId,
  subscription,
}: {
  tenantId: string;
  topicId: string;
  subscription: Subscription;
}) {
  const sources = useQuery({
    queryKey: ["subscription-sources", tenantId, topicId],
    queryFn: async () => {
      const items: components["schemas"]["SourceListItemDto"][] = [];
      let after: string | undefined;
      do {
        const page = await call(() =>
          api.GET("/admin/tenants/{tenantId}/sources", {
            params: { path: { tenantId }, query: { topic_id: topicId, status: "active", after, limit: 100 } },
          }),
        );
        items.push(...page.items);
        after = page.next_cursor ?? undefined;
      } while (after);
      return items;
    },
  });
  const eventType = subscriptionEventType(subscription.match_rules);
  const fieldMappings = parseFieldMappings(mappingExpression(subscription.mapping_config));
  const context = {
    subscriptionId: subscription.id,
    subscriptionPath: `/tenants/${tenantId}/subscriptions/${topicId}/${subscription.id}`,
    eventType,
    payload: payloadPlaceholder(fieldMappings ?? []),
    advancedMapping: fieldMappings === undefined,
  };
  const items = sources.data ?? [];

  return (
    <section className="flex min-w-0 flex-col gap-2 border-y py-3.5" aria-labelledby="subscription-source-path">
      <h3 id="subscription-source-path" className="m-0 text-sm">
        How Events reach this Subscription
      </h3>
      <p className="m-0 text-[13px] text-ink-secondary">
        Publishers address an active Source, not this Subscription. Matching Events then follow this configured path.
      </p>
      <ol aria-label="Subscription Event path" className="m-0 flex list-none flex-wrap items-center gap-1.5 text-xs">
        <li className="rounded-full border px-2.5 py-1">{items.length === 1 ? "Source" : "Sources"}</li>
        <li aria-hidden="true">→</li>
        <li className="rounded-full border px-2.5 py-1">Topic</li>
        <li aria-hidden="true">→</li>
        <li className="rounded-full border px-2.5 py-1">Subscription</li>
        <li aria-hidden="true">→</li>
        <li className="rounded-full border px-2.5 py-1">Destination</li>
      </ol>
      <p className="m-0 text-sm">
        Event type: <code>{eventType || "—"}</code>
      </p>
      {sources.isPending ? <p className="m-0 text-sm">Loading active Sources…</p> : null}
      {sources.error ? (
        <p role="alert">{asProblem(sources.error)?.detail ?? "Active Sources could not be read."}</p>
      ) : null}
      {!sources.isPending && !sources.error && items.length === 0 ? (
        <p className="m-0 text-sm">
          No active Source publishes to this Topic.{" "}
          <Link to={`/tenants/${tenantId}/sources?topic_id=${topicId}`} state={{ openSourceCreate: true }}>
            Create a Source
          </Link>
        </p>
      ) : null}
      {items.length > 0 ? (
        <div className="flex min-w-0 flex-col gap-1.5">
          {items.length > 1 ? <span className="text-xs text-ink-secondary">Choose an upstream Source:</span> : null}
          {items.map((source) => (
            <Button
              key={source.id}
              asChild
              variant="outline"
              size="sm"
              className="h-auto min-w-0 max-w-full justify-start whitespace-normal py-1.5"
            >
              <Link
                className="min-w-0 no-underline"
                to={`/tenants/${tenantId}/sources/${source.id}`}
                state={{ openSourceGuide: source.id, sourceGuideContext: context }}
              >
                <span className="min-w-0 break-all text-left">
                  {source.name} · {source.type} · {source.input_requirements ? "requirements" : "no requirements"}
                </span>
              </Link>
            </Button>
          ))}
        </div>
      ) : null}
    </section>
  );
}

function MappingPlayground({
  tenantId,
  topicId,
  form,
  originalExpression,
  open,
  onOpenChange,
  onReviewInvalidated,
  onConfirmed,
}: {
  tenantId: string;
  topicId: string;
  form: UseFormReturn<SubscriptionValues>;
  originalExpression: string;
  open: boolean;
  onOpenChange: (open: boolean) => void;
  onReviewInvalidated: () => void;
  onConfirmed: (expression: string) => void;
}) {
  const expression = form.watch("mapping");
  const parsedExpression = parseFieldMappings(expression);
  const [mappingMode, setMappingMode] = useState<"fields" | "advanced">(() =>
    parsedExpression ? "fields" : "advanced",
  );
  const [fieldMappings, setFieldMappings] = useState<EditableFieldMapping[]>(() =>
    (parsedExpression?.length ? parsedExpression : [{ output: "", source: "" }]).map(editableFieldMapping),
  );
  const [manual, setManual] = useState(false);
  const [eventId, setEventId] = useState<string>();
  const [manualPayload, setManualPayload] = useState("{}");
  const [manualEventType, setManualEventType] = useState("sample.event");
  const [manualAcceptedAt, setManualAcceptedAt] = useState(() => new Date().toISOString().slice(0, 16));
  const [manualError, setManualError] = useState<string>();
  const [output, setOutput] = useState<unknown>();
  const [evaluationError, setEvaluationError] = useState<string>();
  const previewAttempt = useRef(0);

  const topic = useQuery({
    queryKey: ["topic", tenantId, topicId],
    queryFn: () =>
      call(() =>
        api.GET("/admin/tenants/{tenantId}/topics/{id}", {
          params: { path: { tenantId, id: topicId } },
        }),
      ),
    enabled: open,
  });
  const events = useQuery({
    queryKey: ["mapping-events", tenantId, topicId],
    queryFn: () =>
      call(() =>
        api.GET("/admin/tenants/{tenantId}/events", {
          params: { path: { tenantId }, query: { topic_id: topicId, limit: 20 } },
        }),
      ),
    enabled: open,
  });
  const recentEvents = events.data?.items ?? [];
  const selectedEventId = eventId ?? recentEvents[0]?.event_id;
  const selectedEvent = recentEvents.find((item) => item.event_id === selectedEventId);
  const event = useQuery({
    queryKey: ["event", tenantId, selectedEventId],
    queryFn: () => {
      if (!selectedEventId) throw new Error("No Event is selected.");
      return call(() =>
        api.GET("/admin/tenants/{tenantId}/events/{eventId}/deliveries", {
          params: { path: { tenantId, eventId: selectedEventId } },
        }),
      );
    },
    enabled: open && !manual && Boolean(selectedEventId),
  });

  const resetPreview = () => {
    previewAttempt.current += 1;
    setOutput(undefined);
    setEvaluationError(undefined);
    setManualError(undefined);
  };

  const invalidateReview = () => {
    resetPreview();
    onReviewInvalidated();
  };

  const updateFieldMappings = (next: EditableFieldMapping[]) => {
    setFieldMappings(next);
    form.setValue("mapping", expressionFromFieldMappings(next), { shouldDirty: true, shouldValidate: true });
    form.clearErrors("mapping");
    invalidateReview();
  };

  const preview = useMutation({
    mutationFn: ({
      payload,
      eventType,
      acceptedAt,
      topicName,
    }: {
      payload: unknown;
      eventType: string;
      acceptedAt: string;
      topicName: string;
      attempt: number;
    }) => {
      const transform = mappingEnvelope(expression);
      if (!transform) throw new Error("No mapping expression was provided.");
      return call(() =>
        api.POST("/admin/transform/preview", {
          body: {
            transform,
            sample_input: payload,
            sample_context: { event_type: eventType, topic_name: topicName, accepted_at: acceptedAt },
          },
        }),
      );
    },
    onSuccess: (result, { attempt }) => {
      if (attempt === previewAttempt.current) setOutput(result?.output);
    },
    onError: (failure, { attempt }) => {
      if (attempt !== previewAttempt.current) return;
      const problem = asProblem(failure);
      const syntaxError = fieldError(problem, "transform");
      if (syntaxError) form.setError("mapping", { type: "server", message: syntaxError });
      else setEvaluationError(formError(problem));
    },
  });

  const runPreview = () => {
    form.clearErrors("mapping");
    resetPreview();

    const topicName = topic.data?.key;
    if (!topicName) return;

    let payload: unknown;
    let eventType: string;
    let acceptedAt: string;
    if (manual) {
      const parsed = parseJson(manualPayload);
      if (parsed.error) {
        setManualError(parsed.error);
        return;
      }
      if (!manualEventType.trim() || Number.isNaN(new Date(manualAcceptedAt).getTime())) {
        setManualError("Enter an Event type and acceptance time.");
        return;
      }
      payload = parsed.value;
      eventType = manualEventType.trim();
      acceptedAt = new Date(manualAcceptedAt).toISOString();
    } else {
      if (!selectedEvent || event.data?.payload === undefined || event.data.payload === null) return;
      payload = event.data.payload;
      eventType = selectedEvent.event_type;
      acceptedAt = selectedEvent.accepted_at;
    }

    if (expression.trim() === "") {
      setOutput(payload);
      return;
    }
    preview.mutate({ payload, eventType, acceptedAt, topicName, attempt: previewAttempt.current });
  };

  const input = manual ? parseJson(manualPayload).value : event.data?.payload;
  const contextEventType = manual ? manualEventType : selectedEvent?.event_type;
  const contextAcceptedAt = manual ? manualAcceptedAt : selectedEvent?.accepted_at;
  const unavailable = manual
    ? false
    : !selectedEvent || event.data?.payload === undefined || event.data.payload === null;
  const sourceOptions = [
    ...payloadFieldPaths(input),
    "$context.event_type",
    "$context.topic_name",
    "$context.accepted_at",
  ];

  return (
    <DialogPrimitive.Root
      open={open}
      onOpenChange={(next) => {
        if (next) resetPreview();
        onOpenChange(next);
      }}
    >
      <DialogPrimitive.Trigger asChild>
        <Button type="button" variant="outline" className="self-start">
          {expression.trim() ? "Edit in Playground" : "Add mapping in Playground"}
        </Button>
      </DialogPrimitive.Trigger>
      <DialogPrimitive.Portal>
        <DialogPrimitive.Overlay className="fixed inset-0 z-60 bg-ink/25" />
        <DialogPrimitive.Content className="fixed inset-4 z-70 flex max-h-[calc(100vh-2rem)] flex-col gap-4 overflow-y-auto rounded-lg border bg-surface p-4 shadow-[0_24px_64px_-32px_rgb(23_23_23/0.45)] outline-none md:inset-x-10 lg:inset-x-[max(2.5rem,calc((100vw-80rem)/2))]">
          <div className="flex items-start justify-between gap-3">
            <div>
              <DialogPrimitive.Title className="m-0">Mapping Playground</DialogPrimitive.Title>
              <DialogPrimitive.Description className="m-0 mt-1 text-sm text-ink-secondary">
                Preview this unsaved expression against one accepted Event or a manual sample. Nothing is saved.
              </DialogPrimitive.Description>
            </div>
            <DialogPrimitive.Close
              aria-label="Back to Subscription"
              className="flex size-8 shrink-0 items-center justify-center rounded-md hover:bg-hover-surface focus-visible:ring-[3px] focus-visible:ring-ring/50"
            >
              <X aria-hidden="true" className="size-4" />
            </DialogPrimitive.Close>
          </div>

          <div className="flex flex-wrap items-end gap-2">
            {!manual ? (
              <div className="flex min-w-64 flex-1 flex-col gap-1 text-sm">
                <span id="recent-event-label" className="font-medium">
                  Preview against
                </span>
                <Select
                  value={selectedEventId}
                  onValueChange={(value) => {
                    setEventId(value);
                    invalidateReview();
                  }}
                  disabled={recentEvents.length === 0}
                >
                  <SelectTrigger aria-labelledby="recent-event-label">
                    <SelectValue placeholder={events.isPending ? "Loading Events…" : "No recent Events"} />
                  </SelectTrigger>
                  <SelectContent>
                    {recentEvents.map((item: EventListItem) => (
                      <SelectItem key={item.event_id} value={item.event_id}>
                        {item.event_type} · {new Date(item.accepted_at).toLocaleString()}
                      </SelectItem>
                    ))}
                  </SelectContent>
                </Select>
              </div>
            ) : null}
            <Button
              type="button"
              variant="outline"
              onClick={() => {
                setManual((value) => !value);
                invalidateReview();
              }}
            >
              {manual ? "Choose a recent Event" : "Paste sample JSON"}
            </Button>
          </div>

          {manual ? (
            <div className="grid gap-3 md:grid-cols-2">
              <label htmlFor="manual-payload" className="flex flex-col gap-1 text-sm md:col-span-2">
                <span className="font-medium">Sample input (JSON)</span>
                <Textarea
                  id="manual-payload"
                  value={manualPayload}
                  onChange={(event) => {
                    setManualPayload(event.target.value);
                    invalidateReview();
                  }}
                  className="min-h-32 font-mono text-sm"
                />
              </label>
              <label className="flex flex-col gap-1 text-sm">
                <span className="font-medium">Event type</span>
                <input
                  value={manualEventType}
                  onChange={(event) => {
                    setManualEventType(event.target.value);
                    invalidateReview();
                  }}
                  className="h-9 rounded-md border border-input bg-transparent px-3 text-sm outline-none focus-visible:border-ring focus-visible:ring-[3px] focus-visible:ring-ring/50"
                />
              </label>
              <label className="flex flex-col gap-1 text-sm">
                <span className="font-medium">Accepted at</span>
                <input
                  type="datetime-local"
                  value={manualAcceptedAt}
                  onChange={(event) => {
                    setManualAcceptedAt(event.target.value);
                    invalidateReview();
                  }}
                  className="h-9 rounded-md border border-input bg-transparent px-3 text-sm outline-none focus-visible:border-ring focus-visible:ring-[3px] focus-visible:ring-ring/50"
                />
              </label>
            </div>
          ) : null}

          <div className="flex flex-wrap gap-2 text-xs text-ink-secondary">
            <span className="rounded-full border px-2 py-1">event_type: {contextEventType ?? "—"}</span>
            <span className="rounded-full border px-2 py-1">topic_name: {topic.data?.key ?? "—"}</span>
            <span className="rounded-full border px-2 py-1">accepted_at: {contextAcceptedAt ?? "—"}</span>
          </div>

          <div className="grid gap-4 lg:grid-cols-[minmax(0,0.75fr)_minmax(0,1.5fr)_minmax(0,0.75fr)]">
            <section className="flex min-w-0 flex-col gap-2 rounded-lg border p-3">
              <h3 className="m-0 text-sm">{manual ? "Sample input" : "Accepted Event"}</h3>
              {input !== undefined ? (
                <pre className="m-0 overflow-auto text-sm">{formatJson(input)}</pre>
              ) : (
                <p className="m-0 text-sm text-ink-secondary">Choose a sample with an available payload.</p>
              )}
            </section>
            <section className="flex min-w-0 flex-col gap-2 rounded-lg border p-3">
              <div className="flex items-center justify-between gap-2">
                <h3 className="m-0 text-sm">{mappingMode === "fields" ? "Field mapping" : "Advanced JSONata"}</h3>
                <Button
                  type="button"
                  variant="outline"
                  size="sm"
                  disabled={mappingMode === "advanced" && !parsedExpression}
                  onClick={() => {
                    if (mappingMode === "fields") {
                      setMappingMode("advanced");
                      return;
                    }
                    if (parsedExpression) {
                      setFieldMappings(
                        (parsedExpression.length ? parsedExpression : [{ output: "", source: "" }]).map(
                          editableFieldMapping,
                        ),
                      );
                      setMappingMode("fields");
                    }
                  }}
                >
                  {mappingMode === "fields" ? "Advanced JSONata" : "Field mapping"}
                </Button>
              </div>
              {mappingMode === "fields" ? (
                <div className="flex flex-col gap-3">
                  <p className="m-0 text-xs text-ink-secondary">
                    Name each output field, then choose the Event or context value it receives.
                  </p>
                  {fieldMappings.length > 0 ? (
                    <div className="hidden grid-cols-[minmax(0,0.8fr)_auto_minmax(0,1.2fr)_auto] gap-2 text-xs font-medium text-ink-secondary sm:grid">
                      <span>Output field</span>
                      <span aria-hidden="true" />
                      <span>Event or context field</span>
                      <span aria-hidden="true" />
                    </div>
                  ) : null}
                  {fieldMappings.length === 0 ? (
                    <p className="m-0 text-sm text-ink-secondary">
                      No fields mapped. The accepted payload will be delivered unchanged.
                    </p>
                  ) : null}
                  {fieldMappings.map((row, index) => {
                    const options =
                      row.source && !sourceOptions.includes(row.source)
                        ? [row.source, ...sourceOptions]
                        : sourceOptions;
                    return (
                      <div
                        key={row.id}
                        className="grid min-w-0 grid-cols-[minmax(0,1fr)_auto] gap-2 sm:grid-cols-[minmax(0,0.8fr)_auto_minmax(0,1.2fr)_auto] sm:items-center"
                      >
                        <input
                          aria-label={`Output field ${index + 1}`}
                          placeholder="output_field"
                          value={row.output}
                          onChange={(event) => {
                            const next = fieldMappings.map((item, itemIndex) =>
                              itemIndex === index ? { ...item, output: event.target.value } : item,
                            );
                            updateFieldMappings(next);
                          }}
                          className="h-9 min-w-0 rounded-md border border-input bg-transparent px-3 text-sm outline-none focus-visible:border-ring focus-visible:ring-[3px] focus-visible:ring-ring/50"
                        />
                        <span aria-hidden="true" className="text-ink-secondary">
                          ←
                        </span>
                        <select
                          aria-label={`Event field ${index + 1}`}
                          value={row.source}
                          onChange={(event) => {
                            const next = fieldMappings.map((item, itemIndex) =>
                              itemIndex === index ? { ...item, source: event.target.value } : item,
                            );
                            updateFieldMappings(next);
                          }}
                          className="col-span-2 h-9 min-w-0 rounded-md border border-input bg-transparent px-3 text-sm outline-none focus-visible:border-ring focus-visible:ring-[3px] focus-visible:ring-ring/50 sm:col-span-1"
                        >
                          <option value="">Choose a field…</option>
                          {options.map((option) => (
                            <option key={option} value={option}>
                              {option}
                            </option>
                          ))}
                        </select>
                        <Button
                          type="button"
                          variant="ghost"
                          size="icon-sm"
                          aria-label={`Remove mapping ${index + 1}`}
                          onClick={() =>
                            updateFieldMappings(fieldMappings.filter((_, itemIndex) => itemIndex !== index))
                          }
                        >
                          <X aria-hidden="true" className="size-4" />
                        </Button>
                      </div>
                    );
                  })}
                  <Button
                    type="button"
                    variant="outline"
                    size="sm"
                    className="self-start"
                    onClick={() => {
                      setFieldMappings((current) => [...current, editableFieldMapping()]);
                      invalidateReview();
                    }}
                  >
                    Add field
                  </Button>
                </div>
              ) : (
                <>
                  <Textarea
                    id="playground-mapping"
                    aria-label="Playground mapping expression"
                    aria-invalid={Boolean(form.formState.errors.mapping)}
                    aria-describedby={form.formState.errors.mapping ? "playground-mapping-error" : undefined}
                    value={expression}
                    onChange={(event) => {
                      form.setValue("mapping", event.target.value, { shouldDirty: true, shouldValidate: true });
                      form.clearErrors("mapping");
                      invalidateReview();
                    }}
                    className="min-h-44 font-mono text-sm"
                  />
                  <p className="m-0 text-xs text-ink-secondary">
                    {parsedExpression
                      ? "This expression can return to field mapping without losing information."
                      : "This expression uses advanced JSONata and cannot be represented safely as field rows."}
                  </p>
                </>
              )}
              {form.formState.errors.mapping?.message ? (
                <p id="playground-mapping-error" role="alert" className="m-0 text-sm text-destructive">
                  {form.formState.errors.mapping.message}
                </p>
              ) : null}
            </section>
            <section className="flex min-w-0 flex-col gap-2 rounded-lg border p-3">
              <h3 className="m-0 text-sm">Preview body</h3>
              {evaluationError ? (
                <div className="flex flex-col gap-2">
                  <p role="alert" className="m-0 text-sm text-destructive">
                    {evaluationError}
                  </p>
                  <p className="m-0 text-sm text-ink-secondary">
                    This Event would fail delivery. Equivalent Events would retry and may dead-letter after save.
                  </p>
                </div>
              ) : output !== undefined ? (
                <pre className="m-0 overflow-auto text-sm">{formatJson(output)}</pre>
              ) : (
                <p className="m-0 text-sm text-ink-secondary">
                  Preview this expression to see what would be delivered.
                </p>
              )}
            </section>
          </div>

          <FormError
            message={
              manualError ??
              formError(asProblem(topic.error)) ??
              formError(asProblem(events.error)) ??
              formError(asProblem(event.error))
            }
          />
          <div className="mt-auto flex flex-wrap items-center justify-between gap-2 border-t pt-4">
            <DialogPrimitive.Close asChild>
              <Button type="button" variant="outline">
                Back to Subscription
              </Button>
            </DialogPrimitive.Close>
            <div className="flex flex-wrap gap-2">
              <Button
                type="button"
                variant="outline"
                disabled={preview.isPending || unavailable || topic.isPending}
                onClick={runPreview}
              >
                Preview mapping
              </Button>
              <DialogPrimitive.Close asChild>
                <Button
                  type="button"
                  disabled={expression === originalExpression || output === undefined || Boolean(evaluationError)}
                  onClick={() => onConfirmed(expression)}
                >
                  Confirm mapping change
                </Button>
              </DialogPrimitive.Close>
            </div>
          </div>
        </DialogPrimitive.Content>
      </DialogPrimitive.Portal>
    </DialogPrimitive.Root>
  );
}

/// One form for both create and update: the Admin API takes the same body for each, so splitting it
/// into two near-identical forms would only invite them to drift apart.
function SubscriptionForm({
  tenantId,
  topicId,
  subscription,
  initialPlaygroundOpen = false,
  onSaved,
}: {
  tenantId: string;
  topicId: string;
  subscription?: Subscription;
  initialPlaygroundOpen?: boolean;
  onSaved?: (saved: Subscription | undefined) => void;
}) {
  const queryClient = useQueryClient();
  const destinations = useDestinationOptions(tenantId);
  const destinationOptionsUnavailable = destinations.isPending || destinations.isError;
  const [playgroundOpen, setPlaygroundOpen] = useState(initialPlaygroundOpen);
  const [reviewedExpression, setReviewedExpression] = useState<string>();
  const originalExpression = mappingExpression(subscription?.mapping_config);

  const form = useForm<SubscriptionValues>({
    resolver: zodResolver(subscriptionSchema),
    defaultValues: {
      name: subscription?.name ?? "",
      destination_id: subscription?.destination_id ?? "",
      event_type: subscriptionEventType(subscription?.match_rules),
      mapping: originalExpression,
      method: subscription?.http_delivery.method ?? "POST",
      path: subscription?.http_delivery.path ?? "",
      body: subscription?.http_delivery.body ?? "json",
      headers: formatJson(subscription?.http_delivery.headers) || "{}",
      http_success: subscription?.http_success ? formatJson(subscription.http_success) : "",
      description: subscription?.description ?? "",
    },
  });
  const expression = form.watch("mapping");
  const mappingChanged = expression !== originalExpression;
  const mappingReviewed = !mappingChanged || reviewedExpression === expression;

  const save = useMutation({
    mutationFn: (values: SubscriptionValues) => {
      const httpDelivery: HttpDelivery = {
        version: subscription?.http_delivery.version ?? currentHttpDeliveryVersion,
        method: values.method,
        path: values.path || null,
        headers: parseJson(values.headers).value as Record<string, string>,
        body: values.body,
      };
      const requestBody = {
        name: values.name,
        match_rules: { event_type: values.event_type.trim() },
        destination_id: values.destination_id,
        mapping: mappingEnvelope(values.mapping),
        http_delivery: httpDelivery,
        http_success: values.http_success.trim() ? (parseJson(values.http_success).value as HttpSuccessRule) : null,
        order_index: subscription?.order_index ?? 0,
        description: values.description.trim() || null,
      };

      return call(() =>
        subscription
          ? api.PUT("/admin/tenants/{tenantId}/topics/{topicId}/subscriptions/{id}", {
              params: { path: { tenantId, topicId, id: subscription.id } },
              body: requestBody,
            })
          : api.POST("/admin/tenants/{tenantId}/topics/{topicId}/subscriptions", {
              params: { path: { tenantId, topicId } },
              body: requestBody,
            }),
      );
    },
    onSuccess: (saved) => {
      void queryClient.invalidateQueries({ queryKey: ["subscriptions", tenantId, topicId] });
      void queryClient.invalidateQueries({ queryKey: ["tenant-subscriptions", tenantId] });
      if (subscription)
        void queryClient.invalidateQueries({ queryKey: ["subscription", tenantId, topicId, subscription.id] });
      onSaved?.(saved);
    },
  });

  const submit = form.handleSubmit((values) =>
    save.mutate(values, {
      onError: (failure) => {
        applyProblem(form, failure, formFields);
        const matchRulesError = fieldError(asProblem(failure), "match_rules");
        if (matchRulesError) form.setError("event_type", { type: "server", message: matchRulesError });
      },
    }),
  );

  return (
    <Form {...form}>
      <Panel asChild>
        <form
          className="flex flex-col gap-4"
          aria-label={subscription ? `Edit ${subscription.name}` : "Create a Subscription"}
          noValidate
          onSubmit={submit}
        >
          {/* Both paths open in a sheet that carries the title, so the form states its name rather
              than repeating a heading under one. */}
          <FormError message={formError(asProblem(destinations.error))} />
          <FormError message={formError(asProblem(save.error), writeFields)} />

          <TextField control={form.control} name="name" label="Name" required />
          <SelectField
            control={form.control}
            name="destination_id"
            label="Destination"
            hint={destinations.data?.next_cursor ? "Showing the first 100 active Destinations." : undefined}
            disabled={destinationOptionsUnavailable}
            required
          >
            {activeOnly(destinations.data?.items).map((destination) => (
              <SelectItem key={destination.id} value={destination.id}>
                {destination.name}
              </SelectItem>
            ))}
          </SelectField>
          <TextField
            control={form.control}
            name="event_type"
            label="Event type"
            hint="Changing this affects how future Events route to this Subscription."
            required
          />
          <section aria-labelledby="mapping-summary-heading" className="flex flex-col gap-2">
            <div className="flex flex-wrap items-center justify-between gap-2">
              <h3 id="mapping-summary-heading" className="m-0 text-sm">
                Mapping
              </h3>
              <StatusBadge status={mappingReviewed ? "active" : "pending"}>
                {mappingChanged ? (mappingReviewed ? "Preview reviewed" : "Review required") : "Unchanged"}
              </StatusBadge>
            </div>
            <div className="flex min-w-0 flex-col gap-2 rounded-md border bg-surface-quiet p-3">
              {expression.trim() ? (
                <MappingValue expression={expression} />
              ) : (
                <p className="m-0 text-sm text-ink-secondary">The accepted payload will be delivered unchanged.</p>
              )}
              <p className="m-0 text-xs text-ink-secondary">
                {mappingChanged
                  ? mappingReviewed
                    ? "Mapping change reviewed and ready to save."
                    : "Preview and confirm this mapping change before saving the Subscription."
                  : "Mapping changes are made and reviewed in the Playground."}
              </p>
            </div>
          </section>
          <MappingPlayground
            tenantId={tenantId}
            topicId={topicId}
            form={form}
            originalExpression={originalExpression}
            open={playgroundOpen}
            onOpenChange={setPlaygroundOpen}
            onReviewInvalidated={() => setReviewedExpression(undefined)}
            onConfirmed={setReviewedExpression}
          />

          <fieldset className="flex flex-col gap-4 rounded-md border bg-surface-quiet p-4">
            <legend className="px-1 text-sm font-medium">HTTP delivery</legend>
            <SelectField control={form.control} name="method" label="Method" required>
              {["POST", "PUT", "PATCH", "DELETE", "GET"].map((verb) => (
                <SelectItem key={verb} value={verb}>
                  {verb}
                </SelectItem>
              ))}
            </SelectField>
            <TextField control={form.control} name="path" label="Path (optional)" />
            <TextField control={form.control} name="body" label="Body" required />
            <TextAreaField
              control={form.control}
              name="headers"
              label="Headers (JSON object)"
              className="min-h-24 font-mono text-sm"
              required
            />
            <TextAreaField
              control={form.control}
              name="http_success"
              label="HTTP success rule (JSON, optional)"
              hint="Leave blank for any HTTP 2xx response to succeed."
              className="min-h-24 font-mono text-sm"
            />
          </fieldset>

          <TextField control={form.control} name="description" label="Description (optional)" />

          <Button
            type="submit"
            className="self-start"
            disabled={save.isPending || destinationOptionsUnavailable || !mappingReviewed}
            aria-describedby={mappingReviewed ? undefined : "mapping-save-requirement"}
          >
            {subscription ? "Save changes" : "Create Subscription"}
          </Button>
          {!mappingReviewed ? (
            <p id="mapping-save-requirement" className="m-0 text-sm text-warning-ink">
              Save is unavailable until the changed mapping has a successful preview and is confirmed in the Playground.
            </p>
          ) : null}
        </form>
      </Panel>
    </Form>
  );
}
