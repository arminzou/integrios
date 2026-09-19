import { cleanup, screen, within } from "@testing-library/react";
import type { ReactElement } from "react";
import { afterEach, expect, it } from "vitest";
import { page, stubHttp } from "../test/http";
import { renderScreen } from "../test/router";
import { ConnectorsScreen } from "./Connectors";
import { DestinationsScreen } from "./Destinations";
import { EventsScreen } from "./Events";
import { SourcesScreen } from "./Sources";
import { SubscriptionsScreen } from "./Subscriptions";
import { TenantApiKeysScreen } from "./TenantApiKeys";
import { TenantsScreen } from "./Tenants";
import { TopicsScreen } from "./Topics";

afterEach(cleanup);

const tenantId = "11111111-1111-1111-1111-111111111111";

/// A filter bar states its scope the same way on every list: controls run from specific to broad,
/// a search box is named for the field it matches and says how it matches, and lifecycle is Status
/// (State on API keys, which carry none). A list under an applied filter keeps its bar with no
/// rows, so each screen is rendered empty and asked only what its bar holds, in order.
type Control = { role: "searchbox" | "combobox" | "button"; name: string | RegExp; placeholder?: string };

const bars: { screen: string; element: ReactElement; url: string; controls: Control[] }[] = [
  {
    screen: "Tenants",
    element: <TenantsScreen />,
    url: "/tenants?status=active",
    controls: [
      { role: "searchbox", name: "Name or slug", placeholder: "Name or slug contains…" },
      { role: "combobox", name: "Environment" },
      { role: "combobox", name: "Status" },
    ],
  },
  {
    screen: "Connectors",
    element: <ConnectorsScreen />,
    url: "/connectors?direction=both",
    controls: [{ role: "combobox", name: "Direction" }],
  },
  {
    screen: "API keys",
    element: <TenantApiKeysScreen tenantId={tenantId} />,
    url: `/tenants/${tenantId}/api-keys?state=active`,
    controls: [{ role: "combobox", name: "State" }],
  },
  {
    screen: "Topics",
    element: <TopicsScreen tenantId={tenantId} />,
    url: `/tenants/${tenantId}/topics?name=x`,
    controls: [{ role: "searchbox", name: "Name", placeholder: "Name contains…" }],
  },
  {
    screen: "Sources",
    element: <SourcesScreen tenantId={tenantId} />,
    url: `/tenants/${tenantId}/sources?status=active`,
    controls: [
      { role: "combobox", name: "Type" },
      { role: "combobox", name: "Topic" },
      { role: "combobox", name: "Status" },
    ],
  },
  {
    screen: "Destinations",
    element: <DestinationsScreen tenantId={tenantId} />,
    url: `/tenants/${tenantId}/destinations?status=active`,
    controls: [
      { role: "searchbox", name: "Name", placeholder: "Name contains…" },
      { role: "combobox", name: "Connector" },
      { role: "combobox", name: "Environment" },
      { role: "combobox", name: "Status" },
    ],
  },
  {
    screen: "Events",
    element: <EventsScreen tenantId={tenantId} />,
    url: `/tenants/${tenantId}/events?status=routed`,
    controls: [
      { role: "searchbox", name: "Source Event id", placeholder: "Exact id…" },
      { role: "searchbox", name: "Event type", placeholder: "Exact type, e.g. order.created…" },
      { role: "combobox", name: "Source" },
      { role: "combobox", name: "Topic" },
      { role: "combobox", name: "Event status" },
      { role: "combobox", name: "Delivery status" },
      { role: "button", name: /^Accepted/ },
    ],
  },
  {
    screen: "Subscriptions",
    element: <SubscriptionsScreen tenantId={tenantId} />,
    url: `/tenants/${tenantId}/subscriptions?status=active`,
    controls: [
      { role: "searchbox", name: "Name", placeholder: "Name contains…" },
      { role: "combobox", name: "Topic" },
      { role: "combobox", name: "Destination" },
      { role: "combobox", name: "Status" },
    ],
  },
];

it.each(bars)(
  "$screen bar holds its controls in the stated order, named as stated",
  async ({ element, url, controls }) => {
    stubHttp(() => ({ status: 200, body: page([]) }));
    renderScreen(element, url);

    const filters = await screen.findByRole("region", { name: "Filters" });
    const expected = controls.map(({ role, name, placeholder }) => {
      const control = within(filters).getByRole(role, { name });
      if (placeholder !== undefined) expect(control.getAttribute("placeholder")).toBe(placeholder);
      return control;
    });
    const inBar = [...filters.querySelectorAll("input, button")];
    // Clear filters follows the controls, so the order is checked over the controls alone.
    expect(inBar.slice(0, expected.length)).toEqual(expected);
  },
);
