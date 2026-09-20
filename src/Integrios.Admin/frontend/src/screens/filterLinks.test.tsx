import { cleanup, screen, waitFor } from "@testing-library/react";
import type { ReactElement } from "react";
import { afterEach, expect, it } from "vitest";
import { activityOf, type Call, page, quietBacklog, stubHttp } from "../test/http";
import { renderScreen } from "../test/router";
import { backlogs, EventsScreen } from "./Events";
import { SourcesScreen } from "./Sources";
import { SubscriptionsScreen } from "./Subscriptions";

afterEach(cleanup);

const tenantId = "11111111-1111-1111-1111-111111111111";
const topicId = "22222222-2222-2222-2222-222222222222";
const sourceId = "33333333-3333-3333-3333-333333333333";

/// Filter parameter names are a contract. Cross-screen links, backlog links, chart selections and
/// copied links are hand-built strings that name the Admin API's own parameters, so a rename would
/// silently drop the scope a link promised. Each inbound link is pinned from the receiving side:
/// the URL the sender builds must land on the screen as the applied filter it names, in the
/// request the list makes and in the pill that states it.
const respond = ({ url }: Call) => {
  if (url.pathname.endsWith("/events/backlog")) return { status: 200, body: quietBacklog };
  if (url.pathname.endsWith("/events/activity")) return { status: 200, body: activityOf() };
  return { status: 200, body: page([]) };
};

const listRead = (calls: Call[], suffix: string) =>
  calls.filter(({ url }) => url.pathname.endsWith(suffix) && url.searchParams.get("limit") === "20").at(-1);

type Link = {
  from: string;
  path: string;
  screen: ReactElement;
  suffix: string;
  expected: Record<string, string>;
  pill: string;
};

const links: Link[] = [
  {
    from: "SourceGuide → Events",
    path: `/tenants/${tenantId}/events?source_id=${sourceId}`,
    screen: <EventsScreen tenantId={tenantId} />,
    suffix: "/events",
    expected: { source_id: sourceId },
    pill: "Source",
  },
  {
    // The Event inspector and a Topic both link here with the same parameter, so one case covers both.
    from: "Events and Topics → Subscriptions",
    path: `/tenants/${tenantId}/subscriptions?topic_id=${topicId}`,
    screen: <SubscriptionsScreen tenantId={tenantId} />,
    suffix: "/subscriptions",
    expected: { topic_id: topicId },
    pill: "Topic",
  },
  {
    from: "Subscriptions → Sources",
    path: `/tenants/${tenantId}/sources?topic_id=${topicId}`,
    screen: <SourcesScreen tenantId={tenantId} />,
    suffix: "/sources",
    expected: { topic_id: topicId },
    pill: "Topic",
  },
  ...backlogs.map(({ key, query }) => ({
    from: `backlog ${key} → Events`,
    path: `/tenants/${tenantId}/events?${query}`,
    screen: <EventsScreen tenantId={tenantId} />,
    suffix: "/events",
    expected: Object.fromEntries(new URLSearchParams(query)),
    pill: query.startsWith("delivery_status") ? "Delivery status" : "Event status",
  })),
];

it.each(links)("$from lands on the scope it names", async ({ path, screen: element, suffix, expected, pill }) => {
  const calls = stubHttp(respond);
  renderScreen(element, path);

  await waitFor(() => expect(listRead(calls, suffix)).toBeTruthy());
  expect(Object.fromEntries(listRead(calls, suffix)!.url.searchParams)).toMatchObject(expected);
  // The pill states the scope the list is reading under, so link and control cannot disagree.
  const control = await screen.findByRole("combobox", { name: pill });
  expect(control.getAttribute("data-applied")).toBe("true");
});
