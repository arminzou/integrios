import { keepPreviousData, useQuery } from "@tanstack/react-query";
import { cn } from "cn";
import { CircleCheck, CircleX, Info, LoaderCircle, X } from "lucide-react";
import { Dialog as DialogPrimitive } from "radix-ui";
import { type ReactNode, useEffect, useMemo, useRef, useState } from "react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { api } from "../api/client";
import { formError } from "../api/problem";
import { asProblem, call } from "../api/query";
import { CodeTextarea } from "../ui/codeHighlight";
import { CheckRow, ConfirmAction } from "../ui/controls";
import { payloadFieldPaths } from "../ui/fieldMapping";
import { JsonEditor } from "../ui/jsonEditor";
import { type CurlRequest, parseCurl } from "./curlImport";
import { type EventTypeRule, emptyEventType, guidedExpression, guidedFrom, headerContext } from "./sourceMapping";

/// The Source's Event-identity rule, as the Admin API stores it. `null` is a real choice: the Source
/// then deduplicates nothing. It travels with the draft rather than through the mapping, because the
/// rule is read before the mapping runs and an expression that emitted one would be a second writer.
export type EventIdentityRule = { kind: string; value: string; allowMissing: boolean };

/// What the Builder hands back. `schema` is the Source's stored input requirements, carried through
/// untouched: the Builder authors none, and a document it does not author is not its to rewrite.
export type SourceContractDraft = {
  expression: string;
  schema?: Record<string, unknown>;
  identity: EventIdentityRule | null;
};
type SourceInputType = "webhook" | "broker";

/// Where a Source's Event identity is read from. A kind offers a selector only when it needs one — a
/// message carries its own id — and the offered set is per Source type because the affordances
/// differ: a broker message has no request headers, a webhook request no message id.
///
/// The label says "Message ID", not whose: Azure Service Bus and RabbitMQ leave it to the publisher
/// while SQS and Pub/Sub assign it, so naming either one is wrong for the other. Nor does a broker
/// necessarily have one at all — Kafka identifies a record by its coordinates — which is why the
/// offered set will key on the transport rather than the Source type once a second transport lands.
const identityKinds = [
  { value: "message_id", label: "Message ID", types: ["broker"] },
  { value: "header", label: "Request header", types: ["webhook"] },
  { value: "json_path", label: "JSON body field", types: ["webhook", "broker"] },
] as const;

type HeaderRow = { name: string; value: string };

const segmentsOf = (path: string) =>
  [...path.matchAll(/`([^`]+)`|([A-Za-z_$][\w$]*)/g)].map(([, quoted, plain]) => quoted ?? plain);

function valueAtPath(body: unknown, path: string): unknown {
  return segmentsOf(path).reduce<unknown>(
    (current, part) => (current as Record<string, unknown> | undefined)?.[part],
    body,
  );
}

/// The same discovered field, addressed the way each artefact's reader needs it: the mapping runs
/// through JSONata, the identity rule through `SourceEventIdentityExtractor`, which reads a JSON
/// Pointer. Both are picked from one list, so the Operator types neither and meets one vocabulary.
function pointerFrom(path: string): string {
  return segmentsOf(path)
    .map((segment) => `/${segment.replaceAll("~", "~0").replaceAll("/", "~1")}`)
    .join("");
}

const named = (values: readonly string[]) => values.map((value) => ({ value, label: value }));

/// The share of an editor row each field takes. The leading one carries the name that has to be read
/// exactly — a header like `x-hub-signature-256` — so the split is not even.
const leadField = "min-w-0 flex-[3]";
const trailField = "min-w-0 flex-[2]";

/// The value once it has stopped changing. The acceptance check calls the Admin API, and a request
/// per keystroke in a header name answers a question nobody has finished asking.
function useSettled<T>(value: T, delay: number): T {
  const [settled, setSettled] = useState(value);
  useEffect(() => {
    const timer = setTimeout(() => setSettled(value), delay);
    return () => clearTimeout(timer);
  }, [value, delay]);
  return settled;
}

function Pane({ title, action, children }: { title: string; action?: ReactNode; children: ReactNode }) {
  return (
    <section className="flex min-w-0 flex-col rounded-lg border bg-surface">
      <header className="flex min-h-11 items-center justify-between gap-2 border-b px-3 py-2">
        <h3 className="m-0 text-sm font-semibold">{title}</h3>
        {action}
      </header>
      <div className="flex min-h-0 min-w-0 flex-1 flex-col gap-3 p-3">{children}</div>
    </section>
  );
}

/// Taking one row back out. The icon is the whole control here rather than a supplement to a visible
/// word, as it already is on the surfaces an Operator dismisses: a row repeated per header cannot
/// spend a third of its width on the label, and the accessible name still says which row it removes.
function RemoveRow({ label, onClick }: { label: string; onClick: () => void }) {
  return (
    <Button type="button" variant="ghost" size="icon-sm" aria-label={label} onClick={onClick}>
      <X aria-hidden="true" className="size-4" />
    </Button>
  );
}

function Note({ children }: { children: ReactNode }) {
  return <p className="m-0 text-xs text-ink-secondary">{children}</p>;
}

function Stale() {
  return (
    <p className="m-0 text-xs text-ink-secondary">Showing fields from the last valid sample. Fix it to refresh them.</p>
  );
}

/// Filling the sample from a request an Operator already captured: request bins and browser devtools
/// export it as a `curl` command, and retyping its headers is the slow part. The import replaces
/// what the command carries and leaves the rest of the sample alone; a command that carries neither
/// headers nor a body changes nothing.
function CurlImport({ onImport, onClose }: { onImport: (request: CurlRequest) => void; onClose: () => void }) {
  const [command, setCommand] = useState("");
  const [error, setError] = useState<string | null>(null);
  const submit = () => {
    try {
      const request = parseCurl(command);
      if (request.headers.length === 0 && request.body === null)
        throw new Error("No headers or body found. Paste a curl command with -H or -d options.");
      onImport(request);
      onClose();
    } catch (failure) {
      setError(failure instanceof Error ? failure.message : "The command cannot be read.");
    }
  };
  return (
    /* The form fills the pane it replaces, so importing does not resize the dialog around it. */
    <div className="flex min-h-0 min-w-0 flex-1 flex-col gap-2">
      <label htmlFor="builder-curl" className="text-sm font-medium">
        curl command
      </label>
      <CodeTextarea
        id="builder-curl"
        language="shell"
        name="curl-command"
        placeholder="curl -H 'x-header-name: value' -d '{…}' https://…"
        value={command}
        aria-invalid={error !== null}
        /* Fixed to the pane: a captured command is as long as the provider's headers make it, and a
           box that grew with one would push the verdict and the actions past the foot of the
           window — the same trap the sample editor is bounded against. */
        className="min-h-56 flex-1"
        onChange={(event) => {
          setCommand(event.target.value);
          setError(null);
        }}
      />
      {error ? (
        <p role="alert" className="m-0 text-sm text-destructive">
          {error}
        </p>
      ) : null}
      {/* Leaving the form is the pane's own action, as it is for the mapping's editors, so the form
          carries only the action that is its own. */}
      <Button type="button" size="sm" className="self-start" disabled={command.trim() === ""} onClick={submit}>
        Import
      </Button>
    </div>
  );
}

type Check = {
  expression: string;
  schema: Record<string, unknown> | null;
  identity: EventIdentityRule | null;
  body: unknown;
  headers: Record<string, string> | null;
};

/// The Integrios Event Builder guides the two decisions a Source makes about its input — the Event
/// type and the Event identity — from a sample of that input, and says whether Integrios would accept
/// the sample. Everything it holds is ephemeral: the sample and the choices never leave the browser
/// except to be checked. Only the generated expression and the identity rule return to the Source.
export function EventBuilder({
  draft,
  onUse,
  contractKey,
  sourceType,
}: {
  draft: SourceContractDraft;
  onUse: (draft: SourceContractDraft) => void;
  contractKey: string;
  sourceType: SourceInputType;
}) {
  const webhook = sourceType === "webhook";
  const sampleName = webhook ? "request" : "message";
  const [open, setOpen] = useState(false);
  const [headers, setHeaders] = useState<HeaderRow[]>([{ name: "", value: "" }]);
  const [importing, setImporting] = useState(false);
  const [body, setBody] = useState("{}");
  const [lastValidBody, setLastValidBody] = useState<unknown>({});
  const [bodyError, setBodyError] = useState<string | null>(null);
  const [headerError, setHeaderError] = useState<string | null>(null);
  const [lastValidHeaders, setLastValidHeaders] = useState<Record<string, string>>({});
  const importButton = useRef<HTMLButtonElement>(null);
  /// The rule the stored expression was generated from, when one was. Read back out of the
  /// expression rather than from a stored copy beside it: the expression is the whole contract, so
  /// anything it cannot be read back into is not a rule this form may claim to represent.
  const [eventType, setEventType] = useState<EventTypeRule>(() => guidedFrom(draft.expression) ?? emptyEventType);
  const [identity, setIdentity] = useState<EventIdentityRule | null>(draft.identity);
  /// What the Source had when the Builder opened stays choosable for as long as it is open, whether
  /// or not the sample carries it. A picker otherwise offers only the sample's own headers and fields,
  /// so clearing a saved choice would leave no way back to it short of retyping it into the sample.
  const savedRule = guidedFrom(draft.expression);
  const saved = {
    eventTypeHeaders: savedRule?.source === "header" ? [savedRule.header] : [],
    eventTypePaths: savedRule?.source === "body" ? [savedRule.path] : [],
    identityHeaders: draft.identity?.kind === "header" ? [draft.identity.value] : [],
    identityFields: draft.identity?.kind === "json_path" ? [draft.identity.value] : [],
  };
  const [expression, setExpression] = useState(draft.expression);
  const [mode, setMode] = useState<"guided" | "advanced">(() =>
    draft.expression.trim() === "" || guidedFrom(draft.expression) ? "guided" : "advanced",
  );

  // A body edit is analysed on its own, and only after the Operator has stopped typing: half-typed
  // JSON is not a failure worth reporting yet.
  useEffect(() => {
    const timer = setTimeout(() => {
      try {
        const parsed = JSON.parse(body) as unknown;
        if (parsed === null || typeof parsed !== "object" || Array.isArray(parsed))
          throw new Error("Use a JSON object.");
        setLastValidBody(parsed);
        setBodyError(null);
      } catch (failure) {
        setBodyError(failure instanceof Error ? failure.message : "The request body is not valid JSON.");
      }
    }, 300);
    return () => clearTimeout(timer);
  }, [body]);

  useEffect(() => {
    try {
      setLastValidHeaders(headerContext(headers.filter((row) => row.name.trim() !== "" || row.value !== "")));
      setHeaderError(null);
    } catch (failure) {
      setHeaderError(failure instanceof Error ? failure.message : "The request headers cannot be read.");
    }
  }, [headers]);

  const generated = guidedExpression(eventType);
  const representable = expression.trim() === "" || expression === generated;
  const paths = useMemo(() => payloadFieldPaths(lastValidBody), [lastValidBody]);
  const headerNames = Object.keys(lastValidHeaders);
  /// A header or a field chosen from an earlier sample that this one does not carry. The choice is
  /// kept and named rather than cleared: a sample is one example, and the rule may well be right.
  const dangling = [
    ...(eventType.source === "header" && eventType.header !== "" && !headerNames.includes(eventType.header)
      ? [eventType.header]
      : []),
    ...(eventType.source === "body" && eventType.path !== "" && !paths.includes(eventType.path)
      ? [eventType.path]
      : []),
  ];
  /// An untouched Event type is not a broken mapping. A Source may carry no mapping at all, and then
  /// the input is already an Integrios Event — `SourceContractEvaluator` passes it straight through.
  const eventTypeUnset = eventType.source === "fixed" && eventType.value.trim() === "";
  const eventTypeHalf =
    eventType.source === "header" ? eventType.header === "" : eventType.source === "body" && eventType.path === "";
  /// A kind whose selector is still empty is a half-authored rule; the API would take it and the
  /// Source would then refuse every input, which is the failure this dialog exists to prevent.
  const identityIncomplete = identity !== null && identity.value === "";
  /// What the guided pane would hand back. An untouched Event type means no mapping rather than one
  /// mapping event_type to the empty string, which the runtime would reject on every request.
  const settled = mode === "guided" && eventTypeUnset ? "" : expression;
  /// Something for the Source form to take. What arrived counts as well as what is on screen, so
  /// emptying a rule the Source already had is itself applicable — otherwise an identity could be
  /// added here and never removed.
  const authored =
    settled.trim() !== "" || identity !== null || draft.expression.trim() !== "" || draft.identity !== null;
  /// Complete is all a configuration has to be to leave. Whether this sample would be accepted is the
  /// verdict's question, and a sample is one example: a rule reading a header this sample lacks may be
  /// exactly right for the requests that will arrive.
  const mappable =
    authored &&
    !identityIncomplete &&
    !(mode === "guided" && eventTypeHalf) &&
    (mode !== "advanced" || expression.trim() !== "");

  /// The acceptance check runs whenever there is a whole configuration to check, without being asked
  /// for. It is the Admin API's answer, not the browser's: the preview runs the identity extractor,
  /// the Source's input requirements, the mapping and the output rules in the order ingestion does,
  /// so the verdict cannot drift from what ingestion would decide. With no mapping it checks that the
  /// input is already an Integrios Event, which a provider's own JSON never is.
  /// Whether there is a sample to judge at all. The dialog opens with an empty body and no headers —
  /// on an existing Source, beside a rule already chosen — and a verdict on that would reject a
  /// request nobody sent.
  const sampled =
    headers.some((row) => row.name.trim() !== "") || Object.keys(lastValidBody as Record<string, unknown>).length > 0;
  const checkable = open && mappable && sampled && bodyError === null && headerError === null;
  const current = JSON.stringify({
    expression: settled,
    schema: draft.schema ?? null,
    identity,
    body: lastValidBody,
    headers: webhook ? lastValidHeaders : null,
  } satisfies Check);
  const asked = useSettled(current, 400);
  const verdict = useQuery({
    queryKey: ["source-contract-verdict", asked],
    queryFn: () => {
      const check = JSON.parse(asked) as Check;
      return call(() =>
        api.POST("/admin/connectors/source-contracts/preview", {
          body: {
            schema: check.schema,
            mapping:
              check.expression.trim() === "" ? null : { engine: "jsonata", version: "1", expression: check.expression },
            sample_input: check.body,
            sample_context: check.headers === null ? null : { headers: check.headers },
            event_identity_rule: check.identity
              ? { kind: check.identity.kind, value: check.identity.value, allow_missing: check.identity.allowMissing }
              : null,
          },
        }),
      );
    },
    enabled: checkable,
    // A refusal is the answer, not a fault to retry.
    retry: false,
    staleTime: Number.POSITIVE_INFINITY,
    gcTime: 0,
    placeholderData: keepPreviousData,
  });
  const answered = checkable && asked === current && !verdict.isPlaceholderData && !verdict.isFetching;
  /// The last settled answer, kept on screen while the next is worked out so the line does not blink
  /// out on every keystroke. It carries the check it answered, so its wording follows that check's
  /// identity rule and requirements rather than whatever is chosen by now.
  const [shown, setShown] = useState<Answer | null>(null);
  useEffect(() => {
    if (answered) setShown({ check: JSON.parse(asked) as Check, data: verdict.data, error: verdict.error });
  }, [answered, asked, verdict.data, verdict.error]);

  const reset = () => {
    const rule = guidedFrom(draft.expression);
    setHeaders([{ name: "", value: "" }]);
    setImporting(false);
    setBody("{}");
    setLastValidBody({});
    setBodyError(null);
    setHeaderError(null);
    setLastValidHeaders({});
    setEventType(rule ?? emptyEventType);
    setIdentity(draft.identity);
    setExpression(draft.expression);
    setMode(draft.expression.trim() === "" || rule ? "guided" : "advanced");
    setShown(null);
  };
  const changeOpen = (next: boolean) => {
    reset();
    setOpen(next);
  };
  const status: VerdictStatus = !mappable
    ? {
        kind: "blocked",
        reason: identityIncomplete
          ? "Choose where the Event identity is read from before using this configuration."
          : mode === "advanced"
            ? "Write an expression before using this configuration."
            : eventTypeHalf
              ? "Choose where the Event type is read from before using this configuration."
              : "Choose an Event type or an Event identity before using this configuration.",
      }
    : bodyError !== null || headerError !== null
      ? { kind: "unchecked" }
      : !sampled
        ? { kind: "unsampled" }
        : { kind: "answer", checking: !answered, answer: shown };

  const changeEventType = (rule: EventTypeRule) => {
    setEventType(rule);
    setExpression(guidedExpression(rule));
  };

  /// Returning to guided is destructive only when the expression is not one the guided rule would
  /// generate, so the way back is a confirming control exactly then and a plain one otherwise.
  const toGuided = () => {
    // Including when the editor was emptied: the guided pane is about to claim it represents this
    // expression, so the expression becomes the one it generates.
    setExpression(generated);
    setMode("guided");
  };
  const mappingAction =
    mode === "guided" ? (
      <Button type="button" variant="outline" size="sm" onClick={() => setMode("advanced")}>
        Advanced JSONata
      </Button>
    ) : representable ? (
      <Button type="button" variant="outline" size="sm" onClick={toGuided}>
        Guided mode
      </Button>
    ) : (
      <ConfirmAction
        label="Reset to guided"
        variant="outline"
        question="Reset this expression to the guided mapping?"
        consequence="This expression cannot be shown in the guided form. Resetting replaces it."
        onConfirm={toGuided}
      />
    );

  const useDraft = () => {
    onUse({ expression: settled, schema: draft.schema, identity });
    changeOpen(false);
  };

  return (
    <DialogPrimitive.Root open={open} onOpenChange={changeOpen}>
      <DialogPrimitive.Trigger asChild>
        <Button type="button" variant="outline" className="self-start">
          Open Integrios Event Builder
        </Button>
      </DialogPrimitive.Trigger>
      <DialogPrimitive.Portal>
        <DialogPrimitive.Overlay className="fixed inset-0 z-60 bg-ink/25" />
        {/* Wider than the authoring flyout it opens from: the sample and the decisions read from it
            are read together, and at a sheet's width they cannot be. One column below that.
            As tall as its panes, up to the window, and centred both ways in it. */}
        <DialogPrimitive.Content className="fixed top-1/2 left-1/2 z-70 flex max-h-[calc(100vh-2rem)] w-[calc(100%-2rem)] max-w-6xl -translate-x-1/2 -translate-y-1/2 flex-col gap-4 rounded-lg border bg-canvas p-4 shadow-[0_24px_64px_-32px_rgb(23_23_23/0.45)] outline-none md:w-[calc(100%-4rem)]">
          <div className="flex items-start justify-between gap-3">
            <div className="min-w-0">
              <DialogPrimitive.Title className="m-0">Integrios Event Builder</DialogPrimitive.Title>
              <DialogPrimitive.Description className="m-0 mt-1 text-sm text-ink-secondary">
                Choose the Event type and Event identity the <code>{contractKey || "Source"}</code> reads from each{" "}
                {sampleName}, and check whether Integrios would accept a sample.
              </DialogPrimitive.Description>
            </div>
            <DialogPrimitive.Close
              aria-label="Close the Integrios Event Builder"
              className="-m-1 flex size-8 shrink-0 items-center justify-center rounded-md p-1 hover:bg-hover-surface focus-visible:ring-[3px] focus-visible:ring-ring/50"
            >
              <X aria-hidden="true" className="size-4" />
            </DialogPrimitive.Close>
          </div>

          {/* Only the panes scroll, and only once the dialog has reached the window's height, so the
              verdict and the action it is about stay on screen however long the sample is. */}
          <div className="min-h-0 flex-initial overflow-y-auto">
            <div className="grid min-w-0 gap-4 xl:grid-cols-2">
              <Pane
                title={webhook ? "Sample request" : "Sample message"}
                action={
                  webhook ? (
                    <Button
                      ref={importButton}
                      type="button"
                      variant="outline"
                      size="sm"
                      onClick={() => setImporting(!importing)}
                    >
                      {importing ? "Back to sample" : "Import cURL"}
                    </Button>
                  ) : undefined
                }
              >
                {webhook && importing ? (
                  <CurlImport
                    onClose={() => {
                      setImporting(false);
                      requestAnimationFrame(() => importButton.current?.focus());
                    }}
                    onImport={(request) => {
                      if (request.headers.length > 0) setHeaders(request.headers);
                      if (request.body !== null) setBody(request.body);
                    }}
                  />
                ) : null}
                {webhook && !importing ? (
                  <fieldset className="m-0 flex min-w-0 flex-col gap-2 border-0 p-0">
                    <legend className="text-sm font-medium">Request headers</legend>
                    <Note>Headers from a real request. They are only used to check it, never stored.</Note>
                    {headers.map((row, index) => (
                      // Rows are positional: an Operator may empty a name and type another, so nothing
                      // stable exists to key them by.
                      // biome-ignore lint/suspicious/noArrayIndexKey: positional rows with no stable identity
                      <div key={index} className="flex min-w-0 items-center gap-2">
                        <Input
                          aria-label={`Header ${index + 1} name`}
                          placeholder="x-header-name"
                          className={`${leadField} font-mono text-sm`}
                          value={row.name}
                          onChange={(event) =>
                            setHeaders(
                              headers.map((currentRow, at) =>
                                at === index ? { ...currentRow, name: event.target.value } : currentRow,
                              ),
                            )
                          }
                        />
                        <Input
                          aria-label={`Header ${index + 1} sample value`}
                          placeholder="Sample value"
                          className={trailField}
                          value={row.value}
                          onChange={(event) =>
                            setHeaders(
                              headers.map((currentRow, at) =>
                                at === index ? { ...currentRow, value: event.target.value } : currentRow,
                              ),
                            )
                          }
                        />
                        <RemoveRow
                          label={`Remove header ${index + 1}`}
                          onClick={() => setHeaders(headers.filter((_, at) => at !== index))}
                        />
                      </div>
                    ))}
                    {headerError ? (
                      <p role="alert" className="m-0 text-sm text-destructive">
                        {headerError}
                      </p>
                    ) : null}
                    <Button
                      type="button"
                      variant="outline"
                      size="sm"
                      className="self-start"
                      onClick={() => setHeaders([...headers, { name: "", value: "" }])}
                    >
                      Add header
                    </Button>
                  </fieldset>
                ) : null}

                {importing ? null : (
                  <div className="flex min-w-0 flex-col gap-1.5">
                    <JsonEditor
                      id="builder-body"
                      label={webhook ? "Request body (JSON)" : "Message body (JSON)"}
                      value={body}
                      invalid={bodyError !== null}
                      onChange={setBody}
                    />
                    {bodyError ? (
                      <p role="alert" className="m-0 text-sm text-destructive">
                        {bodyError}
                      </p>
                    ) : null}
                  </div>
                )}
              </Pane>

              <Pane title={mode === "guided" ? "Event fields" : "Advanced JSONata"} action={mappingAction}>
                {mode === "guided" ? (
                  <GuidedFields
                    eventType={eventType}
                    headerNames={headerNames}
                    dangling={dangling}
                    paths={paths}
                    stale={bodyError !== null || headerError !== null}
                    headers={webhook ? lastValidHeaders : undefined}
                    body={lastValidBody}
                    savedHeaders={saved.eventTypeHeaders}
                    savedPaths={saved.eventTypePaths}
                    onChange={changeEventType}
                  />
                ) : (
                  <AdvancedExpression
                    expression={expression}
                    representable={representable}
                    headers={webhook ? lastValidHeaders : undefined}
                    onChange={setExpression}
                  />
                )}
                <IdentityFields
                  identity={identity}
                  sourceType={sourceType}
                  headerNames={headerNames}
                  paths={paths}
                  savedHeaders={saved.identityHeaders}
                  savedFields={saved.identityFields}
                  onChange={setIdentity}
                />
              </Pane>
            </div>
          </div>

          <Verdict status={status} sampleName={sampleName} webhook={webhook} />

          <div className="flex flex-wrap items-center justify-between gap-3">
            <DialogPrimitive.Close asChild>
              <Button type="button" variant="outline">
                Back to Source
              </Button>
            </DialogPrimitive.Close>
            <Button type="button" disabled={!mappable} onClick={useDraft}>
              Use configuration
            </Button>
          </div>
        </DialogPrimitive.Content>
      </DialogPrimitive.Portal>
    </DialogPrimitive.Root>
  );
}

type Answer = {
  check: Check;
  data?: { output: unknown; source_event_id?: string | null };
  error: unknown;
};

type VerdictStatus =
  | { kind: "blocked"; reason: string }
  | { kind: "unchecked" }
  | { kind: "unsampled" }
  | { kind: "answer"; checking: boolean; answer: Answer | null };

/// The dashboard's tone pairs (see `ui/status.tsx`): the word leads and says what happened, and the
/// colour is only the second cue. Guidance and waiting stay quiet; only an answer carries colour.
const verdictTones = {
  quiet: "bg-surface-quiet text-ink-secondary",
  success: "bg-success-surface text-success-ink",
  failure: "bg-danger-surface text-danger-ink",
} as const;

function Callout({
  tone,
  icon,
  busy = false,
  children,
}: {
  tone: keyof typeof verdictTones;
  icon: ReactNode;
  busy?: boolean;
  children: ReactNode;
}) {
  return (
    // Two lines tall whatever it says, so the actions under it stay put as the answer changes.
    <div className={cn("flex min-h-14 min-w-0 items-center gap-2.5 rounded-md px-3 py-2 text-sm", verdictTones[tone])}>
      <span aria-hidden="true" className="flex h-5 shrink-0 items-center">
        {icon}
      </span>
      <div className="flex min-w-0 flex-1 flex-col gap-0.5">{children}</div>
      {/* A spinner beside the answer it will replace, rather than the answer disappearing: the
          Operator keeps reading what was true a moment ago, and knows it is being rechecked. */}
      {busy ? (
        <span aria-hidden="true" className="flex h-5 shrink-0 items-center">
          <LoaderCircle className="size-4 animate-spin" />
        </span>
      ) : null}
    </div>
  );
}

/// A value Integrios resolved from the sample, set apart from the sentence around it. An identity is
/// often a long opaque id, so it may break anywhere rather than widen the dialog.
function Resolved({ children }: { children: ReactNode }) {
  return <code className="rounded bg-surface px-1 py-px font-mono text-xs break-all text-ink">{children}</code>;
}

const iconClass = "size-4";

/// Beside the action it is about: why the configuration cannot be used yet, or what Integrios would
/// do with this sample. Polite, and busy while a check is out, so a screen reader hears the settled
/// answer once rather than every state it passed through while the Operator typed.
function Verdict({ status, sampleName, webhook }: { status: VerdictStatus; sampleName: string; webhook: boolean }) {
  const checking = status.kind === "answer" && status.checking;
  return (
    <div role="status" aria-live="polite" aria-busy={checking} className="min-w-0">
      {status.kind === "blocked" ? (
        <Callout tone="quiet" icon={<Info className={iconClass} />}>
          <p className="m-0">{status.reason}</p>
        </Callout>
      ) : status.kind === "unchecked" ? (
        <Callout tone="quiet" icon={<Info className={iconClass} />}>
          <p className="m-0">Fix the sample to check it.</p>
        </Callout>
      ) : status.kind === "unsampled" ? (
        <Callout tone="quiet" icon={<Info className={iconClass} />}>
          <p className="m-0">Add a sample {sampleName} to check whether Integrios would accept it.</p>
        </Callout>
      ) : status.answer === null ? (
        <Callout tone="quiet" icon={<LoaderCircle className={cn(iconClass, "animate-spin")} />}>
          <p className="m-0">Checking this {sampleName}…</p>
        </Callout>
      ) : status.answer.error ? (
        <Rejected answer={status.answer} sampleName={sampleName} busy={checking} />
      ) : (
        <Accepted answer={status.answer} webhook={webhook} busy={checking} />
      )}
    </div>
  );
}

function Rejected({ answer, sampleName, busy }: { answer: Answer; sampleName: string; busy: boolean }) {
  const details = asProblem(answer.error);
  // The preview keys its refusal by the part of the check that refused, as every Admin validation
  // failure is keyed by the field it is about. The message is the API's own sentence for it, not the
  // generic validation title.
  const [refusedBy, messages] = Object.entries(details?.errors ?? {})[0] ?? ["", []];
  const reason = messages[0] ?? (details ? formError(details) : null);
  return (
    <Callout tone="failure" icon={<CircleX className={iconClass} />} busy={busy}>
      <p className="m-0">
        <strong className="font-semibold">{reason ? "Rejected." : "Not checked."}</strong>{" "}
        {reason ?? `This ${sampleName} could not be checked.`}
      </p>
      {/* The Builder authors neither of these, so a refusal from one has to say where it comes from,
          or it reads as a fault in the two choices made here. */}
      {refusedBy === "schema" ? (
        <p className="m-0 text-xs">
          That comes from this Source's input requirements, edited under Raw event contract.
        </p>
      ) : refusedBy === "sample_input" ? (
        <p className="m-0 text-xs">With no Event type rule, each {sampleName} must already be an Integrios Event.</p>
      ) : null}
    </Callout>
  );
}

function Accepted({ answer, webhook, busy }: { answer: Answer; webhook: boolean; busy: boolean }) {
  const eventType = (answer.data?.output as { event_type?: unknown } | undefined)?.event_type;
  const sourceEventId = answer.data?.source_event_id ?? null;
  const identity = answer.check.identity;
  const identified =
    identity?.kind === "message_id" ? (
      "Identity from the broker's message ID."
    ) : sourceEventId !== null ? (
      <>
        Identity <Resolved>{sourceEventId}</Resolved>.
      </>
    ) : identity === null ? (
      "No identity, so it is not deduplicated."
    ) : (
      "No identity in this sample, which the rule permits."
    );
  return (
    <Callout tone="success" icon={<CircleCheck className={iconClass} />} busy={busy}>
      <p className="m-0">
        <strong className="font-semibold">Accepted</strong>
        {typeof eventType === "string" ? (
          <>
            {" "}
            as <Resolved>{eventType}</Resolved>
          </>
        ) : null}
      </p>
      <p className="m-0 text-xs">
        {identified}
        {/* The sample is not signed with the Source's secret, so this cannot be a promise about it. */}
        {webhook ? " Signature verification is not part of this check." : null}
      </p>
    </Callout>
  );
}

/// The Source's own Event-identity rule, authored beside the mapping because an Operator decides
/// both from the same sample — but never generated into it. The rule is read before the mapping runs
/// and outlives any one expression, so the mapping emitting an identity would mean one concept
/// authored twice, with different permanence.
function IdentityFields({
  identity,
  sourceType,
  headerNames,
  paths,
  savedHeaders,
  savedFields,
  onChange,
}: {
  identity: EventIdentityRule | null;
  sourceType: SourceInputType;
  headerNames: string[];
  paths: string[];
  savedHeaders: string[];
  savedFields: string[];
  onChange: (identity: EventIdentityRule | null) => void;
}) {
  const inputNoun = sourceType === "webhook" ? "request" : "message";
  const kinds = identityKinds.filter((kind) => (kind.types as readonly string[]).includes(sourceType));
  const selector = identity && identity.kind !== "message_id" ? identity.kind : "";

  return (
    <Target title="source_event_id" requirement="Optional">
      <Note>
        Where Integrios reads it, so one {inputNoun} sent twice becomes one Event. Read before the mapping, and never
        taken from it.
      </Note>
      {/* A selector carried across a kind change is submitted under the new one: a header name
          becomes a JSON Pointer the API refuses, and a Pointer becomes a header name it accepts,
          leaving a Source that looks configured and matches no input. So the kind carries its own
          value, and changing it starts that value again. */}
      <Choice
        label="Event identity"
        value={identity?.kind ?? ""}
        options={kinds.map((kind) => ({ value: kind.value, label: kind.label }))}
        noneLabel="No duplicate detection"
        onChange={(kind) =>
          onChange(
            kind === ""
              ? null
              : {
                  kind,
                  value: kind === "message_id" ? "message_id" : "",
                  allowMissing: identity?.allowMissing ?? false,
                },
          )
        }
      />
      {selector === "header" ? (
        <Choice
          label="Identity header"
          value={identity?.value ?? ""}
          options={named(headerNames)}
          saved={savedHeaders}
          noneLabel="Choose a header…"
          onChange={(value) => onChange({ kind: "header", value, allowMissing: identity?.allowMissing ?? false })}
        />
      ) : null}
      {selector === "json_path" ? (
        <Choice
          label="Identity field"
          value={identity?.value ?? ""}
          options={paths.map((path) => ({ value: pointerFrom(path), label: path }))}
          saved={savedFields}
          noneLabel="Choose a field…"
          onChange={(value) => onChange({ kind: "json_path", value, allowMissing: identity?.allowMissing ?? false })}
        />
      ) : null}
      {identity ? (
        /* Offered for every kind rather than gated on one: a message id is application-defined on
           Azure Service Bus and on RabbitMQ, so an absent value is a real state for the kind that
           looks least likely to need the permission. */
        <CheckRow
          checked={identity.allowMissing}
          label={`Accept a ${inputNoun} that carries no value here`}
          hint={`Integrios then reads the identity this Source's Event mapping produces, if it produces one, rather than rejecting the ${inputNoun}. An Event with no identity at all is not deduplicated.`}
          onChange={(allowMissing) => onChange({ ...identity, allowMissing })}
        />
      ) : null}
    </Target>
  );
}

function Target({ title, requirement, children }: { title: string; requirement: string; children: ReactNode }) {
  return (
    <section className="flex min-w-0 flex-col gap-2 border-t pt-3 first:border-t-0 first:pt-0">
      <div className="flex items-baseline justify-between gap-2">
        <h4 className="m-0 font-mono text-sm font-semibold">{title}</h4>
        <span className="text-xs text-ink-secondary">{requirement}</span>
      </div>
      {children}
    </section>
  );
}

function Choice({
  label,
  value,
  options,
  onChange,
  noneLabel,
  saved = [],
}: {
  label: string;
  value: string;
  /// The stored value and the name it is shown under. They differ where the artefact's reader needs
  /// a syntax the Operator should not have to read, as a JSON Pointer identity does.
  options: { value: string; label: string }[];
  onChange: (value: string) => void;
  noneLabel?: string;
  /// Values offered whether or not the sample carries them: what the Source had when the Builder
  /// opened, so clearing a choice never strands it.
  saved?: string[];
}) {
  // A chosen value this sample does not carry stays selected, under its own name, and so does what the
  // Source had. What that absence means for the sample is the verdict's to say.
  const extra = [...saved, value].filter(
    (candidate, at, all) =>
      candidate !== "" && all.indexOf(candidate) === at && !options.some((option) => option.value === candidate),
  );
  const offered = [...extra.map((candidate) => ({ value: candidate, label: candidate })), ...options];
  return (
    <label className="flex min-w-0 flex-col gap-1 text-sm">
      <span className="text-ink-secondary">{label}</span>
      <select
        className="h-9 min-w-0 rounded-md border bg-surface px-2 text-sm"
        value={value}
        onChange={(event) => onChange(event.target.value)}
      >
        {noneLabel ? <option value="">{noneLabel}</option> : null}
        {offered.map((option) => (
          <option key={option.value} value={option.value}>
            {option.label}
          </option>
        ))}
      </select>
    </label>
  );
}

/// Where each normalized Event value comes from. Discovered paths and header names populate the
/// choices; none of them assigns meaning on their own, so every field but the default whole-body
/// payload is an explicit choice.
function GuidedFields({
  eventType,
  headerNames,
  dangling,
  paths,
  headers,
  body,
  stale,
  savedHeaders,
  savedPaths,
  onChange,
}: {
  eventType: EventTypeRule;
  headerNames: string[];
  /// Values chosen that this sample does not carry. They stay visible, and named, rather than
  /// reading as though nothing was ever chosen.
  dangling: string[];
  paths: string[];
  headers?: Record<string, string>;
  body: unknown;
  stale: boolean;
  savedHeaders: string[];
  savedPaths: string[];
  onChange: (rule: EventTypeRule) => void;
}) {
  const derived = eventType.source !== "fixed";
  const eventTypePrefix = eventType.source === "fixed" ? "" : eventType.prefix;
  const selectedValue =
    eventType.source === "fixed"
      ? eventType.value.trim()
      : eventType.source === "header"
        ? headers?.[eventType.header]
        : valueAtPath(body, eventType.path);
  const preview =
    typeof selectedValue === "string" && selectedValue.trim() !== ""
      ? eventTypePrefix.trim()
        ? `${eventTypePrefix.trim()}.${selectedValue}`
        : selectedValue
      : "";

  return (
    <>
      <Note>
        Choose where the Event type comes from. The JSONata mapping is generated from this choice, and the payload is
        always the whole body.
      </Note>
      {stale ? <Stale /> : null}
      {dangling.length > 0 ? (
        <Note>This sample does not carry {dangling.join(", ")}; the check below says what that means for it.</Note>
      ) : null}

      <Target title="event_type" requirement="Required">
        <fieldset className="m-0 flex flex-col gap-1 border-0 p-0">
          <legend className="sr-only">Event type rule</legend>
          <label className="flex items-start gap-2.5 text-sm">
            <input
              type="radio"
              name="builder-event-type-source"
              className="mt-0.5 size-4 shrink-0"
              checked={!derived}
              onChange={() => onChange({ source: "fixed", value: "" })}
            />
            <span>Fixed value</span>
          </label>
          <label className="flex items-start gap-2.5 text-sm">
            <input
              type="radio"
              name="builder-event-type-source"
              className="mt-0.5 size-4 shrink-0"
              checked={derived}
              onChange={() =>
                onChange(
                  headers ? { source: "header", header: "", prefix: "" } : { source: "body", path: "", prefix: "" },
                )
              }
            />
            <span>From input</span>
          </label>
        </fieldset>
        {eventType.source === "fixed" ? (
          <div className="flex min-w-0 flex-col gap-1 text-sm">
            <label htmlFor="builder-fixed-event-type" className="text-ink-secondary">
              Event type
            </label>
            <Input
              id="builder-fixed-event-type"
              placeholder="order.placed"
              value={eventType.value}
              onChange={(event) => onChange({ source: "fixed", value: event.target.value })}
            />
          </div>
        ) : null}
        {derived && headers ? (
          <label className="flex min-w-0 flex-col gap-1 text-sm">
            <span className="text-ink-secondary">Read from</span>
            <select
              className="h-9 min-w-0 rounded-md border bg-surface px-2 text-sm"
              value={eventType.source}
              onChange={(event) =>
                onChange(
                  event.target.value === "header"
                    ? { source: "header", header: "", prefix: eventTypePrefix }
                    : { source: "body", path: "", prefix: eventTypePrefix },
                )
              }
            >
              <option value="header">Request header</option>
              <option value="body">JSON body field</option>
            </select>
          </label>
        ) : null}
        {eventType.source === "header" ? (
          <Choice
            label="Event type header"
            value={eventType.header}
            options={named(headerNames)}
            saved={savedHeaders}
            noneLabel="Choose a header…"
            onChange={(header) => onChange({ source: "header", header, prefix: eventTypePrefix })}
          />
        ) : null}
        {eventType.source === "body" ? (
          <Choice
            label="Event type field"
            value={eventType.path}
            options={named(paths)}
            saved={savedPaths}
            noneLabel="Choose a field…"
            onChange={(path) => onChange({ source: "body", path, prefix: eventTypePrefix })}
          />
        ) : null}
        {derived ? (
          <div className="flex min-w-0 flex-col gap-1 text-sm">
            <label htmlFor="builder-event-prefix" className="text-ink-secondary">
              Prefix (optional)
            </label>
            <Input
              id="builder-event-prefix"
              placeholder="github"
              value={eventTypePrefix}
              onChange={(event) =>
                onChange(
                  eventType.source === "header"
                    ? { source: "header", header: eventType.header, prefix: event.target.value }
                    : {
                        source: "body",
                        path: eventType.source === "body" ? eventType.path : "",
                        prefix: event.target.value,
                      },
                )
              }
            />
          </div>
        ) : null}
        <Note>Preview: {preview === "" ? "—" : preview}</Note>
      </Target>
    </>
  );
}

/// The same expression draft, edited directly, for a contract the guided rule cannot express. No
/// completion: an Operator who has left the guided form has the JSONata documentation, the guided
/// pane already enumerates every path the sample carries, and the preview names a wrong one at once.
function AdvancedExpression({
  expression,
  representable,
  headers,
  onChange,
}: {
  expression: string;
  representable: boolean;
  headers?: Record<string, string>;
  onChange: (expression: string) => void;
}) {
  return (
    <>
      <label htmlFor="builder-expression" className="sr-only">
        Source mapping expression
      </label>
      <CodeTextarea
        id="builder-expression"
        language="text"
        value={expression}
        className="min-h-56"
        onChange={(event) => onChange(event.target.value)}
      />
      <Note>
        The mapping reads the {headers ? "request body, and request headers under " : "message body"}
        {headers ? <code>$context.headers</code> : null}
        {headers ? "." : ""} It must produce <code>event_type</code> and <code>payload</code>.
      </Note>
      <Note>
        {representable
          ? "This expression matches the guided mapping and can return to it."
          : "This expression cannot be shown in the guided form."}
      </Note>
    </>
  );
}
