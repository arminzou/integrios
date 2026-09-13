import { zodResolver } from "@hookform/resolvers/zod";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { cn } from "cn";
import { type ReactNode, useEffect, useState } from "react";
import { useForm, useWatch } from "react-hook-form";
import { z } from "zod";
import { Button } from "@/components/ui/button";
import { FormField } from "@/components/ui/form";
import { api } from "../api/client";
import { formError } from "../api/problem";
import { asProblem, call } from "../api/query";
import type { components } from "../api/schema";
import { snakeIdentifier } from "../identifiers";
import { Callout, Disclosure, FormError } from "../ui/controls";
import { BodyPanel } from "../ui/copy";
import { Form, TextAreaField, TextField } from "../ui/fields";
import { applyProblem } from "../ui/formProblem";

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

/// What the capability pair means to the manifest, and the word the installed Connector is read back
/// under.
const direction = (values: Pick<AuthoringValues, "receive" | "deliver">) =>
  values.receive && values.deliver ? "both" : values.receive ? "source" : "destination";

type ComposeInput = [key: string, contractVersion: number, name: string, description: string | null, direction: string];

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

/// The guided New Connector draft. Admin composes its manifest, and the dashboard applies that
/// returned document unchanged through the immutable version route.
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
  const form = useForm<AuthoringValues>({
    resolver: zodResolver(authoringSchema),
    defaultValues: from
      ? {
          name: from.name,
          key: from.key,
          description: from.description ?? "",
          contract_version: String(Number(from.contract_version) + 1),
          receive: from.direction === "source" || from.direction === "both",
          deliver: from.direction === "destination" || from.direction === "both",
        }
      : blank,
  });
  const values = form.watch();

  // The key follows the name until the Operator writes one themselves, and never touches a key that
  // arrived from an applied Connector: a copied version's key is its identity.
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

  const parsed = authoringSchema.safeParse(values);
  const composeInput: ComposeInput | undefined = parsed.success
    ? [
        parsed.data.key.trim(),
        Number(parsed.data.contract_version),
        parsed.data.name.trim(),
        parsed.data.description.trim() || null,
        direction(parsed.data),
      ]
    : undefined;
  const composeKey = composeInput ? JSON.stringify(composeInput) : "";
  const [settledComposeKey, setSettledComposeKey] = useState("");
  useEffect(() => {
    if (!composeKey) {
      setSettledComposeKey("");
      return;
    }
    const timer = setTimeout(() => setSettledComposeKey(composeKey), 300);
    return () => clearTimeout(timer);
  }, [composeKey]);

  const compose = useQuery({
    queryKey: ["connector-compose", settledComposeKey],
    queryFn: () => {
      const [key, contractVersion, name, description, requestedDirection] = JSON.parse(
        settledComposeKey,
      ) as ComposeInput;
      return call(() =>
        api.POST("/admin/connectors/{key}/versions/{contractVersion}/compose", {
          params: { path: { key, contractVersion } },
          body: { name, description, direction: requestedDirection },
        }),
      );
    },
    enabled: settledComposeKey !== "",
  });
  const compositionIsCurrent = composeKey !== "" && settledComposeKey === composeKey;
  const manifest = compositionIsCurrent ? compose.data?.manifest : undefined;
  const composing = parsed.success && (!compositionIsCurrent || compose.isFetching);
  const composeProblem = compositionIsCurrent ? asProblem(compose.error) : null;

  const apply = useMutation({
    mutationFn: ({ document, key, contractVersion }: { document: unknown; key: string; contractVersion: number }) =>
      call(() =>
        api.PUT("/admin/connectors/{key}/versions/{contractVersion}", {
          params: { path: { key, contractVersion } },
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
    if (!manifest || settledComposeKey !== composeKey) return;
    apply.mutate(
      { document: manifest, key: current.key.trim(), contractVersion: Number(current.contract_version) },
      {
        onError: (failure) => applyProblem(form, failure, applyFields),
      },
    );
  });

  return (
    <Form {...form}>
      <form className="flex flex-col gap-5" noValidate onSubmit={submit}>
        <FormError message={formError(composeProblem)} />
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

        <Section title="Advanced" hint="The manifest Admin composed from this draft.">
          {composing ? (
            <p role="status" className="m-0 text-xs text-ink-secondary">
              Composing manifest…
            </p>
          ) : null}
          <Disclosure label="Manifest JSON">
            {manifest === undefined ? (
              <p className="m-0 text-xs text-ink-secondary">Complete the guided fields to compose a manifest.</p>
            ) : (
              <BodyPanel label="Manifest" value={manifest} unbounded />
            )}
          </Disclosure>
        </Section>

        <Button
          type="submit"
          className="self-start"
          disabled={
            apply.isPending || (parsed.success && (composing || manifest === undefined || composeProblem !== null))
          }
        >
          {from ? "Create version" : "Create Connector"}
        </Button>
      </form>
    </Form>
  );
}
