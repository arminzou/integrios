import { vi } from "vitest";

export type Call = { method: string; url: URL; headers: Headers; body: unknown };

/// Stands in for the Admin API so a workflow test exercises the real typed client, the real request
/// the screen builds, and the real Problem Details handling — everything except the network.
export function stubHttp(
  respond: (
    call: Call,
  ) =>
    | { status: number; body?: unknown; headers?: HeadersInit }
    | Promise<{ status: number; body?: unknown; headers?: HeadersInit }>,
) {
  const calls: Call[] = [];

  vi.stubGlobal(
    "fetch",
    vi.fn(async (input: Request | string, init?: RequestInit) => {
      // The typed client addresses the Admin API absolutely, but the session bootstrap calls
      // `fetch("/auth/session")` directly, and `Request` rejects a relative URL. Resolving against
      // the page's own origin is what the browser does with the same call.
      const request =
        input instanceof Request ? input : new Request(new URL(input, "http://localhost").toString(), init);
      const text = await request.clone().text();
      const call: Call = {
        method: request.method,
        url: new URL(request.url, "http://localhost"),
        headers: new Headers(request.headers),
        body: text === "" ? undefined : (JSON.parse(text) as unknown),
      };
      calls.push(call);

      const { status, body, headers } = await respond(call);
      const responseHeaders = new Headers(headers);
      responseHeaders.set("content-type", "application/problem+json");
      // An Admin action that answers with no body still answers with JSON here, because the stub
      // has no way to signal an empty body that the client will not try to parse.
      return new Response(JSON.stringify(body ?? {}), {
        status,
        headers: responseHeaders,
      });
    }),
  );

  return calls;
}

/// The Event backlog of a Tenant with nothing waiting, for tests whose subject is not the backlog.
export const quietBacklog = {
  awaiting_routing: { count: 0, oldest_at: null },
  unrouted: { count: 0, oldest_at: null },
  dead_lettered_deliveries: { count: 0, oldest_at: null },
};

type ActivityCounts = {
  awaiting_routing?: number;
  unrouted?: number;
  delivery_dead_lettered?: number;
  routed?: number;
};

/// An Event activity read as the Admin API returns it: ordered, zero-filled buckets of equal length
/// from a fixed start, with the counts given per bucket index.
export function activityOf(
  counts: Record<number, ActivityCounts> = {},
  { range = "1h", start = "2026-09-01T09:00:00Z", bucketMinutes = 5, bucketCount = 12 } = {},
) {
  const at = (index: number) => new Date(Date.parse(start) + index * bucketMinutes * 60_000).toISOString();
  return {
    range,
    window_start: at(0),
    window_end: at(bucketCount),
    buckets: Array.from({ length: bucketCount }, (_, index) => ({
      start: at(index),
      end: at(index + 1),
      awaiting_routing: 0,
      unrouted: 0,
      delivery_dead_lettered: 0,
      routed: 0,
      ...counts[index],
    })),
  };
}

/// One cursor page as every Admin list returns it.
export function page(items: unknown[], nextCursor: string | null = null) {
  return { items, next_cursor: nextCursor };
}
