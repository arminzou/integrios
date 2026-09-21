import { keepPreviousData, useQueries, useQuery } from "@tanstack/react-query";
import { cn } from "cn";
import { ChevronLeft, ChevronRight, X } from "lucide-react";
import { Dialog as DialogPrimitive } from "radix-ui";
import { useEffect, useState } from "react";
import type { UseFormReturn } from "react-hook-form";
import { Button } from "@/components/ui/button";
import { api } from "../api/client";
import { fieldError, formError } from "../api/problem";
import { asProblem, call } from "../api/query";
import type { components } from "../api/schema";
import { CodeBlock, CodeTextarea } from "../ui/codeHighlight";
import { FormError } from "../ui/controls";
import {
  expressionFromFieldMappings,
  type FieldMapping,
  parseFieldMappings,
  payloadFieldPaths,
} from "../ui/fieldMapping";
import { parseJson } from "../ui/json";
import { useSettled } from "../ui/settled";
import { since } from "../ui/time";
import type { SubscriptionValues } from "./Subscriptions";

type EventListItem = components["schemas"]["EventListItemDto"];
type EditableFieldMapping = FieldMapping & { id: string };

export const mappingEnvelope = (expression: string) =>
  expression.trim() === "" ? null : { engine: "jsonata", version: "1", expression };

/// One pane of the three: a fixed header, and a body that scrolls on its own once the pane has
/// reached the height the dialog gives it (at lg) or its cap (stacked, below lg).
const pane = "flex min-h-0 min-w-0 flex-col rounded-lg border max-lg:max-h-[60vh]";
const paneHead = "flex min-h-11 flex-wrap items-center justify-between gap-2 px-3 pt-3 pb-2";
/// A pasted sample's context, edited where it is read rather than in a form above the panes.
const contextInput =
  "h-7 w-full min-w-0 rounded border border-input bg-transparent px-1.5 font-mono text-xs outline-none focus-visible:border-ring focus-visible:ring-[3px] focus-visible:ring-ring/50";
const paneBody = "scroll-quiet flex min-h-0 flex-1 flex-col gap-2 overflow-y-auto px-3 pb-3";

/// Everything a preview is asked, whole, or why there is nothing to ask yet.
type PreviewCheck =
  | {
      kind: "request";
      expression: string;
      payload: unknown;
      eventType: string;
      acceptedAt: string;
      topicName: string;
    }
  | { kind: "problem"; message: string }
  | { kind: "none" };

/// What a settled check answered, keyed by the check it answered so Confirm can tell whether it is
/// about what is on screen now.
type PreviewAnswer = {
  for: string;
  output?: unknown;
  evaluationError?: string;
  syntaxError?: string;
  problem?: string;
};

const editableFieldMapping = (row: FieldMapping = { output: "", source: "" }): EditableFieldMapping => ({
  ...row,
  id: crypto.randomUUID(),
});

export function MappingPlayground({
  tenantId,
  topicId,
  form,
  originalExpression,
  open,
  onOpenChange,
  onReviewInvalidated,
  onConfirmed,
}: {
  tenantId: string;
  topicId: string;
  form: UseFormReturn<SubscriptionValues>;
  originalExpression: string;
  open: boolean;
  onOpenChange: (open: boolean) => void;
  onReviewInvalidated: () => void;
  onConfirmed: (expression: string) => void;
}) {
  const expression = form.watch("mapping");
  const parsedExpression = parseFieldMappings(expression);
  const [mappingMode, setMappingMode] = useState<"fields" | "advanced">(() =>
    parsedExpression ? "fields" : "advanced",
  );
  const [fieldMappings, setFieldMappings] = useState<EditableFieldMapping[]>(() =>
    (parsedExpression?.length ? parsedExpression : [{ output: "", source: "" }]).map(editableFieldMapping),
  );
  const [manual, setManual] = useState(false);
  const [eventId, setEventId] = useState<string>();
  const [manualPayload, setManualPayload] = useState("{}");
  // Unset until the Operator types one, so a pasted sample follows the Subscription's own Event type.
  const [manualEventType, setManualEventType] = useState<string>();
  const [manualAcceptedAt, setManualAcceptedAt] = useState(() => new Date().toISOString().slice(0, 16));
  const [shown, setShown] = useState<PreviewAnswer>();

  const topic = useQuery({
    queryKey: ["topic", tenantId, topicId],
    queryFn: () =>
      call(() =>
        api.GET("/admin/tenants/{tenantId}/topics/{id}", {
          params: { path: { tenantId, id: topicId } },
        }),
      ),
    enabled: open,
  });
  // A Subscription only ever maps Events of the types it selects, so those are the only accepted
  // Events worth previewing against. Without a type there is nothing to choose from, and another
  // type's payload would preview a shape this Subscription never receives.
  const subscriptionEventTypes = form.watch("event_types");
  const sampleEventType = manualEventType ?? subscriptionEventTypes[0] ?? "sample.event";
  // ponytail: one read per selected type, merged newest first; a multi-type filter on the Events list
  // replaces this if Subscriptions come to select many types.
  const eventReads = useQueries({
    queries: subscriptionEventTypes.map((eventType) => ({
      queryKey: ["mapping-events", tenantId, topicId, eventType],
      queryFn: () =>
        call(() =>
          api.GET("/admin/tenants/{tenantId}/events", {
            params: { path: { tenantId }, query: { topic_id: topicId, event_type: eventType, limit: 20 } },
          }),
        ),
      enabled: open,
    })),
  });
  const samples = eventReads
    .flatMap((read) => read.data?.items ?? [])
    .sort((left, right) => right.accepted_at.localeCompare(left.accepted_at))
    .slice(0, 20);
  const noSamples =
    subscriptionEventTypes.length === 0 || (eventReads.every((read) => read.isSuccess) && samples.length === 0);
  const pasting = manual || noSamples;
  const sampleIndex = Math.max(
    0,
    samples.findIndex((item) => item.event_id === eventId),
  );
  const selectedEvent: EventListItem | undefined = samples[sampleIndex];
  const selectedEventId = selectedEvent?.event_id;
  const event = useQuery({
    queryKey: ["event", tenantId, selectedEventId],
    queryFn: () => {
      if (!selectedEventId) throw new Error("No Event is selected.");
      return call(() =>
        api.GET("/admin/tenants/{tenantId}/events/{eventId}/deliveries", {
          params: { path: { tenantId, eventId: selectedEventId } },
        }),
      );
    },
    enabled: open && !pasting && Boolean(selectedEventId),
  });

  // Every edit changes what was reviewed, so the Subscription form's own review is withdrawn at once
  // rather than when the next preview lands.
  const invalidateReview = () => onReviewInvalidated();

  const step = (next: EventListItem | undefined) => {
    if (!next) return;
    setEventId(next.event_id);
    invalidateReview();
  };

  const updateFieldMappings = (next: EditableFieldMapping[]) => {
    setFieldMappings(next);
    form.setValue("mapping", expressionFromFieldMappings(next), { shouldDirty: true, shouldValidate: true });
    form.clearErrors("mapping");
    invalidateReview();
  };

  /// The preview runs itself once the Operator pauses, as the Event Builder's check does: output that
  /// follows the edit shows what a change does while it is being made, and a button to ask for it
  /// only left the pane empty until it was pressed. A row with no field chosen yet is left out of the
  /// expression by `expressionFromFieldMappings`, so a half-filled row is not previewed as an error.
  const topicName = topic.data?.key;
  const check = ((): PreviewCheck => {
    // Closed, there is nothing to check. The form around the dialog may already hold every input, so
    // without this a check settled before opening would never answer again once the dialog clears it.
    if (!open || !topicName) return { kind: "none" };
    if (pasting) {
      const parsed = parseJson(manualPayload);
      if (parsed.error) return { kind: "problem", message: parsed.error };
      if (!sampleEventType.trim() || Number.isNaN(new Date(manualAcceptedAt).getTime()))
        return { kind: "problem", message: "Enter an Event type and acceptance time." };
      return {
        kind: "request",
        expression,
        payload: parsed.value,
        eventType: sampleEventType.trim(),
        acceptedAt: new Date(manualAcceptedAt).toISOString(),
        topicName,
      };
    }
    if (!selectedEvent || event.data?.payload === undefined || event.data.payload === null) return { kind: "none" };
    return {
      kind: "request",
      expression,
      payload: event.data.payload,
      eventType: selectedEvent.event_type,
      acceptedAt: selectedEvent.accepted_at,
      topicName,
    };
  })();
  const current = JSON.stringify(check);
  const asked = useSettled(current, 400);
  const askedCheck = JSON.parse(asked) as PreviewCheck;
  const preview = useQuery({
    queryKey: ["mapping-preview", asked],
    queryFn: () => {
      const request = JSON.parse(asked) as PreviewCheck;
      const transform = request.kind === "request" ? mappingEnvelope(request.expression) : null;
      if (request.kind !== "request" || !transform) throw new Error("No mapping expression was provided.");
      return call(() =>
        api.POST("/admin/transform/preview", {
          body: {
            transform,
            sample_input: request.payload,
            sample_context: {
              event_type: request.eventType,
              topic_name: request.topicName,
              accepted_at: request.acceptedAt,
            },
          },
        }),
      );
    },
    enabled: open && askedCheck.kind === "request" && askedCheck.expression.trim() !== "",
    // A refusal is the answer, not a fault to retry.
    retry: false,
    staleTime: Number.POSITIVE_INFINITY,
    gcTime: 0,
    placeholderData: keepPreviousData,
  });
  const settled = ((): PreviewAnswer | undefined => {
    if (askedCheck.kind === "none") return undefined;
    if (askedCheck.kind === "problem") return { for: asked, problem: askedCheck.message };
    // No expression delivers the accepted payload unchanged, which needs no one to evaluate it.
    if (askedCheck.expression.trim() === "") return { for: asked, output: askedCheck.payload };
    if (preview.isFetching || preview.isPlaceholderData) return undefined;
    if (preview.error) {
      const problem = asProblem(preview.error);
      const syntaxError = fieldError(problem, "transform");
      return syntaxError
        ? { for: asked, syntaxError }
        : { for: asked, evaluationError: formError(problem) ?? "The mapping could not be evaluated." };
    }
    return preview.isSuccess ? { for: asked, output: preview.data?.output } : undefined;
  })();
  const settledKey = settled ? JSON.stringify(settled) : undefined;
  // A half-typed expression that does not parse, or a sample being typed, keeps the last successful
  // output on screen: it is still the best picture of what this mapping delivers, and clearing it on
  // every pause would blink the pane while the Operator types. A last failure is not kept: it is
  // about an expression that is already gone.
  useEffect(() => {
    if (!settledKey) return;
    const answer = JSON.parse(settledKey) as PreviewAnswer;
    setShown((previous) => (answer.syntaxError || answer.problem ? { ...answer, output: previous?.output } : answer));
  }, [settledKey]);
  const updating = current !== asked || preview.isFetching;
  const reviewed =
    shown?.for === current &&
    shown.output !== undefined &&
    !shown.syntaxError &&
    !shown.problem &&
    !shown.evaluationError;
  const mappingError = form.formState.errors.mapping?.message ?? (shown?.syntaxError || undefined);

  const input = pasting ? parseJson(manualPayload).value : event.data?.payload;
  const unavailable = pasting
    ? false
    : !selectedEvent || event.data?.payload === undefined || event.data.payload === null;
  const sourceOptions = [
    ...payloadFieldPaths(input),
    "$context.event_type",
    "$context.topic_name",
    "$context.accepted_at",
  ];

  return (
    <DialogPrimitive.Root
      open={open}
      onOpenChange={(next) => {
        if (next) setShown(undefined);
        onOpenChange(next);
      }}
    >
      <DialogPrimitive.Trigger asChild>
        <Button type="button" variant="outline" className="self-start">
          {expression.trim() ? "Edit in Playground" : "Add mapping in Playground"}
        </Button>
      </DialogPrimitive.Trigger>
      <DialogPrimitive.Portal>
        <DialogPrimitive.Overlay className="fixed inset-0 z-60 bg-ink/25" />
        <DialogPrimitive.Content className="fixed inset-4 z-70 flex max-h-[calc(100vh-2rem)] flex-col gap-4 overflow-y-auto rounded-lg border bg-surface p-4 shadow-[0_24px_64px_-32px_rgb(23_23_23/0.45)] outline-none md:inset-x-10 lg:inset-x-[max(2.5rem,calc((100vw-80rem)/2))]">
          <div className="flex items-start justify-between gap-3">
            <div>
              <DialogPrimitive.Title className="m-0">Mapping Playground</DialogPrimitive.Title>
              <DialogPrimitive.Description className="m-0 mt-1 text-sm text-ink-secondary">
                Preview this unsaved expression against one accepted Event or a manual sample. Nothing is saved.
              </DialogPrimitive.Description>
            </div>
            <DialogPrimitive.Close
              aria-label="Back to Subscription"
              className="flex size-8 shrink-0 items-center justify-center rounded-md hover:bg-hover-surface focus-visible:ring-[3px] focus-visible:ring-ring/50"
            >
              <X aria-hidden="true" className="size-4" />
            </DialogPrimitive.Close>
          </div>

          {/* The panes take the rest of the dialog and each scrolls on its own, so a long payload scrolls
              inside its pane instead of stretching the dialog, and a preview landing never resizes the
              row. The two JSON panes get the width: they are what is read and compared, and the mapping
              pane needs only a row's width. Stacked below lg, each capped so the stack stays a page. */}
          <div className="grid gap-4 lg:min-h-80 lg:flex-1 lg:grid-cols-[minmax(0,1fr)_minmax(0,1.15fr)_minmax(0,1fr)]">
            {/* Everything about the input lives in its pane: where it comes from, which one, its context
                and its payload. Above the panes it read as settings for the whole dialog, and a pasted
                sample was shown twice, once to edit and once to read. */}
            <section className={pane}>
              <div className={paneHead}>
                <h3 className="m-0 text-sm">{samples.length > 0 ? "Sample" : "Pasted sample"}</h3>
                {samples.length > 0 ? (
                  <fieldset className="m-0 inline-flex min-w-0 overflow-hidden rounded-md border p-0 text-xs">
                    <legend className="sr-only">Sample source</legend>
                    {(
                      [
                        ["Accepted Event", false],
                        ["Paste JSON", true],
                      ] as const
                    ).map(([label, paste]) => (
                      <button
                        key={label}
                        type="button"
                        aria-pressed={pasting === paste}
                        onClick={() => {
                          setManual(paste);
                          invalidateReview();
                        }}
                        className={cn(
                          "px-2.5 py-1 outline-none focus-visible:ring-[3px] focus-visible:ring-ring/50",
                          pasting === paste
                            ? "bg-selected-surface font-medium text-ink"
                            : "text-ink-secondary hover:bg-hover-surface",
                        )}
                      >
                        {label}
                      </button>
                    ))}
                  </fieldset>
                ) : null}
              </div>
              <div className={paneBody}>
                {noSamples ? (
                  <p className="m-0 text-xs text-ink-secondary">
                    {subscriptionEventTypes.length === 0
                      ? "Select this Subscription's Event types to preview against its accepted Events. Until then, paste a sample."
                      : "No accepted Events of this Subscription's types yet. Send a test Event from the Source's setup guide, or paste a sample."}
                  </p>
                ) : null}
                {!pasting ? (
                  <div className="flex items-center gap-2 text-xs text-ink-secondary">
                    <Button
                      type="button"
                      variant="outline"
                      size="icon-sm"
                      aria-label="Newer Event"
                      disabled={sampleIndex === 0}
                      onClick={() => step(samples[sampleIndex - 1])}
                    >
                      <ChevronLeft aria-hidden="true" />
                    </Button>
                    <span className="min-w-0 flex-1">
                      <span aria-live="polite" className="tabular-nums">
                        {samples.length > 0 ? `${sampleIndex + 1} of ${samples.length}` : "Loading Events…"}
                      </span>
                      {selectedEvent ? (
                        <>
                          {" · "}
                          <span title={new Date(selectedEvent.accepted_at).toLocaleString()}>
                            {since(selectedEvent.accepted_at)}
                          </span>
                        </>
                      ) : null}
                    </span>
                    <Button
                      type="button"
                      variant="outline"
                      size="icon-sm"
                      aria-label="Older Event"
                      disabled={sampleIndex >= samples.length - 1}
                      onClick={() => step(samples[sampleIndex + 1])}
                    >
                      <ChevronRight aria-hidden="true" />
                    </Button>
                  </div>
                ) : null}
                {/* What a mapping reads besides the payload, for this sample: it changes as the sample
                    does, and each value is a field a mapping row can choose. A pasted sample has no
                    Event behind it, so its type and acceptance time are entered right here. */}
                <div className="flex flex-col gap-1">
                  <h4 className="m-0 text-xs font-medium text-ink-secondary">Context</h4>
                  <dl className="m-0 grid grid-cols-[auto_minmax(0,1fr)] items-center gap-x-3.5 gap-y-0.5 font-mono text-xs [&>dd]:m-0 [&>dd]:min-w-0 [&>dd]:break-all [&>dt]:text-ink-secondary">
                    <dt>event_type</dt>
                    <dd>
                      {pasting ? (
                        <input
                          aria-label="Event type"
                          value={sampleEventType}
                          onChange={(event) => {
                            setManualEventType(event.target.value);
                            invalidateReview();
                          }}
                          className={contextInput}
                        />
                      ) : (
                        (selectedEvent?.event_type ?? "—")
                      )}
                    </dd>
                    {!pasting && selectedEvent?.source_event_id ? (
                      <>
                        <dt>source_event_id</dt>
                        <dd>{selectedEvent.source_event_id}</dd>
                      </>
                    ) : null}
                    <dt>topic_name</dt>
                    <dd>{topic.data?.key ?? "—"}</dd>
                    <dt>accepted_at</dt>
                    <dd>
                      {pasting ? (
                        <input
                          type="datetime-local"
                          aria-label="Accepted at"
                          value={manualAcceptedAt}
                          onChange={(event) => {
                            setManualAcceptedAt(event.target.value);
                            invalidateReview();
                          }}
                          className={contextInput}
                        />
                      ) : (
                        (selectedEvent?.accepted_at ?? "—")
                      )}
                    </dd>
                  </dl>
                </div>
                {pasting ? (
                  <>
                    <label htmlFor="manual-payload" className="text-xs font-medium text-ink-secondary">
                      Payload
                    </label>
                    <CodeTextarea
                      id="manual-payload"
                      value={manualPayload}
                      className="min-h-40 lg:flex-1"
                      onChange={(event) => {
                        setManualPayload(event.target.value);
                        invalidateReview();
                      }}
                    />
                  </>
                ) : (
                  <>
                    <h4 className="m-0 text-xs font-medium text-ink-secondary">Payload</h4>
                    {input !== undefined ? (
                      <CodeBlock value={input} />
                    ) : (
                      <p className="m-0 text-sm text-ink-secondary">Choose a sample with an available payload.</p>
                    )}
                  </>
                )}
              </div>
            </section>
            <section className={pane}>
              <div className={paneHead}>
                <h3 className="m-0 text-sm">{mappingMode === "fields" ? "Field mapping" : "Advanced JSONata"}</h3>
                <Button
                  type="button"
                  variant="outline"
                  size="sm"
                  disabled={mappingMode === "advanced" && !parsedExpression}
                  onClick={() => {
                    if (mappingMode === "fields") {
                      setMappingMode("advanced");
                      return;
                    }
                    if (parsedExpression) {
                      setFieldMappings(
                        (parsedExpression.length ? parsedExpression : [{ output: "", source: "" }]).map(
                          editableFieldMapping,
                        ),
                      );
                      setMappingMode("fields");
                    }
                  }}
                >
                  {mappingMode === "fields" ? "Advanced JSONata" : "Field mapping"}
                </Button>
              </div>
              <div className={paneBody}>
                {mappingMode === "fields" ? (
                  <div className="flex flex-col gap-3">
                    <p className="m-0 text-xs text-ink-secondary">
                      Name each output field, then choose the Event or context value it receives.
                    </p>
                    {fieldMappings.length > 0 ? (
                      <div className="hidden grid-cols-[minmax(0,0.8fr)_auto_minmax(0,1.2fr)_auto] gap-2 text-xs font-medium text-ink-secondary sm:grid">
                        <span>Output field</span>
                        <span aria-hidden="true" />
                        <span>Event or context field</span>
                        <span aria-hidden="true" />
                      </div>
                    ) : null}
                    {fieldMappings.length === 0 ? (
                      <p className="m-0 text-sm text-ink-secondary">
                        No fields mapped. The accepted payload will be delivered unchanged.
                      </p>
                    ) : null}
                    {fieldMappings.map((row, index) => {
                      const options =
                        row.source && !sourceOptions.includes(row.source)
                          ? [row.source, ...sourceOptions]
                          : sourceOptions;
                      return (
                        <div
                          key={row.id}
                          className="grid min-w-0 grid-cols-[minmax(0,1fr)_auto] gap-2 sm:grid-cols-[minmax(0,0.8fr)_auto_minmax(0,1.2fr)_auto] sm:items-center"
                        >
                          <input
                            aria-label={`Output field ${index + 1}`}
                            placeholder="output_field"
                            value={row.output}
                            onChange={(event) => {
                              const next = fieldMappings.map((item, itemIndex) =>
                                itemIndex === index ? { ...item, output: event.target.value } : item,
                              );
                              updateFieldMappings(next);
                            }}
                            className="h-9 min-w-0 rounded-md border border-input bg-transparent px-3 text-sm outline-none focus-visible:border-ring focus-visible:ring-[3px] focus-visible:ring-ring/50"
                          />
                          <span aria-hidden="true" className="text-ink-secondary">
                            ←
                          </span>
                          <select
                            aria-label={`Event field ${index + 1}`}
                            value={row.source}
                            onChange={(event) => {
                              const next = fieldMappings.map((item, itemIndex) =>
                                itemIndex === index ? { ...item, source: event.target.value } : item,
                              );
                              updateFieldMappings(next);
                            }}
                            className="col-span-2 h-9 min-w-0 rounded-md border border-input bg-transparent px-3 text-sm outline-none focus-visible:border-ring focus-visible:ring-[3px] focus-visible:ring-ring/50 sm:col-span-1"
                          >
                            <option value="">Choose a field…</option>
                            {options.map((option) => (
                              <option key={option} value={option}>
                                {option}
                              </option>
                            ))}
                          </select>
                          <Button
                            type="button"
                            variant="ghost"
                            size="icon-sm"
                            aria-label={`Remove mapping ${index + 1}`}
                            onClick={() =>
                              updateFieldMappings(fieldMappings.filter((_, itemIndex) => itemIndex !== index))
                            }
                          >
                            <X aria-hidden="true" className="size-4" />
                          </Button>
                        </div>
                      );
                    })}
                    <Button
                      type="button"
                      variant="outline"
                      size="sm"
                      className="self-start"
                      onClick={() => {
                        setFieldMappings((current) => [...current, editableFieldMapping()]);
                        invalidateReview();
                      }}
                    >
                      Add field
                    </Button>
                  </div>
                ) : (
                  <>
                    <CodeTextarea
                      id="playground-mapping"
                      language="text"
                      aria-label="Playground mapping expression"
                      aria-invalid={Boolean(mappingError)}
                      aria-describedby={mappingError ? "playground-mapping-error" : undefined}
                      value={expression}
                      onChange={(event) => {
                        form.setValue("mapping", event.target.value, { shouldDirty: true, shouldValidate: true });
                        form.clearErrors("mapping");
                        invalidateReview();
                      }}
                      className="min-h-44 lg:flex-1"
                    />
                    <p className="m-0 text-xs text-ink-secondary">
                      {parsedExpression
                        ? "This expression can return to field mapping without losing information."
                        : "This expression uses advanced JSONata and cannot be represented safely as field rows."}
                    </p>
                  </>
                )}
                {/* Not an alert: while the Operator types, a half-written expression fails to parse at
                  every pause, and an announcement each time would talk over the typing. The editor
                  names it through aria-describedby instead. */}
                {mappingError ? (
                  <p id="playground-mapping-error" className="m-0 text-sm text-destructive">
                    {mappingError}
                  </p>
                ) : null}
              </div>
            </section>
            <section className={pane}>
              <div className={paneHead}>
                <h3 className="m-0 text-sm">Preview body</h3>
                {/* Seen, not announced: it changes on every pause in typing. */}
                <span aria-hidden="true" className="text-xs text-ink-secondary">
                  {updating ? "Updating…" : ""}
                </span>
              </div>
              <div className={paneBody} aria-busy={updating}>
                {shown?.problem ? <p className="m-0 text-sm text-destructive">{shown.problem}</p> : null}
                {(shown?.syntaxError || shown?.problem) && shown.output !== undefined ? (
                  <p className="m-0 text-xs text-ink-secondary">Showing the last successful preview.</p>
                ) : null}
                {shown?.evaluationError ? (
                  <div className="flex flex-col gap-2">
                    <p className="m-0 text-sm text-destructive">{shown.evaluationError}</p>
                    <p className="m-0 text-sm text-ink-secondary">
                      This Event would fail delivery. Equivalent Events would retry and may dead-letter after save.
                    </p>
                  </div>
                ) : shown?.output !== undefined ? (
                  <CodeBlock value={shown.output} />
                ) : (
                  <p className="m-0 text-sm text-ink-secondary">
                    {unavailable ? "Choose a sample with an available payload." : "The preview appears here."}
                  </p>
                )}
              </div>
            </section>
          </div>

          <FormError
            message={
              formError(asProblem(topic.error)) ??
              formError(asProblem(eventReads.find((read) => read.isError)?.error)) ??
              formError(asProblem(event.error))
            }
          />
          <div className="mt-auto flex flex-wrap items-center justify-between gap-2 border-t border-accent-border pt-4">
            <DialogPrimitive.Close asChild>
              <Button type="button" variant="outline">
                Back to Subscription
              </Button>
            </DialogPrimitive.Close>
            {/* Enabled only by a successful preview of exactly what is on screen now: the output beside
                it is the review, so no separate step is needed to ask for one. */}
            <DialogPrimitive.Close asChild>
              <Button
                type="button"
                disabled={expression === originalExpression || !reviewed}
                onClick={() => onConfirmed(expression)}
              >
                Confirm mapping change
              </Button>
            </DialogPrimitive.Close>
          </div>
        </DialogPrimitive.Content>
      </DialogPrimitive.Portal>
    </DialogPrimitive.Root>
  );
}

/// One form for both create and update: the Admin API takes the same body for each, so splitting it
/// into two near-identical forms would only invite them to drift apart.
