#!/usr/bin/env node
// Measures the Delivery API design's two load checks against one running verification stack.
// Requires the run's env (source artifacts/verify/<run>/state/env). Drives only public APIs.
//   node measure.mjs fault <push|pull> <out-dir>   Connector X's handler stalls 60 s; times Connector Y
//   node measure.mjs intake <out-dir>              Ingestion latency without and with Subscriber load
// Writes samples.jsonl (one line per Event or intake request) and summary.json to <out-dir>.
// Durations and rates come from MEASURE_* variables; the defaults are the recorded method.
import { mkdir, readFile, writeFile } from "node:fs/promises";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const { ADMIN, AUTH, INGESTION, DELIVERY_API, MOCKSINK } = process.env;
if (!ADMIN || !AUTH) throw new Error("Source the run env first.");
const num = (name, fallback) => Number(process.env[name] ?? fallback);
const PULL_CONNECTOR = "5ab5c71b-e000-4000-8000-000000000001";
const STALL_MS = 60_000;
const root = resolve(dirname(fileURLToPath(import.meta.url)), "../../../..");
const sleep = (ms, signal) => new Promise((done) => {
  const t = setTimeout(done, Math.max(0, ms));
  signal?.addEventListener("abort", () => { clearTimeout(t); done(); }, { once: true });
});
const slug = `m${Date.now().toString(36)}`;

async function call(base, method, path, body, headers = {}) {
  const response = await fetch(base + path, {
    method,
    headers: { "Content-Type": "application/json", ...headers },
    body: body === undefined ? undefined : typeof body === "string" ? body : JSON.stringify(body),
    signal: AbortSignal.timeout(30_000),
  });
  const text = await response.text();
  if (!response.ok) throw new Error(`${method} ${path} answered ${response.status}: ${text}`);
  return text ? JSON.parse(text) : null;
}
const authName = AUTH.slice(0, AUTH.indexOf(":")), authValue = AUTH.slice(AUTH.indexOf(":") + 1).trim();
const admin = (method, path, body) => call(ADMIN, method, path, body, { [authName]: authValue });

async function seedTenant(eventTypes) {
  const connector = (await admin("PUT", "/admin/connectors/http/versions/1",
    await readFile(resolve(root, "examples/connectors/http.json"), "utf8"))).connector.id;
  const tenant = (await admin("POST", "/admin/tenants", { slug, name: `Measure ${slug}`, environment: "production" })).id;
  const token = (await admin("POST", `/admin/tenants/${tenant}/tenant-api-keys`, { name: "measure" })).token;
  const topic = (await admin("POST", `/admin/tenants/${tenant}/topics`, { key: "measure", name: "Measure" })).id;
  const source = (await admin("POST", `/admin/tenants/${tenant}/sources`, {
    connector_id: connector, topic_id: topic, name: `${slug}-intake`, type: "event_api", event_types: eventTypes,
    configuration: {}, verification: null, input_requirements: null, mapping: null, event_identity_rule: null,
  })).id;
  await admin("POST", `/admin/tenants/${tenant}/sources/${source}/activate`, "");
  return { connector, tenant, token, topic, source };
}

async function destination(t, name, side) {
  const body = side === "push"
    ? { connector_id: t.connector, name: `${slug}-${name}`, configuration: { base_uri: `${MOCKSINK}/sink/${slug}-${name}` }, authentication: null, environment: "production" }
    : { connector_id: PULL_CONNECTOR, name: `${slug}-${name}`, configuration: {} };
  const id = (await admin("POST", `/admin/tenants/${t.tenant}/destinations`, body)).id;
  const key = side === "pull"
    ? (await admin("POST", `/admin/tenants/${t.tenant}/destinations/${id}/subscriber-keys`, { name })).token : null;
  return { id, key };
}

async function subscribe(t, name, eventType, destinationId, order) {
  const id = (await admin("POST", `/admin/tenants/${t.tenant}/topics/${t.topic}/subscriptions`, {
    name: `${slug}-${name}`, event_types: [eventType], destination_id: destinationId,
    mapping: null, http_delivery: null, http_success: null, order_index: order,
  })).id;
  await admin("POST", `/admin/tenants/${t.tenant}/topics/${t.topic}/subscriptions/${id}/activate`, "");
}

let sequence = 0;
async function postEvent(t, eventType) {
  const at = new Date().toISOString(), started = performance.now();
  const response = await fetch(`${INGESTION}/events?source_id=${t.source}`, {
    method: "POST",
    headers: { "Content-Type": "application/json", Authorization: `Bearer ${t.token}` },
    body: JSON.stringify({ event_type: eventType, source_event_id: `${slug}-${++sequence}`, payload: { n: sequence } }),
    signal: AbortSignal.timeout(30_000),
  }).catch((error) => ({ status: 0, error }));
  const latency_ms = performance.now() - started;
  const body = response.status === 202 ? await response.json() : null;
  if (response.text && response.status !== 202) await response.text();
  return { at, status: response.status, latency_ms, event_id: body?.event_id ?? null };
}

// Posts on a fixed schedule, never waiting for a reply, so a slow answer cannot slow the rate.
async function steady(seconds, perSecond, send) {
  const pending = [];
  const interval = 1000 / perSecond;
  const start = performance.now();
  for (let i = 0; i < seconds * perSecond; i++) {
    await sleep(start + i * interval - performance.now());
    pending.push(send());
  }
  return Promise.all(pending);
}

// The SDK's claim loop: claim one with a 20-second long-poll, handle, ack.
function subscriberLoop(key, handle, signal, stats, waitSeconds = 20) {
  const headers = { "Content-Type": "application/json", Authorization: `Bearer ${key}` };
  return (async () => {
    while (!signal.aborted) {
      let response;
      stats.opened++;
      try {
        response = await fetch(`${DELIVERY_API}/delivery/v1/deliveries:claim`, {
          method: "POST", headers, body: JSON.stringify({ max: 1, wait_seconds: waitSeconds }), signal,
        });
      } catch (error) {
        if (!signal.aborted) { stats.errors++; await sleep(1000, signal); }
        continue;
      }
      stats.claims++;
      if (response.status === 204) { await response.arrayBuffer(); continue; }
      if (response.status !== 200) { stats.errors++; await response.arrayBuffer(); await sleep(1000, signal); continue; }
      for (const d of (await response.json()).deliveries) {
        await handle(d);
        const ack = await fetch(`${DELIVERY_API}/delivery/v1/deliveries/${d.delivery_id}:ack`, {
          method: "POST", headers, body: JSON.stringify({ lease_token: d.lease_token }),
        }).catch(() => ({ status: 0, arrayBuffer: async () => {} }));
        await ack.arrayBuffer();
        if (ack.status === 204) stats.acks++; else stats.errors++;
      }
    }
  })();
}

const quantile = (values, q) => {
  if (!values.length) return null;
  const sorted = [...values].sort((a, b) => a - b);
  return sorted[Math.min(sorted.length - 1, Math.ceil(q * sorted.length) - 1)];
};
const describe = (values) => ({
  n: values.length,
  p50_ms: quantile(values, 0.5), p95_ms: quantile(values, 0.95), max_ms: values.length ? Math.max(...values) : null,
});

async function settled(t, eventId, destinationId) {
  const view = await admin("GET", `/admin/tenants/${t.tenant}/events/${eventId}/deliveries`);
  const attempt = view.delivery_attempts.find((a) => a.destination_id === destinationId && a.status === "succeeded");
  return attempt ? { accepted_at: view.accepted_at, completed_at: attempt.completed_at } : null;
}

async function fault(side) {
  const healthy = num("MEASURE_HEALTHY_SECONDS", 30), stalled = num("MEASURE_FAULT_SECONDS", 60);
  const rate = num("MEASURE_RATE", 1), drain = num("MEASURE_DRAIN_SECONDS", 300);
  const t = await seedTenant(["x.created", "y.created"]);
  const x = await destination(t, "x", side), y = await destination(t, "y", side);
  await subscribe(t, "x", "x.created", x.id, 0);
  await subscribe(t, "y", "y.created", y.id, 1);

  let stalling = false;
  const stop = new AbortController();
  const stats = { x: { opened: 0, claims: 0, acks: 0, errors: 0 }, y: { opened: 0, claims: 0, acks: 0, errors: 0 } };
  const loops = side === "pull" ? [
    subscriberLoop(x.key, async () => { if (stalling) await sleep(STALL_MS, stop.signal); }, stop.signal, stats.x),
    subscriberLoop(y.key, async () => {}, stop.signal, stats.y),
  ] : [];
  const stall = async (on) => {
    stalling = on;
    if (side === "push" && on) {
      await call(MOCKSINK, "POST", "/__admin/mappings", {
        priority: 1, request: { method: "POST", urlPathPattern: `/sink/${slug}-x.*` },
        response: { status: 200, fixedDelayMilliseconds: STALL_MS, jsonBody: { received: true } },
      });
    }
  };

  const sent = [];
  const phase = async (name, seconds) => {
    const results = await steady(seconds, rate, async () => {
      const [rx, ry] = await Promise.all([postEvent(t, "x.created"), postEvent(t, "y.created")]);
      return [{ ...rx, connector: "x", phase: name }, { ...ry, connector: "y", phase: name }];
    });
    sent.push(...results.flat());
  };
  await phase("healthy", healthy);
  await stall(true);
  await phase("fault", stalled);

  // Y is what is measured; X is left stalled and only counted.
  const ys = sent.filter((s) => s.connector === "y" && s.event_id);
  const deadline = Date.now() + drain * 1000;
  const timings = new Map();
  while (timings.size < ys.length && Date.now() < deadline) {
    for (const s of ys) {
      if (timings.has(s.event_id)) continue;
      const done = await settled(t, s.event_id, y.id);
      if (done) timings.set(s.event_id, done);
    }
    if (timings.size < ys.length) await sleep(2000);
  }
  stop.abort();
  await Promise.all(loops);

  const samples = sent.map((s) => {
    const done = s.connector === "y" ? timings.get(s.event_id) : undefined;
    return {
      side, ...s,
      ...(done ?? {}),
      ack_latency_ms: done ? Date.parse(done.completed_at) - Date.parse(done.accepted_at) : null,
    };
  });
  const summary = { mode: "fault", side, slug, healthy_seconds: healthy, fault_seconds: stalled, rate_per_connector: rate, drain_seconds: drain, stall_ms: STALL_MS, subscribers: stats };
  for (const name of ["healthy", "fault"]) {
    const y = samples.filter((s) => s.connector === "y" && s.phase === name);
    summary[name] = {
      posted: y.length,
      intake_errors: y.filter((s) => s.status !== 202).length,
      unsettled: y.filter((s) => s.status === 202 && s.ack_latency_ms === null).length,
      y_ack: describe(y.map((s) => s.ack_latency_ms).filter((v) => v !== null)),
      x_posted: samples.filter((s) => s.connector === "x" && s.phase === name).length,
    };
  }
  return { samples, summary };
}

async function intake() {
  const rounds = num("MEASURE_ROUNDS", 5), seconds = num("MEASURE_PHASE_SECONDS", 30);
  const rate = num("MEASURE_RATE", 20), polls = num("MEASURE_IDLE_POLLS", 200), stream = num("MEASURE_CLAIM_STREAM", 4);
  const t = await seedTenant(["y.created"]);
  const idle = await destination(t, "idle", "pull"), busy = await destination(t, "stream", "pull");
  // Warm Ingestion, its connection pool and the JIT before anything is recorded.
  await steady(10, rate, () => postEvent(t, "y.created"));
  const samples = [];
  const stats = { idle: { opened: 0, claims: 0, acks: 0, errors: 0 }, stream: { opened: 0, claims: 0, acks: 0, errors: 0 } };
  for (let round = 1; round <= rounds; round++) {
    for (const name of ["baseline", "load"]) {
      const stop = new AbortController();
      const loops = name === "load" ? [
        ...Array.from({ length: polls }, () => subscriberLoop(idle.key, async () => {}, stop.signal, stats.idle)),
        ...Array.from({ length: stream }, () => subscriberLoop(busy.key, async () => {}, stop.signal, stats.stream, 0)),
      ] : [];
      if (loops.length) await sleep(3000);
      const results = await steady(seconds, rate, () => postEvent(t, "y.created"));
      samples.push(...results.map((r) => ({ round, phase: name, at: r.at, status: r.status, latency_ms: r.latency_ms })));
      stop.abort();
      await Promise.all(loops);
    }
  }
  const summary = { mode: "intake", slug, rounds, phase_seconds: seconds, rate_per_second: rate, idle_polls: polls, claim_stream: stream, subscribers: stats };
  for (const name of ["baseline", "load"]) {
    const phase = samples.filter((s) => s.phase === name);
    summary[name] = {
      requests: phase.length,
      non_202: phase.filter((s) => s.status !== 202).length,
      latency: describe(phase.filter((s) => s.status === 202).map((s) => s.latency_ms)),
      p95_by_round: Array.from({ length: rounds }, (_, i) =>
        quantile(phase.filter((s) => s.round === i + 1 && s.status === 202).map((s) => s.latency_ms), 0.95)),
    };
  }
  return { samples, summary };
}

const [mode, ...args] = process.argv.slice(2);
const out = mode === "fault" ? args[1] : args[0];
if (!out || (mode === "fault" ? !["push", "pull"].includes(args[0]) : mode !== "intake")) {
  console.error("usage: measure.mjs fault <push|pull> <out-dir> | intake <out-dir>");
  process.exit(2);
}
await mkdir(out, { recursive: true });
const { samples, summary } = mode === "fault" ? await fault(args[0]) : await intake();
await writeFile(resolve(out, "samples.jsonl"), samples.map((s) => JSON.stringify(s)).join("\n") + "\n");
await writeFile(resolve(out, "summary.json"), JSON.stringify(summary, null, 2) + "\n");
console.log(JSON.stringify(summary));
