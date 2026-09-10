import { zodResolver } from "@hookform/resolvers/zod";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { type ReactNode, useState } from "react";
import { useForm } from "react-hook-form";
import { z } from "zod";
import { Button } from "@/components/ui/button";
import { FormField } from "@/components/ui/form";
import { Textarea } from "@/components/ui/textarea";
import { api } from "../api/client";
import { formError } from "../api/problem";
import { asProblem, call } from "../api/query";
import type { components } from "../api/schema";
import { ConfirmAction, Disclosure, FormError } from "../ui/controls";
import { Form, TextAreaField, TextField } from "../ui/fields";
import { applyProblem } from "../ui/formProblem";
import { formatJson, parseJson } from "../ui/json";
import { EventBuilder } from "./EventBuilder";

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
    input_mode: z.enum(["normalized", "native"]),
    contract_key: z.string(),
    mapping: z.string(),
    allow_unverified: z.boolean(),
    hmac: z.boolean(),
    allow_unauthenticated: z.boolean(),
    bearer: z.boolean(),
    api_key: z.boolean(),
  })
  .superRefine((values, ctx) => {
    if (!values.receive && !values.deliver)
      ctx.addIssue({ code: z.ZodIssueCode.custom, path: ["receive"], message: "Choose at least one capability." });
    if (!values.receive || values.input_mode !== "native") return;
    if (values.contract_key.trim() === "")
      ctx.addIssue({ code: z.ZodIssueCode.custom, path: ["contract_key"], message: "Enter a Source contract key." });
    if (values.mapping.trim() === "")
      ctx.addIssue({ code: z.ZodIssueCode.custom, path: ["mapping"], message: "Enter a mapping expression." });
  });

type AuthoringValues = z.infer<typeof authoringSchema>;

const blank: AuthoringValues = {
  name: "",
  key: "",
  description: "",
  contract_version: "1",
  receive: true,
  deliver: false,
  input_mode: "normalized",
  contract_key: "verified_webhook",
  mapping: "",
  allow_unverified: false,
  hmac: false,
  allow_unauthenticated: true,
  bearer: false,
  api_key: false,
};

/// Everything an imported manifest carries that this form has no control for. It is held beside the
/// draft and written back out on apply, so importing a manifest the guided subset does not fully
/// cover never silently drops the parts it does not understand.
type Advanced = {
  rest: Record<string, unknown>;
  presentation: Record<string, unknown>;
  verification_schemes: unknown[];
  authentication_schemes: unknown[];
  extra_source_contracts: unknown[];
  contract_config?: unknown;
  contract_schema?: unknown;
};

const noAdvanced: Advanced = {
  rest: {},
  presentation: {},
  verification_schemes: [],
  authentication_schemes: [],
  extra_source_contracts: [],
};

const hmacScheme = { scheme: "hmac_sha256", required_config: [], required_secret_refs: ["secret"] };
const bearerScheme = { scheme: "bearer_token", required_config: [], required_secret_refs: ["token"] };
const apiKeyScheme = { scheme: "api_key_header", required_config: ["header_name"], required_secret_refs: ["api_key"] };

const isObject = (value: unknown): value is Record<string, unknown> =>
  value !== null && typeof value === "object" && !Array.isArray(value);

const schemeName = (value: unknown) => (isObject(value) ? value.scheme : undefined);

export function buildManifest(values: AuthoringValues, advanced: Advanced): Record<string, unknown> {
  const native = values.receive && values.input_mode === "native";
  const manifest: Record<string, unknown> = {
    ...advanced.rest,
    manifest_schema_version: 1,
    key: values.key.trim(),
    contract_version: Number(values.contract_version) || 1,
    direction: values.receive && values.deliver ? "both" : values.receive ? "source" : "destination",
    source_verification: {
      allow_unverified: values.receive ? values.allow_unverified : true,
      schemes: [...(values.receive && values.hmac ? [hmacScheme] : []), ...advanced.verification_schemes],
    },
    destination_authentication: {
      allow_unauthenticated: values.deliver ? values.allow_unauthenticated : true,
      schemes: [
        ...(values.deliver && values.bearer ? [bearerScheme] : []),
        ...(values.deliver && values.api_key ? [apiKeyScheme] : []),
        ...advanced.authentication_schemes,
      ],
    },
    source_contracts: values.receive
      ? [
          {
            key: native ? values.contract_key.trim() : "event_json",
            contract_version: 1,
            config: advanced.contract_config ?? {},
            ...(native && advanced.contract_schema !== undefined ? { schema: advanced.contract_schema } : {}),
            ...(native ? { mapping: { engine: "jsonata", version: "1", expression: values.mapping.trim() } } : {}),
          },
          ...advanced.extra_source_contracts,
        ]
      : [],
    presentation: {
      event_types: [],
      authoring_presets: [],
      ...advanced.presentation,
      name: values.name.trim(),
      description: values.description.trim() === "" ? null : values.description.trim(),
    },
  };

  if (values.receive && !("source_configuration_schema" in manifest))
    manifest.source_configuration_schema = { type: "object", properties: {}, additionalProperties: true };
  if (values.deliver && !("destination_configuration_schema" in manifest))
    manifest.destination_configuration_schema = {
      type: "object",
      properties: { base_uri: { type: "string", format: "uri" } },
      required: ["base_uri"],
      additionalProperties: false,
    };
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
    "source_verification",
    "destination_authentication",
    "source_contracts",
    "presentation",
  ])
    delete rest[field];

  const verification = isObject(document.source_verification) ? document.source_verification : {};
  const authentication = isObject(document.destination_authentication) ? document.destination_authentication : {};
  const verificationSchemes = Array.isArray(verification.schemes) ? verification.schemes : [];
  const authenticationSchemes = Array.isArray(authentication.schemes) ? authentication.schemes : [];
  const contracts = Array.isArray(document.source_contracts) ? document.source_contracts : [];
  const contract = isObject(contracts[0]) ? contracts[0] : undefined;
  const presentation = isObject(document.presentation) ? { ...document.presentation } : {};
  const name = typeof presentation.name === "string" ? presentation.name : "";
  const description = typeof presentation.description === "string" ? presentation.description : "";
  delete presentation.name;
  delete presentation.description;

  const direction = document.direction;
  const receive = direction === "source" || direction === "both" || contracts.length > 0;
  const deliver = direction === "destination" || direction === "both";
  const mapping = isObject(contract?.mapping) ? contract.mapping : undefined;
  const expression = typeof mapping?.expression === "string" ? mapping.expression : "";

  const advanced: Advanced = {
    rest,
    presentation,
    verification_schemes: verificationSchemes.filter((scheme) => schemeName(scheme) !== "hmac_sha256"),
    authentication_schemes: authenticationSchemes.filter(
      (scheme) => schemeName(scheme) !== "bearer_token" && schemeName(scheme) !== "api_key_header",
    ),
    extra_source_contracts: contracts.slice(1),
    contract_config: contract?.config,
    contract_schema: contract?.schema,
  };

  const kept = [
    ...Object.keys(rest),
    ...Object.keys(presentation).map((field) => `presentation.${field}`),
    ...(advanced.verification_schemes.length > 0 ? ["source_verification.schemes"] : []),
    ...(advanced.authentication_schemes.length > 0 ? ["destination_authentication.schemes"] : []),
    ...(advanced.extra_source_contracts.length > 0 ? ["source_contracts"] : []),
    ...(contract?.schema === undefined ? [] : ["source_contracts[0].schema"]),
  ];

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
      input_mode: expression === "" ? "normalized" : "native",
      contract_key: typeof contract?.key === "string" ? contract.key : blank.contract_key,
      mapping: expression,
      allow_unverified: verification.allow_unverified !== false,
      hmac: verificationSchemes.some((scheme) => schemeName(scheme) === "hmac_sha256"),
      allow_unauthenticated: authentication.allow_unauthenticated !== false,
      bearer: authenticationSchemes.some((scheme) => schemeName(scheme) === "bearer_token"),
      api_key: authenticationSchemes.some((scheme) => schemeName(scheme) === "api_key_header"),
    },
  };
}

function Section({ title, hint, children }: { title: string; hint: string; children: ReactNode }) {
  return (
    <section className="flex flex-col gap-3 border-t pt-4 first:border-t-0 first:pt-0">
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
  /// An import replaces the mapping under the Builder, whose sample and guided choices belong to the
  /// draft that is being discarded; remounting it is what discards them with it.
  const [generation, setGeneration] = useState(0);
  const form = useForm<AuthoringValues>({
    resolver: zodResolver(authoringSchema),
    defaultValues:
      from && copied
        ? { ...copied.values, key: from.key, contract_version: String(Number(from.contract_version) + 1) }
        : blank,
  });
  const values = form.watch();
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
    setGeneration(generation + 1);
  };

  return (
    <Form {...form}>
      <form className="flex flex-col gap-5" onSubmit={submit}>
        <FormError message={formError(asProblem(apply.error), applyFields)} />

        <Section title="Basics" hint="Name the reusable external-system contract.">
          <TextField control={form.control} name="name" label="Name" required />
          {/* A Connector's key is its identity: changing it here would author a different
              Connector rather than a new version of this one. */}
          <TextField
            control={form.control}
            name="key"
            label="Key"
            hint={from ? undefined : "The Connector's stable identifier, such as github."}
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

        <Section title="Capabilities" hint="Choose what Connections built from this Connector may do.">
          <FormField
            control={form.control}
            name="receive"
            render={({ field }) => (
              <CheckRow
                checked={field.value}
                onChange={field.onChange}
                label="Receive Events"
                hint="Connections may be used by Sources."
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
                label="Deliver Events over HTTP"
                hint="Connections may be used by Subscriptions."
              />
            )}
          />
          {form.formState.errors.receive?.message ? (
            <p role="alert" className="m-0 text-sm text-destructive">
              {form.formState.errors.receive.message}
            </p>
          ) : null}
        </Section>

        {values.receive ? (
          <Section
            title="Receive Events"
            hint="Define what an external Publisher sends before Integrios creates an Event."
          >
            <FormField
              control={form.control}
              name="input_mode"
              render={({ field }) => (
                <fieldset className="m-0 flex flex-col gap-1 border-0 p-0">
                  <legend className="mb-1 text-sm font-medium">Incoming request shape</legend>
                  {[
                    {
                      value: "normalized" as const,
                      label: "Integrios Event JSON",
                      hint: "The Publisher already sends event_type and payload.",
                    },
                    {
                      value: "native" as const,
                      label: "Provider-native JSON",
                      hint: "Validate and map the provider's own request into an Event.",
                    },
                  ].map((choice) => (
                    <label key={choice.value} className="flex items-start gap-2.5 py-1 text-sm">
                      <input
                        type="radio"
                        name={field.name}
                        value={choice.value}
                        checked={field.value === choice.value}
                        onChange={() => field.onChange(choice.value)}
                        className="mt-0.5 size-4 shrink-0"
                      />
                      <span className="min-w-0">
                        <span className="font-medium">{choice.label}</span>
                        <span className="mt-0.5 block text-xs text-ink-secondary">{choice.hint}</span>
                      </span>
                    </label>
                  ))}
                </fieldset>
              )}
            />

            {values.input_mode === "native" ? (
              <>
                <TextField
                  control={form.control}
                  name="contract_key"
                  label="Source contract key"
                  hint="Chosen later, when a Source is created from this Connector."
                  className="font-mono text-sm"
                  required
                />
                <div className="flex flex-col gap-2">
                  <h4 className="m-0 text-sm font-medium">Integrios Event</h4>
                  <p className="m-0 text-xs text-ink-secondary">
                    How a request this contract accepts becomes an Event. event_type and payload are required.
                  </p>
                  <pre className="m-0 max-h-40 overflow-auto rounded-md border bg-surface p-2 text-xs">
                    {values.mapping.trim() === "" ? "No mapping configured yet." : values.mapping}
                  </pre>
                  {advanced.contract_schema === undefined ? null : (
                    <p className="m-0 text-xs text-ink-secondary">
                      Input requirements are configured on this contract.
                    </p>
                  )}
                  {form.formState.errors.mapping?.message ? (
                    <p role="alert" className="m-0 text-sm text-destructive">
                      {form.formState.errors.mapping.message}
                    </p>
                  ) : null}
                  <EventBuilder
                    key={generation}
                    contractKey={values.contract_key}
                    draft={{
                      expression: values.mapping,
                      schema: isObject(advanced.contract_schema) ? advanced.contract_schema : undefined,
                    }}
                    onUse={(built) => {
                      form.setValue("mapping", built.expression, { shouldValidate: true });
                      setAdvanced({ ...advanced, contract_schema: built.schema });
                    }}
                  />
                </div>
              </>
            ) : (
              <p className="m-0 text-xs text-ink-secondary">
                The Source contract is generated. event_type and payload are required; source_event_id and metadata are
                optional.
              </p>
            )}

            <fieldset className="m-0 flex flex-col gap-1 border-0 p-0">
              <legend className="mb-1 text-sm font-medium">Webhook verification</legend>
              <FormField
                control={form.control}
                name="allow_unverified"
                render={({ field }) => (
                  <CheckRow checked={field.value} onChange={field.onChange} label="Allow unverified requests" />
                )}
              />
              <FormField
                control={form.control}
                name="hmac"
                render={({ field }) => (
                  <CheckRow
                    checked={field.value}
                    onChange={field.onChange}
                    label="HMAC SHA-256"
                    hint="Connections provide the shared-secret reference."
                  />
                )}
              />
            </fieldset>
          </Section>
        ) : null}

        {values.deliver ? (
          <Section
            title="Deliver Events"
            hint="Define what each destination Connection must provide. This stores no Tenant endpoint."
          >
            <p className="m-0 text-xs text-ink-secondary">
              Destination Connections provide <code className="font-mono">base_uri</code>.
            </p>
            <fieldset className="m-0 flex flex-col gap-1 border-0 p-0">
              <legend className="mb-1 text-sm font-medium">Allowed authentication</legend>
              <FormField
                control={form.control}
                name="allow_unauthenticated"
                render={({ field }) => (
                  <CheckRow checked={field.value} onChange={field.onChange} label="Unauthenticated" />
                )}
              />
              <FormField
                control={form.control}
                name="bearer"
                render={({ field }) => (
                  <CheckRow checked={field.value} onChange={field.onChange} label="Bearer token" />
                )}
              />
              <FormField
                control={form.control}
                name="api_key"
                render={({ field }) => (
                  <CheckRow checked={field.value} onChange={field.onChange} label="API key header" />
                )}
              />
            </fieldset>
          </Section>
        ) : null}

        <Section title="Advanced" hint="The manifest this draft applies, and the way past the guided form.">
          {kept.length > 0 ? (
            <p className="m-0 text-xs text-ink-secondary">
              Kept from the imported manifest and applied unchanged: {kept.join(", ")}.
            </p>
          ) : null}
          <Disclosure label="Generated manifest">
            <pre className="m-0 max-h-80 overflow-auto text-xs">{formatJson(manifest)}</pre>
          </Disclosure>
          <Disclosure label="Import JSON">
            <div className="flex flex-col gap-3">
              <Textarea
                aria-label="Manifest (JSON)"
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
              {importParse && !importError ? (
                <ConfirmAction
                  label="Replace draft"
                  variant="outline"
                  question="Replace this draft with the imported manifest?"
                  consequence="Everything authored in this form is discarded."
                  onConfirm={replaceDraft}
                />
              ) : null}
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
