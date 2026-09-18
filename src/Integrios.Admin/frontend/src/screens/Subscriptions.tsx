import { zodResolver } from "@hookform/resolvers/zod";
import { useInfiniteQuery, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { X } from "lucide-react";
import { useEffect, useState } from "react";
import { useFieldArray, useForm } from "react-hook-form";
import { Link, NavLink, useLocation, useNavigate } from "react-router";
import { z } from "zod";
import { Button } from "@/components/ui/button";
import { SelectItem } from "@/components/ui/select";
import { TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import { api } from "../api/client";
import { fieldError, formError } from "../api/problem";
import { asProblem, call, nextCursor } from "../api/query";
import type { components } from "../api/schema";
import { CodeBlock } from "../ui/codeHighlight";
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
  Section,
  SheetButton,
  WriteStatus,
} from "../ui/controls";
import { CopyInline } from "../ui/copy";
import { parseFieldMappings, payloadPlaceholder } from "../ui/fieldMapping";
import { Filter, FilterSearch, Form, SelectField, TextAreaField, TextField } from "../ui/fields";
import { useFilterParam } from "../ui/filters";
import { applyProblem } from "../ui/formProblem";
import { formatJson, parseJson, sameJson } from "../ui/json";
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
import { activeOnly, useDestinationOptions, useTopicOptions } from "../ui/options";
import { StatusBadge } from "../ui/status";
import { MappingPlayground, mappingEnvelope } from "./SubscriptionPlayground";

type SubscriptionByTenantListItem = components["schemas"]["SubscriptionByTenantListItemDto"];
type Subscription = components["schemas"]["SubscriptionDto"];
type HttpDelivery = components["schemas"]["HttpDeliveryConfiguration"];
type HttpSuccessRule = components["schemas"]["HttpSuccessRule"];

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

const subscriptionSchema = z
  .object({
    name: z.string().trim().min(1, "Enter a name."),
    destination_id: z.string().min(1, "Choose a Destination."),
    event_type: z.string().trim().min(1, "Enter an Event type."),
    mapping: z.string().max(65_536, "Keep the mapping expression at or below 64 KiB."),
    raw_mapping: z.string().superRefine((text, ctx) => {
      if (!text.trim()) return;
      const parsed = parseJson(text);
      if (parsed.error !== undefined) ctx.addIssue({ code: z.ZodIssueCode.custom, message: parsed.error });
    }),
    method: z.enum(["POST", "PUT", "PATCH", "DELETE"]),
    path: z.string(),
    body: z.enum(["json", "none"]),
    headers: z
      .array(z.object({ name: z.string().trim().min(1, "Enter a header name."), value: z.string() }))
      .max(32, "Add at most 32 headers."),
    success_mode: z.enum(["", "json_boolean"]),
    success_field: z.string(),
    success_expected: z.enum(["", "true", "false"]),
    success_diagnostic_field: z.string(),
    success_max_body_bytes: z.string(),
    description: z.string(),
  })
  .superRefine((values, ctx) => {
    const names = new Set<string>();
    values.headers.forEach((header, index) => {
      const normalized = header.name.trim().toLowerCase();
      if (normalized && names.has(normalized))
        ctx.addIssue({
          code: z.ZodIssueCode.custom,
          path: ["headers", index, "name"],
          message: "Use each header name once, ignoring case.",
        });
      names.add(normalized);
    });
    if (values.success_mode !== "json_boolean") return;
    if (!values.success_field.trim())
      ctx.addIssue({ code: z.ZodIssueCode.custom, path: ["success_field"], message: "Enter the response field." });
    if (!values.success_expected)
      ctx.addIssue({
        code: z.ZodIssueCode.custom,
        path: ["success_expected"],
        message: "Choose the expected value.",
      });
    if (values.success_max_body_bytes) {
      const maximum = Number(values.success_max_body_bytes);
      if (!Number.isInteger(maximum) || maximum < 1 || maximum > 1_048_576)
        ctx.addIssue({
          code: z.ZodIssueCode.custom,
          path: ["success_max_body_bytes"],
          message: "Enter an integer from 1 through 1048576.",
        });
    }
  });

export type SubscriptionValues = z.infer<typeof subscriptionSchema>;

function guidedMappingExpression(mapping: unknown): string | null {
  if (mapping === null || mapping === undefined) return "";
  if (typeof mapping !== "object" || mapping === null) return null;
  const expression = (mapping as { expression?: unknown }).expression;
  if (typeof expression !== "string") return null;
  return sameJson(mapping, mappingEnvelope(expression)) ? expression : null;
}

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

function httpSuccess(values: SubscriptionValues): HttpSuccessRule | null {
  if (values.success_mode !== "json_boolean") return null;
  return {
    evaluator: "json_boolean",
    field: values.success_field.trim(),
    expected: values.success_expected === "true",
    ...(values.success_diagnostic_field.trim()
      ? { diagnostic_field: values.success_diagnostic_field.trim() }
      : undefined),
    ...(values.success_max_body_bytes ? { max_body_bytes: Number(values.success_max_body_bytes) } : undefined),
  };
}

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

  return <CodeBlock value={formatMappingExpression(expression)} className="max-h-40 overflow-auto" />;
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
  // Whether there is a list to narrow yet. Until the read answers, neither the filter bar nor the
  // header's create action is rendered: an empty scope answers with the card that replaces the
  // table, carrying the action itself, and a screen that guessed first would retract them.
  const narrowing = narrowable(list.isSuccess, subscriptions.length, applied);

  // An unrouted Event's inspector sends the Operator here to route its type: New Subscription opens on
  // arrival with that type filled in. Read once, then cleared, so reloading does not reopen it.
  const location = useLocation();
  const navigate = useNavigate();
  const requestedEventType = (location.state as { createSubscriptionFor?: string } | null)?.createSubscriptionFor;
  const [creating, setCreating] = useState(requestedEventType !== undefined);
  const [initialEventType, setInitialEventType] = useState(requestedEventType);
  useEffect(() => {
    if (requestedEventType === undefined) return;
    navigate(`${location.pathname}${location.search}`, { replace: true, state: null });
  }, [location.pathname, location.search, navigate, requestedEventType]);
  const create = <SheetButton label="New Subscription" expanded={creating} onOpen={() => setCreating(true)} />;

  return (
    <Page>
      <PageHeader title="Subscriptions" action={narrowing ? create : undefined}>
        Tenant-wide routes from Topics to Destinations.
      </PageHeader>

      {narrowing ? (
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
      ) : null}

      <SplitView>
        <SplitList>
          <ListStatus
            busy={list.isFetching}
            loaded={list.isSuccess}
            problem={asProblem(list.error)}
            empty={subscriptions.length === 0}
            applied={applied}
            noun="Subscriptions"
            emptyText="A Subscription routes matching Events from one Topic to one Destination. Until one exists, accepted Events are delivered nowhere."
            action={create}
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
                  <TableRow
                    key={subscription.id}
                    className="group cursor-pointer has-[a[aria-current=page]]:bg-selected-surface"
                    onClick={openRow}
                  >
                    <RowHeader>
                      <NavLink
                        className="-mx-3 block px-3 py-2 no-underline"
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
                      <div className="flex items-center justify-between gap-3">
                        <StatusBadge status={subscription.status} />
                        <RowChevron />
                      </div>
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
        ) : subscriptions.length > 0 ? (
          <InspectorPlaceholder label="Subscription detail">
            Select a Subscription to inspect its Topic, destination and delivery order.
          </InspectorPlaceholder>
        ) : null}
      </SplitView>

      <CreateSheet
        label="New Subscription"
        description="Routes matching Events from one Topic"
        open={creating}
        onOpenChange={(open) => {
          setCreating(open);
          if (!open) setInitialEventType(undefined);
        }}
      >
        {(close) => (
          <CreateTenantSubscription
            tenantId={tenantId}
            defaultTopicId={topicId}
            defaultEventType={initialEventType}
            onCreated={close}
          />
        )}
      </CreateSheet>
    </Page>
  );
}

function CreateTenantSubscription({
  tenantId,
  defaultTopicId,
  defaultEventType,
  onCreated,
}: {
  tenantId: string;
  defaultTopicId: string;
  defaultEventType?: string;
  onCreated: () => void;
}) {
  const navigate = useNavigate();
  const topics = useTopicOptions(tenantId);
  // A Subscription routes from a Topic to a Destination, so it is the last thing a Tenant can
  // author: both have to exist before this form can say anything.
  const noTopics = topics.isSuccess && activeOnly(topics.data?.items).length === 0;
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
          hint={
            noTopics ? (
              <>
                No active Topics yet, and a Subscription routes from one.{" "}
                <Link to={`/tenants/${tenantId}/topics`}>Create a Topic</Link> first.
              </>
            ) : topics.data?.next_cursor ? (
              "Showing the first 100 active Topics."
            ) : undefined
          }
          disabled={topics.isPending || topics.isError || noTopics}
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
          defaultEventType={defaultEventType}
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
        <ReadError
          problem={problem}
          what="This Subscription"
          back={{ to: `/tenants/${tenantId}/subscriptions`, label: "Back to Subscriptions" }}
        />
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
  const sourcesProblem = asProblem(sources.error);

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
      {sourcesProblem ? <ReadError problem={sourcesProblem} what="Active Sources" /> : null}
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

function SubscriptionForm({
  tenantId,
  topicId,
  subscription,
  defaultEventType,
  initialPlaygroundOpen = false,
  onSaved,
}: {
  tenantId: string;
  topicId: string;
  subscription?: Subscription;
  /// A new Subscription's starting Event type, when it is created for Events already seen.
  defaultEventType?: string;
  initialPlaygroundOpen?: boolean;
  onSaved?: (saved: Subscription | undefined) => void;
}) {
  const queryClient = useQueryClient();
  const destinations = useDestinationOptions(tenantId);
  const destinationOptionsUnavailable = destinations.isPending || destinations.isError;
  const noDestinations = destinations.isSuccess && activeOnly(destinations.data?.items).length === 0;
  const [playgroundOpen, setPlaygroundOpen] = useState(initialPlaygroundOpen);
  const [reviewedExpression, setReviewedExpression] = useState<string>();
  const originalExpression = guidedMappingExpression(subscription?.mapping_config);
  const rawMapping = subscription !== undefined && originalExpression === null;
  const storedSuccess = subscription?.http_success;

  const form = useForm<SubscriptionValues, unknown, SubscriptionValues>({
    resolver: zodResolver(subscriptionSchema),
    defaultValues: {
      name: subscription?.name ?? "",
      destination_id: subscription?.destination_id ?? "",
      event_type: subscription ? subscriptionEventType(subscription.match_rules) : (defaultEventType ?? ""),
      mapping: originalExpression ?? "",
      raw_mapping: rawMapping ? formatJson(subscription.mapping_config) : "",
      method: (subscription?.http_delivery.method ?? "POST") as SubscriptionValues["method"],
      path: subscription?.http_delivery.path ?? "",
      body: (subscription?.http_delivery.body ?? "json") as SubscriptionValues["body"],
      headers: Object.entries(subscription?.http_delivery.headers ?? {}).map(([name, value]) => ({ name, value })),
      success_mode: storedSuccess ? "json_boolean" : "",
      success_field: storedSuccess?.field ?? "",
      success_expected: (storedSuccess?.expected === undefined || storedSuccess.expected === null
        ? ""
        : String(storedSuccess.expected)) as SubscriptionValues["success_expected"],
      success_diagnostic_field: storedSuccess?.diagnostic_field ?? "",
      success_max_body_bytes:
        storedSuccess?.max_body_bytes === undefined || storedSuccess.max_body_bytes === null
          ? ""
          : String(storedSuccess.max_body_bytes),
      description: subscription?.description ?? "",
    },
  });
  const headerRows = useFieldArray({ control: form.control, name: "headers" });
  const expression = form.watch("mapping");
  const successMode = form.watch("success_mode");
  // Delivery runs the mapping even when the request carries no body, so a mapping there could only
  // fail a delivery. A request without a body is saved without one.
  const mapsBody = form.watch("body") === "json";
  const mappingChanged = mapsBody && !rawMapping && expression !== originalExpression;
  const mappingReviewed = !mappingChanged || reviewedExpression === expression;

  const save = useMutation({
    mutationFn: (values: SubscriptionValues) => {
      const httpDelivery: HttpDelivery = {
        version: subscription?.http_delivery.version ?? currentHttpDeliveryVersion,
        method: values.method,
        path: values.path || null,
        headers: Object.fromEntries(values.headers.map((header) => [header.name.trim(), header.value])),
        body: values.body,
      };
      const requestBody = {
        name: values.name,
        match_rules: { event_type: values.event_type.trim() },
        destination_id: values.destination_id,
        mapping: !mapsBody ? null : rawMapping ? parseJson(values.raw_mapping).value : mappingEnvelope(values.mapping),
        http_delivery: httpDelivery,
        http_success: httpSuccess(values),
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

  const submit = form.handleSubmit((values) => {
    if (mapsBody && rawMapping && !values.raw_mapping.trim()) {
      form.setError("raw_mapping", { type: "validate", message: "Enter the stored mapping document." });
      return;
    }
    save.mutate(values, {
      onError: (failure) => {
        applyProblem(form, failure, formFields);
        const matchRulesError = fieldError(asProblem(failure), "match_rules");
        if (matchRulesError) form.setError("event_type", { type: "server", message: matchRulesError });
      },
    });
  });

  return (
    <Form {...form}>
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
        <TextField control={form.control} name="description" label="Description (optional)" />
        <Section title="Routing" hint="Which accepted Events this Subscription delivers, and where they go.">
          <SelectField
            control={form.control}
            name="destination_id"
            label="Destination"
            hint={
              noDestinations ? (
                <>
                  No active Destinations yet, and a Subscription delivers to one.{" "}
                  <Link to={`/tenants/${tenantId}/destinations`}>Create a Destination</Link> first.
                </>
              ) : destinations.data?.next_cursor ? (
                "Showing the first 100 active Destinations."
              ) : undefined
            }
            disabled={destinationOptionsUnavailable || noDestinations}
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
            hint="Match the event_type a Source on this Topic produces. Only Events with this exact type follow this delivery path."
            required
          />
        </Section>
        <Section title="HTTP request" hint="The operation this Subscription sends to its Destination.">
          <SelectField control={form.control} name="method" label="Method" required>
            {["POST", "PUT", "PATCH", "DELETE"].map((verb) => (
              <SelectItem key={verb} value={verb}>
                {verb}
              </SelectItem>
            ))}
          </SelectField>
          <TextField
            control={form.control}
            name="path"
            label="Relative path (optional)"
            hint="Appended to the Destination base URI. A query string is allowed."
          />
          <fieldset className="m-0 flex min-w-0 flex-col gap-3 border-0 p-0">
            <legend className="text-sm font-medium">Request headers</legend>
            <p className="m-0 text-xs text-ink-secondary">
              Add operation-specific headers. Destination authentication supplies its own headers.
            </p>
            {headerRows.fields.map((row, index) => (
              <div
                key={row.id}
                className="grid min-w-0 grid-cols-1 gap-2 sm:grid-cols-[minmax(0,1fr)_minmax(0,1fr)_auto]"
              >
                <TextField
                  control={form.control}
                  name={`headers.${index}.name`}
                  label={`Header ${index + 1} name`}
                  className="font-mono text-sm"
                  required
                />
                <TextField control={form.control} name={`headers.${index}.value`} label={`Header ${index + 1} value`} />
                <Button
                  type="button"
                  variant="ghost"
                  size="icon"
                  className="sm:mt-6"
                  aria-label={`Remove header ${index + 1}`}
                  onClick={() => headerRows.remove(index)}
                >
                  <X aria-hidden="true" className="size-4" />
                </Button>
              </div>
            ))}
            <Button
              type="button"
              variant="outline"
              size="sm"
              className="self-start"
              disabled={headerRows.fields.length >= 32}
              onClick={() => headerRows.append({ name: "", value: "" })}
            >
              Add header
            </Button>
          </fieldset>
          <SelectField control={form.control} name="body" label="Request body" required>
            <SelectItem value="json">Mapped Event JSON</SelectItem>
            <SelectItem value="none">No body</SelectItem>
          </SelectField>
          {!mapsBody ? null : rawMapping ? (
            <TextAreaField
              control={form.control}
              name="raw_mapping"
              label="Raw mapping (JSON)"
              hint="This stored mapping cannot round-trip through the Playground. Saving replaces it exactly."
              language="json"
              className="min-h-40"
              required
            />
          ) : (
            <>
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
                originalExpression={originalExpression ?? ""}
                open={playgroundOpen}
                onOpenChange={setPlaygroundOpen}
                onReviewInvalidated={() => setReviewedExpression(undefined)}
                onConfirmed={setReviewedExpression}
              />
            </>
          )}
        </Section>

        <Section title="Response success" hint="How a successful HTTP response is recognized for this operation.">
          <SelectField
            control={form.control}
            name="success_mode"
            label="Success check"
            emptyLabel="Any HTTP 2xx response"
          >
            <SelectItem value="json_boolean">Response JSON boolean</SelectItem>
          </SelectField>
          {successMode === "json_boolean" ? (
            <>
              <TextField
                control={form.control}
                name="success_field"
                label="Boolean field"
                hint="Top-level response JSON field that signals success."
                required
              />
              <SelectField control={form.control} name="success_expected" label="Expected value" required>
                <SelectItem value="true">True</SelectItem>
                <SelectItem value="false">False</SelectItem>
              </SelectField>
              <TextField
                control={form.control}
                name="success_diagnostic_field"
                label="Diagnostic field (optional)"
                hint="Top-level response field used when the operation reports failure."
              />
              <TextField
                control={form.control}
                name="success_max_body_bytes"
                label="Maximum response bytes (optional)"
                hint="Defaults to 65536. Allowed range: 1 through 1048576."
                type="number"
                min={1}
                max={1_048_576}
                step={1}
              />
            </>
          ) : null}
        </Section>

        <Button
          type="submit"
          className="self-start"
          disabled={save.isPending || destinationOptionsUnavailable || noDestinations || !mappingReviewed}
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
    </Form>
  );
}
