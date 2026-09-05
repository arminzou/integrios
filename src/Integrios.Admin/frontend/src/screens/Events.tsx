import { useEffect, useRef, useState } from "react";
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
type EventActivitySummary = components["schemas"]["EventActivitySummaryDto"];
type SourceListItem = components["schemas"]["SourceListItemDto"];
type Topic = components["schemas"]["AdminTopicResponse"];

const eventStatuses = ["accepted", "processing", "routed", "unrouted", "failed", "dead_lettered"];
const deliveryStatuses = ["pending", "in_flight", "succeeded", "dead_lettered"];

/// Above this width the ledger and the selected Event's inspector sit side by side, so moving focus
/// to the inspector on selection would only be disorienting; below it the inspector follows the
/// ledger in document order, and focus is what makes the newly-visible result findable. Kept as one
/// constant because the CSS breakpoint in index.css must agree with it.
const desktopBreakpoint = "(min-width: 900px)";

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

/// The inverse of `instant`: renders a server instant into the local wall-clock value a
/// `datetime-local` input holds, so an activity-summary window can populate the same fields an
/// Operator would otherwise type into by hand. Kept to whole seconds, matching the inputs' `step`,
/// so applying a summary window round-trips back to (sub-second precision aside) the same instant
/// the summary counted rather than rounding down to the minute and silently excluding Events the
/// button's own count included.
function localInputValue(iso: string): string {
  const date = new Date(iso);
  const pad = (value: number) => String(value).padStart(2, "0");
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}T${pad(date.getHours())}:${pad(date.getMinutes())}:${pad(date.getSeconds())}`;
}

type SummaryKey = "accepted" | "awaiting" | "unrouted" | "deadLettered";

export function EventsScreen({ tenantId, selectedEventId }: { tenantId: string; selectedEventId?: string }) {
  // The applied filters are separate from what is being typed: a source Event identity is a free
  // text field, and re-reading the list on every keystroke would restart the cursor each time.
  const [draft, setDraft] = useState<Filters>(noFilters);
  const [applied, setApplied] = useState<Filters>(noFilters);
  // Which activity-summary item, if any, produced the current filters. Cleared whenever the
  // Operator edits filters by hand, so the pressed state never lies about what is actually applied.
  const [activeSummary, setActiveSummary] = useState<SummaryKey | null>(null);

  const sources = useOptions<SourceListItem>(
    () => api.GET("/admin/tenants/{tenantId}/sources", { params: { path: { tenantId }, query: { limit: 100 } } }),
    `source-options|${tenantId}`,
  );
  const topics = useOptions<Topic>(
    () => api.GET("/admin/tenants/{tenantId}/topics", { params: { path: { tenantId }, query: { limit: 100 } } }),
    `topic-options|${tenantId}`,
  );

  // Source and Topic scope the summary, matching the ledger's own ownership checks; Event-status
  // and Delivery-status filters do not, so the four summary values stay comparable to each other.
  const summary = useResource<EventActivitySummary>(
    () =>
      api.GET("/admin/tenants/{tenantId}/events/activity-summary", {
        params: { path: { tenantId }, query: { source_id: applied.sourceId || undefined, topic_id: applied.topicId || undefined } },
      }),
    `activity-summary|${tenantId}|${applied.sourceId}|${applied.topicId}`,
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

  function selectSummaryItem(key: SummaryKey) {
    if (!summary.data) return;
    const statePatch: Partial<Filters> =
      key === "accepted"
        ? { status: "", deliveryStatus: "" }
        : key === "awaiting"
          ? { status: "accepted", deliveryStatus: "" }
          : key === "unrouted"
            ? { status: "unrouted", deliveryStatus: "" }
            : { status: "", deliveryStatus: "dead_lettered" };
    const next: Filters = {
      ...applied,
      ...statePatch,
      acceptedFrom: localInputValue(summary.data.window_start),
      acceptedTo: localInputValue(summary.data.window_end),
    };
    setDraft(next);
    setApplied(next);
    setActiveSummary(key);
  }

  return (
    <div className="events-layout">
      <div className="ledger-column">
        <h1>Events</h1>
        <p>
          In <Link to={`/tenants/${tenantId}`}>this Tenant</Link>.
        </p>

        <ActivitySummary summary={summary} activeKey={activeSummary} onSelect={selectSummaryItem} />

        <details className="filters-disclosure">
          <summary>Find an Event</summary>
          <form
            onSubmit={(event) => {
              event.preventDefault();
              setApplied(draft);
              setActiveSummary(null);
            }}
          >
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
                step="1"
                value={draft.acceptedFrom}
                onChange={(event) => set({ acceptedFrom: event.target.value })}
              />
            </Field>
            <Field id="event-accepted-to" label="Accepted to">
              <input
                {...fieldProps("event-accepted-to")}
                type="datetime-local"
                step="1"
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
                setActiveSummary(null);
              }}
            >
              Clear filters
            </button>
          </form>
        </details>

        <ListStatus
          busy={list.busy}
          loaded={list.loaded}
          problem={list.problem}
          empty={list.items.length === 0}
          emptyText="No Events in this Tenant match these filters."
        />
        {list.items.length > 0 ? (
          <div className="ledger">
            <table>
              <caption>Events, newest first</caption>
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
                      <Link to={`/tenants/${tenantId}/events/${item.event_id}`} current={item.event_id === selectedEventId}>
                        {item.accepted_at}
                      </Link>
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
          </div>
        ) : null}
        <LoadMore cursor={list.cursor} busy={list.busy} onLoadMore={list.loadMore} />
      </div>

      {/* Keyed by Event id: switching the selection is a distinct inspector, not the same one fed a
          new id. Without this, `useResource`'s state clear lands on a later render than the id
          change itself, so the previous Event's data (and its now-detached heading) would still be
          on screen at the instant focus tries to move, and the fresh heading would never receive it. */}
      {selectedEventId ? <EventInspector key={selectedEventId} tenantId={tenantId} eventId={selectedEventId} /> : null}
    </div>
  );
}

/// A separate bounded operational snapshot, not pagination metadata and never the total number of
/// rows in the Event list below it.
function ActivitySummary({
  summary,
  activeKey,
  onSelect,
}: {
  summary: ReturnType<typeof useResource<EventActivitySummary>>;
  activeKey: SummaryKey | null;
  onSelect: (key: SummaryKey) => void;
}) {
  if (summary.problem)
    return <p role="alert">{summary.problem.detail ?? `The activity summary could not be read (${summary.problem.status}).`}</p>;
  if (!summary.data) return <p>Loading activity summary…</p>;

  const data = summary.data;
  const items: { key: SummaryKey; label: string; value: number | string }[] = [
    { key: "accepted", label: "Events accepted", value: data.events_accepted },
    { key: "awaiting", label: "Awaiting routing", value: data.awaiting_routing },
    { key: "unrouted", label: "Unrouted", value: data.unrouted },
    { key: "deadLettered", label: "Dead-lettered Deliveries", value: data.dead_lettered_deliveries },
  ];

  return (
    <section aria-label="Event activity summary" className="activity-summary">
      <p className="activity-summary-window">
        Last 60 minutes, {data.window_start} to {data.window_end}.
      </p>
      <ul className="activity-summary-list">
        {items.map((item) => (
          <li key={item.key}>
            <button type="button" aria-pressed={activeKey === item.key} onClick={() => onSelect(item.key)}>
              <span className="activity-summary-value">{item.value}</span>
              <span>{item.label}</span>
            </button>
          </li>
        ))}
      </ul>
    </section>
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

/// The selected Event's detail: a persistent inspector beside the ledger on wide screens, and the
/// same content following the ledger in document order on narrow ones (see `.events-layout` in
/// index.css). It reads independently of the ledger list by the route's own Event id, so a direct
/// link resolves the same detail whether or not that row is in the ledger's currently loaded page,
/// and a replay only re-reads this Event rather than the whole ledger.
function EventInspector({ tenantId, eventId }: { tenantId: string; eventId: string }) {
  const event = useResource<EventDetail>(
    () =>
      api.GET("/admin/tenants/{tenantId}/events/{eventId}/deliveries", {
        params: { path: { tenantId, eventId } },
      }),
    `${tenantId}|${eventId}`,
  );

  const heading = useRef<HTMLHeadingElement>(null);
  const focusedFor = useRef<string | null>(null);
  useEffect(() => {
    if ((!event.data && !event.problem) || focusedFor.current === eventId) return;
    focusedFor.current = eventId;
    // Only when the inspector is not already sitting beside the ledger: moving focus there too is
    // disorienting when the result was already visible, and this effect does not re-run on a
    // background refresh (replay) because `eventId` has not changed. jsdom has no `matchMedia`, so
    // component tests exercise the loading/selection logic while the real-browser suite covers the
    // narrow-width focus move against actual layout.
    const isNarrow = typeof window.matchMedia === "function" && !window.matchMedia(desktopBreakpoint).matches;
    if (isNarrow) heading.current?.focus();
  }, [eventId, event.data, event.problem]);

  if (event.problem)
    return (
      <aside className="event-inspector" aria-label="Event detail">
        <h2 ref={heading} tabIndex={-1}>
          Event
        </h2>
        <p role="alert">{event.problem.detail ?? `This Event could not be read (${event.problem.status}).`}</p>
      </aside>
    );
  if (!event.data)
    return (
      <aside className="event-inspector" aria-label="Event detail">
        <p>Loading…</p>
      </aside>
    );

  const current = event.data;
  return (
    <aside className="event-inspector" aria-label="Event detail">
      <h2 ref={heading} tabIndex={-1}>
        Event {current.event_id}
      </h2>
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

      <h3>EventDeliveries</h3>
      {current.event_deliveries?.length ? (
        <div className="table-scroll">
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
        </div>
      ) : (
        <p>This Event has no EventDeliveries.</p>
      )}

      <h3>Delivery timeline</h3>
      {current.delivery_attempts?.length ? (
        <ol className="timeline" aria-label="Every attempt made against this Event's EventDeliveries">
          {current.delivery_attempts.map((attempt) => {
            // Only a terminal "failed" attempt gets the failure marker and its detail line.
            // "in_progress" (leased but not yet finished) and any other in-flight status are
            // neither success nor failure yet, and must not be painted red on a guess.
            const failed = attempt.status === "failed";
            return (
              <li key={attempt.attempt_id} className={failed ? "failed" : undefined}>
                <p>
                  <time>{attempt.started_at}</time> — attempt {attempt.attempt_number} to Subscription{" "}
                  {attempt.subscription_id}: {attempt.status}
                </p>
                {failed ? (
                  <p>
                    {attempt.failure_phase ?? "—"} · HTTP {attempt.response_status_code ?? "—"} ·{" "}
                    {attempt.error_message ?? "—"}
                  </p>
                ) : null}
              </li>
            );
          })}
        </ol>
      ) : (
        <p>No delivery attempts have been made for this Event.</p>
      )}
    </aside>
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
      <input id="event-trace-id" className="trace-value" ref={field} readOnly value={traceId} size={40} />
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
