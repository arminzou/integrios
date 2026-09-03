import { vi } from "vitest";

export type Call = { method: string; url: URL; body: unknown };

/// Stands in for the Admin API so a workflow test exercises the real typed client, the real request
/// the screen builds, and the real Problem Details handling — everything except the network.
export function stubHttp(respond: (call: Call) => { status: number; body?: unknown }) {
  const calls: Call[] = [];

  vi.stubGlobal(
    "fetch",
    vi.fn(async (input: Request | string, init?: RequestInit) => {
      const request = input instanceof Request ? input : new Request(input, init);
      const text = await request.clone().text();
      const call: Call = {
        method: request.method,
        url: new URL(request.url, "http://localhost"),
        body: text === "" ? undefined : (JSON.parse(text) as unknown),
      };
      calls.push(call);

      const { status, body } = respond(call);
      // An Admin action that answers with no body still answers with JSON here, because the stub
      // has no way to signal an empty body that the client will not try to parse.
      return new Response(JSON.stringify(body ?? {}), {
        status,
        headers: { "content-type": "application/problem+json" },
      });
    }),
  );

  return calls;
}

/// One cursor page as every Admin list returns it.
export function page(items: unknown[], nextCursor: string | null = null) {
  return { items, next_cursor: nextCursor };
}
