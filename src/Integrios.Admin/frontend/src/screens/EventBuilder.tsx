import { useMutation } from "@tanstack/react-query";
import { X } from "lucide-react";
import { Dialog as DialogPrimitive } from "radix-ui";
import { type ReactNode, useEffect, useMemo, useRef, useState } from "react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Textarea } from "@/components/ui/textarea";
import { api } from "../api/client";
import { formError } from "../api/problem";
import { asProblem, call } from "../api/query";
import { CheckRow, ConfirmAction } from "../ui/controls";
import { payloadFieldPaths } from "../ui/fieldMapping";
import { formatJson } from "../ui/json";
import {
  type EventTypeRule,
  emptyEventType,
  guidedExpression,
  guidedFrom,
  headerContext,
  type InputRequirement,
  matchesRequirementType,
  type RequirementType,
  requirableFields,
  requirementsSchema,
  requirementTypes,
} from "./sourceMapping";

/// The Source's Event-identity rule, as the Admin API stores it. `null` is a real choice: the Source
/// then deduplicates nothing. It travels with the draft rather than through the mapping, because the
/// rule is read before the mapping runs and an expression that emitted one would be a second writer.
export type EventIdentityRule = { kind: string; value: string; allowMissing: boolean };

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

const targetEnvelope = {
  event_type: "required string",
  source_event_id: "supplied by Event identity when configured",
  payload: "required JSON value",
  metadata: "optional object",
};

/// Reads an existing contract schema back into requirement rows. Only the flat, scalar shape the
/// Admin API accepts is representable here; anything else stays in the manifest it came from rather
/// than being shown as rows that would rewrite it.
function requirementsFrom(schema: Record<string, unknown> | undefined): InputRequirement[] {
  const properties = schema?.properties;
  if (properties === null || typeof properties !== "object") return [];
  const required = Array.isArray(schema?.required) ? schema.required : [];
  return Object.entries(properties as Record<string, unknown>)
    .filter(([name, value]) => required.includes(name) && value !== null && typeof value === "object")
    .map(([name, value]) => [name, (value as { type?: unknown }).type] as const)
    .filter((entry): entry is readonly [string, RequirementType] =>
      requirementTypes.includes(entry[1] as RequirementType),
    )
    .map(([field, type]) => ({ field, type }));
}

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
/// exactly — a header like `x-hub-signature-256`, or a discovered field path — and a row is only
/// about 400 pixels wide at three panes, so the split is not even.
const leadField = "min-w-0 flex-[3]";
const trailField = "min-w-0 flex-[2]";

function Pane({ title, action, children }: { title: string; action?: ReactNode; children: ReactNode }) {
  return (
    <section className="flex min-w-0 flex-col rounded-lg border bg-surface">
      <header className="flex min-h-11 items-center justify-between gap-2 border-b px-3 py-2">
        <h3 className="m-0 text-sm font-semibold">{title}</h3>
        {action}
      </header>
      <div className="flex min-w-0 flex-col gap-3 p-3">{children}</div>
    </section>
  );
}

/// Taking one row back out. The icon is the whole control here rather than a supplement to a visible
/// word, as it already is on the surfaces an Operator dismisses: a row repeated per header or per
/// field cannot spend a third of its width on the label, and the accessible name still says exactly
/// which row it removes.
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

/// The Integrios Event Builder: sample input on the left, the Event fields it is mapped
/// into in the middle, and what Integrios would accept on the right. Everything it holds is
/// ephemeral — the sample, the headers and the guided choices never leave the browser. Only the
/// generated expression and the input-requirements schema are handed back to the Source draft.
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
  const [body, setBody] = useState("{}");
  const [lastValidBody, setLastValidBody] = useState<unknown>({});
  const [bodyError, setBodyError] = useState<string | null>(null);
  const [headerError, setHeaderError] = useState<string | null>(null);
  const [lastValidHeaders, setLastValidHeaders] = useState<Record<string, string>>({});
  const [requirements, setRequirements] = useState<InputRequirement[]>(() => requirementsFrom(draft.schema));
  /// The rule the stored expression was generated from, when one was. Read back out of the
  /// expression rather than from a stored copy beside it: the expression is the whole contract, so
  /// anything it cannot be read back into is not a rule this form may claim to represent.
  const [eventType, setEventType] = useState<EventTypeRule>(() => guidedFrom(draft.expression) ?? emptyEventType);
  const [identity, setIdentity] = useState<EventIdentityRule | null>(draft.identity);
  const [expression, setExpression] = useState(draft.expression);
  const [mode, setMode] = useState<"guided" | "advanced">(() =>
    draft.expression.trim() === "" || guidedFrom(draft.expression) ? "guided" : "advanced",
  );
  const [previewed, setPreviewed] = useState<string | null>(null);
  /// What a preview posts, written by the same effects that publish the parsed sample, so a press
  /// landing between a debounce and the next render still sends the sample that is on screen.
  const sample = useRef<{ body: unknown; headers: Record<string, string> }>({ body: {}, headers: {} });

  // A body edit is analysed on its own rather than on an Analyze press, and only after the Operator
  // has stopped typing: half-typed JSON is not a failure worth reporting yet.
  useEffect(() => {
    const timer = setTimeout(() => {
      try {
        const parsed = JSON.parse(body) as unknown;
        if (parsed === null || typeof parsed !== "object" || Array.isArray(parsed))
          throw new Error("Use a JSON object.");
        sample.current = { ...sample.current, body: parsed };
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
      const context = headerContext(headers.filter((row) => row.name.trim() !== "" || row.value !== ""));
      sample.current = { ...sample.current, headers: context };
      setLastValidHeaders(context);
      setHeaderError(null);
    } catch (failure) {
      setHeaderError(failure instanceof Error ? failure.message : "The request headers cannot be read.");
    }
  }, [headers]);

  const generated = guidedExpression(eventType);
  const representable = expression.trim() === "" || expression === generated;
  const paths = useMemo(() => payloadFieldPaths(lastValidBody), [lastValidBody]);
  const headerNames = Object.keys(lastValidHeaders);
  const fields = requirableFields(lastValidBody);
  const schema = requirementsSchema(requirements);
  const mismatch = requirements.find(
    (row) =>
      row.field !== "" &&
      row.type !== "" &&
      !matchesRequirementType((lastValidBody as Record<string, unknown>)[row.field], row.type),
  );
  const incomplete = requirements.some((row) => row.field === "" || row.type === "");
  /// A header or a field can disappear after it was chosen. The choice is kept and named rather than
  /// silently cleared, but it cannot be carried into a Connector while it addresses nothing.
  const dangling = [
    ...(eventType.source === "header" && eventType.header !== "" && !headerNames.includes(eventType.header)
      ? [eventType.header]
      : []),
    ...(eventType.source === "body" && eventType.path !== "" && !paths.includes(eventType.path)
      ? [eventType.path]
      : []),
  ];
  const eventTypeInput =
    eventType.source === "fixed"
      ? eventType.value.trim()
      : eventType.source === "header"
        ? lastValidHeaders[eventType.header]
        : valueAtPath(lastValidBody, eventType.path);
  /// An untouched Event type is not a broken mapping. A Source may carry no mapping at all, and then
  /// the input is already an Integrios Event — `SourceContractEvaluator` passes it straight through.
  /// A half-chosen one is broken, and is the only shape refused here.
  const eventTypeUnset = eventType.source === "fixed" && eventType.value.trim() === "";
  const eventTypeHalf =
    eventType.source === "header" ? eventType.header === "" : eventType.source === "body" && eventType.path === "";
  const eventTypeSampleInvalid =
    eventType.source !== "fixed" &&
    !eventTypeHalf &&
    dangling.length === 0 &&
    (typeof eventTypeInput !== "string" || eventTypeInput.trim() === "");
  /// A kind whose selector is still empty is a half-authored rule; the API would take it and the
  /// Source would then refuse every input, which is the failure this dialog exists to prevent.
  const identityIncomplete = identity !== null && identity.value === "";
  /// What the guided panes would hand back. An untouched Event type means no mapping rather than one
  /// mapping event_type to the empty string, which the runtime would reject on every request.
  const settled = mode === "guided" && eventTypeUnset ? "" : expression;
  /// Something for the Source form to take. What arrived counts as well as what is on screen, so
  /// emptying a rule the Source already had is itself applicable — otherwise an identity could be
  /// added here and never removed.
  const authored =
    settled.trim() !== "" ||
    identity !== null ||
    schema !== undefined ||
    draft.expression.trim() !== "" ||
    draft.identity !== null;
  const mappable =
    authored &&
    !identityIncomplete &&
    !eventTypeHalf &&
    !eventTypeSampleInvalid &&
    dangling.length === 0 &&
    (mode !== "advanced" || expression.trim() !== "");

  /// What a displayed result was produced from. Any change to the contract or the sample — a header
  /// the mapping reads included — makes both a success and a failure a statement about something
  /// that is no longer on screen.
  const signature = JSON.stringify([expression, schema ?? null, lastValidBody, webhook ? lastValidHeaders : null]);

  const preview = useMutation({
    mutationFn: () =>
      call(() =>
        api.POST("/admin/connectors/source-contracts/preview", {
          body: {
            schema: schema ?? null,
            mapping: { engine: "jsonata", version: "1", expression },
            sample_input: sample.current.body,
            sample_context: webhook ? { headers: sample.current.headers } : null,
          },
        }),
      ),
    onMutate: () => setPreviewed(signature),
  });

  const problem = asProblem(preview.error);
  const message = problem ? (formError(problem) ?? "The preview could not be run.") : null;
  const fresh = previewed === signature;

  const changeIdentity = (rule: EventIdentityRule | null) => {
    setIdentity(rule);
    preview.reset();
  };

  const changeEventType = (rule: EventTypeRule) => {
    setEventType(rule);
    setExpression(guidedExpression(rule));
    preview.reset();
  };

  /// An imported schema this form cannot show as rows is not the Operator's to lose here: it is
  /// carried back out untouched unless requirements replace it.
  const opaqueSchema = requirementsFrom(draft.schema).length === 0 ? draft.schema : undefined;

  const useDraft = () => {
    onUse({ expression: settled, schema: schema ?? opaqueSchema, identity });
    setOpen(false);
  };

  return (
    <DialogPrimitive.Root open={open} onOpenChange={setOpen}>
      <DialogPrimitive.Trigger asChild>
        <Button type="button" variant="outline" className="self-start">
          Open Integrios Event Builder
        </Button>
      </DialogPrimitive.Trigger>
      <DialogPrimitive.Portal>
        <DialogPrimitive.Overlay className="fixed inset-0 z-60 bg-ink/25" />
        {/* Wider than the authoring flyout it opens from: the sample input, the Event
            fields it produces, and the preview are read together, and at a sheet's width they
            cannot be. One column below that, where three would each be too narrow to read. */}
        <DialogPrimitive.Content className="fixed inset-4 z-70 flex max-h-[calc(100vh-2rem)] flex-col gap-4 overflow-y-auto rounded-lg border bg-canvas p-4 shadow-[0_24px_64px_-32px_rgb(23_23_23/0.45)] outline-none md:inset-x-8 xl:inset-x-[max(2rem,calc((100vw-88rem)/2))]">
          <div className="flex items-start justify-between gap-3">
            <div className="min-w-0">
              <DialogPrimitive.Title className="m-0">Integrios Event Builder</DialogPrimitive.Title>
              <DialogPrimitive.Description className="m-0 mt-1 text-sm text-ink-secondary">
                Define how a sample {sampleName} the <span className="font-mono">{contractKey || "Source"}</span>{" "}
                accepts becomes an Integrios Event. Only the mapping and input requirements join the Source draft.
              </DialogPrimitive.Description>
            </div>
            <DialogPrimitive.Close
              aria-label="Close the Integrios Event Builder"
              className="-m-1 flex size-8 shrink-0 items-center justify-center rounded-md p-1 hover:bg-hover-surface focus-visible:ring-[3px] focus-visible:ring-ring/50"
            >
              <X aria-hidden="true" className="size-4" />
            </DialogPrimitive.Close>
          </div>

          <div className="grid min-w-0 gap-4 xl:grid-cols-3">
            <Pane title={webhook ? "Sample request" : "Sample message"}>
              {webhook ? (
                <fieldset className="m-0 flex min-w-0 flex-col gap-2 border-0 p-0">
                  <legend className="text-sm font-medium">Request headers</legend>
                  <Note>Add only headers the mapping reads. Use sample values, never secrets.</Note>
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
                            headers.map((current, at) =>
                              at === index ? { ...current, name: event.target.value } : current,
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
                            headers.map((current, at) =>
                              at === index ? { ...current, value: event.target.value } : current,
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

              <div className="flex min-w-0 flex-col gap-1.5">
                <label htmlFor="builder-body" className="text-sm font-medium">
                  {webhook ? "Request body (JSON)" : "Message body (JSON)"}
                </label>
                <Textarea
                  id="builder-body"
                  spellCheck={false}
                  value={body}
                  onChange={(event) => setBody(event.target.value)}
                  className="min-h-120 font-mono text-sm"
                />
                {bodyError ? (
                  <p role="alert" className="m-0 text-sm text-destructive">
                    {bodyError}
                  </p>
                ) : null}
              </div>

              <details className="rounded-md border">
                <summary className="cursor-pointer list-none px-3 py-2 text-sm font-medium">
                  Input requirements (optional)
                  <span className="ml-1 font-normal text-ink-secondary">
                    {requirements.length === 0 ? "· None configured" : `· ${requirements.length} required`}
                  </span>
                </summary>
                <div className="flex flex-col gap-2 border-t p-3">
                  <Note>
                    Requirements are checked on every {sampleName} this Source accepts, not on the sample. Choose a
                    field the sample carries and the type Integrios should enforce.
                  </Note>
                  {fields.length === 0 && requirements.length === 0 ? (
                    <Note>Add a valid request body with top-level values before defining requirements.</Note>
                  ) : (
                    <>
                      {requirements.map((row, index) => (
                        // biome-ignore lint/suspicious/noArrayIndexKey: positional rows with no stable identity
                        <div key={index} className="flex min-w-0 items-center gap-2">
                          <select
                            aria-label={`Required field ${index + 1}`}
                            className={`h-9 rounded-md border bg-surface px-2 text-sm ${leadField}`}
                            value={row.field}
                            onChange={(event) =>
                              setRequirements(
                                requirements.map((current, at) =>
                                  at === index ? { ...current, field: event.target.value } : current,
                                ),
                              )
                            }
                          >
                            <option value="">Choose a field…</option>
                            {(fields.includes(row.field) || row.field === "" ? fields : [row.field, ...fields]).map(
                              (field) => (
                                <option
                                  key={field}
                                  value={field}
                                  disabled={requirements.some((other, at) => at !== index && other.field === field)}
                                >
                                  {field}
                                </option>
                              ),
                            )}
                          </select>
                          {/* The type is the Operator's declaration of what the provider must always
                              send. Nothing is preselected from the sample: one request's value does
                              not establish the contract's type. */}
                          <select
                            aria-label={`Required field ${index + 1} type`}
                            className={`h-9 rounded-md border bg-surface px-2 text-sm ${trailField}`}
                            value={row.type}
                            onChange={(event) =>
                              setRequirements(
                                requirements.map((current, at) =>
                                  at === index ? { ...current, type: event.target.value as RequirementType } : current,
                                ),
                              )
                            }
                          >
                            <option value="">Choose a type…</option>
                            {requirementTypes.map((type) => (
                              <option key={type} value={type}>
                                {type}
                              </option>
                            ))}
                          </select>
                          <RemoveRow
                            label={`Remove required field ${index + 1}`}
                            onClick={() => setRequirements(requirements.filter((_, at) => at !== index))}
                          />
                        </div>
                      ))}
                      {mismatch ? (
                        <p role="alert" className="m-0 text-sm text-destructive">
                          {mismatch.field} is not {mismatch.type} in this request body.
                        </p>
                      ) : null}
                      <Button
                        type="button"
                        variant="outline"
                        size="sm"
                        className="self-start"
                        disabled={fields.length === 0 || requirements.length >= fields.length}
                        onClick={() => setRequirements([...requirements, { field: "", type: "" }])}
                      >
                        Add required field
                      </Button>
                    </>
                  )}
                </div>
              </details>
            </Pane>

            <div className="flex min-w-0 flex-col gap-4">
              {mode === "guided" ? (
                <GuidedFields
                  eventType={eventType}
                  headerNames={headerNames}
                  dangling={dangling}
                  paths={paths}
                  stale={bodyError !== null || headerError !== null}
                  headers={webhook ? lastValidHeaders : undefined}
                  body={lastValidBody}
                  onChange={changeEventType}
                  onAdvanced={() => setMode("advanced")}
                />
              ) : (
                <AdvancedExpression
                  expression={expression}
                  representable={representable}
                  headers={webhook ? lastValidHeaders : undefined}
                  onChange={(next) => {
                    setExpression(next);
                    preview.reset();
                  }}
                  onGuided={() => {
                    // Including when the editor was emptied: the guided pane is about to claim it
                    // represents this expression, so the expression becomes the one it generates.
                    setExpression(generated);
                    setMode("guided");
                    preview.reset();
                  }}
                />
              )}
              <IdentityFields
                identity={identity}
                sourceType={sourceType}
                headerNames={headerNames}
                paths={paths}
                onChange={changeIdentity}
              />
            </div>

            <Pane title="Normalized Event">
              {message ? (
                <div className="flex flex-col gap-1">
                  <p role="alert" className="m-0 text-sm text-destructive">
                    {message}
                  </p>
                  <Note>This request would be rejected. Fix it and preview again.</Note>
                </div>
              ) : preview.data ? (
                <>
                  <p className="m-0 text-xs font-medium">{fresh ? "Would be accepted" : "Result is out of date"}</p>
                  <pre className="m-0 max-h-96 overflow-auto text-xs">{formatJson(preview.data.output)}</pre>
                  {fresh ? null : <Note>The contract or the sample changed. Preview again to refresh this.</Note>}
                </>
              ) : (
                <>
                  <Note>Target envelope</Note>
                  <pre className="m-0 max-h-96 overflow-auto text-xs">{formatJson(targetEnvelope)}</pre>
                  <Note>Preview to see the Event Integrios would accept. Nothing is saved, and nothing is called.</Note>
                </>
              )}
            </Pane>
          </div>

          {mappable ? null : mode === "advanced" ? (
            <Note>Write an expression before using this configuration.</Note>
          ) : dangling.length > 0 ? (
            <p role="alert" className="m-0 text-sm text-destructive">
              This request no longer carries {dangling.join(", ")}. Choose another value, or restore it above.
            </p>
          ) : eventTypeSampleInvalid ? (
            <p role="alert" className="m-0 text-sm text-destructive">
              The selected Event type input must contain a non-empty string in this sample.
            </p>
          ) : eventTypeHalf ? (
            <Note>Choose where the Event type is read from before using this configuration.</Note>
          ) : identityIncomplete ? (
            <Note>Choose where the Event identity is read from before using this configuration.</Note>
          ) : (
            <Note>Author an Event type or an Event identity before using this configuration.</Note>
          )}

          <div className="flex flex-wrap items-center justify-between gap-3">
            <DialogPrimitive.Close asChild>
              <Button type="button" variant="outline">
                Back to Source
              </Button>
            </DialogPrimitive.Close>
            <div className="flex flex-wrap items-center gap-2">
              <Button
                type="button"
                variant="outline"
                disabled={preview.isPending || bodyError !== null || headerError !== null}
                onClick={() => preview.mutate()}
              >
                Preview normalized Event
              </Button>
              {/* A preview is evidence, not a gate: what stops this is an incomplete requirement row,
                  which would apply a manifest the API must refuse. */}
              <Button type="button" disabled={incomplete || mismatch !== undefined || !mappable} onClick={useDraft}>
                Use configuration
              </Button>
            </div>
          </div>
        </DialogPrimitive.Content>
      </DialogPrimitive.Portal>
    </DialogPrimitive.Root>
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
  onChange,
}: {
  identity: EventIdentityRule | null;
  sourceType: SourceInputType;
  headerNames: string[];
  paths: string[];
  onChange: (identity: EventIdentityRule | null) => void;
}) {
  const inputNoun = sourceType === "webhook" ? "request" : "message";
  const kinds = identityKinds.filter((kind) => (kind.types as readonly string[]).includes(sourceType));
  const selector = identity && identity.kind !== "message_id" ? identity.kind : "";

  return (
    <Pane title="Event identity">
      <Note>
        Where Integrios reads <span className="font-mono">source_event_id</span>, so one {inputNoun} sent twice becomes
        one Event. Read before the mapping, and never taken from it.
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
          noneLabel="Choose a header…"
          onChange={(value) => onChange({ kind: "header", value, allowMissing: identity?.allowMissing ?? false })}
        />
      ) : null}
      {selector === "json_path" ? (
        <Choice
          label="Identity field"
          value={identity?.value ?? ""}
          options={paths.map((path) => ({ value: pointerFrom(path), label: path }))}
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
    </Pane>
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
}: {
  label: string;
  value: string;
  /// The stored value and the name it is shown under. They differ where the artefact's reader needs
  /// a syntax the Operator should not have to read, as a JSON Pointer identity does.
  options: { value: string; label: string }[];
  onChange: (value: string) => void;
  noneLabel?: string;
}) {
  const offered =
    value !== "" && !options.some((option) => option.value === value)
      ? [{ value, label: `${value} (not in this request)` }, ...options]
      : options;
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
  onChange,
  onAdvanced,
}: {
  eventType: EventTypeRule;
  headerNames: string[];
  /// Values chosen from an earlier sample that this one no longer carries. They stay visible, and
  /// named, rather than reading as though nothing was ever chosen.
  dangling: string[];
  paths: string[];
  headers?: Record<string, string>;
  body: unknown;
  stale: boolean;
  onChange: (rule: EventTypeRule) => void;
  onAdvanced: () => void;
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
    <Pane
      title="Event fields"
      action={
        <Button type="button" variant="outline" size="sm" onClick={onAdvanced}>
          Advanced JSONata
        </Button>
      }
    >
      <Note>Choose where each Event value comes from. The JSONata mapping is generated from these choices.</Note>
      {stale ? <Stale /> : null}
      {dangling.length > 0 ? (
        <p role="alert" className="m-0 text-sm text-destructive">
          This request no longer carries {dangling.join(", ")}.
        </p>
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
            noneLabel="Choose a header…"
            onChange={(header) => onChange({ source: "header", header, prefix: eventTypePrefix })}
          />
        ) : null}
        {eventType.source === "body" ? (
          <Choice
            label="Event type field"
            value={eventType.path}
            options={named(paths)}
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

      <Target title="payload" requirement="Required">
        <Note>
          The entire input body, unchanged. Choosing which fields reach a destination belongs to the Subscription that
          knows where the Event is going: dropping them here would drop them for every Subscription on the Topic, and
          from the Event ledger.
        </Note>
      </Target>

      <Target title="source_event_id" requirement="From Event identity">
        <Note>
          Supplied by Event identity when configured. Without an identity, this Event cannot be deduplicated by Source.
        </Note>
      </Target>

      <Target title="metadata" requirement="Optional">
        <Note>Not included. Use Advanced JSONata when transport metadata has to be carried.</Note>
      </Target>
    </Pane>
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
  onGuided,
}: {
  expression: string;
  representable: boolean;
  headers?: Record<string, string>;
  onChange: (expression: string) => void;
  onGuided: () => void;
}) {
  return (
    <Pane
      title="Advanced JSONata"
      action={
        representable ? (
          <Button type="button" variant="outline" size="sm" onClick={onGuided}>
            Guided mode
          </Button>
        ) : (
          // Returning is destructive here, and only here: the expression cannot be shown in the
          // guided form without changing what it means, so going back replaces it.
          <ConfirmAction
            label="Reset to guided"
            variant="outline"
            question="Reset this expression to the guided mapping?"
            consequence="This expression cannot be shown in the guided form. Resetting replaces it."
            onConfirm={onGuided}
          />
        )
      }
    >
      <label htmlFor="builder-expression" className="sr-only">
        Source mapping expression
      </label>
      <Textarea
        id="builder-expression"
        spellCheck={false}
        value={expression}
        className="min-h-56 font-mono text-sm"
        onChange={(event) => onChange(event.target.value)}
      />
      <Note>
        The mapping reads the {headers ? "request body, and request headers under " : "message body"}
        {headers ? <span className="font-mono">$context.headers</span> : null}
        {headers ? "." : ""} It must produce <span className="font-mono">event_type</span> and{" "}
        <span className="font-mono">payload</span>.
      </Note>
      <Note>
        {representable
          ? "This expression matches the guided mapping and can return to it."
          : "This expression cannot be shown in the guided form."}
      </Note>
    </Pane>
  );
}
