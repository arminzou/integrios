import { useMutation, useQueries, useQuery } from "@tanstack/react-query";
import { ChevronLeft, ChevronRight, X } from "lucide-react";
import { Dialog as DialogPrimitive } from "radix-ui";
import { useEffect, useRef, useState } from "react";
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
import { since } from "../ui/time";
import type { SubscriptionValues } from "./Subscriptions";

type EventListItem = components["schemas"]["EventListItemDto"];
type EditableFieldMapping = FieldMapping & { id: string };

export const mappingEnvelope = (expression: string) =>
  expression.trim() === "" ? null : { engine: "jsonata", version: "1", expression };

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
  const [manualError, setManualError] = useState<string>();
  const [output, setOutput] = useState<unknown>();
  const [evaluationError, setEvaluationError] = useState<string>();
  const previewAttempt = useRef(0);

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

  const resetPreview = () => {
    previewAttempt.current += 1;
    setOutput(undefined);
    setEvaluationError(undefined);
    setManualError(undefined);
  };

  const invalidateReview = () => {
    resetPreview();
    onReviewInvalidated();
  };

  // Stepping to another sample re-runs a preview the Operator already asked for, once that sample's
  // payload has loaded: the point of stepping is to see the same mapping against a different shape.
  const rerunOnLoad = useRef(false);
  const step = (next: EventListItem | undefined) => {
    if (!next) return;
    rerunOnLoad.current = output !== undefined || evaluationError !== undefined;
    setEventId(next.event_id);
    invalidateReview();
  };

  const updateFieldMappings = (next: EditableFieldMapping[]) => {
    setFieldMappings(next);
    form.setValue("mapping", expressionFromFieldMappings(next), { shouldDirty: true, shouldValidate: true });
    form.clearErrors("mapping");
    invalidateReview();
  };

  const preview = useMutation({
    mutationFn: ({
      payload,
      eventType,
      acceptedAt,
      topicName,
    }: {
      payload: unknown;
      eventType: string;
      acceptedAt: string;
      topicName: string;
      attempt: number;
    }) => {
      const transform = mappingEnvelope(expression);
      if (!transform) throw new Error("No mapping expression was provided.");
      return call(() =>
        api.POST("/admin/transform/preview", {
          body: {
            transform,
            sample_input: payload,
            sample_context: { event_type: eventType, topic_name: topicName, accepted_at: acceptedAt },
          },
        }),
      );
    },
    onSuccess: (result, { attempt }) => {
      if (attempt === previewAttempt.current) setOutput(result?.output);
    },
    onError: (failure, { attempt }) => {
      if (attempt !== previewAttempt.current) return;
      const problem = asProblem(failure);
      const syntaxError = fieldError(problem, "transform");
      if (syntaxError) form.setError("mapping", { type: "server", message: syntaxError });
      else setEvaluationError(formError(problem));
    },
  });

  const runPreview = () => {
    form.clearErrors("mapping");
    resetPreview();

    const topicName = topic.data?.key;
    if (!topicName) return;

    let payload: unknown;
    let eventType: string;
    let acceptedAt: string;
    if (pasting) {
      const parsed = parseJson(manualPayload);
      if (parsed.error) {
        setManualError(parsed.error);
        return;
      }
      if (!sampleEventType.trim() || Number.isNaN(new Date(manualAcceptedAt).getTime())) {
        setManualError("Enter an Event type and acceptance time.");
        return;
      }
      payload = parsed.value;
      eventType = sampleEventType.trim();
      acceptedAt = new Date(manualAcceptedAt).toISOString();
    } else {
      if (!selectedEvent || event.data?.payload === undefined || event.data.payload === null) return;
      payload = event.data.payload;
      eventType = selectedEvent.event_type;
      acceptedAt = selectedEvent.accepted_at;
    }

    if (expression.trim() === "") {
      setOutput(payload);
      return;
    }
    preview.mutate({ payload, eventType, acceptedAt, topicName, attempt: previewAttempt.current });
  };

  // biome-ignore lint/correctness/useExhaustiveDependencies: runs once per loaded sample, not per render of runPreview.
  useEffect(() => {
    if (!rerunOnLoad.current || event.data?.payload === undefined || event.data.payload === null) return;
    rerunOnLoad.current = false;
    runPreview();
  }, [event.data]);

  const input = pasting ? parseJson(manualPayload).value : event.data?.payload;
  const contextEventType = pasting ? sampleEventType : selectedEvent?.event_type;
  const contextAcceptedAt = pasting ? manualAcceptedAt : selectedEvent?.accepted_at;
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
        if (next) resetPreview();
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

          <div className="flex flex-wrap items-end gap-2">
            {!pasting ? (
              <fieldset className="m-0 flex min-w-64 flex-1 flex-col gap-1 border-0 p-0 text-sm">
                <legend className="mb-1 p-0 font-medium">Preview against</legend>
                <div className="flex items-center gap-2">
                  <p className="m-0 min-w-0 flex-1 rounded-md border px-3 py-2 break-words">
                    {selectedEvent ? (
                      <>
                        <code>{selectedEvent.event_type}</code>
                        {selectedEvent.source_event_id ? <> · {selectedEvent.source_event_id}</> : null} ·{" "}
                        <span title={new Date(selectedEvent.accepted_at).toLocaleString()}>
                          {since(selectedEvent.accepted_at)}
                        </span>
                      </>
                    ) : (
                      "Loading Events…"
                    )}
                  </p>
                  <Button
                    type="button"
                    variant="outline"
                    size="icon"
                    aria-label="Newer Event"
                    disabled={sampleIndex === 0}
                    onClick={() => step(samples[sampleIndex - 1])}
                  >
                    <ChevronLeft aria-hidden="true" />
                  </Button>
                  <span aria-live="polite" className="shrink-0 text-ink-secondary tabular-nums">
                    {samples.length > 0 ? `${sampleIndex + 1} of ${samples.length}` : ""}
                  </span>
                  <Button
                    type="button"
                    variant="outline"
                    size="icon"
                    aria-label="Older Event"
                    disabled={sampleIndex >= samples.length - 1}
                    onClick={() => step(samples[sampleIndex + 1])}
                  >
                    <ChevronRight aria-hidden="true" />
                  </Button>
                </div>
              </fieldset>
            ) : null}
            {samples.length > 0 ? (
              <Button
                type="button"
                variant="outline"
                onClick={() => {
                  setManual((value) => !value);
                  invalidateReview();
                }}
              >
                {manual ? "Use accepted Events" : "Paste sample JSON"}
              </Button>
            ) : null}
          </div>
          {noSamples ? (
            <p className="m-0 text-sm text-ink-secondary">
              {subscriptionEventTypes.length === 0
                ? "Select this Subscription's Event types to preview against its accepted Events. Until then, paste a sample."
                : "No Events of the selected types on this Topic yet. Send a test Event from the Source's setup guide, or paste a sample."}
            </p>
          ) : null}

          {pasting ? (
            <div className="grid gap-3 md:grid-cols-2">
              <label htmlFor="manual-payload" className="flex flex-col gap-1 text-sm md:col-span-2">
                <span className="font-medium">Sample input (JSON)</span>
                <CodeTextarea
                  id="manual-payload"
                  value={manualPayload}
                  onChange={(event) => {
                    setManualPayload(event.target.value);
                    invalidateReview();
                  }}
                />
              </label>
              <label className="flex flex-col gap-1 text-sm">
                <span className="font-medium">Event type</span>
                <input
                  value={sampleEventType}
                  onChange={(event) => {
                    setManualEventType(event.target.value);
                    invalidateReview();
                  }}
                  className="h-9 rounded-md border border-input bg-transparent px-3 text-sm outline-none focus-visible:border-ring focus-visible:ring-[3px] focus-visible:ring-ring/50"
                />
              </label>
              <label className="flex flex-col gap-1 text-sm">
                <span className="font-medium">Accepted at</span>
                <input
                  type="datetime-local"
                  value={manualAcceptedAt}
                  onChange={(event) => {
                    setManualAcceptedAt(event.target.value);
                    invalidateReview();
                  }}
                  className="h-9 rounded-md border border-input bg-transparent px-3 text-sm outline-none focus-visible:border-ring focus-visible:ring-[3px] focus-visible:ring-ring/50"
                />
              </label>
            </div>
          ) : null}

          <div className="flex flex-wrap gap-2 text-xs text-ink-secondary">
            <span className="rounded-full border px-2 py-1">event_type: {contextEventType ?? "—"}</span>
            <span className="rounded-full border px-2 py-1">topic_name: {topic.data?.key ?? "—"}</span>
            <span className="rounded-full border px-2 py-1">accepted_at: {contextAcceptedAt ?? "—"}</span>
          </div>

          <div className="grid gap-4 lg:grid-cols-[minmax(0,0.75fr)_minmax(0,1.5fr)_minmax(0,0.75fr)]">
            <section className="flex min-w-0 flex-col gap-2 rounded-lg border p-3">
              <h3 className="m-0 text-sm">{pasting ? "Sample input" : "Accepted Event"}</h3>
              {input !== undefined ? (
                <CodeBlock value={input} />
              ) : (
                <p className="m-0 text-sm text-ink-secondary">Choose a sample with an available payload.</p>
              )}
            </section>
            <section className="flex min-w-0 flex-col gap-2 rounded-lg border p-3">
              <div className="flex items-center justify-between gap-2">
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
                    aria-invalid={Boolean(form.formState.errors.mapping)}
                    aria-describedby={form.formState.errors.mapping ? "playground-mapping-error" : undefined}
                    value={expression}
                    onChange={(event) => {
                      form.setValue("mapping", event.target.value, { shouldDirty: true, shouldValidate: true });
                      form.clearErrors("mapping");
                      invalidateReview();
                    }}
                    className="min-h-44"
                  />
                  <p className="m-0 text-xs text-ink-secondary">
                    {parsedExpression
                      ? "This expression can return to field mapping without losing information."
                      : "This expression uses advanced JSONata and cannot be represented safely as field rows."}
                  </p>
                </>
              )}
              {form.formState.errors.mapping?.message ? (
                <p id="playground-mapping-error" role="alert" className="m-0 text-sm text-destructive">
                  {form.formState.errors.mapping.message}
                </p>
              ) : null}
            </section>
            <section className="flex min-w-0 flex-col gap-2 rounded-lg border p-3">
              <h3 className="m-0 text-sm">Preview body</h3>
              {evaluationError ? (
                <div className="flex flex-col gap-2">
                  <p role="alert" className="m-0 text-sm text-destructive">
                    {evaluationError}
                  </p>
                  <p className="m-0 text-sm text-ink-secondary">
                    This Event would fail delivery. Equivalent Events would retry and may dead-letter after save.
                  </p>
                </div>
              ) : output !== undefined ? (
                <CodeBlock value={output} />
              ) : (
                <p className="m-0 text-sm text-ink-secondary">
                  Preview this expression to see what would be delivered.
                </p>
              )}
            </section>
          </div>

          <FormError
            message={
              manualError ??
              formError(asProblem(topic.error)) ??
              formError(asProblem(eventReads.find((read) => read.isError)?.error)) ??
              formError(asProblem(event.error))
            }
          />
          <div className="mt-auto flex flex-wrap items-center justify-between gap-2 border-t pt-4">
            <DialogPrimitive.Close asChild>
              <Button type="button" variant="outline">
                Back to Subscription
              </Button>
            </DialogPrimitive.Close>
            <div className="flex flex-wrap gap-2">
              <Button
                type="button"
                variant="outline"
                disabled={preview.isPending || unavailable || topic.isPending}
                onClick={runPreview}
              >
                Preview mapping
              </Button>
              <DialogPrimitive.Close asChild>
                <Button
                  type="button"
                  disabled={expression === originalExpression || output === undefined || Boolean(evaluationError)}
                  onClick={() => onConfirmed(expression)}
                >
                  Confirm mapping change
                </Button>
              </DialogPrimitive.Close>
            </div>
          </div>
        </DialogPrimitive.Content>
      </DialogPrimitive.Portal>
    </DialogPrimitive.Root>
  );
}

/// One form for both create and update: the Admin API takes the same body for each, so splitting it
/// into two near-identical forms would only invite them to drift apart.
