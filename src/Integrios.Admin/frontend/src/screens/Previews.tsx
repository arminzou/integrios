import { zodResolver } from "@hookform/resolvers/zod";
import { useMutation } from "@tanstack/react-query";
import { useState } from "react";
import { useForm } from "react-hook-form";
import { z } from "zod";
import { Button } from "@/components/ui/button";
import { api } from "../api/client";
import { formError } from "../api/problem";
import { asProblem, call } from "../api/query";
import type { components } from "../api/schema";
import { FormError } from "../ui/controls";
import { Form, TextAreaField } from "../ui/fields";
import { formatJson, parseJson } from "../ui/json";
import { Panel } from "../ui/layout";

type Preview = components["schemas"]["PreviewResponse"];

const jsonDocument = z.string().superRefine((text, ctx) => {
  const parsed = parseJson(text);
  if (parsed.error !== undefined) ctx.addIssue({ code: z.ZodIssueCode.custom, message: parsed.error });
});

/// An empty schema is a real choice: the contract then validates nothing before mapping.
const optionalJsonDocument = z.string().superRefine((text, ctx) => {
  if (text.trim() === "") return;
  const parsed = parseJson(text);
  if (parsed.error !== undefined) ctx.addIssue({ code: z.ZodIssueCode.custom, message: parsed.error });
});

const contractSchema = z.object({
  schema: optionalJsonDocument,
  mapping: jsonDocument,
  sample_input: jsonDocument,
});

const transformSchema = z.object({
  transform: jsonDocument,
  sample_input: jsonDocument,
});

type ContractValues = z.infer<typeof contractSchema>;
type TransformValues = z.infer<typeof transformSchema>;

const documentOrNull = (text: string) => (text.trim() === "" ? null : parseJson(text).value);

function Result({ output }: { output: Preview | null }) {
  if (!output) return null;
  return (
    <div className="flex flex-col gap-2">
      <h4 className="m-0 text-sm font-semibold">Result</h4>
      <pre className="m-0 text-sm">{formatJson(output.output)}</pre>
    </div>
  );
}

/// The two stateless dry-runs the Admin API already owns. Neither reads or writes Tenant data: they
/// evaluate a document against a sample so an author can see the result before saving it. They are
/// offered beside the authoring they belong to rather than as a separate tooling screen.
export function SourceContractPreview() {
  const [output, setOutput] = useState<Preview | null>(null);
  const form = useForm<ContractValues>({
    resolver: zodResolver(contractSchema),
    defaultValues: { schema: "", mapping: "{}", sample_input: "{}" },
  });

  // A preview saves nothing, but it is still a write in every other sense: one call, its own busy
  // state, and Problem Details when the evaluation is refused.
  const preview = useMutation({
    mutationFn: (values: ContractValues) =>
      call(() =>
        api.POST("/admin/connectors/source-contracts/preview", {
          body: {
            schema: documentOrNull(values.schema),
            mapping: parseJson(values.mapping).value,
            sample_input: parseJson(values.sample_input).value,
            sample_context: null,
          },
        }),
      ),
    onSuccess: (result) => setOutput(result ?? null),
  });

  const submit = form.handleSubmit((values) => {
    setOutput(null);
    preview.mutate(values);
  });

  return (
    <Form {...form}>
      <Panel asChild>
        <form className="flex flex-col gap-4" onSubmit={submit}>
          <h2>Preview a Source contract</h2>
          <p className="m-0 text-ink-secondary">
            Nothing is saved. This evaluates a schema and mapping against a sample payload.
          </p>
          <FormError message={formError(asProblem(preview.error))} />

          <TextAreaField
            control={form.control}
            name="schema"
            label="Schema (JSON, optional)"
            className="min-h-32 font-mono text-sm"
          />
          <TextAreaField
            control={form.control}
            name="mapping"
            label="Mapping (JSON)"
            className="min-h-32 font-mono text-sm"
            required
          />
          <TextAreaField
            control={form.control}
            name="sample_input"
            label="Sample input (JSON)"
            className="min-h-32 font-mono text-sm"
            required
          />

          <Button type="submit" variant="outline" className="self-start" disabled={preview.isPending}>
            Preview contract
          </Button>
          <Result output={output} />
        </form>
      </Panel>
    </Form>
  );
}

export function TransformPreview() {
  const [output, setOutput] = useState<Preview | null>(null);
  const form = useForm<TransformValues>({
    resolver: zodResolver(transformSchema),
    defaultValues: { transform: "{}", sample_input: "{}" },
  });

  const preview = useMutation({
    mutationFn: (values: TransformValues) =>
      call(() =>
        api.POST("/admin/transform/preview", {
          body: {
            transform: parseJson(values.transform).value,
            sample_input: parseJson(values.sample_input).value,
            sample_context: null,
          },
        }),
      ),
    onSuccess: (result) => setOutput(result ?? null),
  });

  const submit = form.handleSubmit((values) => {
    setOutput(null);
    preview.mutate(values);
  });

  return (
    <Form {...form}>
      <Panel asChild>
        <form className="flex flex-col gap-4" onSubmit={submit}>
          <h3>Preview a mapping</h3>
          <p className="m-0 text-ink-secondary">Nothing is saved. This evaluates a mapping against a sample payload.</p>
          <FormError message={formError(asProblem(preview.error))} />

          <TextAreaField
            control={form.control}
            name="transform"
            label="Mapping (JSON)"
            className="min-h-32 font-mono text-sm"
            required
          />
          <TextAreaField
            control={form.control}
            name="sample_input"
            label="Sample input (JSON)"
            className="min-h-32 font-mono text-sm"
            required
          />

          <Button type="submit" variant="outline" className="self-start" disabled={preview.isPending}>
            Preview mapping
          </Button>
          <Result output={output} />
        </form>
      </Panel>
    </Form>
  );
}
