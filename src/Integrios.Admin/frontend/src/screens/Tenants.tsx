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
import { ConfirmAction, Disclosure, FormError, ListStatus, LoadMore } from "../ui/controls";
import { Filter, Form, TextField } from "../ui/fields";
import { applyProblem } from "../ui/formProblem";
import { Details, Page, PageHeader, Panel, RowHeader, TableCard } from "../ui/layout";

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
      <PageHeader title="Tenants" />

      <Disclosure label="New Tenant">
        <CreateTenant />
      </Disclosure>

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
              {tenants.map((tenant) => (
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
        <LoadMore hasMore={list.hasNextPage} busy={list.isFetching} onLoadMore={() => void list.fetchNextPage()} />
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
  const tenant = useQuery({
    queryKey: ["tenant", tenantId],
    queryFn: () => call(() => api.GET("/admin/tenants/{id}", { params: { path: { id: tenantId } } })),
  });

  const problem = asProblem(tenant.error);
  if (problem)
    return (
      <>
        <h1>Tenant</h1>
        <p role="alert">{problem.detail ?? `This Tenant could not be read (${problem.status}).`}</p>
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

      <EditTenant key={current.updated_at} tenant={current} />
    </Page>
  );
}

function EditTenant({ tenant }: { tenant: Tenant }) {
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
    onSuccess: reread,
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
