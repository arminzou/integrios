import { zodResolver } from "@hookform/resolvers/zod";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { cn } from "cn";
import { type ReactNode, useEffect, useState } from "react";
import { useForm, useWatch } from "react-hook-form";
import { z } from "zod";
import { Button } from "@/components/ui/button";
import { FormField } from "@/components/ui/form";
import { Label } from "@/components/ui/label";
import { Textarea } from "@/components/ui/textarea";
import { api } from "../api/client";
import { formError } from "../api/problem";
import { asProblem, call } from "../api/query";
import type { components } from "../api/schema";
import { snakeIdentifier } from "../identifiers";
import { Callout, ConfirmAction, Disclosure, FormError } from "../ui/controls";
import { BodyPanel } from "../ui/copy";
import { Form, TextAreaField, TextField } from "../ui/fields";
import { applyProblem } from "../ui/formProblem";
import { parseJson } from "../ui/json";

type Connector = components["schemas"]["ConnectorDto"];

const applyFields = ["key"] as const;

const authoringSchema = z
  .object({
    name: z.string().trim().min(1, "Enter a name."),
    key: z.string().trim().min(1, "Enter a key."),
    description: z.string(),
    contract_version: z.string().regex(/^[1-9]\d*$/, "Enter a version of 1 or more."),
    receive: z.boolean(),
    deliver: z.boolean(),
  })
  .superRefine((values, ctx) => {
    if (!values.receive && !values.deliver)
      ctx.addIssue({
        code: z.ZodIssueCode.custom,
        path: ["receive"],
        message: "Choose at least one. A Connector that permits neither cannot be authored against.",
      });
  });

type AuthoringValues = z.infer<typeof authoringSchema>;

const blank: AuthoringValues = {
  name: "",
  key: "",
  description: "",
  contract_version: "1",
  receive: true,
  deliver: false,
};

/// Everything an imported manifest carries that this form has no control for. It is held beside the
/// draft and written back out on apply, so importing a manifest the guided subset does not fully
/// cover never silently drops the parts it does not understand.
type Advanced = {
  rest: Record<string, unknown>;
  presentation: Record<string, unknown>;
};

const noAdvanced: Advanced = {
  rest: {},
  presentation: {},
};

const isObject = (value: unknown): value is Record<string, unknown> =>
  value !== null && typeof value === "object" && !Array.isArray(value);

/// What the capability pair means to the manifest, and the word the installed Connector is read back
/// under.
const direction = (values: Pick<AuthoringValues, "receive" | "deliver">) =>
  values.receive && values.deliver ? "both" : values.receive ? "source" : "destination";

export function buildManifest(values: AuthoringValues, advanced: Advanced): Record<string, unknown> {
  const manifest: Record<string, unknown> = {
    ...advanced.rest,
    manifest_schema_version: 1,
    key: values.key.trim(),
    contract_version: Number(values.contract_version) || 1,
    direction: direction(values),
    source_verification: advanced.rest.source_verification ?? { allow_unverified: true, schemes: [] },
    destination_authentication: advanced.rest.destination_authentication ?? {
      allow_unauthenticated: true,
      schemes: [],
    },
    presentation: {
      event_types: [],
      authoring_presets: [],
      ...advanced.presentation,
      name: values.name.trim(),
      description: values.description.trim() === "" ? null : values.description.trim(),
    },
  };

  // A capability carries its configuration schema both ways. The document is built over whatever an
  // imported manifest held, and the server refuses a schema the direction does not permit, so
  // turning a capability off has to take its schema with it rather than leaving the imported one
  // standing under a direction that no longer allows it.
  if (values.receive)
    manifest.source_configuration_schema ??= { type: "object", properties: {}, additionalProperties: true };
  else delete manifest.source_configuration_schema;

  if (values.deliver)
    manifest.destination_configuration_schema ??= {
      type: "object",
      properties: { base_uri: { type: "string", format: "uri" } },
      required: ["base_uri"],
      additionalProperties: false,
    };
  else delete manifest.destination_configuration_schema;

  return manifest;
}

/// Reads an applied manifest back into the guided draft. Whatever the form has no control for is
/// carried in `advanced` and named in `kept`, because an Operator who imports a manifest is owed a
/// statement of what this form is not showing them rather than a quiet rewrite.
export function fromManifest(document: Record<string, unknown>): {
  values: AuthoringValues;
  advanced: Advanced;
  kept: string[];
} {
  const rest = { ...document };
  for (const field of [
    "manifest_schema_version",
    "key",
    "contract_version",
    "direction",
    "presentation",
    "source_contracts",
    "http_success",
  ])
    delete rest[field];

  const presentation = isObject(document.presentation) ? { ...document.presentation } : {};
  const name = typeof presentation.name === "string" ? presentation.name : "";
  const description = typeof presentation.description === "string" ? presentation.description : "";
  delete presentation.name;
  delete presentation.description;

  const applied = document.direction;
  const receive = applied === "source" || applied === "both";
  const deliver = applied === "destination" || applied === "both";

  const advanced: Advanced = {
    rest,
    presentation,
  };

  const kept = [...Object.keys(rest), ...Object.keys(presentation).map((field) => `presentation.${field}`)];

  return {
    advanced,
    kept,
    values: {
      name,
      key: typeof document.key === "string" ? document.key : "",
      description,
      contract_version: String(document.contract_version ?? 1),
      receive,
      deliver,
    },
  };
}

function Section({
  title,
  hint,
  className,
  children,
}: {
  title: string;
  hint: string;
  className?: string;
  children: ReactNode;
}) {
  return (
    <section className={cn("flex flex-col gap-3 border-t pt-4 first:border-t-0 first:pt-0", className)}>
      <div>
        <h3 className="m-0 text-sm font-semibold">{title}</h3>
        <p className="m-0 mt-0.5 text-xs text-ink-secondary">{hint}</p>
      </div>
      {children}
    </section>
  );
}

/// A checkbox states one capability or one allowed scheme, so it is the platform control rather than
/// a widget: the label wraps the input, which is what gives it its name and its target area without
/// an id to keep in step.
function CheckRow({
  checked,
  onChange,
  label,
  hint,
}: {
  checked: boolean;
  onChange: (checked: boolean) => void;
  label: string;
  hint?: string;
}) {
  return (
    <label className="flex items-start gap-2.5 py-1 text-sm">
      <input
        type="checkbox"
        checked={checked}
        onChange={(event) => onChange(event.target.checked)}
        className="mt-0.5 size-4 shrink-0"
      />
      <span className="min-w-0">
        <span className="font-medium">{label}</span>
        {hint ? <span className="mt-0.5 block text-xs text-ink-secondary">{hint}</span> : null}
      </span>
    </label>
  );
}

/// The guided New Connector draft. It produces the same manifest the Admin API already owns and
/// applies it through the same immutable version route; nothing about the guided form is persisted.
///
/// `from` starts the draft as a copy of an applied version. A Connector version is immutable, so the
/// copy is a new version the Operator chooses and reviews — never an edit of the version it came
/// from, and never of one already behind it.
export function ConnectorAuthoring({
  from,
  onApplied,
}: {
  from?: Connector;
  onApplied?: (applied: Connector | undefined) => void;
}) {
  const queryClient = useQueryClient();
  const copied = from ? fromManifest((from.manifest ?? {}) as Record<string, unknown>) : undefined;
  const [advanced, setAdvanced] = useState<Advanced>(copied?.advanced ?? noAdvanced);
  const [kept, setKept] = useState<string[]>(copied?.kept ?? []);
  const [imported, setImported] = useState("");
  const form = useForm<AuthoringValues>({
    resolver: zodResolver(authoringSchema),
    defaultValues:
      from && copied
        ? { ...copied.values, key: from.key, contract_version: String(Number(from.contract_version) + 1) }
        : blank,
  });
  const values = form.watch();

  // The key follows the name until the Operator writes one themselves, and never touches a key that
  // arrived with a manifest: a copied version's key is its identity, and an imported one is the
  // author's own choice.
  //
  // Authorship is recorded from the Operator's own keystroke rather than read off the form's dirty
  // state: that state is recomputed against the defaults whenever a field returns to one, which
  // marked a key this form had written as authored — and froze it — the moment the name was cleared.
  const [keyAuthored, setKeyAuthored] = useState(false);
  const authoredName = useWatch({ control: form.control, name: "name" });
  useEffect(() => {
    if (from || keyAuthored) return;
    form.setValue("key", snakeIdentifier(authoredName));
  }, [from, keyAuthored, authoredName, form]);
  const manifest = buildManifest(values, advanced);

  const apply = useMutation({
    mutationFn: (document: Record<string, unknown>) =>
      call(() =>
        api.PUT("/admin/connectors/{key}/versions/{contractVersion}", {
          params: {
            path: { key: String(document.key), contractVersion: Number(document.contract_version) },
          },
          body: document,
        }),
      ),
    onSuccess: (applied) => {
      void queryClient.invalidateQueries({ queryKey: ["connectors"] });
      void queryClient.invalidateQueries({ queryKey: ["connector-options"] });
      onApplied?.(applied);
    },
  });

  const submit = form.handleSubmit((current) => {
    // Applying to a version that already exists updates it in place, which is what an immutable
    // Connector version is not. Versions only move forward from the one being copied.
    if (from && Number(current.contract_version) <= Number(from.contract_version)) {
      form.setError("contract_version", {
        message: `Version ${from.contract_version} is applied. Choose a later version.`,
      });
      return;
    }
    apply.mutate(buildManifest(current, advanced), {
      onError: (failure) => applyProblem(form, failure, applyFields),
    });
  });

  const importParse = imported.trim() === "" ? undefined : parseJson(imported);
  const importError =
    importParse?.error ??
    (importParse && !isObject(importParse.value) ? "The manifest must be a JSON object." : undefined);

  const replaceDraft = () => {
    if (!importParse || !isObject(importParse.value)) return;
    const read = fromManifest(importParse.value);
    form.reset(read.values);
    setAdvanced(read.advanced);
    setKept(read.kept);
    setImported("");
    // An imported key is the manifest author's own, so the name stops deciding it.
    setKeyAuthored(true);
  };

  return (
    <Form {...form}>
      <form className="flex flex-col gap-5" noValidate onSubmit={submit}>
        <FormError message={formError(asProblem(apply.error), applyFields)} />

        <Section title="Basics" hint="Name the reusable external-system contract.">
          <TextField control={form.control} name="name" label="Name" required />
          {/* A Connector's key is its identity: changing it here would author a different
              Connector rather than a new version of this one. */}
          <TextField
            control={form.control}
            name="key"
            label="Key"
            hint={from ? undefined : "Names this Connector in manifests and Source or Destination authoring."}
            onChange={() => setKeyAuthored(true)}
            className="font-mono text-sm"
            readOnly={from !== undefined}
            required
          />
          <TextAreaField control={form.control} name="description" label="Description" className="min-h-20" />
          <TextField
            control={form.control}
            name="contract_version"
            label="Contract version"
            hint={
              from
                ? `Version ${from.contract_version} stays as it is. This applies a new one.`
                : "A Connector version is immutable. Applying to a new version installs it."
            }
            type="number"
            min={1}
            step={1}
            required
          />
        </Section>

        <Section className="relative" title="Capabilities" hint="What Tenants may author from this Connector.">
          <FormField
            control={form.control}
            name="receive"
            render={({ field }) => (
              <CheckRow
                checked={field.value}
                onChange={field.onChange}
                label="Permit Sources"
                hint="A Tenant can build a Source on it, to receive Events from the external system."
              />
            )}
          />
          <FormField
            control={form.control}
            name="deliver"
            render={({ field }) => (
              <CheckRow
                checked={field.value}
                onChange={field.onChange}
                label="Permit Destinations"
                hint="A Tenant can build a Destination on it, to deliver Events to the external system over HTTP."
              />
            )}
          />
          {/* The pair is what the manifest's direction is made of, and direction is the word the
              Connector is read back under, so the choice is named in the word it becomes. */}
          {values.receive || values.deliver ? (
            <p className="m-0 text-xs text-ink-secondary">
              Applies as direction <span className="font-mono">{direction(values)}</span>. Pick both if one external
              system does both.
            </p>
          ) : null}
          <Callout message={form.formState.errors.receive?.message} />
        </Section>

        {values.receive ? (
          <Section
            title="Sources"
            hint="Source-specific verification, input requirements, mapping, and identity are chosen when authoring a Source."
          >
            <p className="m-0 text-xs text-ink-secondary">
              The Connector only declares its reusable capability. It stores no callback, request shape, secret
              reference, or Event mapping.
            </p>
          </Section>
        ) : null}

        {values.deliver ? (
          <Section
            title="Destinations"
            hint="Destination and Subscription authoring own the concrete outbound contract."
          >
            <p className="m-0 text-xs text-ink-secondary">
              A Destination supplies reachability and authentication. A Subscription supplies its HTTP operation,
              non-authentication headers, mapping, and optional success rule.
            </p>
          </Section>
        ) : null}

        <Section title="Advanced" hint="The manifest this draft applies, and how to start from one you already have.">
          {kept.length > 0 ? (
            <p className="m-0 text-xs text-ink-secondary">
              Kept from the imported manifest and applied unchanged: {kept.join(", ")}.
            </p>
          ) : null}
          {/* One document, read out and written in, so one disclosure. Two collapsed boxes both
              labelled JSON left the direction of each to be carried entirely by its label. */}
          <Disclosure label="Manifest JSON">
            <div className="flex flex-col gap-4">
              <BodyPanel label="Manifest" value={manifest} unbounded />
              <div className="flex flex-col gap-3 border-t pt-4">
                <div className="flex flex-col gap-1">
                  <Label htmlFor="manifest-import">Replace with pasted JSON</Label>
                  <p className="m-0 text-xs text-ink-secondary">
                    A manifest from another deployment, or one written by hand.
                  </p>
                </div>
                <Textarea
                  id="manifest-import"
                  value={imported}
                  spellCheck={false}
                  onChange={(event) => setImported(event.target.value)}
                  className="min-h-40 font-mono text-sm"
                />
                {importError ? (
                  <p role="alert" className="m-0 text-sm text-destructive">
                    {importError}
                  </p>
                ) : null}
                {imported.trim() === "" ? (
                  <p className="m-0 text-xs text-ink-secondary">Paste a manifest to replace this draft.</p>
                ) : null}
                <ConfirmAction
                  label="Replace draft"
                  variant="outline"
                  question="Replace this draft with the imported manifest?"
                  consequence="Everything authored in this form is discarded."
                  disabled={!importParse || importError !== undefined}
                  onConfirm={replaceDraft}
                />
              </div>
            </div>
          </Disclosure>
        </Section>

        <Button type="submit" className="self-start" disabled={apply.isPending}>
          {from ? "Create version" : "Create Connector"}
        </Button>
      </form>
    </Form>
  );
}
