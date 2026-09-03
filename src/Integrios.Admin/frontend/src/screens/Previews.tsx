import { useState } from "react";
import { api } from "../api/client";
import { formError } from "../api/problem";
import type { components } from "../api/schema";
import { Field, FormError, fieldProps } from "../ui/controls";
import { formatJson, parseJson } from "../ui/json";
import { useAction } from "../ui/useAction";

type Preview = components["schemas"]["PreviewResponse"];

/// The two stateless dry-runs the Admin API already owns. Neither reads or writes Tenant data: they
/// evaluate a document against a sample so an author can see the result before saving it. They are
/// offered beside the authoring they belong to rather than as a separate tooling screen.
export function SourceContractPreview() {
  const [schema, setSchema] = useState("");
  const [mapping, setMapping] = useState("{}");
  const [sampleInput, setSampleInput] = useState("{}");
  const [errors, setErrors] = useState<Record<string, string | undefined>>({});
  const [output, setOutput] = useState<Preview | null>(null);
  const { busy, problem, run } = useAction();

  return (
    <form
      onSubmit={(event) => {
        event.preventDefault();
        // An empty schema is a real choice: the contract then validates nothing before mapping.
        const parsedSchema = schema.trim() === "" ? { value: null } : parseJson(schema);
        const parsedMapping = parseJson(mapping);
        const parsedInput = parseJson(sampleInput);
        setErrors({
          schema: parsedSchema.error,
          mapping: parsedMapping.error,
          sampleInput: parsedInput.error,
        });
        if (parsedSchema.error ?? parsedMapping.error ?? parsedInput.error) return;

        setOutput(null);
        void run(
          () =>
            api.POST("/admin/connectors/source-contracts/preview", {
              body: {
                schema: parsedSchema.value,
                mapping: parsedMapping.value,
                sample_input: parsedInput.value,
                sample_context: null,
              },
            }),
          (result) => setOutput(result ?? null),
        );
      }}
    >
      <h2>Preview a Source contract</h2>
      <p>Nothing is saved. This evaluates a schema and mapping against a sample payload.</p>
      <FormError message={formError(problem)} />
      <Field id="preview-contract-schema" label="Schema (JSON, optional)" error={errors.schema}>
        <textarea
          {...fieldProps("preview-contract-schema", errors.schema)}
          rows={6}
          value={schema}
          onChange={(event) => setSchema(event.target.value)}
        />
      </Field>
      <Field id="preview-contract-mapping" label="Mapping (JSON)" error={errors.mapping}>
        <textarea
          {...fieldProps("preview-contract-mapping", errors.mapping)}
          rows={6}
          value={mapping}
          onChange={(event) => setMapping(event.target.value)}
          required
        />
      </Field>
      <Field id="preview-contract-input" label="Sample input (JSON)" error={errors.sampleInput}>
        <textarea
          {...fieldProps("preview-contract-input", errors.sampleInput)}
          rows={6}
          value={sampleInput}
          onChange={(event) => setSampleInput(event.target.value)}
          required
        />
      </Field>
      <button type="submit" disabled={busy}>
        Preview contract
      </button>
      {output ? (
        <>
          <h3>Result</h3>
          <pre>{formatJson(output.output)}</pre>
        </>
      ) : null}
    </form>
  );
}

export function TransformPreview() {
  const [transform, setTransform] = useState("{}");
  const [sampleInput, setSampleInput] = useState("{}");
  const [errors, setErrors] = useState<Record<string, string | undefined>>({});
  const [output, setOutput] = useState<Preview | null>(null);
  const { busy, problem, run } = useAction();

  return (
    <form
      onSubmit={(event) => {
        event.preventDefault();
        const parsedTransform = parseJson(transform);
        const parsedInput = parseJson(sampleInput);
        setErrors({ transform: parsedTransform.error, sampleInput: parsedInput.error });
        if (parsedTransform.error ?? parsedInput.error) return;

        setOutput(null);
        void run(
          () =>
            api.POST("/admin/transform/preview", {
              body: {
                transform: parsedTransform.value,
                sample_input: parsedInput.value,
                sample_context: null,
              },
            }),
          (result) => setOutput(result ?? null),
        );
      }}
    >
      <h3>Preview a mapping</h3>
      <p>Nothing is saved. This evaluates a mapping against a sample payload.</p>
      <FormError message={formError(problem)} />
      <Field id="preview-transform" label="Mapping (JSON)" error={errors.transform}>
        <textarea
          {...fieldProps("preview-transform", errors.transform)}
          rows={6}
          value={transform}
          onChange={(event) => setTransform(event.target.value)}
          required
        />
      </Field>
      <Field id="preview-transform-input" label="Sample input (JSON)" error={errors.sampleInput}>
        <textarea
          {...fieldProps("preview-transform-input", errors.sampleInput)}
          rows={6}
          value={sampleInput}
          onChange={(event) => setSampleInput(event.target.value)}
          required
        />
      </Field>
      <button type="submit" disabled={busy}>
        Preview mapping
      </button>
      {output ? (
        <>
          <h4>Result</h4>
          <pre>{formatJson(output.output)}</pre>
        </>
      ) : null}
    </form>
  );
}
