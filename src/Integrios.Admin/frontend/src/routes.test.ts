import { describe, expect, it } from "vitest";
import { parseRoute } from "./routes";

const tenant = "11111111-1111-1111-1111-111111111111";
const topic = "22222222-2222-2222-2222-222222222222";
const subscription = "33333333-3333-3333-3333-333333333333";

describe("parseRoute", () => {
  it("keeps the Tenant from the path rather than inferring one", () => {
    expect(parseRoute(`/tenants/${tenant}/sources`)).toEqual({ name: "sources", tenantId: tenant });
    expect(parseRoute(`/tenants/${tenant}/connections/${topic}`)).toEqual({
      name: "connection",
      tenantId: tenant,
      connectionId: topic,
    });
  });

  it("carries every owning route value for a nested Subscription", () => {
    expect(parseRoute(`/tenants/${tenant}/topics/${topic}/subscriptions/${subscription}`)).toEqual({
      name: "subscription",
      tenantId: tenant,
      topicId: topic,
      subscriptionId: subscription,
    });
  });

  it("refuses a route value that is not an identifier instead of passing it to the API", () => {
    expect(parseRoute("/tenants/not-a-tenant/sources").name).toBe("unknown");
    expect(parseRoute(`/tenants/${tenant}/topics/not-a-topic`).name).toBe("unknown");
  });

  it("does not resolve a capability the dashboard does not own", () => {
    expect(parseRoute(`/tenants/${tenant}/events`).name).toBe("unknown");
    expect(parseRoute(`/tenants/${tenant}/connections/${topic}/extra`).name).toBe("unknown");
    expect(parseRoute("/connectors/all").name).toBe("unknown");
  });

  it("treats the deployment root and the Tenants list as the same screen", () => {
    expect(parseRoute("/")).toEqual({ name: "tenants" });
    expect(parseRoute("/tenants")).toEqual({ name: "tenants" });
  });
});
