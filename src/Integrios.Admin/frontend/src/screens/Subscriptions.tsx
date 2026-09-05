import { useState } from "react";
import { Link, useNavigate } from "react-router";
import { api } from "../api/client";
import { fieldError, formError } from "../api/problem";
import type { components } from "../api/schema";
import { ConfirmAction, Disclosure, Field, FormError, fieldProps, ListStatus, LoadMore } from "../ui/controls";
import { formatJson, parseJson } from "../ui/json";
import { useAction } from "../ui/useAction";
import { useCursorList } from "../ui/useCursorList";
import { useOptions } from "../ui/useOptions";
import { useResource } from "../ui/useResource";
import { TransformPreview } from "./Previews";

type SubscriptionListItem = components["schemas"]["SubscriptionListItemDto"];
type Subscription = components["schemas"]["SubscriptionDto"];
type HttpDelivery = components["schemas"]["HttpDeliveryConfiguration"];
type ConnectionListItem = components["schemas"]["ConnectionListItemDto"];

const writeFields = [
  "name",
  "match_rules",
  "destination_connection_id",
  "mapping",
  "http_delivery",
  "order_index",
  "description",
];

/// The version the dashboard authors. The server owns the meaning of each version, so an existing
/// Subscription keeps whatever version it already carries rather than being silently upgraded.
const currentHttpDeliveryVersion = 1;

export function SubscriptionsSection({
  tenantId,
  topicId,
  topicName,
}: {
  tenantId: string;
  topicId: string;
  topicName: string;
}) {
  const [status, setStatus] = useState("");
  const navigate = useNavigate();
  const list = useCursorList<SubscriptionListItem>(
    (after) =>
      api.GET("/admin/tenants/{tenantId}/topics/{topicId}/subscriptions", {
        params: {
          path: { tenantId, topicId },
          query: { status: status || undefined, after: after ?? undefined, limit: 20 },
        },
      }),
    `subscriptions|${tenantId}|${topicId}|${status}`,
  );

  return (
    <>
      <h2>Subscriptions on {topicName}</h2>
      <Disclosure label="New Subscription">
        <SubscriptionForm
          tenantId={tenantId}
          topicId={topicId}
          onSaved={(created) => {
            list.reload();
            if (created) navigate(`/tenants/${tenantId}/topics/${topicId}/subscriptions/${created.id}`);
          }}
        />
      </Disclosure>

      <h3>All Subscriptions</h3>
      <Field id="subscription-status" label="Status">
        <select
          {...fieldProps("subscription-status")}
          value={status}
          onChange={(event) => setStatus(event.target.value)}
        >
          <option value="">Any status</option>
          <option value="active">Active</option>
          <option value="disabled">Disabled</option>
        </select>
      </Field>

      <ListStatus
        busy={list.busy}
        loaded={list.loaded}
        problem={list.problem}
        empty={list.items.length === 0}
        emptyText={`${topicName} has no Subscriptions matching this filter.`}
      />
      {list.items.length > 0 ? (
        <div className="table-card">
          <table>
            <caption>Subscriptions on {topicName}, newest first</caption>
            <thead>
              <tr>
                <th scope="col">Name</th>
                <th scope="col">Status</th>
                <th scope="col">Order</th>
                <th scope="col">Description</th>
              </tr>
            </thead>
            <tbody>
              {list.items.map((subscription) => (
                <tr key={subscription.id}>
                  <th scope="row">
                    <Link to={`/tenants/${tenantId}/topics/${topicId}/subscriptions/${subscription.id}`}>
                      {subscription.name}
                    </Link>
                  </th>
                  <td>{subscription.status}</td>
                  <td>{subscription.order_index}</td>
                  <td>{subscription.description ?? "—"}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      ) : null}
      <LoadMore cursor={list.cursor} busy={list.busy} onLoadMore={list.loadMore} />

      <TransformPreview />
    </>
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
  const subscription = useResource<Subscription>(
    () =>
      api.GET("/admin/tenants/{tenantId}/topics/{topicId}/subscriptions/{id}", {
        params: { path: { tenantId, topicId, id: subscriptionId } },
      }),
    `${tenantId}|${topicId}|${subscriptionId}`,
  );
  const { busy, problem, run } = useAction();

  if (subscription.problem)
    return (
      <>
        <h1>Subscription</h1>
        <p role="alert">
          {subscription.problem.detail ?? `This Subscription could not be read (${subscription.problem.status}).`}
        </p>
      </>
    );
  if (!subscription.data) return <p>Loading…</p>;

  const current = subscription.data;
  return (
    <>
      <h1>{current.name}</h1>
      <p>
        On <Link to={`/tenants/${tenantId}/topics/${topicId}`}>its Topic</Link>.
      </p>
      <dl>
        <dt>Status</dt>
        <dd>{current.status}</dd>
        <dt>Order</dt>
        <dd>{current.order_index}</dd>
        <dt>Destination Connection</dt>
        <dd>
          <Link to={`/tenants/${tenantId}/connections/${current.destination_connection_id}`}>
            {current.destination_connection_id}
          </Link>
        </dd>
      </dl>

      <SubscriptionForm
        key={current.updated_at}
        tenantId={tenantId}
        topicId={topicId}
        subscription={current}
        onSaved={subscription.reload}
      />

      <TransformPreview />

      <FormError message={formError(problem)} />
      {current.status === "active" ? (
        <ConfirmAction
          label="Deactivate Subscription"
          question={`Deactivate the Subscription "${current.name}"? It stops receiving Events from this Topic.`}
          confirmLabel={`Deactivate ${current.name}`}
          busy={busy}
          onConfirm={() =>
            void run(
              () =>
                api.POST("/admin/tenants/{tenantId}/topics/{topicId}/subscriptions/{id}/deactivate", {
                  params: { path: { tenantId, topicId, id: current.id } },
                }),
              subscription.reload,
            )
          }
        />
      ) : null}
    </>
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
  onSaved: (saved: Subscription | undefined) => void;
}) {
  const connections = useOptions<ConnectionListItem>(
    () =>
      api.GET("/admin/tenants/{tenantId}/connections", {
        params: { path: { tenantId }, query: { status: "active", limit: 100 } },
      }),
    `connection-options|${tenantId}`,
  );

  const prefix = subscription ? "subscription" : "create-subscription";
  const [name, setName] = useState(subscription?.name ?? "");
  const [destination, setDestination] = useState(subscription?.destination_connection_id ?? "");
  const [orderIndex, setOrderIndex] = useState(String(subscription?.order_index ?? 0));
  const [description, setDescription] = useState(subscription?.description ?? "");
  const [matchRules, setMatchRules] = useState(() => formatJson(subscription?.match_rules) || "{}");
  const [matchRulesError, setMatchRulesError] = useState<string | undefined>(undefined);
  const [mapping, setMapping] = useState(() => formatJson(subscription?.mapping_config));
  const [mappingError, setMappingError] = useState<string | undefined>(undefined);
  const [method, setMethod] = useState(subscription?.http_delivery.method ?? "POST");
  const [path, setPath] = useState(subscription?.http_delivery.path ?? "");
  const [body, setBody] = useState(subscription?.http_delivery.body ?? "json");
  const [headers, setHeaders] = useState(() => formatJson(subscription?.http_delivery.headers) || "{}");
  const [headersError, setHeadersError] = useState<string | undefined>(undefined);
  const { busy, problem, run } = useAction();
  const connectionOptionsUnavailable = connections.busy || connections.problem !== null;

  return (
    <form
      onSubmit={(event) => {
        event.preventDefault();

        const parsedRules = parseJson(matchRules);
        setMatchRulesError(parsedRules.error);
        const parsedHeaders = parseJson(headers);
        setHeadersError(parsedHeaders.error);
        // An empty mapping is a real choice: it means the Event is delivered unmapped.
        const parsedMapping = mapping.trim() === "" ? { value: null } : parseJson(mapping);
        setMappingError(parsedMapping.error);
        if (parsedRules.error !== undefined || parsedHeaders.error !== undefined || parsedMapping.error !== undefined)
          return;

        const httpDelivery: HttpDelivery = {
          version: subscription?.http_delivery.version ?? currentHttpDeliveryVersion,
          method,
          path: path || null,
          headers: parsedHeaders.value as Record<string, string>,
          body,
        };
        const requestBody = {
          name,
          match_rules: parsedRules.value,
          destination_connection_id: destination,
          mapping: parsedMapping.value,
          http_delivery: httpDelivery,
          order_index: Number(orderIndex),
          description: description || null,
        };

        void run(
          () =>
            subscription
              ? api.PATCH("/admin/tenants/{tenantId}/topics/{topicId}/subscriptions/{id}", {
                  params: { path: { tenantId, topicId, id: subscription.id } },
                  body: requestBody,
                })
              : api.POST("/admin/tenants/{tenantId}/topics/{topicId}/subscriptions", {
                  params: { path: { tenantId, topicId } },
                  body: requestBody,
                }),
          onSaved,
        );
      }}
    >
      <h3>{subscription ? `Edit ${subscription.name}` : "Create a Subscription"}</h3>
      <FormError message={formError(connections.problem)} />
      <FormError message={formError(problem, writeFields)} />

      <Field id={`${prefix}-name`} label="Name" error={fieldError(problem, "name")}>
        <input
          {...fieldProps(`${prefix}-name`, fieldError(problem, "name"))}
          value={name}
          onChange={(event) => setName(event.target.value)}
          required
        />
      </Field>
      <Field
        id={`${prefix}-destination`}
        label="Destination Connection"
        error={fieldError(problem, "destination_connection_id")}
        hint={connections.truncated ? "Showing the first 100 active Connections." : undefined}
      >
        <select
          {...fieldProps(
            `${prefix}-destination`,
            fieldError(problem, "destination_connection_id"),
            connections.truncated,
          )}
          value={destination}
          onChange={(event) => setDestination(event.target.value)}
          disabled={connectionOptionsUnavailable}
          required
        >
          <option value="">Choose a Connection</option>
          {connections.items.map((connection) => (
            <option key={connection.id} value={connection.id}>
              {connection.name}
            </option>
          ))}
        </select>
      </Field>
      <Field
        id={`${prefix}-order`}
        label="Order"
        error={fieldError(problem, "order_index")}
        hint="Lower numbers are delivered first."
      >
        <input
          {...fieldProps(`${prefix}-order`, fieldError(problem, "order_index"), true)}
          type="number"
          step={1}
          value={orderIndex}
          onChange={(event) => setOrderIndex(event.target.value)}
          required
        />
      </Field>
      <Field
        id={`${prefix}-match-rules`}
        label="Match rules (JSON)"
        error={matchRulesError ?? fieldError(problem, "match_rules")}
      >
        <textarea
          {...fieldProps(`${prefix}-match-rules`, matchRulesError ?? fieldError(problem, "match_rules"))}
          rows={6}
          value={matchRules}
          onChange={(event) => setMatchRules(event.target.value)}
          required
        />
      </Field>
      <Field
        id={`${prefix}-mapping`}
        label="Mapping (JSON, optional)"
        error={mappingError ?? fieldError(problem, "mapping")}
        hint="Leave empty to deliver the Event unmapped."
      >
        <textarea
          {...fieldProps(`${prefix}-mapping`, mappingError ?? fieldError(problem, "mapping"), true)}
          rows={6}
          value={mapping}
          onChange={(event) => setMapping(event.target.value)}
        />
      </Field>

      <fieldset>
        <legend>HTTP delivery</legend>
        <Field id={`${prefix}-method`} label="Method">
          <select
            {...fieldProps(`${prefix}-method`)}
            value={method}
            onChange={(event) => setMethod(event.target.value)}
            required
          >
            {["POST", "PUT", "PATCH", "DELETE", "GET"].map((verb) => (
              <option key={verb} value={verb}>
                {verb}
              </option>
            ))}
          </select>
        </Field>
        <Field id={`${prefix}-path`} label="Path (optional)">
          <input {...fieldProps(`${prefix}-path`)} value={path} onChange={(event) => setPath(event.target.value)} />
        </Field>
        <Field id={`${prefix}-body`} label="Body">
          <input
            {...fieldProps(`${prefix}-body`)}
            value={body}
            onChange={(event) => setBody(event.target.value)}
            required
          />
        </Field>
        <Field id={`${prefix}-headers`} label="Headers (JSON object)" error={headersError}>
          <textarea
            {...fieldProps(`${prefix}-headers`, headersError)}
            rows={4}
            value={headers}
            onChange={(event) => setHeaders(event.target.value)}
            required
          />
        </Field>
      </fieldset>

      <Field id={`${prefix}-description`} label="Description (optional)" error={fieldError(problem, "description")}>
        <input
          {...fieldProps(`${prefix}-description`, fieldError(problem, "description"))}
          value={description}
          onChange={(event) => setDescription(event.target.value)}
        />
      </Field>
      <button type="submit" disabled={busy || connectionOptionsUnavailable}>
        {subscription ? "Save changes" : "Create Subscription"}
      </button>
    </form>
  );
}
