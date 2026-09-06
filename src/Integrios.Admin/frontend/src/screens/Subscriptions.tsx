import { zodResolver } from "@hookform/resolvers/zod";
import { useInfiniteQuery, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useForm } from "react-hook-form";
import { Link, useNavigate } from "react-router";
import { z } from "zod";
import { Button } from "@/components/ui/button";
import { TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import { api } from "../api/client";
import { formError } from "../api/problem";
import { asProblem, call, nextCursor } from "../api/query";
import type { components } from "../api/schema";
import { ConfirmAction, Disclosure, FilterBar, FormError, ListStatus, LoadMore, WriteStatus } from "../ui/controls";
import { Filter, Form, SelectField, TextAreaField, TextField } from "../ui/fields";
import { useFilterParam } from "../ui/filters";
import { applyProblem } from "../ui/formProblem";
import { formatJson, parseJson } from "../ui/json";
import { Details, Page, PageHeader, Panel, RowHeader, TableCard } from "../ui/layout";
import { StatusBadge } from "../ui/status";
import { TransformPreview } from "./Previews";

type SubscriptionListItem = components["schemas"]["SubscriptionListItemDto"];
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

export function SubscriptionsSection({
  tenantId,
  topicId,
  topicName,
}: {
  tenantId: string;
  topicId: string;
  topicName: string;
}) {
  const [status, setStatus] = useFilterParam("status");
  const navigate = useNavigate();
  const list = useInfiniteQuery({
    queryKey: ["subscriptions", tenantId, topicId, { status }],
    queryFn: ({ pageParam }) =>
      call(() =>
        api.GET("/admin/tenants/{tenantId}/topics/{topicId}/subscriptions", {
          params: {
            path: { tenantId, topicId },
            query: { status: status || undefined, after: pageParam ?? undefined, limit: 20 },
          },
        }),
      ),
    initialPageParam: null as string | null,
    getNextPageParam: nextCursor<SubscriptionListItem>,
  });
  const subscriptions = list.data?.pages.flatMap((page) => page.items) ?? [];

  return (
    <section className="flex flex-col gap-6">
      <h2>Subscriptions on {topicName}</h2>
      <Disclosure label="New Subscription">
        <SubscriptionForm
          tenantId={tenantId}
          topicId={topicId}
          onSaved={(created) => {
            if (created) navigate(`/tenants/${tenantId}/topics/${topicId}/subscriptions/${created.id}`);
          }}
        />
      </Disclosure>

      <div className="flex flex-col gap-4">
        <h3>All Subscriptions</h3>
        <FilterBar applied={(status ? 1 : 0) as number}>
          <Filter id="subscription-status" label="Status" value={status} onChange={setStatus}>
            <option value="">Any status</option>
            <option value="active">Active</option>
            <option value="disabled">Disabled</option>
          </Filter>
        </FilterBar>

        <ListStatus
          busy={list.isFetching}
          loaded={list.isSuccess}
          problem={asProblem(list.error)}
          empty={subscriptions.length === 0}
          emptyText={`${topicName} has no Subscriptions matching this filter.`}
        />
        {subscriptions.length > 0 ? (
          <TableCard caption={`Subscriptions on ${topicName}, newest first`}>
            <TableHeader>
              <TableRow>
                <TableHead scope="col">Name</TableHead>
                <TableHead scope="col">Status</TableHead>
                <TableHead scope="col">Order</TableHead>
                <TableHead scope="col">Description</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {subscriptions.map((subscription) => (
                <TableRow key={subscription.id}>
                  <RowHeader>
                    <Link
                      className="underline"
                      to={`/tenants/${tenantId}/topics/${topicId}/subscriptions/${subscription.id}`}
                    >
                      {subscription.name}
                    </Link>
                  </RowHeader>
                  <TableCell>
                    <StatusBadge status={subscription.status} />
                  </TableCell>
                  <TableCell>{subscription.order_index}</TableCell>
                  <TableCell>{subscription.description ?? "—"}</TableCell>
                </TableRow>
              ))}
            </TableBody>
          </TableCard>
        ) : null}
        <LoadMore hasMore={list.hasNextPage} busy={list.isFetching} onLoadMore={() => void list.fetchNextPage()} />
      </div>

      <TransformPreview />
    </section>
  );
}

export function SubscriptionScreen({
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
    },
  });

  const problem = asProblem(subscription.error);
  if (problem)
    return (
      <>
        <h1>Subscription</h1>
        <p role="alert">{problem.detail ?? `This Subscription could not be read (${problem.status}).`}</p>
      </>
    );
  if (!subscription.data) return <p>Loading…</p>;

  const current = subscription.data;
  return (
    <Page>
      <PageHeader title={current.name}>
        On{" "}
        <Link className="underline" to={`/tenants/${tenantId}/topics/${topicId}`}>
          its Topic
        </Link>
        .
      </PageHeader>

      <Panel>
        <Details>
          <dt>Status</dt>
          <dd>
            <StatusBadge status={current.status} />
          </dd>
          <dt>Order</dt>
          <dd>{current.order_index}</dd>
          <dt>Destination Connection</dt>
          <dd>
            <Link
              className="font-mono text-sm underline"
              to={`/tenants/${tenantId}/connections/${current.destination_connection_id}`}
            >
              {current.destination_connection_id}
            </Link>
          </dd>
        </Details>
      </Panel>

      <SubscriptionForm key={current.updated_at} tenantId={tenantId} topicId={topicId} subscription={current} />

      <TransformPreview />

      <div className="flex flex-col gap-3">
        <WriteStatus done={deactivate.isSuccess}>Subscription deactivated.</WriteStatus>
        <FormError message={formError(asProblem(deactivate.error))} />
        {current.status === "active" ? (
          <ConfirmAction
            label="Deactivate Subscription"
            question={`Deactivate the Subscription "${current.name}"? It stops receiving Events from this Topic.`}
            confirmLabel={`Deactivate ${current.name}`}
            busy={deactivate.isPending}
            onConfirm={() => deactivate.mutate()}
          />
        ) : null}
      </div>
    </Page>
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
  const connections = useQuery({
    queryKey: ["connection-options", tenantId],
    queryFn: () =>
      call(() =>
        api.GET("/admin/tenants/{tenantId}/connections", {
          params: { path: { tenantId }, query: { status: "active", limit: 100 } },
        }),
      ),
  });
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
        <form className="flex flex-col gap-4" onSubmit={submit}>
          <h3>{subscription ? `Edit ${subscription.name}` : "Create a Subscription"}</h3>
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
            <option value="">Choose a Connection</option>
            {(connections.data?.items ?? []).map((connection) => (
              <option key={connection.id} value={connection.id}>
                {connection.name}
              </option>
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
                <option key={verb} value={verb}>
                  {verb}
                </option>
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
