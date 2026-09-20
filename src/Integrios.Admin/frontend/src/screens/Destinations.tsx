import { zodResolver } from "@hookform/resolvers/zod";
import { useInfiniteQuery, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { type Control, type FieldValues, type Path, useForm } from "react-hook-form";
import { Link, NavLink, useNavigate } from "react-router";
import { z } from "zod";
import { Button } from "@/components/ui/button";
import { SelectItem } from "@/components/ui/select";
import { TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import { api } from "../api/client";
import { formError } from "../api/problem";
import { asProblem, call, nextCursor } from "../api/query";
import type { components } from "../api/schema";
import { CodeBlock } from "../ui/codeHighlight";
import {
  appliedNote,
  ConfirmAction,
  CreateSheet,
  EditSheet,
  FilterBar,
  FormError,
  ListStatus,
  LoadMore,
  narrowable,
  ReadError,
  Section,
  SheetButton,
  WriteStatus,
} from "../ui/controls";
import { CopyInline } from "../ui/copy";
import { Filter, FilterSearch, Form, SelectField, TextAreaField, TextField } from "../ui/fields";
import { useListFilters } from "../ui/filters";
import { applyProblem } from "../ui/formProblem";
import { formatJson, isObject, object, parseJson, sameJson } from "../ui/json";
import {
  CloseInspector,
  Details,
  Inspector,
  InspectorPlaceholder,
  openRow,
  Page,
  PageHeader,
  RowChevron,
  RowHeader,
  SplitList,
  SplitView,
  TableCard,
} from "../ui/layout";
import { environmentsIn, useConnectorOptions, useDestinationOptions } from "../ui/options";
import { StatusBadge } from "../ui/status";
import { Day } from "../ui/time";

/// Destinations is the authoring pattern every other capability copies. Its parts, in the order they
/// appear below:
///
/// - A list screen is a heading, a create panel behind a disclosure so it never dominates the list,
///   a filter, and the rows in a bordered card. A list is a `useInfiniteQuery` whose key carries the
///   filters, because a cursor is only valid for the filters it was issued under; paging is an
///   explicit Load more over `next_cursor`, never a page number or a total.
/// - A form is a Zod schema plus `useForm`. The schema is the only place the form's rules live; the
///   submit handler does the conversion a form of strings always needs — text to a JSON document, an
///   untouched optional field to `null` — and names the request body the typed client sends.
/// - A write is a `useMutation`, and on success it invalidates the queries it affected so the screen
///   re-reads authoritative server state rather than patching a second local copy of it.
/// - A rejected write comes back as Problem Details, thrown by `call` and caught here.
///   `applyProblem` puts each field-keyed message on its own control and `formError` renders
///   whatever was attributed to no rendered field, so the Admin API stays the authority on what is
///   wrong with a document.
/// - A picker over another capability is a real `<select>`, and an irreversible action is
///   `ConfirmAction`, which names what it is about to change before it can be confirmed.
///
/// What is capability-specific — which fields exist, what they mean, which mutations the Admin API
/// offers — stays here rather than moving into a shared form abstraction.

type DestinationListItem = components["schemas"]["DestinationListItemDto"];
type Destination = components["schemas"]["DestinationDto"];
type Connector = components["schemas"]["ConnectorDto"];
type DestinationAuthentication = components["schemas"]["DestinationAuthenticationSelectionRequest"];

type ScalarType = "string" | "number" | "integer" | "boolean";
type ScalarField = {
  name: string;
  type: ScalarType;
  required: boolean;
  options?: unknown[];
  format?: "uri" | "hostname";
  minLength?: number;
  maxLength?: number;
  minimum?: number;
  maximum?: number;
};
type AuthenticationScheme = { scheme: string; config: string[]; secretRefs: string[] };
type DestinationContract = {
  configuration: ScalarField[];
  allowUnauthenticated: boolean;
  authenticationSchemes: AuthenticationScheme[];
};

/// The fields each form renders, so a message the server attributes to one of them lands on that
/// control and everything else lands at form level.
const editFields = ["name", "environment", "description"] as const;
const createFields = ["connector_id", ...editFields] as const;

/// A domain JSON document, authored as text. Well-formedness is all the dashboard checks; the
/// Connector contract, and the server, remain the authority on whether the document is valid.
const jsonDocument = z.string().superRefine((text, ctx) => {
  const parsed = parseJson(text);
  if (parsed.error !== undefined) ctx.addIssue({ code: z.ZodIssueCode.custom, message: parsed.error });
});

const editSchema = z.object({
  name: z.string().trim().min(1, "Enter a name."),
  configuration_fields: z.record(z.string(), z.string()),
  raw_configuration: jsonDocument,
  authentication_scheme: z.string(),
  authentication_config_fields: z.record(z.string(), z.string()),
  authentication_secret_ref_fields: z.record(z.string(), z.string()),
  raw_authentication: jsonDocument,
  environment: z.string(),
  description: z.string(),
});

const createSchema = editSchema.extend({
  connector_id: z.string().min(1, "Choose a Connector."),
});

type EditValues = z.infer<typeof editSchema>;
type CreateValues = z.infer<typeof createSchema>;

/// An optional field left untouched is absent, not empty.
const optional = (text: string) => text.trim() || null;

const stringList = (value: unknown): string[] | null =>
  Array.isArray(value) && value.every((item) => typeof item === "string") ? value : null;

function destinationContract(manifest: unknown): DestinationContract | null {
  const document = object(manifest);
  const schema = object(document.destination_configuration_schema);
  if (schema.type !== "object" || !isObject(schema.properties)) return null;

  const required = stringList(schema.required ?? []) ?? [];
  const configuration = Object.entries(schema.properties).map(([name, value]) => {
    const field = object(value);
    if (!(["string", "number", "integer", "boolean"] as unknown[]).includes(field.type)) return null;
    const options = field.enum === undefined ? undefined : Array.isArray(field.enum) ? field.enum : null;
    if (options === null) return null;
    return {
      name,
      type: field.type as ScalarType,
      required: required.includes(name),
      options,
      format: field.format === "uri" || field.format === "hostname" ? field.format : undefined,
      minLength: typeof field.minLength === "number" ? field.minLength : undefined,
      maxLength: typeof field.maxLength === "number" ? field.maxLength : undefined,
      minimum: typeof field.minimum === "number" ? field.minimum : undefined,
      maximum: typeof field.maximum === "number" ? field.maximum : undefined,
    } satisfies ScalarField;
  });
  if (configuration.some((field) => field === null)) return null;

  const authentication = object(document.destination_authentication);
  if (typeof authentication.allow_unauthenticated !== "boolean" || !Array.isArray(authentication.schemes)) return null;
  const authenticationSchemes = authentication.schemes.map((value) => {
    const scheme = object(value);
    const config = stringList(scheme.required_config);
    const secretRefs = stringList(scheme.required_secret_refs);
    return typeof scheme.scheme === "string" && config && secretRefs
      ? { scheme: scheme.scheme, config, secretRefs }
      : null;
  });
  if (authenticationSchemes.some((scheme) => scheme === null)) return null;

  return {
    configuration: configuration as ScalarField[],
    allowUnauthenticated: authentication.allow_unauthenticated,
    authenticationSchemes: authenticationSchemes as AuthenticationScheme[],
  };
}

function scalarValue(field: ScalarField, value: string): unknown {
  if (value === "") return field.required && field.type === "string" && !field.options ? "" : undefined;
  if (field.options) return JSON.parse(value) as unknown;
  if (field.type === "number" || field.type === "integer") return value === "" ? undefined : Number(value);
  if (field.type === "boolean") return value === "" ? undefined : value === "true";
  return value;
}

function configurationDocument(fields: ScalarField[], values: Record<string, string>): Record<string, unknown> {
  return Object.fromEntries(
    fields.flatMap((field) => {
      const value = scalarValue(field, values[field.name] ?? "");
      return value === undefined ? [] : [[field.name, value]];
    }),
  );
}

function configurationFields(fields: ScalarField[], configuration: unknown): Record<string, string> | null {
  if (!isObject(configuration)) return null;
  const values: Record<string, string> = {};
  for (const field of fields) {
    if (!Object.hasOwn(configuration, field.name)) {
      values[field.name] = "";
      continue;
    }
    const value = configuration[field.name];
    if (
      (field.type === "string" && typeof value !== "string") ||
      ((field.type === "number" || field.type === "integer") && typeof value !== "number") ||
      (field.type === "integer" && !Number.isInteger(value)) ||
      (field.type === "boolean" && typeof value !== "boolean") ||
      (field.options && !field.options.some((option) => sameJson(option, value)))
    )
      return null;
    values[field.name] = field.options ? JSON.stringify(value) : String(value);
  }
  return sameJson(configuration, configurationDocument(fields, values)) ? values : null;
}

function authenticationDocument(
  contract: DestinationContract,
  schemeName: string,
  configValues: Record<string, string>,
  secretRefValues: Record<string, string>,
) {
  if (!schemeName) return null;
  const scheme = contract.authenticationSchemes.find((candidate) => candidate.scheme === schemeName);
  if (!scheme) return null;
  return {
    scheme: scheme.scheme,
    config: Object.fromEntries(scheme.config.map((name) => [name, configValues[name] ?? ""])),
    secret_refs: Object.fromEntries(scheme.secretRefs.map((name) => [name, secretRefValues[name] ?? ""])),
  };
}

function authenticationFields(contract: DestinationContract, authentication: unknown) {
  if (authentication === null || authentication === undefined)
    return contract.allowUnauthenticated
      ? { scheme: "", config: {} as Record<string, string>, secretRefs: {} as Record<string, string> }
      : null;
  if (!isObject(authentication) || typeof authentication.scheme !== "string") return null;
  const scheme = contract.authenticationSchemes.find((candidate) => candidate.scheme === authentication.scheme);
  if (!scheme || !isObject(authentication.config) || !isObject(authentication.secret_refs)) return null;
  const config: Record<string, string> = {};
  const secretRefs: Record<string, string> = {};
  for (const name of scheme.config) {
    if (typeof authentication.config[name] !== "string") return null;
    config[name] = authentication.config[name];
  }
  for (const name of scheme.secretRefs) {
    if (typeof authentication.secret_refs[name] !== "string") return null;
    secretRefs[name] = authentication.secret_refs[name];
  }
  const fields = { scheme: scheme.scheme, config, secretRefs };
  return sameJson(authentication, authenticationDocument(contract, fields.scheme, fields.config, fields.secretRefs))
    ? fields
    : null;
}

const acronym = new Set(["api", "http", "https", "id", "uri", "url"]);
const fieldLabel = (value: string) =>
  value
    .split("_")
    .map((word, index) =>
      acronym.has(word.toLowerCase())
        ? word.toUpperCase()
        : index === 0
          ? `${word.charAt(0).toUpperCase()}${word.slice(1)}`
          : word,
    )
    .join(" ");

function scalarHint(field: ScalarField): string | undefined {
  const hints: string[] = [];
  if (field.format === "uri") hints.push("Absolute URI.");
  if (field.format === "hostname") hints.push("Hostname.");
  if (field.minLength !== undefined) hints.push(`At least ${field.minLength} characters.`);
  if (field.maxLength !== undefined) hints.push(`At most ${field.maxLength} characters.`);
  if (field.minimum !== undefined) hints.push(`Minimum ${field.minimum}.`);
  if (field.maximum !== undefined) hints.push(`Maximum ${field.maximum}.`);
  return hints.join(" ") || undefined;
}

function ConfigurationFields<TValues extends FieldValues>({
  control,
  fields,
}: {
  control: Control<TValues>;
  fields: ScalarField[];
}) {
  if (fields.length === 0)
    return <p className="m-0 text-sm text-ink-secondary">This Connector requires no Destination configuration.</p>;
  return fields.map((field) => {
    const name = `configuration_fields.${field.name}` as Path<TValues>;
    const options = field.options ?? (field.type === "boolean" ? [true, false] : undefined);
    return options ? (
      <SelectField
        key={field.name}
        control={control}
        name={name}
        label={fieldLabel(field.name)}
        hint={scalarHint(field)}
        emptyLabel={field.required ? undefined : "Not set"}
        required={field.required}
      >
        {options.map((option) => (
          <SelectItem key={JSON.stringify(option)} value={JSON.stringify(option)}>
            {typeof option === "string"
              ? fieldLabel(option)
              : typeof option === "boolean"
                ? option
                  ? "True"
                  : "False"
                : String(option)}
          </SelectItem>
        ))}
      </SelectField>
    ) : (
      <TextField
        key={field.name}
        control={control}
        name={name}
        label={fieldLabel(field.name)}
        hint={scalarHint(field)}
        type={field.type === "number" || field.type === "integer" ? "number" : field.format === "uri" ? "url" : "text"}
        step={field.type === "integer" ? 1 : field.type === "number" ? "any" : undefined}
        min={field.minimum}
        max={field.maximum}
        minLength={field.minLength}
        maxLength={field.maxLength}
        required={field.required}
      />
    );
  });
}

function AuthenticationFields<TValues extends FieldValues>({
  control,
  contract,
  schemeName,
  onSchemeChange,
}: {
  control: Control<TValues>;
  contract: DestinationContract;
  schemeName: string;
  onSchemeChange: (scheme: AuthenticationScheme | null) => void;
}) {
  const scheme = contract.authenticationSchemes.find((candidate) => candidate.scheme === schemeName);
  return (
    <Section title="Authentication" hint="How every Subscription using this Destination authenticates.">
      <SelectField
        control={control}
        name={"authentication_scheme" as Path<TValues>}
        label="Authentication"
        emptyLabel={contract.allowUnauthenticated ? "No authentication" : undefined}
        required={!contract.allowUnauthenticated}
        onChange={(value) =>
          onSchemeChange(contract.authenticationSchemes.find((candidate) => candidate.scheme === value) ?? null)
        }
      >
        {contract.authenticationSchemes.map((option) => (
          <SelectItem key={option.scheme} value={option.scheme}>
            {fieldLabel(option.scheme)}
          </SelectItem>
        ))}
      </SelectField>
      {scheme?.config.map((name) => (
        <TextField
          key={name}
          control={control}
          name={`authentication_config_fields.${name}` as Path<TValues>}
          label={fieldLabel(name)}
          required
        />
      ))}
      {scheme?.secretRefs.map((name) => (
        <TextField
          key={name}
          control={control}
          name={`authentication_secret_ref_fields.${name}` as Path<TValues>}
          label={`${fieldLabel(name)} secret reference`}
          hint="Enter the reference name only, never the secret value."
          required
        />
      ))}
    </Section>
  );
}

const destinationFilters = ["name", "connector", "environment", "status"] as const;

export function DestinationsScreen({
  tenantId,
  selectedDestinationId,
}: {
  tenantId: string;
  selectedDestinationId?: string;
}) {
  const filters = useListFilters(destinationFilters);
  const { status, environment, connector, name } = filters.values;
  const connectors = useConnectorOptions();
  const destinationOptions = useDestinationOptions(tenantId);
  const applied = filters.applied;
  // Read off the Destination list the screen already holds for naming.
  const environments = environmentsIn(destinationOptions.data?.items);
  const list = useInfiniteQuery({
    queryKey: ["destinations", tenantId, { status, environment, connector, name }],
    queryFn: ({ pageParam }) =>
      call(() =>
        api.GET("/admin/tenants/{tenantId}/destinations", {
          params: {
            path: { tenantId },
            query: {
              status: status || undefined,
              environment: environment || undefined,
              connector: connector || undefined,
              name: name || undefined,
              after: pageParam ?? undefined,
              limit: 20,
            },
          },
        }),
      ),
    initialPageParam: null as string | null,
    getNextPageParam: nextCursor<DestinationListItem>,
  });
  const destinations = list.data?.pages.flatMap((page) => page.items) ?? [];
  // Whether there is a list to narrow yet. Until the read answers, neither the filter bar nor the
  // header's create action is rendered: an empty scope answers with the card that replaces the
  // table, carrying the action itself, and a screen that guessed first would retract them.
  const narrowing = narrowable(list.isSuccess, destinations.length, applied);

  const [creating, setCreating] = useState(false);
  const create = <SheetButton label="New Destination" expanded={creating} onOpen={() => setCreating(true)} />;

  return (
    <Page>
      <PageHeader title="Destinations" action={narrowing ? create : undefined}>
        Tenant-owned endpoints built from a Connector. Subscriptions deliver to one of these.
      </PageHeader>

      {narrowing ? (
        <FilterBar applied={applied} onClear={filters.clear}>
          <FilterSearch
            id="destination-name"
            label="Name"
            placeholder="Name contains…"
            value={name}
            onChange={(value) => filters.set("name", value)}
          />
          <Filter
            id="destination-connector"
            label="Connector"
            value={connector}
            onChange={(value) => filters.set("connector", value)}
            hint={connectors.data?.next_cursor ? "Showing the first 100 Connectors." : undefined}
          >
            {(connectors.data?.items ?? []).map((option) => (
              <SelectItem key={option.id} value={option.key}>
                {option.key}
              </SelectItem>
            ))}
          </Filter>
          {/* The environments a Tenant actually uses, read off the rows it already has rather than
            from a fixed list: environment is free text on a Destination, so there is no vocabulary
            to enumerate. */}
          <Filter
            id="destination-environment"
            label="Environment"
            value={environment}
            onChange={(value) => filters.set("environment", value)}
            hint={
              destinationOptions.data?.next_cursor ? "Showing environments from the first 100 Destinations." : undefined
            }
          >
            {environments.map((option) => (
              <SelectItem key={option} value={option}>
                {option}
              </SelectItem>
            ))}
          </Filter>
          <Filter
            id="destination-status"
            label="Status"
            value={status}
            onChange={(value) => filters.set("status", value)}
          >
            <SelectItem value="active">Active</SelectItem>
            <SelectItem value="inactive">Inactive</SelectItem>
          </Filter>
        </FilterBar>
      ) : null}

      <SplitView>
        <SplitList>
          <ListStatus
            busy={list.isFetching}
            loaded={list.isSuccess}
            problem={asProblem(list.error)}
            empty={destinations.length === 0}
            applied={applied}
            noun="Destinations"
            emptyText={
              <>
                A Tenant-owned endpoint built from a <Link to="/connectors">Connector</Link>. Subscriptions deliver to
                one of these.
              </>
            }
            action={create}
          />
          {destinations.length > 0 ? (
            <TableCard
              caption={`Destinations, newest first${appliedNote(applied)}`}
              footer={
                <LoadMore
                  noun="Destination"
                  hasMore={list.hasNextPage}
                  busy={list.isFetching}
                  loaded={destinations.length}
                  onLoadMore={() => void list.fetchNextPage()}
                />
              }
            >
              <TableHeader>
                <TableRow>
                  <TableHead scope="col">Name</TableHead>
                  <TableHead scope="col">Connector</TableHead>
                  <TableHead scope="col">Environment</TableHead>
                  <TableHead scope="col">Status</TableHead>
                  <TableHead scope="col">Description</TableHead>
                  <TableHead scope="col">Updated</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {destinations.map((destination) => (
                  <TableRow
                    key={destination.id}
                    className="group cursor-pointer has-[a[aria-current=page]]:bg-selected-surface"
                    onClick={openRow}
                  >
                    <RowHeader>
                      {/* The route is the selection, so `aria-current` follows the URL rather than a
                        separately tracked flag — the same contract the Event ledger already has. */}
                      <NavLink
                        className="-mx-3 block px-3 py-2 no-underline"
                        to={`/tenants/${tenantId}/destinations/${destination.id}`}
                        end
                      >
                        {destination.name}
                      </NavLink>
                    </RowHeader>
                    <TableCell className="font-mono">{destination.connector_key}</TableCell>
                    <TableCell>{destination.environment ?? "—"}</TableCell>
                    <TableCell>
                      <StatusBadge status={destination.status} />
                    </TableCell>
                    <TableCell className="text-ink-secondary">{destination.description ?? "—"}</TableCell>
                    <TableCell className="text-ink-secondary">
                      <div className="flex items-center justify-between gap-3">
                        <Day value={destination.updated_at} />
                        <RowChevron />
                      </div>
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </TableCard>
          ) : null}
        </SplitList>

        {/* Keyed by Destination id so switching rows is a distinct panel rather than the same one
              fed a new id, which is what keeps a stale name from being on screen when focus moves. */}
        {selectedDestinationId ? (
          <DestinationInspector key={selectedDestinationId} tenantId={tenantId} destinationId={selectedDestinationId} />
        ) : destinations.length > 0 ? (
          <InspectorPlaceholder label="Destination detail">
            Select a Destination to read its configuration and authentication here.
          </InspectorPlaceholder>
        ) : null}
      </SplitView>

      <CreateSheet
        label="New Destination"
        description="Tenant-owned endpoint built from a Connector"
        open={creating}
        onOpenChange={setCreating}
      >
        {(close) => <CreateDestination tenantId={tenantId} onCreated={close} />}
      </CreateSheet>
    </Page>
  );
}

function CreateDestination({ tenantId, onCreated }: { tenantId: string; onCreated: () => void }) {
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const connectors = useConnectorOptions();
  const connectorOptions = (connectors.data?.items ?? []).filter(
    (connector) => connector.direction === "destination" || connector.direction === "both",
  );
  const connectorOptionsUnavailable = connectors.isPending || connectors.isError;
  // Connectors are deployment-wide: a Tenant cannot author its way out of this one, so the sentence
  // sends an Operator to the deployment's own list rather than anywhere in this Tenant.
  const noConnectors = connectors.isSuccess && connectorOptions.length === 0;

  const form = useForm<CreateValues>({
    resolver: zodResolver(createSchema),
    defaultValues: {
      connector_id: "",
      name: "",
      configuration_fields: {},
      raw_configuration: "{}",
      authentication_scheme: "",
      authentication_config_fields: {},
      authentication_secret_ref_fields: {},
      raw_authentication: "null",
      environment: "",
      description: "",
    },
  });
  const connectorId = form.watch("connector_id");
  const connector = useQuery({
    queryKey: ["connector", connectorId],
    queryFn: () => call(() => api.GET("/admin/connectors/{id}", { params: { path: { id: connectorId } } })),
    enabled: connectorId !== "",
  });
  const contract = destinationContract(connector.data?.manifest);
  const selectedConnectorUnavailable =
    connectorId !== "" && (connector.isPending || connector.isError || contract === null);
  const cannotAuthor = connectorOptionsUnavailable || noConnectors || selectedConnectorUnavailable;
  const authenticationScheme = form.watch("authentication_scheme");

  const create = useMutation({
    mutationFn: (values: CreateValues) => {
      if (!contract) throw new Error("The selected Connector has no authorable Destination contract.");
      return call(() =>
        api.POST("/admin/tenants/{tenantId}/destinations", {
          params: { path: { tenantId } },
          body: {
            connector_id: values.connector_id,
            name: values.name,
            configuration: configurationDocument(contract.configuration, values.configuration_fields),
            authentication: authenticationDocument(
              contract,
              values.authentication_scheme,
              values.authentication_config_fields,
              values.authentication_secret_ref_fields,
            ),
            environment: optional(values.environment),
            description: optional(values.description),
          },
        }),
      );
    },
    onSuccess: (created) => {
      void queryClient.invalidateQueries({ queryKey: ["destinations", tenantId] });
      onCreated();
      if (created) navigate(`/tenants/${tenantId}/destinations/${created.id}`);
    },
  });

  const submit = form.handleSubmit((values) => {
    if (!contract) {
      form.setError("connector_id", { type: "required", message: "Choose an authorable Connector." });
      return;
    }
    if (!contract.allowUnauthenticated && !values.authentication_scheme) {
      form.setError("authentication_scheme", { type: "required", message: "Choose an authentication method." });
      return;
    }
    const scheme = contract?.authenticationSchemes.find(
      (candidate) => candidate.scheme === values.authentication_scheme,
    );
    let invalid = false;
    for (const name of scheme?.config ?? [])
      if (!values.authentication_config_fields[name]?.trim()) {
        form.setError(`authentication_config_fields.${name}`, {
          type: "required",
          message: `Enter ${fieldLabel(name).toLowerCase()}.`,
        });
        invalid = true;
      }
    for (const name of scheme?.secretRefs ?? [])
      if (!values.authentication_secret_ref_fields[name]?.trim()) {
        form.setError(`authentication_secret_ref_fields.${name}`, {
          type: "required",
          message: "Enter a secret reference.",
        });
        invalid = true;
      }
    if (invalid) return;
    create.mutate(values, { onError: (failure) => applyProblem(form, failure, createFields) });
  });

  return (
    <Form {...form}>
      <form className="flex flex-col gap-4" noValidate onSubmit={submit} aria-label="Create a Destination">
        <FormError message={formError(asProblem(connectors.error ?? connector.error))} />
        <FormError
          message={
            connector.data && !contract
              ? "This Connector's Destination contract cannot be authored by the guided form."
              : undefined
          }
        />
        <FormError message={formError(asProblem(create.error), createFields)} />

        <SelectField
          control={form.control}
          name="connector_id"
          label="Connector"
          hint={
            noConnectors ? (
              <>
                No active destination-capable Connectors exist yet, and a Destination is built from one.{" "}
                <Link to="/connectors">Create a Connector</Link> first.
              </>
            ) : connectors.data?.next_cursor ? (
              "Showing the first 100 active destination-capable Connectors."
            ) : undefined
          }
          disabled={connectorOptionsUnavailable || noConnectors}
          onChange={() => {
            form.setValue("configuration_fields", {});
            form.setValue("authentication_scheme", "");
            form.setValue("authentication_config_fields", {});
            form.setValue("authentication_secret_ref_fields", {});
          }}
          required
        >
          {connectorOptions.map((connector) => (
            <SelectItem key={connector.id} value={connector.id}>
              {connector.name} (v{connector.contract_version})
            </SelectItem>
          ))}
        </SelectField>
        <TextField control={form.control} name="name" label="Name" required />
        {contract ? (
          <>
            <Section
              title="Configuration"
              hint="Reusable endpoint and Destination-wide settings declared by this Connector."
            >
              <ConfigurationFields control={form.control} fields={contract.configuration} />
            </Section>
            <AuthenticationFields
              control={form.control}
              contract={contract}
              schemeName={authenticationScheme}
              onSchemeChange={(scheme) => {
                form.setValue(
                  "authentication_config_fields",
                  Object.fromEntries((scheme?.config ?? []).map((name) => [name, ""])),
                );
                form.setValue(
                  "authentication_secret_ref_fields",
                  Object.fromEntries((scheme?.secretRefs ?? []).map((name) => [name, ""])),
                );
              }}
            />
          </>
        ) : null}
        <TextField control={form.control} name="environment" label="Environment (optional)" />
        <TextField control={form.control} name="description" label="Description (optional)" />

        <Button type="submit" className="self-start" disabled={create.isPending || cannotAuthor}>
          Create Destination
        </Button>
      </form>
    </Form>
  );
}

/// The Connector a Destination was built from, as an Operator names it: the manifest key and the
/// contract version it is pinned to. Falls back to the identifier when the list has not resolved it.
function connectorLabel(
  connectors: { id: string; key: string; contract_version: number | string }[] | undefined,
  id: string,
): string {
  const connector = connectors?.find((item) => item.id === id);
  return connector ? `${connector.key} v${connector.contract_version}` : id;
}

function DestinationInspector({ tenantId, destinationId }: { tenantId: string; destinationId: string }) {
  // A Destination is built from a Connector, and which one it was is the first thing an Operator
  // checks when its configuration looks wrong. The identifier answers a different question.
  const connectors = useConnectorOptions();
  const [notice, setNotice] = useState("");
  const destination = useQuery({
    queryKey: ["destination", tenantId, destinationId],
    queryFn: () =>
      call(() =>
        api.GET("/admin/tenants/{tenantId}/destinations/{id}", {
          params: { path: { tenantId, id: destinationId } },
        }),
      ),
  });

  const problem = asProblem(destination.error);
  if (problem)
    return (
      <Inspector label="Destination detail">
        <h2 className="m-0">Destination</h2>
        <ReadError
          problem={problem}
          what="This Destination"
          back={{ to: `/tenants/${tenantId}/destinations`, label: "Back to Destinations" }}
        />
      </Inspector>
    );
  if (!destination.data) return <Inspector label="Destination detail">Loading…</Inspector>;

  const current = destination.data;
  return (
    <Inspector label="Destination detail">
      {/* The identity on the left, the state that qualifies it on the right: the two things an
          Operator checks before reading anything else in the panel. */}
      <div className="flex items-start justify-between gap-3">
        <div className="group min-w-0">
          <h2 className="break-all">{current.name}</h2>
          <span className="block text-xs text-ink-secondary">
            <CopyInline label="Destination id" value={current.id} />
          </span>
        </div>
        <div className="flex shrink-0 items-center gap-1.5">
          <StatusBadge status={current.status} className="mt-0.5" />
          <CloseInspector to={`/tenants/${tenantId}/destinations`} label="Close the Destination detail" />
        </div>
      </div>

      <Details className="border-b pb-3.5">
        <dt>Connector</dt>
        <dd>
          <Link to={`/connectors/${current.connector_id}`}>
            {connectorLabel(connectors.data?.items, current.connector_id)}
          </Link>
        </dd>
        <dt>Environment</dt>
        <dd>{current.environment ?? "—"}</dd>
        <dt>Destination authentication</dt>
        <dd>{current.authentication ? current.authentication.scheme : "Not configured"}</dd>
      </Details>

      <section className="flex min-w-0 flex-col gap-2">
        <h3 className="eyebrow">Configuration</h3>
        <CodeBlock value={current.configuration} />
        <p className="m-0 text-xs text-ink-secondary">
          An update replaces this object outright rather than merging fields.
        </p>
      </section>

      <WriteStatus done={notice !== ""}>{notice}</WriteStatus>
      <EditDestination key={current.updated_at} tenantId={tenantId} destination={current} onDone={setNotice} />
    </Inspector>
  );
}

function EditDestination({
  tenantId,
  destination,
  onDone,
}: {
  tenantId: string;
  destination: Destination;
  onDone: (notice: string) => void;
}) {
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  /// Both reads that can now be wrong: this Destination, and any list it appears in.
  const reread = () => {
    void queryClient.invalidateQueries({ queryKey: ["destination", tenantId, destination.id] });
    void queryClient.invalidateQueries({ queryKey: ["destinations", tenantId] });
  };
  const connector = useQuery({
    queryKey: ["connector", destination.connector_id],
    queryFn: () =>
      call(() => api.GET("/admin/connectors/{id}", { params: { path: { id: destination.connector_id } } })),
  });

  const setStatus = useMutation({
    mutationFn: (action: "activate" | "deactivate") =>
      call(() =>
        action === "activate"
          ? api.POST("/admin/tenants/{tenantId}/destinations/{id}/activate", {
              params: { path: { tenantId, id: destination.id } },
            })
          : api.POST("/admin/tenants/{tenantId}/destinations/{id}/deactivate", {
              params: { path: { tenantId, id: destination.id } },
            }),
      ),
    onSuccess: (_, action) => {
      reread();
      onDone(action === "activate" ? "Destination activated." : "Destination deactivated.");
    },
  });
  const remove = useMutation({
    mutationFn: () =>
      call(() =>
        api.DELETE("/admin/tenants/{tenantId}/destinations/{id}", {
          params: { path: { tenantId, id: destination.id } },
        }),
      ),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ["destinations", tenantId] });
      navigate(`/tenants/${tenantId}/destinations`);
    },
  });

  return (
    <div className="flex flex-col gap-4">
      <div className="flex flex-wrap items-start gap-2">
        <EditSheet label="Edit">
          {(close) =>
            connector.isPending ? (
              <p role="status" className="m-0 text-sm text-ink-secondary">
                Loading Connector contract…
              </p>
            ) : (
              <EditDestinationForm
                tenantId={tenantId}
                destination={destination}
                connector={connector.data ?? null}
                connectorError={connector.error}
                onSaved={reread}
                onClose={close}
              />
            )
          }
        </EditSheet>
        {destination.status === "active" ? (
          <ConfirmAction
            label="Deactivate"
            variant="outline"
            consequence={`Deactivating ${destination.name} is refused while Active Subscriptions deliver to it; deactivate or move those first. Deliveries already queued are not cancelled.`}
            question={`Deactivate the Destination "${destination.name}"?`}
            confirmLabel={`Deactivate ${destination.name}`}
            busy={setStatus.isPending}
            onConfirm={() => setStatus.mutate("deactivate")}
          />
        ) : (
          <Button type="button" disabled={setStatus.isPending} onClick={() => setStatus.mutate("activate")}>
            Activate
          </Button>
        )}
        <ConfirmAction
          label="Delete"
          consequence="This Destination cannot be restored. Existing deliveries continue from their snapshots and keep its name in history. Deletion is refused while any Subscription references it."
          question={`Delete the Destination "${destination.name}"?`}
          confirmLabel={`Delete ${destination.name}`}
          busy={remove.isPending}
          onConfirm={() => remove.mutate()}
        />
      </div>
      <FormError message={formError(asProblem(setStatus.error ?? remove.error))} />
    </div>
  );
}

function EditDestinationForm({
  tenantId,
  destination,
  connector,
  connectorError,
  onSaved,
  onClose,
}: {
  tenantId: string;
  destination: Destination;
  connector: Connector | null;
  connectorError: Error | null;
  onSaved: () => void;
  onClose: () => void;
}) {
  const contract = destinationContract(connector?.manifest);
  const guidedConfiguration = contract ? configurationFields(contract.configuration, destination.configuration) : null;
  const guidedAuthentication = contract ? authenticationFields(contract, destination.authentication) : null;
  const form = useForm<EditValues>({
    resolver: zodResolver(editSchema),
    defaultValues: {
      name: destination.name,
      configuration_fields: guidedConfiguration ?? {},
      raw_configuration: formatJson(destination.configuration) || "{}",
      authentication_scheme: guidedAuthentication?.scheme ?? "",
      authentication_config_fields: guidedAuthentication?.config ?? {},
      authentication_secret_ref_fields: guidedAuthentication?.secretRefs ?? {},
      raw_authentication: destination.authentication === null ? "null" : formatJson(destination.authentication),
      environment: destination.environment ?? "",
      description: destination.description ?? "",
    },
  });
  const authenticationScheme = form.watch("authentication_scheme");

  const save = useMutation({
    mutationFn: (values: EditValues) =>
      call(() =>
        api.PUT("/admin/tenants/{tenantId}/destinations/{id}", {
          params: { path: { tenantId, id: destination.id } },
          body: {
            name: values.name,
            configuration:
              contract && guidedConfiguration
                ? configurationDocument(contract.configuration, values.configuration_fields)
                : parseJson(values.raw_configuration).value,
            authentication:
              contract && guidedAuthentication
                ? authenticationDocument(
                    contract,
                    values.authentication_scheme,
                    values.authentication_config_fields,
                    values.authentication_secret_ref_fields,
                  )
                : (parseJson(values.raw_authentication).value as DestinationAuthentication | null),
            environment: optional(values.environment),
            description: optional(values.description),
          },
        }),
      ),
    onSuccess: () => {
      onSaved();
      onClose();
    },
  });
  const submit = form.handleSubmit((values) => {
    if (contract && guidedAuthentication && !contract.allowUnauthenticated && !values.authentication_scheme) {
      form.setError("authentication_scheme", { type: "required", message: "Choose an authentication method." });
      return;
    }
    const scheme = contract?.authenticationSchemes.find(
      (candidate) => candidate.scheme === values.authentication_scheme,
    );
    let invalid = false;
    for (const name of scheme?.config ?? [])
      if (!values.authentication_config_fields[name]?.trim()) {
        form.setError(`authentication_config_fields.${name}`, {
          type: "required",
          message: `Enter ${fieldLabel(name).toLowerCase()}.`,
        });
        invalid = true;
      }
    for (const name of scheme?.secretRefs ?? [])
      if (!values.authentication_secret_ref_fields[name]?.trim()) {
        form.setError(`authentication_secret_ref_fields.${name}`, {
          type: "required",
          message: "Enter a secret reference.",
        });
        invalid = true;
      }
    if (invalid) return;
    save.mutate(values, { onError: (failure) => applyProblem(form, failure, editFields) });
  });

  return (
    <Form {...form}>
      <form className="flex flex-col gap-4" aria-label={`Edit ${destination.name}`} noValidate onSubmit={submit}>
        <FormError message={formError(asProblem(connectorError))} />
        <FormError message={formError(asProblem(save.error), editFields)} />

        <TextField control={form.control} name="name" label="Name" required />
        {contract && guidedConfiguration ? (
          <Section title="Configuration" hint="Reusable endpoint and Destination-wide settings.">
            <ConfigurationFields control={form.control} fields={contract.configuration} />
          </Section>
        ) : (
          <Section
            title="Raw configuration"
            hint="This stored document cannot round-trip through the Connector's guided fields. Saving replaces it exactly."
          >
            <TextAreaField
              control={form.control}
              name="raw_configuration"
              label="Raw configuration (JSON)"
              language="json"
              className="min-h-40"
              required
            />
          </Section>
        )}
        {contract && guidedAuthentication ? (
          <AuthenticationFields
            control={form.control}
            contract={contract}
            schemeName={authenticationScheme}
            onSchemeChange={(scheme) => {
              form.setValue(
                "authentication_config_fields",
                Object.fromEntries((scheme?.config ?? []).map((name) => [name, ""])),
              );
              form.setValue(
                "authentication_secret_ref_fields",
                Object.fromEntries((scheme?.secretRefs ?? []).map((name) => [name, ""])),
              );
            }}
          />
        ) : (
          <Section
            title="Raw authentication"
            hint="This stored document cannot round-trip through the Connector's guided fields. Saving replaces it exactly."
          >
            <TextAreaField
              control={form.control}
              name="raw_authentication"
              label="Raw authentication (JSON)"
              hint="Secret reference names only; never enter secret values."
              language="json"
              className="min-h-32"
              required
            />
          </Section>
        )}
        <TextField control={form.control} name="environment" label="Environment (optional)" />
        <TextField control={form.control} name="description" label="Description (optional)" />

        <Button type="submit" className="self-start" disabled={save.isPending}>
          Save changes
        </Button>
        <WriteStatus done={save.isSuccess}>Changes saved.</WriteStatus>
      </form>
    </Form>
  );
}
