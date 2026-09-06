import { zodResolver } from "@hookform/resolvers/zod";
import { useInfiniteQuery, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { useForm } from "react-hook-form";
import { Link, useNavigate } from "react-router";
import { z } from "zod";
import { Button } from "@/components/ui/button";
import { TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import { api } from "../api/client";
import { formError } from "../api/problem";
import { asProblem, call, nextCursor } from "../api/query";
import type { components } from "../api/schema";
import { ConfirmAction, FormError, ListStatus, LoadMore, useCreatePanel, WriteStatus } from "../ui/controls";
import { Filter, Form, TextField } from "../ui/fields";
import { useFilterParam } from "../ui/filters";
import { applyProblem } from "../ui/formProblem";
import { Details, Page, PageHeader, Panel, RowHeader, TableCard } from "../ui/layout";
import { StatusBadge } from "../ui/status";
import { Timestamp } from "../ui/time";

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

export function TenantsScreen() {
  const [status, setStatus] = useFilterParam("status");
  const create = useCreatePanel("new-tenant");
  const list = useInfiniteQuery({
    queryKey: ["tenants", { status }],
    queryFn: ({ pageParam }) =>
      call(() =>
        api.GET("/admin/tenants", {
          params: { query: { status: status || undefined, after: pageParam ?? undefined, limit: 20 } },
        }),
      ),
    initialPageParam: null as string | null,
    getNextPageParam: nextCursor<Tenant>,
  });
  const tenants = list.data?.pages.flatMap((page) => page.items) ?? [];

  return (
    <Page>
      <PageHeader title="Tenants" action={<Button {...create.triggerProps}>New Tenant</Button>} />

      <Panel {...create.panelProps} className="max-w-none">
        <CreateTenant />
      </Panel>

      <section className="flex flex-col gap-4">
        <h2>All Tenants</h2>
        <Filter id="tenant-status" label="Status" value={status} onChange={setStatus}>
          <option value="">Any status</option>
          <option value="active">Active</option>
          <option value="disabled">Disabled</option>
        </Filter>

        <ListStatus
          busy={list.isFetching}
          loaded={list.isSuccess}
          problem={asProblem(list.error)}
          empty={tenants.length === 0}
          emptyText="No Tenants match this filter."
        />
        {tenants.length > 0 ? (
          <TableCard
            caption="Tenants, newest first"
            footer={
              <LoadMore
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
                <TableHead scope="col">Status</TableHead>
                <TableHead scope="col">Environment</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {tenants.map((tenant) => (
                <TableRow key={tenant.id}>
                  <RowHeader>
                    <Link className="underline" to={`/tenants/${tenant.id}`}>
                      {tenant.name}
                    </Link>
                  </RowHeader>
                  <TableCell>{tenant.slug}</TableCell>
                  <TableCell>
                    <StatusBadge status={tenant.status} />
                  </TableCell>
                  <TableCell>{tenant.environment ?? "—"}</TableCell>
                </TableRow>
              ))}
            </TableBody>
          </TableCard>
        ) : null}
      </section>
    </Page>
  );
}

function CreateTenant() {
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
      if (created) navigate(`/tenants/${created.id}`);
    },
  });

  const submit = form.handleSubmit((values) =>
    create.mutate(values, { onError: (failure) => applyProblem(form, failure, createFields) }),
  );

  return (
    <Form {...form}>
      <Panel asChild>
        <form className="flex flex-col gap-4" onSubmit={submit}>
          <h2>Create a Tenant</h2>
          <FormError message={formError(asProblem(create.error), createFields)} />

          <TextField control={form.control} name="slug" label="Slug" required />
          <TextField control={form.control} name="name" label="Name" required />
          <TextField control={form.control} name="environment" label="Environment (optional)" />
          <TextField control={form.control} name="description" label="Description (optional)" />

          <Button type="submit" className="self-start" disabled={create.isPending}>
            Create Tenant
          </Button>
        </form>
      </Panel>
    </Form>
  );
}

export function TenantScreen({ tenantId }: { tenantId: string }) {
  const [notice, setNotice] = useState("");
  const edit = useCreatePanel("edit-tenant");
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
  // The same read the ledger uses, unscoped, so both screens report the same window.
  const activity = useQuery({
    queryKey: ["activity-summary", tenantId, {}],
    queryFn: () =>
      call(() => api.GET("/admin/tenants/{tenantId}/events/activity-summary", { params: { path: { tenantId } } })),
  });

  const problem = asProblem(tenant.error);
  if (problem)
    return (
      <>
        <h1>Overview</h1>
        <p role="alert">{problem.detail ?? `This Tenant could not be read (${problem.status}).`}</p>
      </>
    );
  if (!tenant.data) return <p>Loading…</p>;

  const current = tenant.data;
  const deadLettered = Number(activity.data?.dead_lettered_deliveries ?? 0);

  return (
    <Page>
      <PageHeader title="Overview" action={<Button {...edit.triggerProps}>Edit Tenant</Button>}>
        What is configured for {current.name}, and what currently needs an Operator.
      </PageHeader>

      {/* Absent when there is nothing to act on. A banner that is always there stops being read. */}
      {deadLettered > 0 ? (
        <div className="flex flex-wrap items-center justify-between gap-4 rounded-lg border border-danger-surface bg-danger-surface p-4 text-danger-ink">
          <div>
            <strong>
              {deadLettered} dead-lettered {deadLettered === 1 ? "Delivery" : "Deliveries"}
            </strong>
            <p className="m-0 text-sm">
              These have exhausted their retry budget. Replay is offered on each one in the Event inspector.
            </p>
          </div>
          <Button asChild variant="outline" size="sm">
            <Link className="no-underline" to={`/tenants/${tenantId}/events?delivery_status=dead_lettered`}>
              Open Events
            </Link>
          </Button>
        </div>
      ) : null}

      <Panel {...edit.panelProps} className="max-w-none">
        <EditTenant key={current.updated_at} tenant={current} onDone={() => setNotice("Tenant deactivated.")} />
      </Panel>
      <WriteStatus done={notice !== ""}>{notice}</WriteStatus>

      <section aria-label="Configured in this Tenant">
        <ul className="m-0 grid list-none grid-cols-[repeat(auto-fit,minmax(9rem,1fr))] gap-3 p-0">
          {(
            [
              ["Topics", overview.data?.topics],
              ["Connections", overview.data?.connections],
              ["Sources", overview.data?.sources],
              ["Subscriptions", overview.data?.subscriptions],
              ["Live API keys", overview.data?.live_api_keys],
            ] as const
          ).map(([label, value]) => (
            <li key={label} className="rounded-lg border bg-surface px-4 py-3">
              <span className="block font-serif text-2xl tabular-nums">{value ?? "—"}</span>
              <span className="text-sm text-ink-secondary">{label}</span>
            </li>
          ))}
        </ul>
      </section>

      <div className="grid gap-4 min-[1180px]:grid-cols-2">
        <Panel className="max-w-none">
          <h2 className="m-0 mb-4 text-lg">Tenant</h2>
          <Details>
            <dt>Name</dt>
            <dd>{current.name}</dd>
            <dt>Slug</dt>
            <dd className="font-mono text-sm">{current.slug}</dd>
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
            <dd className="font-mono text-sm break-all">{overview.data?.ingestion_endpoint ?? "—"}</dd>
          </Details>
          {current.description ? <p className="m-0 mt-4 text-ink-secondary">{current.description}</p> : null}
        </Panel>

        <Panel className="max-w-none">
          <h2 className="m-0 mb-4 text-lg">Last 60 minutes</h2>
          <Details>
            <dt>Events accepted</dt>
            <dd className="tabular-nums">{activity.data?.events_accepted ?? "—"}</dd>
            <dt>Awaiting routing</dt>
            <dd className="tabular-nums">{activity.data?.awaiting_routing ?? "—"}</dd>
            <dt>Unrouted</dt>
            <dd className="tabular-nums">{activity.data?.unrouted ?? "—"}</dd>
            <dt>Dead-lettered Deliveries</dt>
            <dd className="tabular-nums">{activity.data?.dead_lettered_deliveries ?? "—"}</dd>
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

function EditTenant({ tenant, onDone }: { tenant: Tenant; onDone: () => void }) {
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
        api.PATCH("/admin/tenants/{id}", {
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

  const deactivate = useMutation({
    mutationFn: () => call(() => api.POST("/admin/tenants/{id}/deactivate", { params: { path: { id: tenant.id } } })),
    onSuccess: () => {
      reread();
      onDone();
    },
  });

  const submit = form.handleSubmit((values) =>
    save.mutate(values, { onError: (failure) => applyProblem(form, failure, updateFields) }),
  );

  return (
    <div className="flex flex-col gap-6">
      <Form {...form}>
        <Panel asChild>
          <form className="flex flex-col gap-4" onSubmit={submit}>
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
        </Panel>
      </Form>

      {/* Deactivation is offered only where the API actually owns it; there is no invented
          reactivation to make the pair look symmetrical. */}
      {tenant.status === "active" ? (
        <div className="flex flex-col items-start gap-2">
          <ConfirmAction
            label="Deactivate Tenant"
            question={`Deactivate the Tenant "${tenant.name}" (${tenant.slug})? Its Sources stop accepting Events.`}
            confirmLabel={`Deactivate ${tenant.name}`}
            busy={deactivate.isPending}
            onConfirm={() => deactivate.mutate()}
          />
          <FormError message={formError(asProblem(deactivate.error))} />
        </div>
      ) : null}
    </div>
  );
}
