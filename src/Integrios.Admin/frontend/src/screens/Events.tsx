import { useRef, useState } from "react";
import { api } from "../api/client";
import { formError } from "../api/problem";
import type { components } from "../api/schema";
import { ConfirmAction, Field, FormError, Link, ListStatus, LoadMore, fieldProps } from "../ui/controls";
import { useAction } from "../ui/useAction";
import { useCursorList } from "../ui/useCursorList";
import { useOptions } from "../ui/useOptions";
import { useResource } from "../ui/useResource";

type EventListItem = components["schemas"]["EventListItemDto"];
type EventDetail = components["schemas"]["EventDto"];
type EventDelivery = components["schemas"]["EventDeliveryDto"];
type SourceListItem = components["schemas"]["SourceListItemDto"];
type Topic = components["schemas"]["AdminTopicResponse"];

const eventStatuses = ["accepted", "processing", "routed", "unrouted", "failed", "dead_lettered"];
const deliveryStatuses = ["pending", "in_flight", "succeeded", "dead_lettered"];

type Filters = {
  status: string;
  deliveryStatus: string;
  sourceId: string;
  topicId: string;
  sourceEventId: string;
  acceptedFrom: string;
  acceptedTo: string;
};

const noFilters: Filters = {
  status: "",
  deliveryStatus: "",
  sourceId: "",
  topicId: "",
  sourceEventId: "",
  acceptedFrom: "",
  acceptedTo: "",
};

/// A local datetime-local value carries no offset, so it is sent as an instant the server can read
/// unambiguously rather than as the browser's own wall clock.
function instant(value: string): string | undefined {
  return value ? new Date(value).toISOString() : undefined;
}

export function EventsScreen({ tenantId }: { tenantId: string }) {
  // The applied filters are separate from what is being typed: a source Event identity is a free
  // text field, and re-reading the list on every keystroke would restart the cursor each time.
  const [draft, setDraft] = useState<Filters>(noFilters);
  const [applied, setApplied] = useState<Filters>(noFilters);

  const sources = useOptions<SourceListItem>(
    () => api.GET("/admin/tenants/{tenantId}/sources", { params: { path: { tenantId }, query: { limit: 100 } } }),
    `source-options|${tenantId}`,
  );
  const topics = useOptions<Topic>(
    () => api.GET("/admin/tenants/{tenantId}/topics", { params: { path: { tenantId }, query: { limit: 100 } } }),
    `topic-options|${tenantId}`,
  );

  const list = useCursorList<EventListItem>(
    (after) =>
      api.GET("/admin/tenants/{tenantId}/events", {
        params: {
          path: { tenantId },
          query: {
            status: applied.status || undefined,
            delivery_status: applied.deliveryStatus || undefined,
            source_id: applied.sourceId || undefined,
            topic_id: applied.topicId || undefined,
            source_event_id: applied.sourceEventId || undefined,
            accepted_from: instant(applied.acceptedFrom),
            accepted_to: instant(applied.acceptedTo),
            after: after ?? undefined,
            limit: 20,
          },
        },
      }),
    `events|${tenantId}|${JSON.stringify(applied)}`,
  );

  const set = (patch: Partial<Filters>) => setDraft((previous) => ({ ...previous, ...patch }));

  return (
    <>
      <h1>Events</h1>
      <p>
        In <Link to={`/tenants/${tenantId}`}>this Tenant</Link>.
      </p>

      <form
        onSubmit={(event) => {
          event.preventDefault();
          setApplied(draft);
        }}
      >
        <h2>Find an Event</h2>
        <FormError message={formError(sources.problem ?? topics.problem)} />
        <Field id="event-status" label="Event status" hint="How far the Event itself got.">
          <select
            {...fieldProps("event-status", undefined, true)}
            value={draft.status}
            onChange={(event) => set({ status: event.target.value })}
          >
            <option value="">Any Event status</option>
            {eventStatuses.map((status) => (
              <option key={status} value={status}>
                {status}
              </option>
            ))}
          </select>
        </Field>
        {/* Delivery status is a separate filter over Delivery state. An Event matches when one of
            its EventDeliveries is in that state; the Event's own status is untouched by it. */}
        <Field
          id="event-delivery-status"
          label="Delivery status"
          hint="Matches Events with at least one EventDelivery in this state."
        >
          <select
            {...fieldProps("event-delivery-status", undefined, true)}
            value={draft.deliveryStatus}
            onChange={(event) => set({ deliveryStatus: event.target.value })}
          >
            <option value="">Any Delivery status</option>
            {deliveryStatuses.map((status) => (
              <option key={status} value={status}>
                {status}
              </option>
            ))}
          </select>
        </Field>
        <Field
          id="event-source"
          label="Source"
          hint={sources.truncated ? "Showing the first 100 Sources." : undefined}
        >
          <select
            {...fieldProps("event-source", undefined, sources.truncated)}
            value={draft.sourceId}
            onChange={(event) => set({ sourceId: event.target.value })}
            disabled={sources.busy || sources.problem !== null}
          >
            <option value="">Any Source</option>
            {sources.items.map((source) => (
              <option key={source.id} value={source.id}>
                {source.type} · {source.id}
              </option>
            ))}
          </select>
        </Field>
        <Field id="event-topic" label="Topic" hint={topics.truncated ? "Showing the first 100 Topics." : undefined}>
          <select
            {...fieldProps("event-topic", undefined, topics.truncated)}
            value={draft.topicId}
            onChange={(event) => set({ topicId: event.target.value })}
            disabled={topics.busy || topics.problem !== null}
          >
            <option value="">Any Topic</option>
            {topics.items.map((topic) => (
              <option key={topic.id} value={topic.id}>
                {topic.name}
              </option>
            ))}
          </select>
        </Field>
        <Field
          id="event-source-event-id"
          label="Source Event id"
          hint="The identity the sending system gave the Event. Matched exactly."
        >
          <input
            {...fieldProps("event-source-event-id", undefined, true)}
            value={draft.sourceEventId}
            onChange={(event) => set({ sourceEventId: event.target.value })}
          />
        </Field>
        <Field id="event-accepted-from" label="Accepted from">
          <input
            {...fieldProps("event-accepted-from")}
            type="datetime-local"
            value={draft.acceptedFrom}
            onChange={(event) => set({ acceptedFrom: event.target.value })}
          />
        </Field>
        <Field id="event-accepted-to" label="Accepted to">
          <input
            {...fieldProps("event-accepted-to")}
            type="datetime-local"
            value={draft.acceptedTo}
            onChange={(event) => set({ acceptedTo: event.target.value })}
          />
        </Field>
        <button type="submit">Apply filters</button>
        <button
          type="button"
          onClick={() => {
            setDraft(noFilters);
            setApplied(noFilters);
          }}
        >
          Clear filters
        </button>
      </form>

      <ListStatus
        busy={list.busy}
        loaded={list.loaded}
        problem={list.problem}
        empty={list.items.length === 0}
        emptyText="No Events in this Tenant match these filters."
      />
      {list.items.length > 0 ? (
        <table>
          <caption>Events, newest accepted first</caption>
          <thead>
            <tr>
              <th scope="col">Accepted</th>
              <th scope="col">Type</th>
              <th scope="col">Source Event id</th>
              <th scope="col">Event status</th>
              <th scope="col">Deliveries</th>
            </tr>
          </thead>
          <tbody>
            {list.items.map((item) => (
              <tr key={item.event_id}>
                <th scope="row">
                  <Link to={`/tenants/${tenantId}/events/${item.event_id}`}>{item.accepted_at}</Link>
                </th>
                <td>{item.event_type}</td>
                <td>{item.source_event_id ?? "—"}</td>
                <td>{item.status}</td>
                <td>
                  <DeliveryCounts counts={item.deliveries} />
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      ) : null}
      <LoadMore cursor={list.cursor} busy={list.busy} onLoadMore={list.loadMore} />
    </>
  );
}

/// Delivery state is reported per state and always labelled as Delivery state. A dead-lettered
/// Delivery is never folded into the Event's own status, in this cell or anywhere else.
function DeliveryCounts({ counts }: { counts: components["schemas"]["EventDeliveryCounts"] }) {
  const states: [string, number | string][] = [
    ["pending", counts.pending],
    ["in flight", counts.in_flight],
    ["succeeded", counts.succeeded],
    ["dead-lettered", counts.dead_lettered],
  ];
  const present = states.filter(([, count]) => Number(count) > 0);

  if (present.length === 0) return <>No EventDeliveries</>;
  return <>{present.map(([label, count]) => `${count} ${label}`).join(", ")}</>;
}

export function EventScreen({ tenantId, eventId }: { tenantId: string; eventId: string }) {
  const event = useResource<EventDetail>(
    () =>
      api.GET("/admin/tenants/{tenantId}/events/{eventId}/deliveries", {
        params: { path: { tenantId, eventId } },
      }),
    `${tenantId}|${eventId}`,
  );

  if (event.problem)
    return (
      <>
        <h1>Event</h1>
        <p role="alert">{event.problem.detail ?? `This Event could not be read (${event.problem.status}).`}</p>
      </>
    );
  if (!event.data) return <p>Loading…</p>;

  const current = event.data;
  return (
    <>
      <h1>Event {current.event_id}</h1>
      <p>
        In <Link to={`/tenants/${tenantId}/events`}>this Tenant's Events</Link>.
      </p>
      <dl>
        <dt>Event status</dt>
        <dd>{current.status}</dd>
        <dt>Accepted</dt>
        <dd>{current.accepted_at}</dd>
        <dt>Processed</dt>
        <dd>{current.processed_at ?? "Not processed"}</dd>
        <dt>Failed</dt>
        <dd>{current.failed_at ?? "Not failed"}</dd>
      </dl>

      <TraceId traceId={current.trace_id ?? null} />

      <h2>EventDeliveries</h2>
      {current.event_deliveries?.length ? (
        <table>
          <caption>One EventDelivery per matched Subscription</caption>
          <thead>
            <tr>
              <th scope="col">Subscription</th>
              <th scope="col">Delivery status</th>
              <th scope="col">Attempts (lifetime / retry cycle)</th>
              <th scope="col">Deliver after</th>
              <th scope="col">Failed</th>
              <th scope="col">Recovery</th>
            </tr>
          </thead>
          <tbody>
            {current.event_deliveries.map((delivery) => (
              <tr key={delivery.event_delivery_id}>
                <th scope="row">{delivery.subscription_id}</th>
                <td>{delivery.status}</td>
                <td>
                  {delivery.lifetime_attempt_count} / {delivery.retry_cycle_attempt_count}
                </td>
                <td>{delivery.deliver_after ?? "—"}</td>
                <td>{delivery.failed_at ?? "—"}</td>
                <td>
                  <ReplayDelivery
                    tenantId={tenantId}
                    eventId={eventId}
                    delivery={delivery}
                    onReplayed={event.reload}
                  />
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      ) : (
        <p>This Event has no EventDeliveries.</p>
      )}

      <h2>Delivery attempts</h2>
      {current.delivery_attempts?.length ? (
        <table>
          <caption>Every attempt made against this Event's EventDeliveries</caption>
          <thead>
            <tr>
              <th scope="col">Started</th>
              <th scope="col">Attempt</th>
              <th scope="col">Subscription</th>
              <th scope="col">Outcome</th>
              <th scope="col">Failure phase</th>
              <th scope="col">Response</th>
              <th scope="col">Error</th>
            </tr>
          </thead>
          <tbody>
            {current.delivery_attempts.map((attempt) => (
              <tr key={attempt.attempt_id}>
                <th scope="row">{attempt.started_at}</th>
                <td>{attempt.attempt_number}</td>
                <td>{attempt.subscription_id}</td>
                <td>{attempt.status}</td>
                <td>{attempt.failure_phase ?? "—"}</td>
                <td>{attempt.response_status_code ?? "—"}</td>
                <td>{attempt.error_message ?? "—"}</td>
              </tr>
            ))}
          </tbody>
        </table>
      ) : (
        <p>No delivery attempts have been made for this Event.</p>
      )}
    </>
  );
}

/// The trace identity is an opaque lookup value for whatever observability backend the deployment
/// runs. The dashboard hands it over and does not know, link to, or embed that backend.
function TraceId({ traceId }: { traceId: string | null }) {
  const [copied, setCopied] = useState(false);
  const field = useRef<HTMLInputElement>(null);

  if (!traceId) return <p>This Event carries no trace identity.</p>;

  return (
    <p>
      <label htmlFor="event-trace-id">Trace id</label>
      <input id="event-trace-id" ref={field} readOnly value={traceId} size={40} />
      <button
        type="button"
        onClick={() => {
          // Clipboard access can be unavailable or refused; selecting the value still lets the
          // Operator copy it, so the control is never a dead end.
          field.current?.select();
          void navigator.clipboard
            ?.writeText(traceId)
            .then(() => setCopied(true))
            .catch(() => setCopied(false));
        }}
      >
        Copy trace id
      </button>
      <span role="status">{copied ? "Trace id copied." : ""}</span>
    </p>
  );
}

/// Replay is the recovery action the API already owns, and it owns it only for a dead-lettered
/// EventDelivery. Offering it on any other state would invent a recovery the domain does not have.
function ReplayDelivery({
  tenantId,
  eventId,
  delivery,
  onReplayed,
}: {
  tenantId: string;
  eventId: string;
  delivery: EventDelivery;
  onReplayed: () => void;
}) {
  const { busy, problem, run } = useAction();

  if (delivery.status !== "dead_lettered") return <span>—</span>;

  return (
    <>
      <ConfirmAction
        label="Replay"
        question={`Replay the dead-lettered delivery to Subscription ${delivery.subscription_id}? It is queued for delivery again.`}
        confirmLabel="Replay this delivery"
        busy={busy}
        onConfirm={() =>
          void run(
            () =>
              api.POST("/admin/tenants/{tenantId}/events/{eventId}/deliveries/{deliveryId}/replay", {
                params: { path: { tenantId, eventId, deliveryId: delivery.event_delivery_id } },
              }),
            onReplayed,
          )
        }
      />
      <FormError message={formError(problem)} />
    </>
  );
}
