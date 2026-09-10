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
import { ConfirmAction } from "../ui/controls";
import { payloadFieldPaths } from "../ui/fieldMapping";
import { formatJson } from "../ui/json";
import {
  type Completion,
  completionsAt,
  duplicatePayloadFields,
  emptyGuided,
  type GuidedMapping,
  guidedExpression,
  headerContext,
  type InputRequirement,
  matchesRequirementType,
  type RequirementType,
  requirableFields,
  requirementsSchema,
  requirementTypes,
} from "./sourceMapping";

export type SourceContractDraft = { expression: string; schema?: Record<string, unknown> };

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

/// The share of an editor row each field takes. The leading one carries the name that has to be read
/// exactly — a header like `x-hub-signature-256`, or a discovered field path — and a row is only
/// about 400 pixels wide at three panes, so the split is not even.
const leadField = "min-w-0 flex-[3]";
const trailField = "min-w-0 flex-[2]";

/// Which part of the Source-contract pipeline refused the preview. The Admin API answers with one
/// message that names the document it was reading, so the stage is read back off that name rather
/// than guessed at — an unrecognized message keeps the neutral label instead of a wrong one.
function failingStage(message: string): string {
  if (message.startsWith("schema")) return "Input requirements";
  if (message.startsWith("sample_input") || message.startsWith("input")) return "Representative request";
  if (message.startsWith("mapping") || message.startsWith("Failed to compile")) return "Mapping";
  if (message.startsWith("Source mapping output") || message.startsWith("Transform evaluation"))
    return "Normalized Event";
  return "Preview";
}

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
    <p className="m-0 text-xs text-ink-secondary">
      Showing fields from the last valid request body. Fix the body to refresh them.
    </p>
  );
}

/// The Integrios Event Builder: representative input on the left, the Event fields it is mapped
/// into in the middle, and what Integrios would accept on the right. Everything it holds is
/// ephemeral — the sample, the headers and the guided choices never leave the browser. Only the
/// generated expression and the input-requirements schema are handed back to the Connector draft.
export function EventBuilder({
  draft,
  onUse,
  contractKey,
}: {
  draft: SourceContractDraft;
  onUse: (draft: SourceContractDraft) => void;
  contractKey: string;
}) {
  const [open, setOpen] = useState(false);
  const [headers, setHeaders] = useState<HeaderRow[]>([{ name: "", value: "" }]);
  const [body, setBody] = useState("{}");
  const [lastValidBody, setLastValidBody] = useState<unknown>({});
  const [bodyError, setBodyError] = useState<string | null>(null);
  const [headerError, setHeaderError] = useState<string | null>(null);
  const [lastValidHeaders, setLastValidHeaders] = useState<Record<string, string>>({});
  const [requirements, setRequirements] = useState<InputRequirement[]>(() => requirementsFrom(draft.schema));
  const [guided, setGuided] = useState<GuidedMapping>(emptyGuided);
  const [expression, setExpression] = useState(draft.expression);
  const [mode, setMode] = useState<"guided" | "advanced">("guided");
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

  const generated = guidedExpression(guided);
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
    ...(guided.eventHeader && !headerNames.includes(guided.eventHeader) ? [guided.eventHeader] : []),
    ...(guided.identityHeader && !headerNames.includes(guided.identityHeader) ? [guided.identityHeader] : []),
    ...(guided.actionPath && !paths.includes(guided.actionPath) ? [guided.actionPath] : []),
    ...guided.payloadRows.filter((row) => row.source !== "" && !paths.includes(row.source)).map((row) => row.source),
  ];
  const duplicates = guided.payloadMode === "fields" ? duplicatePayloadFields(guided.payloadRows) : [];
  /// event_type is required and cannot be inferred, so a guided draft that names no source for it is
  /// refused here rather than sent as an empty string the runtime would reject on every request.
  const mappable =
    mode === "advanced"
      ? expression.trim() !== ""
      : (guided.eventPrefix.trim() !== "" || guided.eventHeader !== "") &&
        dangling.length === 0 &&
        duplicates.length === 0;

  /// What a displayed result was produced from. Any change to the contract or the sample — a header
  /// the mapping reads included — makes both a success and a failure a statement about something
  /// that is no longer on screen.
  const signature = JSON.stringify([expression, schema ?? null, lastValidBody, lastValidHeaders]);

  const preview = useMutation({
    mutationFn: () =>
      call(() =>
        api.POST("/admin/connectors/source-contracts/preview", {
          body: {
            schema: schema ?? null,
            mapping: { engine: "jsonata", version: "1", expression },
            sample_input: sample.current.body,
            sample_context: { headers: sample.current.headers },
          },
        }),
      ),
    onMutate: () => setPreviewed(signature),
  });

  const problem = asProblem(preview.error);
  const message = problem ? (formError(problem) ?? "The preview could not be run.") : null;
  // The stage is read off the API's own message for the request document, not off the rendered text:
  // that text may lead with the generic validation title, which names no stage at all.
  const stage = problem ? failingStage(problem.errors[""]?.[0] ?? problem.detail ?? "") : null;
  const fresh = previewed === signature;

  const changeGuided = (change: Partial<GuidedMapping>) => {
    const next = { ...guided, ...change };
    setGuided(next);
    setExpression(guidedExpression(next));
    preview.reset();
  };

  /// An imported schema this form cannot show as rows is not the Operator's to lose here: it is
  /// carried back out untouched unless requirements replace it.
  const opaqueSchema = requirementsFrom(draft.schema).length === 0 ? draft.schema : undefined;

  const useDraft = () => {
    onUse({ expression, schema: schema ?? opaqueSchema });
    setOpen(false);
  };

  return (
    <DialogPrimitive.Root
      open={open}
      onOpenChange={(next) => {
        // Opening onto an expression this form did not generate starts where that expression can be
        // read: the editor that owns it.
        if (next && expression.trim() !== "" && expression !== generated) setMode("advanced");
        setOpen(next);
      }}
    >
      <DialogPrimitive.Trigger asChild>
        <Button type="button" variant="outline" className="self-start">
          Open Integrios Event Builder
        </Button>
      </DialogPrimitive.Trigger>
      <DialogPrimitive.Portal>
        <DialogPrimitive.Overlay className="fixed inset-0 z-60 bg-ink/25" />
        {/* Wider than the authoring flyout it opens from: the representative request, the Event
            fields it produces, and the preview are read together, and at a sheet's width they
            cannot be. One column below that, where three would each be too narrow to read. */}
        <DialogPrimitive.Content className="fixed inset-4 z-70 flex max-h-[calc(100vh-2rem)] flex-col gap-4 overflow-y-auto rounded-lg border bg-canvas p-4 shadow-[0_24px_64px_-32px_rgb(23_23_23/0.45)] outline-none md:inset-x-8 xl:inset-x-[max(2rem,calc((100vw-88rem)/2))]">
          <div className="flex items-start justify-between gap-3">
            <div className="min-w-0">
              <DialogPrimitive.Title className="m-0">Integrios Event Builder</DialogPrimitive.Title>
              <DialogPrimitive.Description className="m-0 mt-1 text-sm text-ink-secondary">
                Define how a request the <span className="font-mono">{contractKey || "Source"}</span> contract accepts
                becomes an Integrios Event. Nothing here is saved with the Connector except the mapping and the input
                requirements.
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
            <Pane title="Representative request">
              {/* Headers come before the body because a webhook usually carries the Event's identity
                  and type in them, and the body is what those choices are then read against. */}
              <fieldset className="m-0 flex min-w-0 flex-col gap-2 border-0 p-0">
                <legend className="text-sm font-medium">Request headers</legend>
                <Note>Add only headers the mapping reads. Use representative values, never secrets.</Note>
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
                      aria-label={`Header ${index + 1} representative value`}
                      placeholder="Representative value"
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

              <div className="flex min-w-0 flex-col gap-1.5">
                <label htmlFor="builder-body" className="text-sm font-medium">
                  Request body (JSON)
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
                    Requirements are checked on every request this Source accepts, not on the sample. Choose a field the
                    sample carries and the type Integrios should enforce.
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

            {mode === "guided" ? (
              <GuidedFields
                guided={guided}
                headerNames={headerNames}
                dangling={dangling}
                paths={paths}
                stale={bodyError !== null || headerError !== null}
                headers={lastValidHeaders}
                body={lastValidBody}
                onChange={changeGuided}
                onAdvanced={() => setMode("advanced")}
              />
            ) : (
              <AdvancedExpression
                expression={expression}
                representable={representable}
                headers={lastValidHeaders}
                body={lastValidBody}
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

            <Pane title="Normalized Event">
              {message ? (
                <div className="flex flex-col gap-1">
                  <p className="m-0 text-xs font-medium">{stage}</p>
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
                <Note>Preview to see the Event Integrios would accept. Nothing is saved, and nothing is called.</Note>
              )}
            </Pane>
          </div>

          {mappable ? null : mode === "advanced" ? (
            <Note>Write an expression before using this configuration.</Note>
          ) : dangling.length > 0 ? (
            <p role="alert" className="m-0 text-sm text-destructive">
              This request no longer carries {dangling.join(", ")}. Choose another value, or restore it above.
            </p>
          ) : duplicates.length > 0 ? (
            <p role="alert" className="m-0 text-sm text-destructive">
              Payload field {duplicates.join(", ")} is named more than once.
            </p>
          ) : (
            <Note>Give event_type a source — a text prefix or a header — before using this configuration.</Note>
          )}

          <div className="flex flex-wrap items-center justify-between gap-3">
            <DialogPrimitive.Close asChild>
              <Button type="button" variant="outline">
                Back to Connector
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
  options: string[];
  onChange: (value: string) => void;
  noneLabel?: string;
}) {
  const offered = value !== "" && !options.includes(value) ? [value, ...options] : options;
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
          <option key={option} value={option}>
            {options.includes(option) ? option : `${option} (not in this request)`}
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
  guided,
  headerNames,
  dangling,
  paths,
  headers,
  body,
  stale,
  onChange,
  onAdvanced,
}: {
  guided: GuidedMapping;
  headerNames: string[];
  /// Values chosen from an earlier sample that this one no longer carries. They stay visible, and
  /// named, rather than reading as though nothing was ever chosen.
  dangling: string[];
  paths: string[];
  headers: Record<string, string>;
  body: unknown;
  stale: boolean;
  onChange: (change: Partial<GuidedMapping>) => void;
  onAdvanced: () => void;
}) {
  const value = (path: string) =>
    path.split(".").reduce<unknown>((current, part) => (current as Record<string, unknown> | undefined)?.[part], body);
  const preview = [
    guided.eventPrefix.trim(),
    guided.eventHeader ? headers[guided.eventHeader] : "",
    guided.actionPath ? String(value(guided.actionPath) ?? "") : "",
  ]
    .filter(Boolean)
    .join(".");

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
        <div className="flex min-w-0 flex-col gap-1 text-sm">
          <label htmlFor="builder-event-prefix" className="text-ink-secondary">
            Text prefix
          </label>
          <Input
            id="builder-event-prefix"
            value={guided.eventPrefix}
            onChange={(event) => onChange({ eventPrefix: event.target.value })}
          />
        </div>
        <Choice
          label="Event name from header"
          value={guided.eventHeader}
          options={headerNames}
          noneLabel="No header"
          onChange={(eventHeader) => onChange({ eventHeader })}
        />
        <Choice
          label="Append field"
          value={guided.actionPath}
          options={paths}
          noneLabel="None"
          onChange={(actionPath) => onChange({ actionPath })}
        />
        <Note>Preview: {preview === "" ? "—" : preview}</Note>
      </Target>

      <Target title="source_event_id" requirement="Optional">
        <Choice
          label="From header"
          value={guided.identityHeader}
          options={headerNames}
          noneLabel="None"
          onChange={(identityHeader) => onChange({ identityHeader })}
        />
        {guided.identityHeader ? (
          <label className="flex items-start gap-2.5 text-sm">
            <input
              type="checkbox"
              className="mt-0.5 size-4 shrink-0"
              checked={guided.requireIdentity}
              onChange={(event) => onChange({ requireIdentity: event.target.checked })}
            />
            <span>Reject the request when this value is missing</span>
          </label>
        ) : null}
      </Target>

      <Target title="payload" requirement="Required">
        <fieldset className="m-0 flex flex-col gap-1 border-0 p-0">
          <legend className="sr-only">Payload source</legend>
          {[
            { value: "entire" as const, label: "Use the entire request body" },
            { value: "fields" as const, label: "Choose payload fields" },
          ].map((option) => (
            <label key={option.value} className="flex items-start gap-2.5 text-sm">
              <input
                type="radio"
                name="builder-payload-mode"
                className="mt-0.5 size-4 shrink-0"
                checked={guided.payloadMode === option.value}
                onChange={() => onChange({ payloadMode: option.value })}
              />
              <span>{option.label}</span>
            </label>
          ))}
        </fieldset>
        {guided.payloadMode === "fields" ? (
          <>
            {guided.payloadRows.map((row, index) => (
              // biome-ignore lint/suspicious/noArrayIndexKey: positional rows with no stable identity
              <div key={index} className="flex min-w-0 items-center gap-2">
                <Input
                  aria-label={`Payload field ${index + 1} name`}
                  placeholder="output_field"
                  className={`${trailField} font-mono text-sm`}
                  value={row.output}
                  onChange={(event) =>
                    onChange({
                      payloadRows: guided.payloadRows.map((current, at) =>
                        at === index ? { ...current, output: event.target.value } : current,
                      ),
                    })
                  }
                />
                <select
                  aria-label={`Payload field ${index + 1} source`}
                  className={`h-9 rounded-md border bg-surface px-2 text-sm ${leadField}`}
                  value={row.source}
                  onChange={(event) =>
                    onChange({
                      payloadRows: guided.payloadRows.map((current, at) =>
                        at === index ? { ...current, source: event.target.value } : current,
                      ),
                    })
                  }
                >
                  <option value="">Choose a field…</option>
                  {(row.source === "" || paths.includes(row.source) ? paths : [row.source, ...paths]).map((path) => (
                    <option key={path} value={path}>
                      {paths.includes(path) ? path : `${path} (not in this request)`}
                    </option>
                  ))}
                </select>
                <RemoveRow
                  label={`Remove payload field ${index + 1}`}
                  onClick={() => onChange({ payloadRows: guided.payloadRows.filter((_, at) => at !== index) })}
                />
              </div>
            ))}
            {guided.payloadRows.every((row) => row.output.trim() === "" || row.source === "") ? (
              <Note>Name a field and choose where it comes from, or the payload is sent empty.</Note>
            ) : null}
            <Button
              type="button"
              variant="outline"
              size="sm"
              className="self-start"
              onClick={() => onChange({ payloadRows: [...guided.payloadRows, { output: "", source: "" }] })}
            >
              Add payload field
            </Button>
          </>
        ) : null}
      </Target>

      <Target title="metadata" requirement="Optional">
        <Note>Not included. Use Advanced JSONata when transport metadata has to be carried.</Note>
      </Target>
    </Pane>
  );
}

/// The same expression draft, edited directly. Completion offers the representative request's own
/// paths, the bounded context, and functions the runtime evaluator really has.
function AdvancedExpression({
  expression,
  representable,
  headers,
  body,
  onChange,
  onGuided,
}: {
  expression: string;
  representable: boolean;
  headers: Record<string, string>;
  body: unknown;
  onChange: (expression: string) => void;
  onGuided: () => void;
}) {
  const editor = useRef<HTMLTextAreaElement>(null);
  const [suggestions, setSuggestions] = useState<{ start: number; items: Completion[] }>({ start: 0, items: [] });
  const [selected, setSelected] = useState(0);

  const refresh = (element: HTMLTextAreaElement) => {
    setSuggestions(completionsAt(element.value, element.selectionStart, { headers, body }));
    setSelected(0);
  };

  const insert = (item: Completion) => {
    const element = editor.current;
    if (!element) return;
    element.setRangeText(item.insert, suggestions.start, element.selectionStart, "end");
    element.selectionStart -= item.cursorBack ?? 0;
    element.selectionEnd = element.selectionStart;
    onChange(element.value);
    setSuggestions({ start: 0, items: [] });
    element.focus();
  };

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
        ref={editor}
        spellCheck={false}
        value={expression}
        className="min-h-56 font-mono text-sm"
        onChange={(event) => {
          onChange(event.target.value);
          refresh(event.target);
        }}
        onClick={(event) => refresh(event.currentTarget)}
        onBlur={() => setSuggestions({ start: 0, items: [] })}
        onKeyDown={(event) => {
          if (suggestions.items.length === 0) return;
          if (event.key === "ArrowDown" || event.key === "ArrowUp") {
            event.preventDefault();
            const step = event.key === "ArrowDown" ? 1 : suggestions.items.length - 1;
            setSelected((current) => (current + step) % suggestions.items.length);
          } else if (event.key === "Enter" || event.key === "Tab") {
            event.preventDefault();
            insert(suggestions.items[selected]);
          } else if (event.key === "Escape") {
            event.preventDefault();
            setSuggestions({ start: 0, items: [] });
          }
        }}
      />
      {suggestions.items.length > 0 ? (
        <ul
          aria-label="JSONata suggestions"
          className="m-0 flex max-h-48 list-none flex-col overflow-auto rounded-md border p-0"
        >
          {suggestions.items.map((item, index) => (
            <li key={item.label}>
              <button
                type="button"
                aria-current={index === selected}
                className="flex w-full items-baseline gap-2 px-2 py-1.5 text-left text-sm aria-[current=true]:bg-selected-surface hover:bg-hover-surface"
                onMouseDown={(event) => event.preventDefault()}
                onClick={() => insert(item)}
              >
                <code className="font-mono text-xs">{item.label}</code>
                <span className="min-w-0 truncate text-xs text-ink-secondary">{item.detail}</span>
              </button>
            </li>
          ))}
        </ul>
      ) : null}
      <Note>
        Type <span className="font-mono">$</span> or <span className="font-mono">$context.headers.</span> for
        suggestions, and use the arrow keys and Enter to insert one.
      </Note>
      <Note>
        {representable
          ? "This expression matches the guided mapping and can return to it."
          : "This expression cannot be shown in the guided form."}
      </Note>
    </Pane>
  );
}
