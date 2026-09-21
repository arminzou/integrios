import { zodResolver } from "@hookform/resolvers/zod";
import { useInfiniteQuery, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Fragment, useEffect, useState } from "react";
import { useForm, useWatch } from "react-hook-form";
import { Link, useNavigate } from "react-router";
import { z } from "zod";
import { Button } from "@/components/ui/button";
import { SelectItem } from "@/components/ui/select";
import { TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import { api } from "../api/client";
import { formError } from "../api/problem";
import { asProblem, call, nextCursor } from "../api/query";
import type { components } from "../api/schema";
import { dnsLabel } from "../identifiers";
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
  SheetButton,
  WriteStatus,
} from "../ui/controls";
import { Filter, FilterSearch, Form, TextField } from "../ui/fields";
import { useListFilters } from "../ui/filters";
import { applyProblem } from "../ui/formProblem";
import { Details, Page, PageHeader, Panel, RowHeader, TableCard } from "../ui/layout";
import { monoInput } from "../ui/mono";
import { environmentsIn, useTenantOptions } from "../ui/options";
import { StatusBadge } from "../ui/status";
import { since, Timestamp } from "../ui/time";
import { activityOutcomes, outcomeTotals, useEventActivity } from "./EventActivity";
import { backlogs, useEventBacklog } from "./Events";

type Tenant = components["schemas"]["TenantDto"];

const updateFields = ["name", "environment", "description"] as const;
const createFields = ["slug", ...updateFields] as const;

const updateSchema = z.object({
  name: z.string().trim().min(1, "Enter a name."),
  environment: z.string(),
  description: z.string(),
});

/// The slug is the Tenant's stable identity in the API and is chosen once, at creation; the update
/// form does not offer it because the Admin API does not accept it.
const createSchema = updateSchema.extend({
  slug: z.string().trim().min(1, "Enter a slug."),
});

type UpdateValues = z.infer<typeof updateSchema>;
type CreateValues = z.infer<typeof createSchema>;

const optional = (text: string) => text.trim() || null;

const tenantFilters = ["name", "environment", "status"] as const;

export function TenantsScreen() {
  const filters = useListFilters(tenantFilters);
  const { status, name, environment } = filters.values;
  const applied = filters.applied;
  const tenantOptions = useTenantOptions();
  // A URL value outside these stays visible regardless.
  const environments = environmentsIn(tenantOptions.data?.items);
  const list = useInfiniteQuery({
    queryKey: ["tenants", { status, name, environment }],
    queryFn: ({ pageParam }) =>
      call(() =>
        api.GET("/admin/tenants", {
          params: {
            query: {
              status: status || undefined,
              name: name || undefined,
              environment: environment || undefined,
              after: pageParam ?? undefined,
              limit: 20,
            },
          },
        }),
      ),
    initialPageParam: null as string | null,
    getNextPageParam: nextCursor<Tenant>,
  });
  const tenants = list.data?.pages.flatMap((page) => page.items) ?? [];
  // Whether there is a list to narrow yet. Until the read answers, neither the filter bar nor the
  // header's create action is rendered: an empty scope answers with the card that replaces the
  // table, carrying the action itself, and a screen that guessed first would retract them.
  const narrowing = narrowable(list.isSuccess, tenants.length, applied);
  const [creating, setCreating] = useState(false);
  const create = <SheetButton label="New Tenant" expanded={creating} onOpen={() => setCreating(true)} />;

  return (
    <Page>
      <PageHeader title="Tenants" action={narrowing ? create : undefined}>
        Every Tenant in this deployment. A Tenant is an ownership and isolation boundary, not a user.
      </PageHeader>

      {narrowing ? (
        <FilterBar applied={applied} onClear={filters.clear}>
          <FilterSearch
            id="tenant-name"
            label="Name or slug"
            placeholder="Name or slug contains…"
            value={name}
            onChange={(value) => filters.set("name", value)}
          />
          <Filter
            id="tenant-environment"
            label="Environment"
            value={environment}
            onChange={(value) => filters.set("environment", value)}
            hint={tenantOptions.data?.next_cursor ? "Showing environments from the first 100 Tenants." : undefined}
          >
            {environments.map((option) => (
              <SelectItem key={option} value={option}>
                {option}
              </SelectItem>
            ))}
          </Filter>
          <Filter id="tenant-status" label="Status" value={status} onChange={(value) => filters.set("status", value)}>
            <SelectItem value="active">Active</SelectItem>
            <SelectItem value="inactive">Inactive</SelectItem>
          </Filter>
        </FilterBar>
      ) : null}

      <ListStatus
        busy={list.isFetching}
        loaded={list.isSuccess}
        problem={asProblem(list.error)}
        empty={tenants.length === 0}
        applied={applied}
        noun="Tenants"
        emptyText="An ownership and isolation boundary, not a user. Everything else in this deployment is authored inside one."
        action={create}
      />
      {tenants.length > 0 ? (
        <TableCard
          caption={`Tenants, newest first${appliedNote(applied)}`}
          footer={
            <LoadMore
              noun="Tenant"
              hasMore={list.hasNextPage}
              busy={list.isFetching}
              loaded={tenants.length}
              onLoadMore={() => void list.fetchNextPage()}
            />
          }
        >
          <TableHeader>
            <TableRow>
              <TableHead scope="col">Name</TableHead>
              <TableHead scope="col">Slug</TableHead>
              <TableHead scope="col">Environment</TableHead>
              <TableHead scope="col">Status</TableHead>
              <TableHead scope="col">Description</TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {tenants.map((tenant) => (
              <TableRow key={tenant.id}>
                <RowHeader>
                  <Link className="no-underline" to={`/tenants/${tenant.id}`}>
                    {tenant.name}
                  </Link>
                </RowHeader>
                <TableCell className="font-mono whitespace-nowrap">{tenant.slug}</TableCell>
                <TableCell>{tenant.environment ?? "—"}</TableCell>
                <TableCell>
                  <StatusBadge status={tenant.status} />
                </TableCell>
                <TableCell className="text-ink-secondary">{tenant.description ?? "—"}</TableCell>
              </TableRow>
            ))}
          </TableBody>
        </TableCard>
      ) : null}

      <CreateSheet
        label="New Tenant"
        description="An ownership and isolation boundary, not a user"
        open={creating}
        onOpenChange={setCreating}
      >
        {(close) => <CreateTenant onCreated={close} />}
      </CreateSheet>
    </Page>
  );
}

function CreateTenant({ onCreated }: { onCreated: () => void }) {
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const form = useForm<CreateValues>({
    resolver: zodResolver(createSchema),
    defaultValues: { slug: "", name: "", environment: "", description: "" },
  });

  const create = useMutation({
    mutationFn: (values: CreateValues) =>
      call(() =>
        api.POST("/admin/tenants", {
          body: {
            slug: values.slug,
            name: values.name,
            environment: optional(values.environment),
            description: optional(values.description),
          },
        }),
      ),
    onSuccess: (created) => {
      form.reset();
      void queryClient.invalidateQueries({ queryKey: ["tenants"] });
      onCreated();
      if (created) navigate(`/tenants/${created.id}`);
    },
  });

  const submit = form.handleSubmit((values) =>
    create.mutate(values, { onError: (failure) => applyProblem(form, failure, createFields) }),
  );

  // The slug follows the name until an Operator writes one of their own, so the common case is not a
  // transliteration done by hand. Authorship is recorded from their own keystroke rather than read
  // off the form's dirty state, which is recomputed against the defaults whenever a field returns to
  // one — that would freeze a slug this form wrote the moment the name was cleared.
  const [slugAuthored, setSlugAuthored] = useState(false);
  const authoredName = useWatch({ control: form.control, name: "name" });
  useEffect(() => {
    if (slugAuthored) return;
    form.setValue("slug", dnsLabel(authoredName));
  }, [slugAuthored, authoredName, form]);

  return (
    <Form {...form}>
      <form className="flex flex-col gap-4" noValidate onSubmit={submit} aria-label="Create a Tenant">
        <FormError message={formError(asProblem(create.error), createFields)} />

        {/* The label first, the key second: an Operator knows what they are calling this Tenant
            before they know what to call it in a secret path, and the list puts the two in the
            same order. */}
        <TextField control={form.control} name="name" label="Name" required />
        {/* Chosen once: the Admin API does not accept a changed slug, and it is where this Tenant's
            secrets are looked up, so a later correction would move them out from under it. */}
        <TextField
          control={form.control}
          name="slug"
          label="Slug"
          hint="Chosen once, and never changed. It names this Tenant's secret paths."
          onChange={() => setSlugAuthored(true)}
          className={monoInput}
          required
        />

        <TextField control={form.control} name="environment" label="Environment (optional)" />
        <TextField control={form.control} name="description" label="Description (optional)" />

        <Button type="submit" className="self-start" disabled={create.isPending}>
          Create Tenant
        </Button>
      </form>
    </Form>
  );
}

export function TenantScreen({ tenantId }: { tenantId: string }) {
  const [notice, setNotice] = useState("");
  const tenant = useQuery({
    queryKey: ["tenant", tenantId],
    queryFn: () => call(() => api.GET("/admin/tenants/{id}", { params: { path: { id: tenantId } } })),
  });
  // What this Tenant has configured. Counts only, and only configuration: the ledger is a cursor
  // list with no total, and a count of Events here would make the two disagree.
  const overview = useQuery({
    queryKey: ["tenant-overview", tenantId],
    queryFn: () => call(() => api.GET("/admin/tenants/{id}/overview", { params: { path: { id: tenantId } } })),
  });
  // The Events chart's own 1-hour read, so both screens name and count the same four outcomes.
  const activity = useEventActivity(tenantId, "1h");
  const lastHour = activity.data ? outcomeTotals(activity.data.buckets) : null;

  const problem = asProblem(tenant.error);
  if (problem)
    return (
      <>
        <h1>Overview</h1>
        <ReadError problem={problem} what="This Tenant" back={{ to: "/tenants", label: "Go to Tenants" }} />
      </>
    );
  if (!tenant.data) return <p>Loading…</p>;

  const current = tenant.data;

  return (
    <Page>
      <PageHeader
        title="Overview"
        action={
          <div className="flex flex-wrap items-start justify-end gap-2">
            <EditSheet label="Edit">
              {(close) => <EditTenant key={current.updated_at} tenant={current} onSaved={close} />}
            </EditSheet>
            <TenantLifecycle tenant={current} onDone={(notice) => setNotice(notice)} />
          </div>
        }
      >
        What is configured for {current.name}, and what currently needs an Operator.
      </PageHeader>

      <NeedsAttention tenantId={tenantId} />

      <WriteStatus done={notice !== ""}>{notice}</WriteStatus>

      <section aria-label="Configured in this Tenant">
        <ul className="m-0 grid list-none grid-cols-[repeat(auto-fit,minmax(9.375rem,1fr))] gap-2.5 p-0">
          {(
            [
              ["Topics", overview.data?.topics, `/tenants/${tenantId}/topics`],
              ["Destinations", overview.data?.destinations, `/tenants/${tenantId}/destinations`],
              ["Sources", overview.data?.sources, `/tenants/${tenantId}/sources`],
              ["Subscriptions", overview.data?.subscriptions, `/tenants/${tenantId}/subscriptions`],
              ["Live API keys", overview.data?.live_api_keys, `/tenants/${tenantId}/tenant-api-keys`],
            ] as const
          ).map(([label, value, to]) => (
            <li key={label}>
              <Link
                to={to}
                className="block rounded-lg border bg-surface px-3.5 py-3 no-underline hover:bg-hover-surface focus-visible:outline-none focus-visible:ring-[3px] focus-visible:ring-ring/50"
              >
                <span className="block font-serif text-2xl leading-tight tabular-nums">{value ?? "—"}</span>
                <span className="text-[13px] text-ink-secondary">{label}</span>
              </Link>
            </li>
          ))}
        </ul>
      </section>

      <div className="grid gap-4 min-[1180px]:grid-cols-[minmax(0,1fr)_25rem]">
        <Panel className="max-w-none p-4">
          <h2 className="mb-3.5">Tenant</h2>
          <Details>
            <dt>Name</dt>
            <dd>{current.name}</dd>
            <dt>Slug</dt>
            <dd className="font-mono">{current.slug}</dd>
            <dt>Environment</dt>
            <dd>{current.environment ?? "—"}</dd>
            <dt>Status</dt>
            <dd>
              <StatusBadge status={current.status} />
            </dd>
            <dt>Created</dt>
            <dd>
              <Timestamp value={current.created_at} />
            </dd>
            <dt>Ingestion endpoint</dt>
            <dd className="font-mono break-all">{overview.data?.ingestion_endpoint ?? "—"}</dd>
          </Details>
          {current.description ? <p className="m-0 mt-4 text-ink-secondary">{current.description}</p> : null}
        </Panel>

        <Panel className="max-w-none p-4">
          <h2 className="mb-3.5">Last 60 minutes</h2>
          <Details>
            <dt>Events accepted</dt>
            <dd className="tabular-nums">
              {lastHour ? Object.values(lastHour).reduce((sum, value) => sum + value, 0) : "—"}
            </dd>
            {activityOutcomes.map(({ key, label }) => (
              <Fragment key={key}>
                <dt>{label}</dt>
                <dd className="tabular-nums">{lastHour?.[key] ?? "—"}</dd>
              </Fragment>
            ))}
          </Details>
          <Button asChild variant="outline" size="sm" className="mt-4 self-start">
            <Link className="no-underline" to={`/tenants/${tenantId}/events`}>
              Open the ledger
            </Link>
          </Button>
        </Panel>
      </div>
    </Page>
  );
}

/// The same backlog read as the Events screen's Right now, listing only what is waiting. Every
/// backlog is shown however old, with no threshold and no suppression: a count the Operator cannot
/// see is work nobody is doing.
function NeedsAttention({ tenantId }: { tenantId: string }) {
  const backlog = useEventBacklog(tenantId);
  const problem = asProblem(backlog.error);
  if (problem) return <ReadError problem={problem} what="The current backlog" />;
  if (!backlog.data) return null;
  const data = backlog.data;
  const waiting = backlogs.filter((item) => Number(data[item.key].count) > 0);

  return (
    <section aria-labelledby="needs-attention" className="flex flex-col gap-2.5">
      <h2 id="needs-attention" className="m-0">
        Needs attention
      </h2>
      {waiting.length === 0 ? (
        <p className="m-0 text-[13px] text-ink-secondary">Nothing is waiting on an Operator.</p>
      ) : (
        <ul className="m-0 flex list-none flex-col gap-2 p-0">
          {waiting.map((item) => {
            const { count, oldest_at } = data[item.key];
            return (
              <li
                key={item.key}
                className="flex flex-wrap items-center justify-between gap-3 rounded-lg bg-danger-surface px-4 py-3 text-danger-ink"
              >
                <div>
                  <strong className="tabular-nums">
                    {count} {item.noun}
                  </strong>
                  {oldest_at ? <p className="m-0 text-sm">Oldest {since(oldest_at)}</p> : null}
                </div>
                <Button asChild variant="outline" size="sm">
                  <Link
                    className="no-underline"
                    to={`/tenants/${tenantId}/events?${item.query}`}
                    aria-label={`Open ${item.noun} in Events`}
                  >
                    Open in Events
                  </Link>
                </Button>
              </li>
            );
          })}
        </ul>
      )}
    </section>
  );
}

function EditTenant({ tenant, onSaved }: { tenant: Tenant; onSaved: () => void }) {
  const queryClient = useQueryClient();
  const reread = () => {
    void queryClient.invalidateQueries({ queryKey: ["tenant", tenant.id] });
    void queryClient.invalidateQueries({ queryKey: ["tenants"] });
  };
  const form = useForm<UpdateValues>({
    resolver: zodResolver(updateSchema),
    defaultValues: {
      name: tenant.name,
      environment: tenant.environment ?? "",
      description: tenant.description ?? "",
    },
  });

  const save = useMutation({
    mutationFn: (values: UpdateValues) =>
      call(() =>
        api.PUT("/admin/tenants/{id}", {
          params: { path: { id: tenant.id } },
          body: {
            name: values.name,
            environment: optional(values.environment),
            description: optional(values.description),
          },
        }),
      ),
    onSuccess: reread,
  });

  return (
    <div className="flex flex-col gap-6">
      <Form {...form}>
        <form
          className="flex flex-col gap-4"
          noValidate
          onSubmit={form.handleSubmit((values) =>
            save.mutate(values, {
              onSuccess: onSaved,
              onError: (failure) => applyProblem(form, failure, updateFields),
            }),
          )}
        >
          <h2>Edit {tenant.name}</h2>
          <FormError message={formError(asProblem(save.error), updateFields)} />

          <TextField control={form.control} name="name" label="Name" required />
          <TextField control={form.control} name="environment" label="Environment (optional)" />
          <TextField control={form.control} name="description" label="Description (optional)" />

          <Button type="submit" className="self-start" disabled={save.isPending}>
            Save changes
          </Button>
          <WriteStatus done={save.isSuccess}>Changes saved.</WriteStatus>
        </form>
      </Form>
    </div>
  );
}

/// Tenant activation is reversible and non-cascading: deactivating a Tenant fences all of its intake
/// without changing its Sources, Destinations, or Subscriptions, and activating resumes exactly what
/// was there. It sits on the screen rather than inside the edit sheet, because it is not part of
/// editing and a form is not a thing to scroll past to reach it.
function TenantLifecycle({ tenant, onDone }: { tenant: Tenant; onDone: (notice: string) => void }) {
  const queryClient = useQueryClient();
  const setStatus = useMutation({
    mutationFn: (action: "activate" | "deactivate") =>
      call(() =>
        action === "activate"
          ? api.POST("/admin/tenants/{id}/activate", { params: { path: { id: tenant.id } } })
          : api.POST("/admin/tenants/{id}/deactivate", { params: { path: { id: tenant.id } } }),
      ),
    onSuccess: (_, action) => {
      onDone(action === "activate" ? "Tenant activated." : "Tenant deactivated.");
      return queryClient.invalidateQueries({ queryKey: ["tenant", tenant.id] });
    },
  });

  return (
    <div className="flex flex-col items-start gap-2">
      {tenant.status === "active" ? (
        <ConfirmAction
          label="Deactivate"
          question={`Deactivate the Tenant "${tenant.name}" (${tenant.slug})? Its Sources stop accepting Events.`}
          confirmLabel={`Deactivate ${tenant.name}`}
          busy={setStatus.isPending}
          onConfirm={() => setStatus.mutate("deactivate")}
        />
      ) : (
        <Button type="button" disabled={setStatus.isPending} onClick={() => setStatus.mutate("activate")}>
          Activate
        </Button>
      )}
      <FormError message={formError(asProblem(setStatus.error))} />
    </div>
  );
}
