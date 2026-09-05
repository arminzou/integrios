import { zodResolver } from "@hookform/resolvers/zod";
import { useState } from "react";
import { useForm } from "react-hook-form";
import { Link, useNavigate } from "react-router";
import { z } from "zod";
import { Button } from "@/components/ui/button";
import { TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import { api } from "../api/client";
import { formError } from "../api/problem";
import type { components } from "../api/schema";
import { ConfirmAction, Disclosure, FormError, ListStatus, LoadMore } from "../ui/controls";
import { Filter, Form, TextField } from "../ui/fields";
import { applyProblem } from "../ui/formProblem";
import { Details, Page, PageHeader, Panel, RowHeader, TableCard } from "../ui/layout";
import { useAction } from "../ui/useAction";
import { useCursorList } from "../ui/useCursorList";
import { useResource } from "../ui/useResource";

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
  const [status, setStatus] = useState("");
  const list = useCursorList<Tenant>(
    (after) =>
      api.GET("/admin/tenants", {
        params: { query: { status: status || undefined, after: after ?? undefined, limit: 20 } },
      }),
    `tenants|${status}`,
  );

  return (
    <Page>
      <PageHeader title="Tenants" />

      <Disclosure label="New Tenant">
        <CreateTenant onCreated={list.reload} />
      </Disclosure>

      <section className="flex flex-col gap-4">
        <h2>All Tenants</h2>
        <Filter id="tenant-status" label="Status" value={status} onChange={setStatus}>
          <option value="">Any status</option>
          <option value="active">Active</option>
          <option value="disabled">Disabled</option>
        </Filter>

        <ListStatus
          busy={list.busy}
          loaded={list.loaded}
          problem={list.problem}
          empty={list.items.length === 0}
          emptyText="No Tenants match this filter."
        />
        {list.items.length > 0 ? (
          <TableCard caption="Tenants, newest first">
            <TableHeader>
              <TableRow>
                <TableHead scope="col">Name</TableHead>
                <TableHead scope="col">Slug</TableHead>
                <TableHead scope="col">Status</TableHead>
                <TableHead scope="col">Environment</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {list.items.map((tenant) => (
                <TableRow key={tenant.id}>
                  <RowHeader>
                    <Link className="underline" to={`/tenants/${tenant.id}`}>
                      {tenant.name}
                    </Link>
                  </RowHeader>
                  <TableCell>{tenant.slug}</TableCell>
                  <TableCell>{tenant.status}</TableCell>
                  <TableCell>{tenant.environment ?? "—"}</TableCell>
                </TableRow>
              ))}
            </TableBody>
          </TableCard>
        ) : null}
        <LoadMore cursor={list.cursor} busy={list.busy} onLoadMore={list.loadMore} />
      </section>
    </Page>
  );
}

function CreateTenant({ onCreated }: { onCreated: () => void }) {
  const navigate = useNavigate();
  const { busy, problem, run } = useAction();
  const form = useForm<CreateValues>({
    resolver: zodResolver(createSchema),
    defaultValues: { slug: "", name: "", environment: "", description: "" },
  });

  const submit = form.handleSubmit(async (values) => {
    const failure = await run(
      () =>
        api.POST("/admin/tenants", {
          body: {
            slug: values.slug,
            name: values.name,
            environment: optional(values.environment),
            description: optional(values.description),
          },
        }),
      (created) => {
        form.reset();
        onCreated();
        if (created) navigate(`/tenants/${created.id}`);
      },
    );
    if (failure) applyProblem(form, failure, createFields);
  });

  return (
    <Form {...form}>
      <Panel asChild>
        <form className="flex flex-col gap-4" onSubmit={submit}>
          <h2>Create a Tenant</h2>
          <FormError message={formError(problem, createFields)} />

          <TextField control={form.control} name="slug" label="Slug" required />
          <TextField control={form.control} name="name" label="Name" required />
          <TextField control={form.control} name="environment" label="Environment (optional)" />
          <TextField control={form.control} name="description" label="Description (optional)" />

          <Button type="submit" className="self-start" disabled={busy}>
            Create Tenant
          </Button>
        </form>
      </Panel>
    </Form>
  );
}

export function TenantScreen({ tenantId }: { tenantId: string }) {
  const tenant = useResource<Tenant>(
    () => api.GET("/admin/tenants/{id}", { params: { path: { id: tenantId } } }),
    tenantId,
  );

  if (tenant.problem)
    return (
      <>
        <h1>Tenant</h1>
        <p role="alert">{tenant.problem.detail ?? `This Tenant could not be read (${tenant.problem.status}).`}</p>
      </>
    );
  if (!tenant.data) return <p>Loading…</p>;

  const current = tenant.data;
  return (
    <Page>
      <PageHeader title={current.name} />

      <Panel>
        <Details>
          <dt>Slug</dt>
          <dd>{current.slug}</dd>
          <dt>Status</dt>
          <dd>{current.status}</dd>
          <dt>Environment</dt>
          <dd>{current.environment ?? "—"}</dd>
          <dt>Description</dt>
          <dd>{current.description ?? "—"}</dd>
        </Details>
      </Panel>

      <section className="flex flex-col gap-3">
        <h2>Capabilities in {current.name}</h2>
        <ul className="m-0 flex list-none flex-wrap gap-2 p-0">
          {[
            ["Connections", "connections"],
            ["Topics and Subscriptions", "topics"],
            ["Sources", "sources"],
            ["Tenant API keys", "tenant-api-keys"],
            ["Events and Deliveries", "events"],
          ].map(([label, segment]) => (
            <li key={segment}>
              <Link
                className="inline-flex rounded-md border bg-surface px-3 py-2 text-sm no-underline hover:bg-hover-surface"
                to={`/tenants/${tenantId}/${segment}`}
              >
                {label}
              </Link>
            </li>
          ))}
        </ul>
      </section>

      <EditTenant key={current.updated_at} tenant={current} onSaved={tenant.reload} />
    </Page>
  );
}

function EditTenant({ tenant, onSaved }: { tenant: Tenant; onSaved: () => void }) {
  const { busy, problem, run } = useAction();
  const form = useForm<UpdateValues>({
    resolver: zodResolver(updateSchema),
    defaultValues: {
      name: tenant.name,
      environment: tenant.environment ?? "",
      description: tenant.description ?? "",
    },
  });

  const submit = form.handleSubmit(async (values) => {
    const failure = await run(
      () =>
        api.PATCH("/admin/tenants/{id}", {
          params: { path: { id: tenant.id } },
          body: {
            name: values.name,
            environment: optional(values.environment),
            description: optional(values.description),
          },
        }),
      onSaved,
    );
    if (failure) applyProblem(form, failure, updateFields);
  });

  return (
    <div className="flex flex-col gap-6">
      <Form {...form}>
        <Panel asChild>
          <form className="flex flex-col gap-4" onSubmit={submit}>
            <h2>Edit {tenant.name}</h2>
            <FormError message={formError(problem, updateFields)} />

            <TextField control={form.control} name="name" label="Name" required />
            <TextField control={form.control} name="environment" label="Environment (optional)" />
            <TextField control={form.control} name="description" label="Description (optional)" />

            <Button type="submit" className="self-start" disabled={busy}>
              Save changes
            </Button>
          </form>
        </Panel>
      </Form>

      {/* Deactivation is offered only where the API actually owns it; there is no invented
          reactivation to make the pair look symmetrical. */}
      {tenant.status === "active" ? (
        <ConfirmAction
          label="Deactivate Tenant"
          question={`Deactivate the Tenant "${tenant.name}" (${tenant.slug})? Its Sources stop accepting Events.`}
          confirmLabel={`Deactivate ${tenant.name}`}
          busy={busy}
          onConfirm={() =>
            void run(() => api.POST("/admin/tenants/{id}/deactivate", { params: { path: { id: tenant.id } } }), onSaved)
          }
        />
      ) : null}
    </div>
  );
}
