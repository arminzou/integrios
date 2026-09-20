import { type UseQueryResult, useInfiniteQuery, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { cn } from "cn";
import { Fragment, useEffect, useRef, useState } from "react";
import { Link, NavLink, useSearchParams } from "react-router";
import { Button } from "@/components/ui/button";
import { SelectItem } from "@/components/ui/select";
import { TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import { api } from "../api/client";
import { formError } from "../api/problem";
import { asProblem, call, nextCursor } from "../api/query";
import type { components } from "../api/schema";
import { AcceptedRangePill } from "../ui/acceptedRange";
import {
  appliedNote,
  ConfirmAction,
  FilterBar,
  FormError,
  ListStatus,
  LoadMore,
  narrowable,
  ReadError,
  WriteStatus,
} from "../ui/controls";
import { BodyPanel, CopyInline, CopyValue } from "../ui/copy";
import { Filter, FilterSearch, SearchFieldChooser } from "../ui/fields";
import { useListFilters } from "../ui/filters";
import {
  CloseInspector,
  Inspector,
  InspectorLoading,
  InspectorPlaceholder,
  openRow,
  PageHeader,
  RowChevron,
  RowHeader,
  TableCard,
} from "../ui/layout";
import { nameIn, useDestinationOptions, useTopicOptions } from "../ui/options";
import { StatusBadge, statusLabel, statusMarker } from "../ui/status";
import { dayLabel, localDay, since, TimeOfDay, Timestamp } from "../ui/time";
import { EventActivity } from "./EventActivity";

type EventListItem = components["schemas"]["EventListItemDto"];
type EventDelivery = components["schemas"]["EventDeliveryDiagnosticsDto"];
type EventBacklog = components["schemas"]["EventBacklogDto"];

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

/// How the URL spells each filter: the Admin API's own query parameters, so a link an Operator
/// copies out of the dashboard reads like the request it produces. The accepted range is carried,
/// applied and requested as instants; only the Accepted pill's Custom inputs ever hold a wall clock.
const eventFilters = [
  "source_event_id",
  "event_type",
  "source_id",
  "topic_id",
  "status",
  "delivery_status",
  "accepted_from",
  "accepted_to",
] as const;

/// The three backlogs, in the order both monitoring surfaces list them. Each opens the ledger under
/// its own status filter alone: a backlog is current state however old, so any accepted-time range
/// carried along would hide exactly the oldest items the count includes.
export const backlogs = [
  { key: "awaiting_routing", label: "Awaiting routing", noun: "Events awaiting routing", query: "status=accepted" },
  { key: "unrouted", label: "Unrouted", noun: "unrouted Events", query: "status=unrouted" },
  {
    key: "dead_lettered_deliveries",
    label: "Dead-lettered Deliveries",
    noun: "dead-lettered Deliveries",
    query: "delivery_status=dead_lettered",
  },
] as const;

export function useEventBacklog(tenantId: string) {
  return useQuery({
    queryKey: ["event-backlog", tenantId],
    queryFn: () => call(() => api.GET("/admin/tenants/{tenantId}/events/backlog", { params: { path: { tenantId } } })),
    // Current state is only current if it is read again; the same cadence as the ledger's freshness.
    refetchInterval: 15_000,
  });
}

/// The two identities an Event carries, and the one free-text filter that matches either. They are
/// exact matches on different columns and the Admin API ANDs them, so two boxes could be set to
/// identities belonging to different Events — an empty ledger with nothing saying why. One box whose
/// field is chosen inside it cannot express that.
type IdentityField = "source_event_id" | "event_type";

const identities: Record<IdentityField, { label: string; placeholder: string; hint: string }> = {
  source_event_id: {
    label: "Source Event id",
    placeholder: "Exact id…",
    hint: "The sending system's id for this Event. Matched exactly, including case.",
  },
  event_type: {
    label: "Event type",
    placeholder: "Exact type…",
    hint: "The routing name Subscriptions match on. Matched exactly, ignoring case.",
  },
};

/// Every filter unset, so a scope replaces the current one without touching parameters that are not filters.
const noFilters = Object.fromEntries(eventFilters.map((name) => [name, ""]));

export function EventsScreen({ tenantId, selectedEventId }: { tenantId: string; selectedEventId?: string }) {
  // The URL is the scope the ledger reads under, so a filtered ledger is a link and Back restores
  // the previous scope. Every filter applies as it changes; a value typed in a free-text box is not
  // scope until it is committed.
  const [searchParams] = useSearchParams();
  const query = searchParams.toString();
  // Selecting and closing an Event keeps the ledger's scope, so the list beside the inspector is
  // still the one the Operator selected from.
  const search = query ? `?${query}` : "";
  const filters = useListFilters(eventFilters);
  const applied = filters.values;
  const appliedCount = filters.applied;
  // Which identity the one find box is matching. It follows the URL, so a link carrying either
  // parameter opens the box on that field; an unfiltered ledger opens on the id, the identity an
  // Operator arrives with most often.
  const [chosenField, setChosenField] = useState<IdentityField>("source_event_id");
  const findField: IdentityField = applied.event_type
    ? "event_type"
    : applied.source_event_id
      ? "source_event_id"
      : chosenField;
  // Changing the field carries what is typed across rather than making it be typed again, and clears
  // the parameter it leaves in the same write, so only one identity filter is ever applied.
  const chooseField = (next: IdentityField) => {
    setChosenField(next);
    if (applied[findField]) filters.patch({ [findField]: "", [next]: applied[findField] });
  };
  // Shown beside the box while it is hovered or focused, and always the box's own description.
  const findHint = identities[findField].hint;

  const sources = useQuery({
    queryKey: ["source-options", tenantId],
    queryFn: () =>
      call(() =>
        api.GET("/admin/tenants/{tenantId}/sources", { params: { path: { tenantId }, query: { limit: 100 } } }),
      ),
  });
  const topics = useTopicOptions(tenantId);

  const backlog = useEventBacklog(tenantId);

  // The ledger and its freshness count read under exactly the same filters.
  const filterQuery = Object.fromEntries(eventFilters.map((name) => [name, applied[name] || undefined]));
  const ledgerKey = ["events", tenantId, applied];
  const list = useInfiniteQuery({
    queryKey: ledgerKey,
    queryFn: ({ pageParam }) =>
      call(() =>
        api.GET("/admin/tenants/{tenantId}/events", {
          params: { path: { tenantId }, query: { ...filterQuery, after: pageParam ?? undefined, limit: 20 } },
        }),
      ),
    initialPageParam: null as string | null,
    getNextPageParam: nextCursor<EventListItem>,
  });
  const events = list.data?.pages.flatMap((page) => page.items) ?? [];

  // Rows never move while an Operator reads them. The first page's watermark marks what the ledger
  // has shown; the count of what arrived since is polled while the tab is visible, and the rows only
  // change when the Operator asks for them.
  const queryClient = useQueryClient();
  const watermark = list.data?.pages[0]?.watermark ?? null;
  const freshness = useQuery({
    queryKey: ["event-freshness", tenantId, applied, watermark],
    queryFn: () =>
      call(() =>
        api.GET("/admin/tenants/{tenantId}/events/freshness", {
          params: { path: { tenantId }, query: { ...filterQuery, watermark: watermark ?? "" } },
        }),
      ),
    enabled: watermark !== null,
    refetchInterval: 15_000,
  });
  function showNew() {
    // Keep only the first page and read it again: later pages would continue from rows that are no
    // longer where they were, and the fresh first page brings the next watermark with it.
    queryClient.setQueryData<typeof list.data>(ledgerKey, (data) =>
      data ? { pages: data.pages.slice(0, 1), pageParams: data.pageParams.slice(0, 1) } : data,
    );
    void queryClient.invalidateQueries({ queryKey: ledgerKey, exact: true });
    void queryClient.invalidateQueries({ queryKey: ["event-backlog", tenantId] });
  }
  // Whether there is a ledger to narrow yet. Until the read answers, the filter form is not
  // rendered: a Tenant that has accepted nothing has nothing to filter, and a screen that guessed
  // first would retract the form a moment later.
  const narrowing = narrowable(list.isSuccess, events.length, appliedCount);

  return (
    // Title, backlog and filters describe the whole screen, so they run its full width; only the
    // ledger and the Event it has open are the two columns.
    <div className="flex flex-col gap-5">
      <PageHeader
        title="Events"
        action={
          <Button asChild variant="outline">
            <Link to={`/tenants/${tenantId}`}>Tenant overview</Link>
          </Button>
        }
      >
        Everything accepted for this Tenant, newest first.
      </PageHeader>

      <RightNow
        backlog={backlog}
        activeQuery={query}
        onSelect={(next) => filters.patch({ ...noFilters, ...Object.fromEntries(new URLSearchParams(next)) })}
      />

      <EventActivity
        tenantId={tenantId}
        selectedFrom={applied.accepted_from || undefined}
        selectedTo={applied.accepted_to || undefined}
        onSelect={(from, to, replace) => filters.patch({ accepted_from: from, accepted_to: to }, { replace })}
      />

      {narrowing ? (
        <div className="flex flex-col gap-4">
          <FormError message={formError(asProblem(sources.error ?? topics.error))} />

          <div className="flex flex-col gap-1.5">
            <FilterBar applied={appliedCount} onClear={filters.clear}>
              <FilterSearch
                id="event-find"
                key={findField}
                label={identities[findField].label}
                placeholder={identities[findField].placeholder}
                hint={findHint}
                value={applied[findField]}
                onChange={(value) => filters.set(findField, value)}
                leading={
                  <SearchFieldChooser
                    label="Find an Event by"
                    value={findField}
                    onChange={(next) => chooseField(next as IdentityField)}
                  >
                    {Object.entries(identities).map(([name, identity]) => (
                      <SelectItem key={name} value={name}>
                        {identity.label}
                      </SelectItem>
                    ))}
                  </SearchFieldChooser>
                }
              />
              <Filter
                id="event-source"
                label="Source"
                value={applied.source_id}
                onChange={(value) => filters.set("source_id", value)}
                hint={sources.data?.next_cursor ? "Showing the first 100 Sources." : undefined}
                disabled={sources.isPending || sources.isError}
              >
                {/* The name is what an Operator authored the Source under and what the ledger's own
                    rows show; its type and identifier answer questions this filter is not asking. */}
                {(sources.data?.items ?? []).map((source) => (
                  <SelectItem key={source.id} value={source.id}>
                    {source.name}
                  </SelectItem>
                ))}
              </Filter>
              <Filter
                id="event-topic"
                label="Topic"
                value={applied.topic_id}
                onChange={(value) => filters.set("topic_id", value)}
                hint={topics.data?.next_cursor ? "Showing the first 100 Topics." : undefined}
                disabled={topics.isPending || topics.isError}
              >
                {(topics.data?.items ?? []).map((topic) => (
                  <SelectItem key={topic.id} value={topic.id}>
                    {topic.name}
                  </SelectItem>
                ))}
              </Filter>
              <Filter
                id="event-status"
                label="Event status"
                value={applied.status}
                onChange={(value) => filters.set("status", value)}
              >
                {eventStatuses.map((status) => (
                  <SelectItem key={status} value={status}>
                    {statusLabel(status)}
                  </SelectItem>
                ))}
              </Filter>
              {/* Delivery status is a separate filter over Delivery state. An Event matches when one
                of its EventDeliveries is in that state; the Event's own status is untouched by it. */}
              <Filter
                id="event-delivery-status"
                label="Delivery status"
                value={applied.delivery_status}
                onChange={(value) => filters.set("delivery_status", value)}
              >
                {deliveryStatuses.map((status) => (
                  <SelectItem key={status} value={status}>
                    {statusLabel(status)}
                  </SelectItem>
                ))}
              </Filter>
              <AcceptedRangePill
                from={applied.accepted_from}
                to={applied.accepted_to}
                onChange={(accepted_from, accepted_to) => filters.patch({ accepted_from, accepted_to })}
              />
            </FilterBar>
          </div>
        </div>
      ) : null}

      <div
        data-layout="events"
        className="flex flex-col gap-5 min-[1180px]:flex-row min-[1180px]:items-start min-[1180px]:gap-4"
      >
        <div className="min-w-0 min-[1180px]:flex-1">
          <ListStatus
            busy={list.isFetching}
            loaded={list.isSuccess}
            problem={asProblem(list.error)}
            empty={events.length === 0}
            applied={appliedCount}
            noun="Events"
            emptyText="Nothing has been accepted for this Tenant yet. Events arrive through a Source, on the intake endpoint."
          />
          <FreshnessNotice freshness={freshness} onShow={showNew} />
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
                  <TableHead scope="col">Source Event id</TableHead>
                  <TableHead scope="col">Event Type</TableHead>
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
                    <TableRow
                      className="group cursor-pointer has-[a[aria-current=page]]:bg-selected-surface"
                      onClick={openRow}
                    >
                      <RowHeader>
                        {/* NavLink marks the selected row itself: the route is the selection, so
                            `aria-current` follows the URL rather than a separately tracked flag. */}
                        <NavLink
                          className="-mx-3 block px-3 py-2 no-underline"
                          to={`/tenants/${tenantId}/events/${item.event_id}${search}`}
                          end
                        >
                          <TimeOfDay value={item.accepted_at} />
                        </NavLink>
                      </RowHeader>
                      <TableCell className="text-[13px]">
                        {item.source_event_id ? (
                          <CopyInline oneLine label="Source Event id" value={item.source_event_id} />
                        ) : (
                          <span className="text-ink-secondary">—</span>
                        )}
                      </TableCell>
                      {/* Copyable for the same reason the Source Event id is: the find box matches a
                          type exactly, so the way to search for one is to take it from a row. */}
                      <TableCell className="text-[13px]">
                        <CopyInline oneLine label="Event type" value={item.event_type} />
                      </TableCell>
                      <TableCell>
                        <StatusBadge status={item.status} />
                      </TableCell>
                      <TableCell>
                        <div className="flex items-center justify-between gap-3">
                          <DeliveryCounts counts={item.deliveries} />
                          <RowChevron />
                        </div>
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
          <EventInspector key={selectedEventId} tenantId={tenantId} eventId={selectedEventId} search={search} />
        ) : events.length > 0 ? (
          <InspectorPlaceholder label="Event detail">
            Select an Event to read its Deliveries, attempts, and trace identity here.
          </InspectorPlaceholder>
        ) : null}
      </div>
    </div>
  );
}

/// Current backlogs, however old: never windowed and never the total of the ledger below. A zero stays
/// on screen, quietly, so the strip reads the same shape whether or not anything is wrong.
function RightNow({
  backlog,
  activeQuery,
  onSelect,
}: {
  backlog: UseQueryResult<EventBacklog>;
  activeQuery: string;
  onSelect: (query: string) => void;
}) {
  const problem = asProblem(backlog.error);
  if (problem) return <ReadError problem={problem} what="The current backlog" />;
  if (!backlog.data) return <p>Loading the current backlog…</p>;
  const data = backlog.data;

  return (
    <section aria-labelledby="right-now" className="flex flex-col gap-2.5">
      <div className="flex flex-wrap items-baseline gap-x-3">
        <h2 id="right-now" className="m-0">
          Right now
        </h2>
        <p className="m-0 text-[13px] text-ink-secondary">
          Current state, however old. Select one to filter the ledger by its status.
        </p>
      </div>
      <ul className="m-0 grid list-none grid-cols-1 gap-2.5 p-0 min-[640px]:grid-cols-3">
        {backlogs.map((item) => {
          const { count, oldest_at } = data[item.key];
          const waiting = Number(count) > 0;
          return (
            <li key={item.key}>
              {/* Pressed follows the URL, so it is true only while this status alone scopes the ledger. */}
              <button
                type="button"
                aria-pressed={activeQuery === item.query}
                onClick={() => onSelect(item.query)}
                className="group flex h-full w-full cursor-pointer flex-col items-start gap-0.5 rounded-lg border bg-surface px-3.5 py-3 text-left hover:bg-surface-quiet aria-pressed:border-accent-border aria-pressed:bg-selected-surface aria-pressed:text-selected-ink focus-visible:ring-[3px] focus-visible:ring-ring/50"
              >
                <span
                  className={cn("font-serif text-[28px] leading-tight tabular-nums", !waiting && "text-ink-secondary")}
                >
                  {count}
                </span>
                <span className="text-[13px]">{item.label}</span>
                <span className="text-xs text-ink-secondary group-aria-pressed:text-selected-ink">
                  {waiting && oldest_at ? `Oldest ${since(oldest_at)}` : "Nothing waiting"}
                </span>
              </button>
            </li>
          );
        })}
      </ul>
    </section>
  );
}

/// A polite live region that is always present, so a screen reader hears the count change without
/// focus moving. An expired or foreign watermark stops the count rather than guessing one.
function FreshnessNotice({
  freshness,
  onShow,
}: {
  freshness: UseQueryResult<components["schemas"]["EventFreshnessDto"]>;
  onShow: () => void;
}) {
  const count = Number(freshness.data?.count ?? 0);
  const message = freshness.isError
    ? "The ledger can no longer tell what is new since it was read."
    : count > 0
      ? `${count}${freshness.data?.capped ? "+" : ""} new ${count === 1 ? "Event" : "Events"} since you opened this`
      : null;
  return (
    <div role="status">
      {message ? (
        <div className="mb-2.5 flex flex-wrap items-center justify-between gap-2 rounded-lg border bg-selected-surface px-3.5 py-2 text-[13px] text-selected-ink">
          <span>{message}</span>
          <Button type="button" size="sm" variant="outline" onClick={onShow}>
            Show
          </Button>
        </div>
      ) : null}
    </div>
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

  // The same pill as a count, in the quiet tone: an Event with nothing to deliver is a state, not a fault.
  if (present.length === 0) return <StatusBadge status="none">None</StatusBadge>;
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
function EventInspector({ tenantId, eventId, search }: { tenantId: string; eventId: string; search: string }) {
  // A dead-lettered Delivery is read to find out where it was going. The destination has a name the
  // Tenant's own Destination list already carries; the Subscription does not, because the Admin API
  // lists Subscriptions under their Topic and an Event does not say which Topic matched it.
  const destinationOptions = useDestinationOptions(tenantId);
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

  const closed = `/tenants/${tenantId}/events${search}`;

  const problem = asProblem(event.error);
  if (problem)
    return (
      <Inspector label="Event detail" fill>
        {/* The close is offered on a failed read too: an Event that cannot be read is exactly when
            an Operator wants the ledger back, and without this the only way out is the browser. */}
        <div className="flex items-start justify-between gap-3">
          <h2 ref={heading} tabIndex={-1} className="m-0">
            Event
          </h2>
          <CloseInspector to={closed} label="Close the Event detail" />
        </div>
        <ReadError problem={problem} what="This Event" back={{ to: closed, label: "Back to Events" }} />
      </Inspector>
    );
  if (!event.data) return <InspectorLoading label="Event detail" />;

  const current = event.data;
  const attempts = current.delivery_attempts ?? [];
  // The most recent attempts are the ones being triaged; a long retry history is context an Operator
  // asks for rather than scrolls past. Ordered oldest first, so the recent end is the tail.
  const shownAttempts = allAttempts ? attempts : attempts.slice(-attemptWindow);

  return (
    <Inspector label="Event detail" fill>
      <div className="flex items-start justify-between gap-3">
        {/* The identifier under a panel heading is the same shape on every detail screen: mono, one
            step down, in secondary ink, and wrapped rather than clipped so it reads whole. This one
            adds the copy control the others lack, because pasting it elsewhere is what it is for. */}
        <h2 ref={heading} tabIndex={-1} className="group min-w-0">
          {/* The space is explicit: the heading's accessible name is "Event <id>", and JSX drops a
              trailing space before an element on the next line. */}
          Event{" "}
          <span className="block text-xs font-normal text-ink-secondary">
            <CopyInline label="Event id" value={current.event_id} />
          </span>
        </h2>
        <div className="flex shrink-0 items-center gap-1.5">
          <StatusBadge status={current.status} className="mt-0.5" />
          <CloseInspector to={closed} label="Close the Event detail" />
        </div>
      </div>
      <dl className="m-0 grid grid-cols-[minmax(0,auto)_minmax(0,1fr)] gap-x-3 gap-y-1.5 border-b pb-3.5 text-[13px] [&>dd]:m-0 [&>dd]:text-right [&>dt]:m-0 [&>dt]:text-ink-secondary">
        <dt>Source</dt>
        <dd>
          {current.source_name ?? current.source_id ?? "—"}
          {current.source_deleted ? " (deleted)" : ""}
        </dd>
        <dt>Topic</dt>
        <dd>
          {current.topic_key ?? current.topic_name ?? current.topic_id ?? "—"}
          {current.topic_deleted ? " (deleted)" : ""}
        </dd>
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
        <CopyValue
          id="event-trace-id"
          label="Trace id"
          value={current.trace_id}
          action={
            // Only when the deployment configured a tracing product; the link leaves the dashboard,
            // so it opens a new tab that cannot reach back into this one.
            current.trace_url ? (
              <Button asChild variant="outline">
                <a href={current.trace_url} target="_blank" rel="noopener noreferrer">
                  Open trace
                </a>
              </Button>
            ) : null
          }
        />
      ) : (
        <p>This Event carries no trace identity.</p>
      )}

      <section className="flex flex-col gap-2">
        <h3 className="eyebrow">Event deliveries</h3>
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
                className="flex items-center justify-between gap-2 rounded-md bg-surface-quiet px-2.5 py-2"
              >
                <div className="min-w-0 text-[13px]">
                  <span className="block truncate">
                    {delivery.subscription_name ?? delivery.subscription_id}
                    {delivery.subscription_deleted ? " (deleted)" : ""}
                  </span>
                  <span className="block truncate font-mono text-xs text-ink-secondary">
                    → {delivery.destination_name ?? nameIn(destinationOptions.data?.items, delivery.destination_id)}
                    {delivery.destination_deleted ? " (deleted)" : ""}
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
                      // A replayed Delivery leaves the dead-lettered backlog the strip and rail count.
                      void queryClient.invalidateQueries({ queryKey: ["event-backlog", tenantId] });
                    }}
                  />
                </div>
              </li>
            ))}
          </ul>
        ) : current.status === "unrouted" && current.unrouted_actionable && current.topic_id && current.event_type ? (
          // Unrouted means no active Subscription matched this type, so the missing Subscription is
          // the answer; the sheet opens on arrival with this Event's Topic and type already chosen.
          <div className="flex flex-col items-start gap-2">
            <p className="m-0 text-[13px]">
              No active Subscription matches <code>{current.event_type}</code> on this Topic.
            </p>
            <Button asChild size="sm">
              <Link
                to={`/tenants/${tenantId}/subscriptions?topic_id=${current.topic_id}`}
                state={{ createSubscriptionFor: current.event_type }}
              >
                Create Subscription
              </Link>
            </Button>
          </div>
        ) : current.status === "unrouted" && current.unrouted_has_current_match ? (
          <p className="m-0 text-[13px]">
            A matching active Subscription now exists. This retained Event remains unrouted; later matching Events use
            the current routing.
          </p>
        ) : current.status === "unrouted" ? (
          // Historical-only: the Event stays as it was, but current configuration can no longer
          // route its type, so a Subscription for it could not be authored.
          current.topic_deleted ? (
            <p className="m-0 text-[13px]">
              This Event's Topic has been deleted, so current configuration can no longer route it.
            </p>
          ) : (
            <p className="m-0 text-[13px]">
              No Source on this Topic declares <code>{current.event_type}</code> any more, so current configuration can
              no longer route it.
            </p>
          )
        ) : (
          <p className="m-0 text-[13px]">No deliveries.</p>
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
            className="m-0 list-none border-l border-dotted pl-5 text-[13px]"
            aria-label="Every delivery attempt for this Event"
          >
            {shownAttempts.map((attempt) => {
              // Only a terminal "failed" attempt gets the failure marker and its detail line.
              // "in_progress" (leased but not yet finished) and any other in-flight status are
              // neither success nor failure yet, and must not be painted red on a guess.
              const failed = attempt.status === "failed";
              return (
                <li
                  key={attempt.attempt_id}
                  className={cn(
                    "relative pb-4 last:pb-0 before:absolute before:top-1 before:-left-[25px] before:size-2.5 before:rounded-full before:content-['']",
                    statusMarker(attempt.status),
                  )}
                >
                  {/* Time, then outcome, then what was attempted. The badge sits at the same place
                      on every entry so the column of them is readable top to bottom, which is how
                      an Operator finds the attempt that failed rather than reading each sentence. */}
                  <p className="m-0 flex flex-wrap items-center gap-x-2 gap-y-1">
                    <span className="font-semibold">
                      <Timestamp value={attempt.started_at} />
                    </span>
                    <StatusBadge status={attempt.status} />
                    <span className="text-ink-secondary">
                      attempt {attempt.attempt_number} to Subscription{" "}
                      <span className="font-mono">{attempt.subscription_id}</span>
                    </span>
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
          <p className="m-0 text-[13px]">No delivery attempts have been made for this Event.</p>
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
        variant="outline"
        question={`Replay the dead-lettered delivery to Subscription ${delivery.subscription_id}? It is queued for delivery again.`}
        confirmLabel="Replay this delivery"
        busy={replay.isPending}
        onConfirm={() => replay.mutate()}
      />
      <FormError message={formError(asProblem(replay.error))} />
    </div>
  );
}
