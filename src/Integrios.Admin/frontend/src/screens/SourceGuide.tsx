import { useQuery } from "@tanstack/react-query";
import { X } from "lucide-react";
import { Dialog as DialogPrimitive } from "radix-ui";
import { useEffect, useRef, useState } from "react";
import { Link, useLocation, useNavigate } from "react-router";
import { Button } from "@/components/ui/button";
import { api } from "../api/client";
import { asProblem, call } from "../api/query";
import type { components } from "../api/schema";
import { BodyPanel } from "../ui/copy";
import { Details } from "../ui/layout";

type Source = components["schemas"]["SourceDto"];
type JsonObject = Record<string, unknown>;

export type SourceGuideContext = {
  subscriptionId: string;
  subscriptionPath: string;
  eventType: string;
  payload: Record<string, unknown>;
  advancedMapping: boolean;
};

function object(value: unknown): JsonObject {
  return value !== null && typeof value === "object" && !Array.isArray(value) ? (value as JsonObject) : {};
}

function text(value: unknown, key: string): string | null {
  const found = object(value)[key];
  return typeof found === "string" && found.trim() ? found : null;
}

function append(base: string, path: string) {
  return `${base.replace(/\/$/, "")}${path}`;
}

function shellQuote(value: string) {
  return `'${value.replaceAll("'", `'"'"'`)}'`;
}

export function SourceGuide({ tenantId, source }: { tenantId: string; source: Source }) {
  const location = useLocation();
  const navigate = useNavigate();
  const routeState = location.state as { openSourceGuide?: string; sourceGuideContext?: SourceGuideContext } | null;
  const [open, setOpen] = useState(routeState?.openSourceGuide === source.id);
  const [context] = useState(() =>
    routeState?.openSourceGuide === source.id ? routeState.sourceGuideContext : undefined,
  );
  const trigger = useRef<HTMLButtonElement>(null);
  const active = source.status === "active";
  useEffect(() => {
    if (routeState?.openSourceGuide !== source.id) return;
    navigate(`${location.pathname}${location.search}`, { replace: true, state: null });
  }, [location.pathname, location.search, navigate, routeState?.openSourceGuide, source.id]);
  const connectorId = source.connector_id;
  const connector = useQuery({
    queryKey: ["connector", connectorId],
    queryFn: () => call(() => api.GET("/admin/connectors/{id}", { params: { path: { id: connectorId } } })),
    enabled: open && Boolean(connectorId),
  });
  const topic = useQuery({
    queryKey: ["topic", tenantId, source.topic_id],
    queryFn: () =>
      call(() =>
        api.GET("/admin/tenants/{tenantId}/topics/{id}", { params: { path: { tenantId, id: source.topic_id } } }),
      ),
    enabled: open,
  });
  const overview = useQuery({
    queryKey: ["tenant-overview", tenantId],
    queryFn: () => call(() => api.GET("/admin/tenants/{id}/overview", { params: { path: { id: tenantId } } })),
    enabled: open && source.type !== "queue",
  });

  return (
    <section className="flex flex-col gap-2 border-b pb-3.5" aria-labelledby="connect-source-heading">
      <h3 id="connect-source-heading" className="m-0 text-sm">
        Connect this Source
      </h3>
      <p className="m-0 text-[13px] text-ink-secondary">
        {source.type === "queue"
          ? "The Publisher sends to the configured broker entity; Integrios consumes and publishes accepted Events to this Source's Topic."
          : "The Publisher sends to this Source; Integrios validates the input and publishes accepted Events to its Topic."}
      </p>
      {!active ? <p className="m-0 text-sm text-destructive">This Source cannot accept new Events.</p> : null}
      <DialogPrimitive.Root open={open} onOpenChange={setOpen}>
        <DialogPrimitive.Trigger asChild>
          <Button ref={trigger} type="button" variant="outline" size="sm" className="self-start">
            Open setup guide
          </Button>
        </DialogPrimitive.Trigger>
        <DialogPrimitive.Portal>
          <DialogPrimitive.Overlay className="fixed inset-0 z-60 bg-ink/25" />
          <DialogPrimitive.Content
            className="fixed inset-4 z-70 flex max-h-[calc(100vh-2rem)] min-w-0 flex-col gap-5 overflow-y-auto rounded-lg border bg-surface p-4 shadow-[0_24px_64px_-32px_rgb(23_23_23/0.45)] outline-none md:inset-x-10 lg:inset-x-[max(2.5rem,calc((100vw-72rem)/2))]"
            onCloseAutoFocus={(event) => {
              event.preventDefault();
              trigger.current?.focus();
            }}
          >
            <header className="flex items-start justify-between gap-3">
              <div className="min-w-0">
                <DialogPrimitive.Title className="m-0">Publish through this Source</DialogPrimitive.Title>
                <DialogPrimitive.Description className="m-0 mt-1 text-sm text-ink-secondary">
                  {intro(source.type)}
                </DialogPrimitive.Description>
              </div>
              <DialogPrimitive.Close
                aria-label="Back to Source"
                className="flex size-8 shrink-0 items-center justify-center rounded-md hover:bg-hover-surface focus-visible:ring-[3px] focus-visible:ring-ring/50"
              >
                <X aria-hidden="true" className="size-4" />
              </DialogPrimitive.Close>
            </header>

            {!active ? (
              <div className="rounded-lg bg-danger-surface p-3 text-sm text-danger-ink">
                <strong>This Source cannot accept new Events.</strong> Its former configuration remains visible for
                historical Event attribution, but runnable copy actions are unavailable.
              </div>
            ) : null}

            <ol
              aria-label="Event path"
              className="m-0 flex list-none flex-wrap items-center gap-2 rounded-lg bg-surface-quiet p-3 text-sm"
            >
              <li className="rounded-full border bg-surface px-3 py-1">External Publisher</li>
              <li aria-hidden="true">→</li>
              <li className="rounded-full border bg-surface px-3 py-1">{source.type} Source</li>
              <li aria-hidden="true">→</li>
              <li className="min-w-0 rounded-full border bg-surface px-3 py-1 break-all">
                {topic.data?.name ?? source.topic_id}
              </li>
              <li aria-hidden="true">→</li>
              <li className="rounded-full border bg-surface px-3 py-1">Matching Subscriptions</li>
            </ol>

            <GuideBody
              tenantId={tenantId}
              source={source}
              active={active}
              connector={connector.data}
              topicName={topic.data?.name}
              ingestionEndpoint={overview.data?.ingestion_endpoint}
              context={context}
              problem={asProblem(connector.error ?? topic.error ?? overview.error)?.detail}
            />

            <section className="grid gap-4 md:grid-cols-2">
              <div className="rounded-lg border p-4">
                <h3 className="mt-0 text-base">Expected result</h3>
                <p className="mb-2 text-sm">
                  {active
                    ? "Integrios accepts the Event durably before asynchronous routing and delivery. The accepted Event appears in the Tenant ledger with this Source and Topic."
                    : "When this Source was active, accepted Events appeared in the Tenant ledger with this Source and Topic."}
                </p>
                <Button asChild variant="outline" size="sm">
                  <Link className="no-underline" to={`/tenants/${tenantId}/events?source_id=${source.id}`}>
                    Open accepted Events
                  </Link>
                </Button>
              </div>
              <div className="rounded-lg border p-4">
                <h3 className="mt-0 text-base">If nothing arrives downstream</h3>
                <p className="m-0 text-sm">
                  A rejected input never becomes an Event. An accepted Event whose <code>event_type</code> matches no
                  active Subscription remains unrouted; inspect the Event ledger before changing the Source.
                </p>
              </div>
            </section>
          </DialogPrimitive.Content>
        </DialogPrimitive.Portal>
      </DialogPrimitive.Root>
    </section>
  );
}

function GuideBody({
  tenantId,
  source,
  active,
  connector,
  topicName,
  ingestionEndpoint,
  context,
  problem,
}: {
  tenantId: string;
  source: Source;
  active: boolean;
  connector?: components["schemas"]["ConnectorDto"];
  topicName?: string;
  ingestionEndpoint?: string | null;
  context?: SourceGuideContext;
  problem?: string;
}) {
  if (problem) return <p role="alert">{problem}</p>;
  if (!connector || !topicName || (source.type !== "queue" && !ingestionEndpoint)) return <p>Loading guide…</p>;

  const contract = source.input_requirements ? object(source.input_requirements) : null;
  const baseUri = ingestionEndpoint ?? "";

  return (
    <>
      <section className="rounded-lg border p-4" aria-labelledby="source-guide-facts">
        <h3 id="source-guide-facts" className="mt-0 text-base">
          Resolved configuration
        </h3>
        <Details>
          <dt>Connector</dt>
          <dd>
            {connector.name} ({connector.key} v{connector.contract_version})
          </dd>
          <dt>Input requirements</dt>
          <dd>{contract ? "Configured" : "Not configured"}</dd>
          <dt>Topic</dt>
          <dd>{topicName}</dd>
          <dt>Trust</dt>
          <dd>{trust(source)}</dd>
        </Details>
      </section>

      {context ? (
        <p className="m-0 text-sm">
          Subscription context · Event type: <code>{context.eventType || "—"}</code>
        </p>
      ) : null}

      {source.type === "event_api" ? (
        <EventApiGuide tenantId={tenantId} source={source} active={active} baseUri={baseUri} context={context} />
      ) : source.type === "webhook" ? (
        <WebhookGuide source={source} active={active} baseUri={baseUri} contract={contract} />
      ) : (
        <QueueGuide source={source} active={active} contract={contract} />
      )}
      {context?.advancedMapping ? (
        <p className="m-0 text-sm text-ink-secondary">
          This Subscription uses advanced JSONata, so payload fields are not inferred.{" "}
          <Link to={context.subscriptionPath} state={{ openSubscriptionPlayground: context.subscriptionId }}>
            Open its Mapping Playground
          </Link>
          .
        </p>
      ) : null}
    </>
  );
}

function EventApiGuide({
  tenantId,
  source,
  active,
  baseUri,
  context,
}: {
  tenantId: string;
  source: Source;
  active: boolean;
  baseUri: string;
  context?: SourceGuideContext;
}) {
  const endpoint = append(baseUri, `/events?source_id=${encodeURIComponent(source.id)}`);
  const envelope = {
    event_type: context?.eventType || "<event-type>",
    source_event_id: "<stable-source-event-id>",
    payload: context?.payload ?? {},
  };
  const json = JSON.stringify(envelope, null, 2);
  const http = `POST ${endpoint}\nAuthorization: Bearer <TenantApiKey>\nContent-Type: application/json\n\n${json}`;
  const curl = `curl --request POST ${shellQuote(endpoint)} \\\n  --header 'Authorization: Bearer <TenantApiKey>' \\\n  --header 'Content-Type: application/json' \\\n  --data ${shellQuote(JSON.stringify(envelope))}`;
  const csharp = `using System.Net.Http.Headers;\nusing System.Net.Http.Json;\nusing System.Text.Json.Nodes;\n\nusing var client = new HttpClient();\nclient.DefaultRequestHeaders.Authorization =\n    new AuthenticationHeaderValue("Bearer", "<TenantApiKey>");\n\nusing var response = await client.PostAsync(\n    "${endpoint}",\n    JsonContent.Create(JsonNode.Parse("""\n${json}\n""")));\nresponse.EnsureSuccessStatusCode();`;

  return (
    <section className="flex flex-col gap-4" aria-labelledby="event-api-guide">
      <div>
        <h3 id="event-api-guide" className="mt-0 text-base">
          Construct the Event request
        </h3>
        <p className="m-0 text-sm">
          Address this Source by id. <code>source_event_id</code> is the stable source identity used for idempotency,
          <code> event_type</code> selects matching Subscriptions, and <code>payload</code> is their mapping root. Use a
          live key created under <Link to={`/tenants/${tenantId}/tenant-api-keys`}>Tenant API keys</Link>; existing keys
          cannot be recovered here.
        </p>
      </div>
      <div className="grid min-w-0 gap-4 lg:grid-cols-3">
        <BodyPanel label="HTTP request" value={http} copyable={active} />
        <BodyPanel label="cURL request" value={curl} copyable={active} />
        <BodyPanel label="C# HttpClient request" value={csharp} copyable={active} />
      </div>
    </section>
  );
}

function WebhookGuide({
  source,
  active,
  baseUri,
  contract,
}: {
  source: Source;
  active: boolean;
  baseUri: string;
  contract: JsonObject | null;
}) {
  const callbackId = text(source.configuration, "callback_id");
  const callback = callbackId ? append(baseUri, `/webhooks/${callbackId}`) : "Callback identity unavailable";
  const schema = contract?.schema;

  return (
    <section className="flex flex-col gap-4" aria-labelledby="webhook-guide">
      <div>
        <h3 id="webhook-guide" className="mt-0 text-base">
          Configure the provider callback
        </h3>
        <p className="m-0 text-sm">
          Give the external provider this stable URL. It sends its native JSON request; Integrios verifies and
          normalizes it through the selected Source contract. Do not wrap it in the Event API envelope.
        </p>
      </div>
      <BodyPanel label="Webhook callback URL" value={callback} copyable={active && Boolean(callbackId)} />
      <p className="m-0 text-sm">
        Required media type: <code>application/json</code>
      </p>
      {schema !== undefined ? (
        <BodyPanel label="Declared native input schema" value={schema} copyable={active} />
      ) : (
        <p className="m-0 text-sm text-ink-secondary">
          This contract declares no native example or schema. Use the provider's JSON payload and the selected
          verification scheme; Integrios does not invent provider fields.
        </p>
      )}
    </section>
  );
}

function QueueGuide({ source, active, contract }: { source: Source; active: boolean; contract: JsonObject | null }) {
  const configuration = object(source.configuration);
  const transport = object(configuration.transport_config);
  const namespace = text(transport, "namespace") ?? "—";
  const queue = text(transport, "queue_name");
  const topic = text(transport, "topic_name");
  const subscription = text(transport, "subscription_name");
  const address = queue ? `${namespace}/${queue}` : `${namespace}/${topic ?? "—"}/subscriptions/${subscription ?? "—"}`;

  return (
    <section className="flex flex-col gap-4" aria-labelledby="queue-guide">
      <div>
        <h3 id="queue-guide" className="mt-0 text-base">
          Publish to the broker
        </h3>
        <p className="m-0 text-sm">
          The external Publisher sends to Azure Service Bus, not an Integrios HTTP endpoint. Integrios owns the
          receiver, completes accepted messages, abandons transient failures for redelivery, and dead-letters malformed
          or rejected input.
        </p>
      </div>
      <BodyPanel label="Broker entity" value={address} copyable={active} />
      {contract?.schema !== undefined ? (
        <BodyPanel label="Declared native input schema" value={contract.schema} copyable={active} />
      ) : (
        <p className="m-0 text-sm text-ink-secondary">
          This mapped Source contract declares no safe message example. Send its native JSON input; the contract derives
          the Event type, source identity, and payload.
        </p>
      )}
    </section>
  );
}

function intro(type: string) {
  if (type === "event_api")
    return "An Event API client constructs the Integrios Event envelope and authenticates with a TenantApiKey.";
  if (type === "webhook")
    return "An external provider posts its native request to the Source callback; Integrios verifies and normalizes it.";
  return "An external Publisher sends to the configured broker entity; Integrios consumes and normalizes the message.";
}

function trust(source: Source) {
  if (source.type === "event_api") return "TenantApiKey";
  if (source.type === "webhook") return source.verification?.scheme ?? "Unverified";
  return text(object(source.configuration).authentication, "scheme") ?? "—";
}
