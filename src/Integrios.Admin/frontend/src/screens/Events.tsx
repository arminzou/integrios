import { type UseQueryResult, useInfiniteQuery, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Fragment, useEffect, useMemo, useRef, useState } from "react";
import { useForm } from "react-hook-form";
import { Link, NavLink, useSearchParams } from "react-router";
import { Button } from "@/components/ui/button";
import { SelectItem } from "@/components/ui/select";
import { TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import { api } from "../api/client";
import { formError } from "../api/problem";
import { asProblem, call, nextCursor } from "../api/query";
import type { components } from "../api/schema";
import { appliedNote, ConfirmAction, FilterBar, FormError, ListStatus, LoadMore, WriteStatus } from "../ui/controls";
import { BodyPanel, CopyInline, CopyValue } from "../ui/copy";
import { FilterSelectField, FilterTextField, Form } from "../ui/fields";
import { CloseInspector, Inspector, InspectorPlaceholder, PageHeader, RowHeader, TableCard } from "../ui/layout";
import { nameIn, useConnectionOptions, useTopicOptions } from "../ui/options";
import { StatusBadge, statusLabel } from "../ui/status";
import { dayLabel, localDay, TimeOfDay, Timestamp } from "../ui/time";

type EventListItem = components["schemas"]["EventListItemDto"];
type EventDelivery = components["schemas"]["EventDeliveryDto"];
type EventActivitySummary = components["schemas"]["EventActivitySummaryDto"];

const eventStatuses = ["accepted", "processing", "routed", "unrouted", "failed", "dead_lettered"];
const deliveryStatuses = ["pending", "in_flight", "succeeded", "dead_lettered"];

/// How much of a retry history the inspector shows unasked. Enough to cover one exhausted budget
/// and the attempt that preceded it; the rest is there on request.
const attemptWindow = 5;

/// Above this width the ledger and the selected Event's inspector sit side by side, so moving focus
/// to the inspector on selection would only be disorienting; below it the inspector follows the
/// ledger in document order, and focus is what makes the newly-visible result findable. The layout
/// utilities below carry the same 1180px, so the two cannot drift apart unnoticed. It is the width
/// every other split in the dashboard breaks at, chosen by content fit: below it a ledger and a
/// 400-pixel inspector side by side leave the ledger narrower than its own columns.
const desktopBreakpoint = "(min-width: 1180px)";

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

/// How the URL spells each filter. Snake case matches the Admin API's own query parameters, so a
/// link an Operator copies out of the dashboard reads like the request it produces.
///
/// The two accepted-window fields are marked as instants because they are the one place the form's
/// value and the URL's value must differ. A `datetime-local` control holds a local wall clock with
/// no offset, and a link carrying that raw would resolve to a different moment for a colleague in
/// another zone. The URL therefore carries the unambiguous instant and the form converts at the
/// boundary, exactly as the request already does.
const filterParams: { field: keyof Filters; name: string; isInstant?: true }[] = [
  { field: "status", name: "status" },
  { field: "deliveryStatus", name: "delivery_status" },
  { field: "sourceId", name: "source_id" },
  { field: "topicId", name: "topic_id" },
  { field: "sourceEventId", name: "source_event_id" },
  { field: "acceptedFrom", name: "accepted_from", isInstant: true },
  { field: "acceptedTo", name: "accepted_to", isInstant: true },
];

function readFilters(params: URLSearchParams): Filters {
  const filters = { ...noFilters };
  for (const { field, name, isInstant } of filterParams) {
    const value = params.get(name);
    if (value) filters[field] = isInstant ? localInputValue(value) : value;
  }
  return filters;
}

function writeFilters(values: Filters): URLSearchParams {
  const params = new URLSearchParams();
  for (const { field, name, isInstant } of filterParams) {
    const value = values[field];
    if (!value) continue;
    const written = isInstant ? instant(value) : value;
    if (written) params.set(name, written);
  }
  return params;
}

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
  // text field, and re-reading the list on every keystroke would restart the cursor each time. The
  // form holds what is being typed; the URL holds what the ledger is actually reading under, so a
  // filtered ledger is a link and the back button restores the previous scope.
  const [searchParams, setSearchParams] = useSearchParams();
  const query = searchParams.toString();
  const applied = useMemo(() => readFilters(new URLSearchParams(query)), [query]);
  // What is actually narrowing the ledger right now, counted from the URL rather than from the form,
  // so a value typed but not yet applied is not claimed as scope.
  const appliedCount = Object.values(applied).filter(Boolean).length;
  // Which activity-summary item, if any, produced the current filters. Cleared whenever the
  // Operator edits filters by hand, so the pressed state never lies about what is actually applied.
  const [activeSummary, setActiveSummary] = useState<SummaryKey | null>(null);
  const form = useForm<Filters>({ defaultValues: applied });

  // The URL can change without the form having produced it — the empty state's Clear filters, the
  // back button, a pasted link. The form follows it rather than keeping values the ledger is no
  // longer reading under.
  useEffect(() => {
    form.reset(applied);
  }, [applied, form]);

  const sources = useQuery({
    queryKey: ["source-options", tenantId],
    queryFn: () =>
      call(() =>
        api.GET("/admin/tenants/{tenantId}/sources", { params: { path: { tenantId }, query: { limit: 100 } } }),
      ),
  });
  const topics = useTopicOptions(tenantId);

  // Source and Topic scope the summary, matching the ledger's own ownership checks; Event-status
  // and Delivery-status filters do not, so the four summary values stay comparable to each other.
  const summary = useQuery({
    queryKey: ["activity-summary", tenantId, { sourceId: applied.sourceId, topicId: applied.topicId }],
    queryFn: () =>
      call(() =>
        api.GET("/admin/tenants/{tenantId}/events/activity-summary", {
          params: {
            path: { tenantId },
            query: { source_id: applied.sourceId || undefined, topic_id: applied.topicId || undefined },
          },
        }),
      ),
  });

  const list = useInfiniteQuery({
    queryKey: ["events", tenantId, applied],
    queryFn: ({ pageParam }) =>
      call(() =>
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
              after: pageParam ?? undefined,
              limit: 20,
            },
          },
        }),
      ),
    initialPageParam: null as string | null,
    getNextPageParam: nextCursor<EventListItem>,
  });
  const events = list.data?.pages.flatMap((page) => page.items) ?? [];

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
    setSearchParams(writeFilters(next));
    setActiveSummary(key);
  }

  return (
    // Title, summary and filters describe the whole screen, so they run its full width; only the
    // ledger and the Event it has open are the two columns.
    <div className="flex flex-col gap-5">
      <PageHeader
        title="Events"
        action={
          <Button asChild variant="outline">
            <Link className="no-underline" to={`/tenants/${tenantId}`}>
              Tenant overview
            </Link>
          </Button>
        }
      >
        Everything accepted for this Tenant, newest first.
      </PageHeader>

      <ActivitySummary summary={summary} activeKey={activeSummary} onSelect={selectSummaryItem} />

      <Form {...form}>
        <form
          className="flex flex-col gap-4"
          onSubmit={form.handleSubmit((values) => {
            setSearchParams(writeFilters(values));
            setActiveSummary(null);
          })}
        >
          <FormError message={formError(asProblem(sources.error ?? topics.error))} />

          <FilterBar
            applied={appliedCount}
            onClear={() => {
              setSearchParams(new URLSearchParams());
              setActiveSummary(null);
            }}
          >
            <FilterSelectField
              control={form.control}
              name="status"
              label="Event status"
              hint="How far the Event itself got."
            >
              {eventStatuses.map((status) => (
                <SelectItem key={status} value={status}>
                  {statusLabel(status)}
                </SelectItem>
              ))}
            </FilterSelectField>
            {/* Delivery status is a separate filter over Delivery state. An Event matches when one
                  of its EventDeliveries is in that state; the Event's own status is untouched by it. */}
            <FilterSelectField
              control={form.control}
              name="deliveryStatus"
              label="Delivery status"
              hint="Matches Events with at least one EventDelivery in this state."
            >
              {deliveryStatuses.map((status) => (
                <SelectItem key={status} value={status}>
                  {statusLabel(status)}
                </SelectItem>
              ))}
            </FilterSelectField>
            <FilterSelectField
              control={form.control}
              name="sourceId"
              label="Source"
              hint={sources.data?.next_cursor ? "Showing the first 100 Sources." : undefined}
              disabled={sources.isPending || sources.isError}
            >
              {(sources.data?.items ?? []).map((source) => (
                <SelectItem key={source.id} value={source.id}>
                  {source.type} · {source.id}
                </SelectItem>
              ))}
            </FilterSelectField>
            <FilterSelectField
              control={form.control}
              name="topicId"
              label="Topic"
              hint={topics.data?.next_cursor ? "Showing the first 100 Topics." : undefined}
              disabled={topics.isPending || topics.isError}
            >
              {(topics.data?.items ?? []).map((topic) => (
                <SelectItem key={topic.id} value={topic.id}>
                  {topic.name}
                </SelectItem>
              ))}
            </FilterSelectField>
            <FilterTextField
              control={form.control}
              name="sourceEventId"
              label="Source Event id"
              placeholder="Any"
              hint="The identity the sending system gave the Event. Matched exactly."
            />
            <FilterTextField
              control={form.control}
              name="acceptedFrom"
              label="Accepted from"
              type="datetime-local"
              step="1"
            />
            <FilterTextField
              control={form.control}
              name="acceptedTo"
              label="Accepted to"
              type="datetime-local"
              step="1"
            />

            {/* Apply stays explicit. Seven controls that each re-queried on change would issue six
                  requests on the way to the scope the Operator actually wanted. */}
            <Button type="submit">Apply filters</Button>
          </FilterBar>
        </form>
      </Form>

      <ListStatus
        busy={list.isFetching}
        loaded={list.isSuccess}
        problem={asProblem(list.error)}
        empty={events.length === 0}
        emptyText="No Events in this Tenant match these filters."
      />

      <div
        data-layout="events"
        className="flex flex-col gap-5 min-[1180px]:flex-row min-[1180px]:items-start min-[1180px]:gap-4"
      >
        <div className="min-w-0 min-[1180px]:flex-1">
          {events.length > 0 ? (
            <TableCard
              caption={`Events, newest first${appliedNote(appliedCount)}`}
              footer={
                <LoadMore
                  noun="Event"
                  hasMore={list.hasNextPage}
                  busy={list.isFetching}
                  loaded={events.length}
                  onLoadMore={() => void list.fetchNextPage()}
                />
              }
            >
              <TableHeader>
                <TableRow>
                  <TableHead scope="col">Accepted</TableHead>
                  <TableHead scope="col">Type</TableHead>
                  <TableHead scope="col">Source Event id</TableHead>
                  <TableHead scope="col">Event status</TableHead>
                  <TableHead scope="col">Deliveries</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {events.map((item, index) => (
                  <Fragment key={item.event_id}>
                    {/* The day is stated once, where it changes, so every row below it carries only
                        its time. The ledger is ordered by acceptance, so a change of day is always
                        a boundary rather than something that can recur. */}
                    {index === 0 || localDay(item.accepted_at) !== localDay(events[index - 1].accepted_at) ? (
                      <TableRow className="hover:bg-transparent">
                        <th scope="colgroup" colSpan={5} className="bg-muted px-4 py-1 text-left text-xs font-medium">
                          {dayLabel(item.accepted_at)}
                        </th>
                      </TableRow>
                    ) : null}
                    <TableRow className="group has-[a[aria-current=page]]:bg-selected-surface">
                      <RowHeader>
                        {/* NavLink marks the selected row itself: the route is the selection, so
                            `aria-current` follows the URL rather than a separately tracked flag. */}
                        <NavLink className="no-underline" to={`/tenants/${tenantId}/events/${item.event_id}`} end>
                          <TimeOfDay value={item.accepted_at} />
                        </NavLink>
                      </RowHeader>
                      <TableCell>{item.event_type}</TableCell>
                      <TableCell>
                        {item.source_event_id ? (
                          <CopyInline label="Source Event id" value={item.source_event_id} />
                        ) : (
                          <span className="text-ink-secondary">—</span>
                        )}
                      </TableCell>
                      <TableCell>
                        <StatusBadge status={item.status} />
                      </TableCell>
                      <TableCell>
                        <DeliveryCounts counts={item.deliveries} />
                      </TableCell>
                    </TableRow>
                  </Fragment>
                ))}
              </TableBody>
            </TableCard>
          ) : null}
        </div>

        {/* Keyed by Event id: switching the selection is a distinct inspector, not the same one fed
              a new id. Without this, `useResource`'s state clear lands on a later render than the id
              change itself, so the previous Event's data (and its now-detached heading) would still
              be on screen at the instant focus tries to move, and the fresh heading would never
              receive it. */}
        {selectedEventId ? (
          <EventInspector key={selectedEventId} tenantId={tenantId} eventId={selectedEventId} />
        ) : (
          <InspectorPlaceholder label="Event detail">
            Select an Event to read its Deliveries, attempts, and trace identity here.
          </InspectorPlaceholder>
        )}
      </div>
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
  summary: UseQueryResult<EventActivitySummary>;
  activeKey: SummaryKey | null;
  onSelect: (key: SummaryKey) => void;
}) {
  const problem = asProblem(summary.error);
  if (problem)
    return <p role="alert">{problem.detail ?? `The activity summary could not be read (${problem.status}).`}</p>;
  if (!summary.data) return <p>Loading activity summary…</p>;

  const data = summary.data;
  const items: { key: SummaryKey; label: string; value: number | string }[] = [
    { key: "accepted", label: "Events accepted", value: data.events_accepted },
    { key: "awaiting", label: "Awaiting routing", value: data.awaiting_routing },
    { key: "unrouted", label: "Unrouted", value: data.unrouted },
    { key: "deadLettered", label: "Dead-lettered Deliveries", value: data.dead_lettered_deliveries },
  ];

  return (
    <section aria-label="Event activity summary" className="flex flex-col gap-2.5">
      <p className="m-0 text-[13px] text-ink-secondary">
        Last 60 minutes, <Timestamp value={data.window_start} /> to <Timestamp value={data.window_end} />.
      </p>
      {/* Four equal tracks, halving to two below the split's own breakpoint: the values are read
          against each other, so a row that reflows by content width stops being comparable. */}
      <ul className="m-0 grid list-none grid-cols-2 gap-2.5 p-0 min-[1180px]:grid-cols-4">
        {items.map((item) => (
          <li key={item.key}>
            <button
              type="button"
              aria-pressed={activeKey === item.key}
              onClick={() => onSelect(item.key)}
              className="group flex h-full w-full cursor-pointer flex-col items-start gap-0.5 rounded-lg border bg-surface px-3.5 py-3 text-left hover:bg-surface-quiet aria-pressed:border-accent-border aria-pressed:bg-selected-surface aria-pressed:text-selected-ink focus-visible:ring-[3px] focus-visible:ring-ring/50"
            >
              <span className="font-serif text-[28px] leading-tight tabular-nums">{item.value}</span>
              <span className="text-[13px] text-ink-secondary group-aria-pressed:text-selected-ink">{item.label}</span>
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
    ["in_flight", counts.in_flight],
    ["succeeded", counts.succeeded],
    ["dead_lettered", counts.dead_lettered],
  ];
  const present = states.filter(([, count]) => Number(count) > 0);

  if (present.length === 0) return <span className="text-ink-secondary">No EventDeliveries</span>;
  return (
    <span className="flex flex-wrap gap-1">
      {present.map(([status, count]) => (
        <StatusBadge key={status} status={status}>
          {count} {statusLabel(status).toLowerCase()}
        </StatusBadge>
      ))}
    </span>
  );
}

/// The selected Event's detail: a persistent inspector beside the ledger on wide screens, and the
/// same content following the ledger in document order on narrow ones. It reads independently of
/// the ledger list by the route's own Event id, so a direct link resolves the same detail whether
/// or not that row is in the ledger's currently loaded page, and a replay only re-reads this Event
/// rather than the whole ledger.
function EventInspector({ tenantId, eventId }: { tenantId: string; eventId: string }) {
  // A dead-lettered Delivery is read to find out where it was going. The destination has a name the
  // Tenant's own Connection list already carries; the Subscription does not, because the Admin API
  // lists Subscriptions under their Topic and an Event does not say which Topic matched it.
  const connectionOptions = useConnectionOptions(tenantId);
  const queryClient = useQueryClient();
  const eventKey = ["event", tenantId, eventId];
  // Held here rather than on the replay control: a replayed Delivery leaves `dead_lettered`, which
  // is exactly the condition that control renders under, so it is gone by the time the write it
  // just made has anything to report. The inspector is keyed by Event id and survives the re-read.
  const [replayed, setReplayed] = useState(false);
  const [allAttempts, setAllAttempts] = useState(false);
  const event = useQuery({
    queryKey: eventKey,
    queryFn: () =>
      call(() =>
        api.GET("/admin/tenants/{tenantId}/events/{eventId}/deliveries", {
          params: { path: { tenantId, eventId } },
        }),
      ),
  });

  const heading = useRef<HTMLHeadingElement>(null);
  const focusedFor = useRef<string | null>(null);
  useEffect(() => {
    if ((!event.data && !event.isError) || focusedFor.current === eventId) return;
    focusedFor.current = eventId;
    // Only when the inspector is not already sitting beside the ledger: moving focus there too is
    // disorienting when the result was already visible, and this effect does not re-run on a
    // background refresh (replay) because `eventId` has not changed. jsdom has no `matchMedia`, so
    // component tests exercise the loading/selection logic while the real-browser suite covers the
    // narrow-width focus move against actual layout.
    const isNarrow = typeof window.matchMedia === "function" && !window.matchMedia(desktopBreakpoint).matches;
    if (isNarrow) heading.current?.focus();
  }, [eventId, event.data, event.isError]);

  const closed = `/tenants/${tenantId}/events`;

  const problem = asProblem(event.error);
  if (problem)
    return (
      <Inspector label="Event detail">
        {/* The close is offered on a failed read too: an Event that cannot be read is exactly when
            an Operator wants the ledger back, and without this the only way out is the browser. */}
        <div className="flex items-start justify-between gap-3">
          <h2 ref={heading} tabIndex={-1} className="m-0">
            Event
          </h2>
          <CloseInspector to={closed} label="Close the Event detail" />
        </div>
        <p role="alert">{problem.detail ?? `This Event could not be read (${problem.status}).`}</p>
      </Inspector>
    );
  if (!event.data)
    return (
      <Inspector label="Event detail">
        <p>Loading…</p>
      </Inspector>
    );

  const current = event.data;
  const attempts = current.delivery_attempts ?? [];
  // The most recent attempts are the ones being triaged; a long retry history is context an Operator
  // asks for rather than scrolls past. Ordered oldest first, so the recent end is the tail.
  const shownAttempts = allAttempts ? attempts : attempts.slice(-attemptWindow);

  return (
    <Inspector label="Event detail">
      <div className="flex items-start justify-between gap-3">
        {/* Truncated rather than wrapped: a 36-character identifier broken over three lines at 320
            is the largest thing in the panel and says nothing more than its first characters do.
            What an Operator does with it is paste it elsewhere, so the affordance is the copy. */}
        <h2 ref={heading} tabIndex={-1} className="group min-w-0">
          {/* The space is explicit: the heading's accessible name is "Event <id>", and JSX drops a
              trailing space before an element on the next line. */}
          Event{" "}
          <span className="block text-xs text-ink-secondary">
            <CopyInline label="Event id" value={current.event_id} />
          </span>
        </h2>
        <div className="flex shrink-0 items-center gap-1.5">
          <StatusBadge status={current.status} className="mt-0.5" />
          <CloseInspector to={closed} label="Close the Event detail" />
        </div>
      </div>
      <dl className="m-0 grid grid-cols-[minmax(0,auto)_minmax(0,1fr)] gap-x-3 gap-y-1.5 border-b pb-3.5 text-[13px] [&>dd]:m-0 [&>dd]:text-right [&>dt]:m-0 [&>dt]:text-ink-secondary">
        <dt>Accepted</dt>
        <dd>
          <Timestamp value={current.accepted_at} />
        </dd>
        <dt>Processed</dt>
        <dd>{current.processed_at ? <Timestamp value={current.processed_at} /> : "Not processed"}</dd>
        <dt>Failed</dt>
        <dd>{current.failed_at ? <Timestamp value={current.failed_at} /> : "Not failed"}</dd>
      </dl>

      {current.payload !== undefined && current.payload !== null ? (
        <BodyPanel label="Accepted payload" value={current.payload} />
      ) : null}
      {current.metadata !== undefined && current.metadata !== null ? (
        <BodyPanel label="Metadata" value={current.metadata} />
      ) : null}

      {current.trace_id ? (
        <CopyValue id="event-trace-id" label="Trace id" value={current.trace_id} />
      ) : (
        <p>This Event carries no trace identity.</p>
      )}

      <section className="flex flex-col gap-2">
        <h3 className="eyebrow">EventDeliveries</h3>
        <WriteStatus done={replayed}>Queued for delivery again.</WriteStatus>
        {current.event_deliveries?.length ? (
          // One entry per matched Subscription, stacked rather than tabulated. The inspector is a
          // fixed 400 pixels beside the ledger, and six columns in that width put Replay — the only
          // recovery the platform offers — behind a sideways scroll an Operator triaging a dead
          // letter would have to discover. Stacked, the state and the control that answers it sit
          // together on the line, and nothing here scrolls.
          <ul className="m-0 flex list-none flex-col gap-2 p-0" aria-label="One EventDelivery per matched Subscription">
            {current.event_deliveries.map((delivery) => (
              <li
                key={delivery.event_delivery_id}
                className="flex items-center justify-between gap-2 rounded-md border bg-surface-quiet px-2.5 py-2"
              >
                <div className="min-w-0 text-[13px]">
                  <span className="block truncate font-mono">{delivery.subscription_id}</span>
                  <span className="block truncate font-mono text-xs text-ink-secondary">
                    → {nameIn(connectionOptions.data?.items, delivery.destination_connection_id)}
                  </span>
                  <span className="block text-xs text-ink-secondary">
                    <span title="Lifetime attempts / attempts in the current retry cycle">
                      {delivery.lifetime_attempt_count} / {delivery.retry_cycle_attempt_count} attempts
                    </span>
                    {delivery.failed_at ? (
                      <>
                        {" · failed "}
                        <Timestamp value={delivery.failed_at} />
                      </>
                    ) : null}
                    {delivery.deliver_after ? (
                      <>
                        {" · next "}
                        <Timestamp value={delivery.deliver_after} />
                      </>
                    ) : null}
                  </span>
                </div>
                <div className="flex shrink-0 items-center gap-2">
                  <StatusBadge status={delivery.status} />
                  <ReplayDelivery
                    tenantId={tenantId}
                    eventId={eventId}
                    delivery={delivery}
                    onReplayed={() => {
                      setReplayed(true);
                      void queryClient.invalidateQueries({ queryKey: eventKey });
                    }}
                  />
                </div>
              </li>
            ))}
          </ul>
        ) : (
          <p>This Event has no EventDeliveries.</p>
        )}
      </section>

      <section className="flex flex-col gap-2">
        <h3 className="eyebrow">Delivery timeline</h3>
        {attempts.length > shownAttempts.length ? (
          <Button type="button" variant="outline" size="sm" className="self-start" onClick={() => setAllAttempts(true)}>
            Show all {attempts.length} attempts
          </Button>
        ) : null}
        {attempts.length ? (
          <ol
            className="m-0 list-none border-l pl-5"
            aria-label="Every attempt made against this Event's EventDeliveries"
          >
            {shownAttempts.map((attempt) => {
              // Only a terminal "failed" attempt gets the failure marker and its detail line.
              // "in_progress" (leased but not yet finished) and any other in-flight status are
              // neither success nor failure yet, and must not be painted red on a guess.
              const failed = attempt.status === "failed";
              return (
                <li
                  key={attempt.attempt_id}
                  className={`relative pb-4 last:pb-0 before:absolute before:top-1.5 before:-left-[25px] before:size-2.5 before:rounded-full before:content-[''] ${
                    failed ? "before:bg-danger-ink" : "before:bg-selected-ink"
                  }`}
                >
                  <p className="m-0">
                    <span className="font-semibold">
                      <Timestamp value={attempt.started_at} />
                    </span>{" "}
                    — attempt {attempt.attempt_number} to Subscription{" "}
                    <span className="font-mono text-sm">{attempt.subscription_id}</span>: {statusLabel(attempt.status)}
                  </p>
                  {failed ? (
                    <p className="m-0 text-ink-secondary">
                      {[
                        attempt.response_status_code ? `HTTP ${attempt.response_status_code}` : null,
                        attempt.failure_phase ? `during ${attempt.failure_phase}` : null,
                      ]
                        .filter(Boolean)
                        .join(" ") || "No response was recorded"}
                      {attempt.error_message ? `. ${attempt.error_message}` : "."}
                    </p>
                  ) : null}
                  {/* What was sent and what came back, on the attempt that did it rather than on
                      the Event: a retry cycle can send the same payload to a destination that
                      answers differently each time, and it is the difference that diagnoses it. */}
                  {attempt.request_payload !== undefined && attempt.request_payload !== null ? (
                    <BodyPanel label="Sent" value={attempt.request_payload} note="After any Subscription mapping." />
                  ) : null}
                  {attempt.response_body ? (
                    <BodyPanel
                      label="Returned"
                      value={attempt.response_body}
                      truncated={attempt.response_body_truncated}
                    />
                  ) : null}
                </li>
              );
            })}
          </ol>
        ) : (
          <p>No delivery attempts have been made for this Event.</p>
        )}
      </section>
    </Inspector>
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
  // A replay re-reads only this Event: the ledger beside it is a separate query and is not
  // refetched as a side effect of recovering one Delivery.
  const replay = useMutation({
    mutationFn: () =>
      call(() =>
        api.POST("/admin/tenants/{tenantId}/events/{eventId}/deliveries/{deliveryId}/replay", {
          params: { path: { tenantId, eventId, deliveryId: delivery.event_delivery_id } },
        }),
      ),
    onSuccess: onReplayed,
  });

  if (delivery.status !== "dead_lettered") return <span>—</span>;

  return (
    <div className="flex flex-col items-start gap-2">
      <ConfirmAction
        label="Replay"
        question={`Replay the dead-lettered delivery to Subscription ${delivery.subscription_id}? It is queued for delivery again.`}
        confirmLabel="Replay this delivery"
        busy={replay.isPending}
        onConfirm={() => replay.mutate()}
      />
      <FormError message={formError(asProblem(replay.error))} />
    </div>
  );
}
