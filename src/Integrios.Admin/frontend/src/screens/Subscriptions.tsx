import { zodResolver } from "@hookform/resolvers/zod";
import { useInfiniteQuery, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
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
import { activeOnly, useConnectionOptions, useTopicOptions } from "../ui/options";
import { StatusBadge } from "../ui/status";
import { TransformPreview } from "./Previews";

type SubscriptionByTenantListItem = components["schemas"]["SubscriptionByTenantListItemDto"];
type Subscription = components["schemas"]["SubscriptionDto"];
type HttpDelivery = components["schemas"]["HttpDeliveryConfiguration"];

const writeFields = [
  "name",
  "match_rules",
  "destination_connection_id",
  "mapping",
  "http_delivery",
  "order_index",
  "description",
] as const;

/// The rows the form itself renders. `http_delivery` is not one of them: the server names the whole
/// delivery configuration, which is spread across four controls here, so its message stays at form
/// level rather than being attached to an arbitrary one of them.
const formFields = [
  "name",
  "destination_connection_id",
  "order_index",
  "match_rules",
  "mapping",
  "description",
] as const;

/// The version the dashboard authors. The server owns the meaning of each version, so an existing
/// Subscription keeps whatever version it already carries rather than being silently upgraded.
const currentHttpDeliveryVersion = 1;

const jsonDocument = z.string().superRefine((text, ctx) => {
  const parsed = parseJson(text);
  if (parsed.error !== undefined) ctx.addIssue({ code: z.ZodIssueCode.custom, message: parsed.error });
});

/// An empty mapping is a real choice: it means the Event is delivered unmapped.
const optionalJsonDocument = z.string().superRefine((text, ctx) => {
  if (text.trim() === "") return;
  const parsed = parseJson(text);
  if (parsed.error !== undefined) ctx.addIssue({ code: z.ZodIssueCode.custom, message: parsed.error });
});

const subscriptionSchema = z.object({
  name: z.string().trim().min(1, "Enter a name."),
  destination_connection_id: z.string().min(1, "Choose a Connection."),
  order_index: z.string().regex(/^-?\d+$/, "Enter a whole number."),
  match_rules: jsonDocument,
  mapping: optionalJsonDocument,
  method: z.string().min(1),
  path: z.string(),
  body: z.string().min(1, "Enter a body format."),
  headers: jsonDocument,
  description: z.string(),
});

type SubscriptionValues = z.infer<typeof subscriptionSchema>;

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
  const [connectionId, setConnectionId] = useFilterParam("connection_id");
  const [status, setStatus] = useFilterParam("status");
  const topics = useTopicOptions(tenantId);
  const connections = useConnectionOptions(tenantId);
  const applied = [name, topicId, connectionId, status].filter(Boolean).length;
  const list = useInfiniteQuery({
    queryKey: ["tenant-subscriptions", tenantId, { name, topicId, connectionId, status }],
    queryFn: ({ pageParam }) =>
      call(() =>
        api.GET("/admin/tenants/{tenantId}/subscriptions", {
          params: {
            path: { tenantId },
            query: {
              name: name || undefined,
              topic_id: topicId || undefined,
              connection_id: connectionId || undefined,
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
        Tenant-wide routes from Topics to destination Connections.
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
          id="subscription-connection"
          label="Connection"
          value={connectionId}
          onChange={setConnectionId}
          hint={connections.data?.next_cursor ? "Showing the first 100 Connections." : undefined}
        >
          {(connections.data?.items ?? []).map((connection) => (
            <SelectItem key={connection.id} value={connection.id}>
              {connection.name}
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
                  <TableHead scope="col">Destination Connection</TableHead>
                  <TableHead scope="col">Status</TableHead>
                  <TableHead scope="col" className="text-right">
                    Order
                  </TableHead>
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
                        to={`/tenants/${tenantId}/connections/${subscription.destination_connection_id}`}
                      >
                        {subscription.destination_connection_name}
                      </Link>
                    </TableCell>
                    <TableCell>
                      <StatusBadge status={subscription.status} />
                    </TableCell>
                    <TableCell className="text-right">{subscription.order_index}</TableCell>
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

      {/* The mapping sandbox follows the list it belongs to rather than sitting inside a
          Subscription: it evaluates a transform against a sample document and persists nothing, so
          it is a tool for authoring any Subscription here, not detail about the selected one. */}
      <TransformPreview />
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
  return (
    <Inspector label="Subscription detail">
      <div className="flex items-start justify-between gap-3">
        <div className="min-w-0">
          <h2>{current.name}</h2>
          <span className="block font-mono text-xs break-all text-ink-secondary">{current.id}</span>
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
        <dt>Destination Connection</dt>
        <dd>
          <Link to={`/tenants/${tenantId}/connections/${current.destination_connection_id}`}>Open Connection</Link>
        </dd>
        <dt>Order</dt>
        <dd>{current.order_index}</dd>
        <dt>Description</dt>
        <dd>{current.description ?? "—"}</dd>
      </Details>

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

/// One form for both create and update: the Admin API takes the same body for each, so splitting it
/// into two near-identical forms would only invite them to drift apart.
function SubscriptionForm({
  tenantId,
  topicId,
  subscription,
  onSaved,
}: {
  tenantId: string;
  topicId: string;
  subscription?: Subscription;
  onSaved?: (saved: Subscription | undefined) => void;
}) {
  const queryClient = useQueryClient();
  const connections = useConnectionOptions(tenantId);
  const connectionOptionsUnavailable = connections.isPending || connections.isError;

  const form = useForm<SubscriptionValues>({
    resolver: zodResolver(subscriptionSchema),
    defaultValues: {
      name: subscription?.name ?? "",
      destination_connection_id: subscription?.destination_connection_id ?? "",
      order_index: String(subscription?.order_index ?? 0),
      match_rules: formatJson(subscription?.match_rules) || "{}",
      mapping: formatJson(subscription?.mapping_config),
      method: subscription?.http_delivery.method ?? "POST",
      path: subscription?.http_delivery.path ?? "",
      body: subscription?.http_delivery.body ?? "json",
      headers: formatJson(subscription?.http_delivery.headers) || "{}",
      description: subscription?.description ?? "",
    },
  });

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
        match_rules: parseJson(values.match_rules).value,
        destination_connection_id: values.destination_connection_id,
        mapping: values.mapping.trim() === "" ? null : parseJson(values.mapping).value,
        http_delivery: httpDelivery,
        order_index: Number(values.order_index),
        description: values.description.trim() || null,
      };

      return call(() =>
        subscription
          ? api.PATCH("/admin/tenants/{tenantId}/topics/{topicId}/subscriptions/{id}", {
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
    save.mutate(values, { onError: (failure) => applyProblem(form, failure, formFields) }),
  );

  return (
    <Form {...form}>
      <Panel asChild>
        <form
          className="flex flex-col gap-4"
          aria-label={subscription ? `Edit ${subscription.name}` : "Create a Subscription"}
          onSubmit={submit}
        >
          {/* Both paths open in a sheet that carries the title, so the form states its name rather
              than repeating a heading under one. */}
          <FormError message={formError(asProblem(connections.error))} />
          <FormError message={formError(asProblem(save.error), writeFields)} />

          <TextField control={form.control} name="name" label="Name" required />
          <SelectField
            control={form.control}
            name="destination_connection_id"
            label="Destination Connection"
            hint={connections.data?.next_cursor ? "Showing the first 100 active Connections." : undefined}
            disabled={connectionOptionsUnavailable}
            required
          >
            {activeOnly(connections.data?.items).map((connection) => (
              <SelectItem key={connection.id} value={connection.id}>
                {connection.name}
              </SelectItem>
            ))}
          </SelectField>
          <TextField
            control={form.control}
            name="order_index"
            label="Order"
            hint="Lower numbers are delivered first."
            type="number"
            step={1}
            required
          />
          <TextAreaField
            control={form.control}
            name="match_rules"
            label="Match rules (JSON)"
            className="min-h-32 font-mono text-sm"
            required
          />
          <TextAreaField
            control={form.control}
            name="mapping"
            label="Mapping (JSON, optional)"
            hint="Leave empty to deliver the Event unmapped."
            className="min-h-32 font-mono text-sm"
          />

          <fieldset className="flex flex-col gap-4 rounded-md border p-4">
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
          </fieldset>

          <TextField control={form.control} name="description" label="Description (optional)" />

          <Button type="submit" className="self-start" disabled={save.isPending || connectionOptionsUnavailable}>
            {subscription ? "Save changes" : "Create Subscription"}
          </Button>
        </form>
      </Panel>
    </Form>
  );
}
