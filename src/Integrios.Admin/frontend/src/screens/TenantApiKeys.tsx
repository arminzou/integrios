import { zodResolver } from "@hookform/resolvers/zod";
import { useState } from "react";
import { useForm } from "react-hook-form";
import { Link } from "react-router";
import { z } from "zod";
import { Button } from "@/components/ui/button";
import { TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import { api } from "../api/client";
import { formError } from "../api/problem";
import type { components } from "../api/schema";
import { ConfirmAction, Disclosure, FormError, ListStatus, LoadMore } from "../ui/controls";
import { Filter, Form, TextField } from "../ui/fields";
import { applyProblem } from "../ui/formProblem";
import { Page, PageHeader, Panel, RowHeader, TableCard } from "../ui/layout";
import { useAction } from "../ui/useAction";
import { useCursorList } from "../ui/useCursorList";

type TenantApiKeyListItem = components["schemas"]["TenantApiKeyListItemDto"];
type CreatedKey = components["schemas"]["CreateTenantApiKeyResult"];

const createFields = ["name", "description", "expires_at"] as const;

const createSchema = z.object({
  name: z.string().trim().min(1, "Enter a name."),
  description: z.string(),
  expires_at: z.string(),
});

type CreateValues = z.infer<typeof createSchema>;

export function TenantApiKeysScreen({ tenantId }: { tenantId: string }) {
  const [state, setState] = useState("");
  const list = useCursorList<TenantApiKeyListItem>(
    (after) =>
      api.GET("/admin/tenants/{tenantId}/tenant-api-keys", {
        params: { path: { tenantId }, query: { state: state || undefined, after: after ?? undefined, limit: 20 } },
      }),
    `tenant-api-keys|${tenantId}|${state}`,
  );

  return (
    <Page>
      <PageHeader title="Tenant API keys">
        In{" "}
        <Link className="underline" to={`/tenants/${tenantId}`}>
          this Tenant
        </Link>
        .
      </PageHeader>

      <Disclosure label="New Tenant API key">
        <CreateTenantApiKey tenantId={tenantId} onCreated={list.reload} />
      </Disclosure>

      <section className="flex flex-col gap-4">
        <h2>All Tenant API keys</h2>
        <Filter id="tenant-api-key-state" label="State" value={state} onChange={setState}>
          <option value="">Any state</option>
          <option value="active">Active</option>
          <option value="expired">Expired</option>
          <option value="revoked">Revoked</option>
        </Filter>

        <ListStatus
          busy={list.busy}
          loaded={list.loaded}
          problem={list.problem}
          empty={list.items.length === 0}
          emptyText="This Tenant has no API keys matching this filter."
        />
        {list.items.length > 0 ? (
          <TableCard caption="Tenant API keys, newest first">
            <TableHeader>
              <TableRow>
                <TableHead scope="col">Name</TableHead>
                <TableHead scope="col">Prefix</TableHead>
                <TableHead scope="col">State</TableHead>
                <TableHead scope="col">Expires</TableHead>
                <TableHead scope="col">Last used</TableHead>
                <TableHead scope="col">Action</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {list.items.map((key) => (
                <TableRow key={key.id}>
                  <RowHeader>{key.name}</RowHeader>
                  {/* Only the prefix is ever stored or shown. The key itself exists once, at creation. */}
                  <TableCell className="font-mono text-sm">{key.key_prefix}</TableCell>
                  <TableCell>{key.state}</TableCell>
                  <TableCell>{key.expires_at ?? "Never"}</TableCell>
                  <TableCell>{key.last_used_at ?? "Never used"}</TableCell>
                  <TableCell>
                    <RevokeTenantApiKey tenantId={tenantId} apiKey={key} onRevoked={list.reload} />
                  </TableCell>
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

function RevokeTenantApiKey({
  tenantId,
  apiKey,
  onRevoked,
}: {
  tenantId: string;
  apiKey: TenantApiKeyListItem;
  onRevoked: () => void;
}) {
  const { busy, problem, run } = useAction();

  if (apiKey.state === "revoked") return <span>Revoked</span>;

  return (
    <div className="flex flex-col items-start gap-2">
      <ConfirmAction
        label="Revoke"
        question={`Revoke the Tenant API key "${apiKey.name}" (${apiKey.key_prefix})? Callers using it stop being authenticated immediately.`}
        confirmLabel={`Revoke ${apiKey.name}`}
        busy={busy}
        onConfirm={() =>
          void run(
            () =>
              api.POST("/admin/tenants/{tenantId}/tenant-api-keys/{id}/revoke", {
                params: { path: { tenantId, id: apiKey.id } },
              }),
            onRevoked,
          )
        }
      />
      <FormError message={formError(problem)} />
    </div>
  );
}

function CreateTenantApiKey({ tenantId, onCreated }: { tenantId: string; onCreated: () => void }) {
  const [created, setCreated] = useState<CreatedKey | null>(null);
  const { busy, problem, run } = useAction();
  const form = useForm<CreateValues>({
    resolver: zodResolver(createSchema),
    defaultValues: { name: "", description: "", expires_at: "" },
  });

  const submit = form.handleSubmit(async (values) => {
    const failure = await run(
      () =>
        api.POST("/admin/tenants/{tenantId}/tenant-api-keys", {
          params: { path: { tenantId } },
          body: {
            name: values.name,
            description: values.description.trim() || null,
            // A local datetime-local value carries no offset, so it is sent as an instant the
            // server can read unambiguously rather than as the browser's own wall clock.
            expires_at: values.expires_at ? new Date(values.expires_at).toISOString() : null,
          },
        }),
      (result) => {
        form.reset();
        setCreated(result ?? null);
        onCreated();
      },
    );
    if (failure) applyProblem(form, failure, createFields);
  });

  return (
    <div className="flex flex-col gap-4">
      <Form {...form}>
        <Panel asChild>
          <form className="flex flex-col gap-4" onSubmit={submit}>
            <h2>Create a Tenant API key</h2>
            <FormError message={formError(problem, createFields)} />

            <TextField control={form.control} name="name" label="Name" required />
            <TextField control={form.control} name="description" label="Description (optional)" />
            <TextField
              control={form.control}
              name="expires_at"
              label="Expires (optional)"
              hint="Leave empty for a key that does not expire."
              type="datetime-local"
            />

            <Button type="submit" className="self-start" disabled={busy}>
              Create Tenant API key
            </Button>
          </form>
        </Panel>
      </Form>

      {/* The token exists in this response and nowhere else — the server stores only its hash, so it
          is shown once, here, and is gone as soon as this panel is dismissed. */}
      {created ? (
        <Panel asChild aria-label={`New Tenant API key ${created.tenant_api_key.name}`}>
          <section className="flex flex-col gap-3">
            <h3>Copy the key for {created.tenant_api_key.name} now</h3>
            <p role="status" className="m-0 text-ink-secondary">
              This key is shown once. It cannot be read again after you dismiss this message.
            </p>
            <output className="rounded-md border bg-surface-quiet px-3 py-2 font-mono text-sm break-all">
              {created.token}
            </output>
            <Button type="button" variant="outline" className="self-start" onClick={() => setCreated(null)}>
              I have copied the key
            </Button>
          </section>
        </Panel>
      ) : null}
    </div>
  );
}
