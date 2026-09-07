import { zodResolver } from "@hookform/resolvers/zod";
import { useInfiniteQuery, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { useForm } from "react-hook-form";
import { Link, NavLink, useNavigate } from "react-router";
import { z } from "zod";
import { Button } from "@/components/ui/button";
import { SelectItem } from "@/components/ui/select";
import { TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import { api } from "../api/client";
import { formError } from "../api/problem";
import { asProblem, call, nextCursor } from "../api/query";
import type { components } from "../api/schema";
import {
  appliedNote,
  ConfirmAction,
  CreateSheet,
  EditSheet,
  FilterBar,
  FormError,
  ListStatus,
  LoadMore,
  WriteStatus,
} from "../ui/controls";
import { Filter, Form, SelectField, TextAreaField } from "../ui/fields";
import { useFilterParam } from "../ui/filters";
import { applyProblem } from "../ui/formProblem";
import { formatJson, parseJson } from "../ui/json";
import {
  CloseInspector,
  Details,
  Inspector,
  InspectorPlaceholder,
  Page,
  PageHeader,
  RowHeader,
  SplitList,
  SplitView,
  TableCard,
} from "../ui/layout";
import { activeOnly, nameIn, useConnectionOptions, useTopicOptions } from "../ui/options";
import { StatusBadge } from "../ui/status";

type SourceListItem = components["schemas"]["SourceListItemDto"];
type Source = components["schemas"]["SourceDto"];

const sourceTypes = [
  { value: "event_api", label: "Event API" },
  { value: "webhook", label: "Webhook" },
  { value: "queue", label: "Queue" },
];

const createFields = ["connection_id", "topic_id", "type", "configuration"] as const;
const editFields = ["configuration"] as const;

/// A domain JSON document, authored as text: well-formedness is all the dashboard checks, and the
/// server stays the authority on whether the document is valid for this Source type.
const jsonDocument = z.string().superRefine((text, ctx) => {
  const parsed = parseJson(text);
  if (parsed.error !== undefined) ctx.addIssue({ code: z.ZodIssueCode.custom, message: parsed.error });
});

const createSchema = z.object({
  connection_id: z.string().min(1, "Choose a Connection."),
  topic_id: z.string().min(1, "Choose a Topic."),
  type: z.string().min(1, "Choose a type."),
  configuration: jsonDocument,
});

const editSchema = z.object({ configuration: jsonDocument });

type CreateValues = z.infer<typeof createSchema>;
type EditValues = z.infer<typeof editSchema>;

export function SourcesScreen({ tenantId, selectedSourceId }: { tenantId: string; selectedSourceId?: string }) {
  const connectionOptions = useConnectionOptions(tenantId);
  const topicOptions = useTopicOptions(tenantId);
  const [status, setStatus] = useFilterParam("status");
  const [type, setType] = useFilterParam("type");
  const list = useInfiniteQuery({
    queryKey: ["sources", tenantId, { status, type }],
    queryFn: ({ pageParam }) =>
      call(() =>
        api.GET("/admin/tenants/{tenantId}/sources", {
          params: {
            path: { tenantId },
            query: { status: status || undefined, type: type || undefined, after: pageParam ?? undefined, limit: 20 },
          },
        }),
      ),
    initialPageParam: null as string | null,
    getNextPageParam: nextCursor<SourceListItem>,
  });
  const sources = list.data?.pages.flatMap((page) => page.items) ?? [];

  return (
    <Page>
      <PageHeader
        title="Sources"
        action={
          <CreateSheet label="New Source" description="A Source binds one Connection to one Topic">
            {(close) => <CreateSource tenantId={tenantId} onCreated={close} />}
          </CreateSheet>
        }
      >
        A Source binds one Connection to one Topic and selects the contract its input is read as.
      </PageHeader>

      <FilterBar applied={(status ? 1 : 0) + (type ? 1 : 0)}>
        <Filter id="source-status" label="Status" value={status} onChange={setStatus}>
          <SelectItem value="active">Active</SelectItem>
          <SelectItem value="revoked">Revoked</SelectItem>
        </Filter>
        <Filter id="source-type" label="Type" value={type} onChange={setType}>
          {sourceTypes.map((option) => (
            <SelectItem key={option.value} value={option.value}>
              {option.label}
            </SelectItem>
          ))}
        </Filter>
      </FilterBar>

      <ListStatus
        busy={list.isFetching}
        loaded={list.isSuccess}
        problem={asProblem(list.error)}
        empty={sources.length === 0}
        emptyText="This Tenant has no Sources matching these filters."
      />

      <SplitView>
        <SplitList>
          {sources.length > 0 ? (
            <TableCard
              caption={`Sources, newest first${appliedNote((status ? 1 : 0) + (type ? 1 : 0))}`}
              footer={
                <LoadMore
                  noun="Source"
                  hasMore={list.hasNextPage}
                  busy={list.isFetching}
                  loaded={sources.length}
                  onLoadMore={() => void list.fetchNextPage()}
                />
              }
            >
              <TableHeader>
                <TableRow>
                  <TableHead scope="col">Connection</TableHead>
                  <TableHead scope="col">Topic</TableHead>
                  <TableHead scope="col">Type</TableHead>
                  <TableHead scope="col">Status</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {sources.map((source) => (
                  <TableRow key={source.id} className="has-[a[aria-current=page]]:bg-selected-surface">
                    <RowHeader>
                      <NavLink
                        className="font-mono text-[13px] no-underline"
                        to={`/tenants/${tenantId}/sources/${source.id}`}
                        end
                      >
                        {nameIn(connectionOptions.data?.items, source.connection_id)}
                      </NavLink>
                    </RowHeader>
                    <TableCell>
                      <Link className="font-mono text-[13px]" to={`/tenants/${tenantId}/topics/${source.topic_id}`}>
                        → {nameIn(topicOptions.data?.items, source.topic_id)}
                      </Link>
                    </TableCell>
                    <TableCell>{source.type}</TableCell>
                    <TableCell>
                      <StatusBadge status={source.status} />
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </TableCard>
          ) : null}
        </SplitList>

        {selectedSourceId ? (
          <SourceInspector key={selectedSourceId} tenantId={tenantId} sourceId={selectedSourceId} />
        ) : (
          <InspectorPlaceholder label="Source detail">
            Select a Source to read the Connection and Topic it binds together.
          </InspectorPlaceholder>
        )}
      </SplitView>
    </Page>
  );
}

function CreateSource({ tenantId, onCreated }: { tenantId: string; onCreated: () => void }) {
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const connections = useConnectionOptions(tenantId);
  const topics = useTopicOptions(tenantId);
  const optionsUnavailable = connections.isPending || topics.isPending || connections.isError || topics.isError;

  const form = useForm<CreateValues>({
    resolver: zodResolver(createSchema),
    defaultValues: { connection_id: "", topic_id: "", type: "webhook", configuration: "{}" },
  });

  const create = useMutation({
    mutationFn: (values: CreateValues) =>
      call(() =>
        api.POST("/admin/tenants/{tenantId}/sources", {
          params: { path: { tenantId } },
          body: {
            connection_id: values.connection_id,
            topic_id: values.topic_id,
            type: values.type,
            configuration: parseJson(values.configuration).value,
          },
        }),
      ),
    onSuccess: (created) => {
      void queryClient.invalidateQueries({ queryKey: ["sources", tenantId] });
      onCreated();
      if (created) navigate(`/tenants/${tenantId}/sources/${created.id}`);
    },
  });

  const submit = form.handleSubmit((values) =>
    create.mutate(values, { onError: (failure) => applyProblem(form, failure, createFields) }),
  );

  return (
    <Form {...form}>
      <form className="flex flex-col gap-4" onSubmit={submit} aria-label="Create a Source">
        <FormError message={formError(asProblem(connections.error ?? topics.error))} />
        <FormError message={formError(asProblem(create.error), createFields)} />

        <SelectField
          control={form.control}
          name="connection_id"
          label="Connection"
          hint={connections.data?.next_cursor ? "Showing the first 100 active Connections." : undefined}
          disabled={connections.isPending || connections.isError}
          required
        >
          {activeOnly(connections.data?.items).map((connection) => (
            <SelectItem key={connection.id} value={connection.id}>
              {connection.name}
            </SelectItem>
          ))}
        </SelectField>
        <SelectField
          control={form.control}
          name="topic_id"
          label="Topic"
          hint={topics.data?.next_cursor ? "Showing the first 100 active Topics." : undefined}
          disabled={topics.isPending || topics.isError}
          required
        >
          {activeOnly(topics.data?.items).map((topic) => (
            <SelectItem key={topic.id} value={topic.id}>
              {topic.name}
            </SelectItem>
          ))}
        </SelectField>
        <SelectField control={form.control} name="type" label="Type" required>
          {sourceTypes.map((option) => (
            <SelectItem key={option.value} value={option.value}>
              {option.label}
            </SelectItem>
          ))}
        </SelectField>
        <TextAreaField
          control={form.control}
          name="configuration"
          label="Configuration (JSON)"
          className="min-h-40 font-mono text-sm"
          required
        />

        <Button type="submit" className="self-start" disabled={create.isPending || optionsUnavailable}>
          Create Source
        </Button>
      </form>
    </Form>
  );
}

function SourceInspector({ tenantId, sourceId }: { tenantId: string; sourceId: string }) {
  const connectionOptions = useConnectionOptions(tenantId);
  const topicOptions = useTopicOptions(tenantId);
  const [notice, setNotice] = useState("");
  const source = useQuery({
    queryKey: ["source", tenantId, sourceId],
    queryFn: () =>
      call(() => api.GET("/admin/tenants/{tenantId}/sources/{id}", { params: { path: { tenantId, id: sourceId } } })),
  });

  const problem = asProblem(source.error);
  if (problem)
    return (
      <Inspector label="Source detail">
        <h2 className="m-0">Source</h2>
        <p role="alert">{problem.detail ?? `This Source could not be read (${problem.status}).`}</p>
      </Inspector>
    );
  if (!source.data) return <Inspector label="Source detail">Loading…</Inspector>;

  const current = source.data;
  return (
    <Inspector label="Source detail">
      <div className="flex items-start justify-between gap-3">
        <h2 className="font-mono break-all">{current.id}</h2>
        <div className="flex shrink-0 items-center gap-1.5">
          <StatusBadge status={current.status} className="mt-0.5" />
          <CloseInspector to={`/tenants/${tenantId}/sources`} label="Close the Source detail" />
        </div>
      </div>

      <Details className="border-b pb-3.5">
        <dt>Type</dt>
        <dd>{current.type}</dd>
        <dt>Connection</dt>
        <dd>
          <Link className="font-mono" to={`/tenants/${tenantId}/connections/${current.connection_id}`}>
            {nameIn(connectionOptions.data?.items, current.connection_id)}
          </Link>
        </dd>
        <dt>Topic</dt>
        <dd>
          <Link className="font-mono" to={`/tenants/${tenantId}/topics/${current.topic_id}`}>
            {nameIn(topicOptions.data?.items, current.topic_id)}
          </Link>
        </dd>
        <dt>Revoked</dt>
        <dd>{current.revoked_at ?? "Not revoked"}</dd>
      </Details>

      <WriteStatus done={notice !== ""}>{notice}</WriteStatus>
      <EditSource
        key={current.updated_at}
        tenantId={tenantId}
        source={current}
        onDone={() => setNotice("Source revoked.")}
      />
    </Inspector>
  );
}

/// The Admin API owns exactly one Source update — its configuration. Type, Connection, and Topic are
/// fixed at creation, so they are shown rather than offered as editable fields.
function EditSource({ tenantId, source, onDone }: { tenantId: string; source: Source; onDone: () => void }) {
  const queryClient = useQueryClient();
  const reread = () => {
    void queryClient.invalidateQueries({ queryKey: ["source", tenantId, source.id] });
    void queryClient.invalidateQueries({ queryKey: ["sources", tenantId] });
  };
  const form = useForm<EditValues>({
    resolver: zodResolver(editSchema),
    defaultValues: { configuration: formatJson(source.configuration) },
  });

  const save = useMutation({
    mutationFn: (values: EditValues) =>
      call(() =>
        api.PATCH("/admin/tenants/{tenantId}/sources/{id}", {
          params: { path: { tenantId, id: source.id } },
          body: { configuration: parseJson(values.configuration).value },
        }),
      ),
    onSuccess: reread,
  });

  const revoke = useMutation({
    mutationFn: () =>
      call(() =>
        api.DELETE("/admin/tenants/{tenantId}/sources/{id}", {
          params: { path: { tenantId, id: source.id } },
        }),
      ),
    onSuccess: () => {
      reread();
      onDone();
    },
  });

  return (
    <div className="flex flex-col gap-6">
      <EditSheet label="Edit this Source" description="An update replaces the configuration outright">
        {(close) => (
          <Form {...form}>
            <form
              className="flex flex-col gap-4"
              aria-label={`Edit ${source.type} Source`}
              onSubmit={form.handleSubmit((values) =>
                save.mutate(values, {
                  onSuccess: close,
                  onError: (failure) => applyProblem(form, failure, editFields),
                }),
              )}
            >
              <FormError message={formError(asProblem(save.error), editFields)} />

              <TextAreaField
                control={form.control}
                name="configuration"
                label="Configuration (JSON)"
                className="min-h-56 font-mono text-sm"
                required
              />

              <Button type="submit" className="self-start" disabled={save.isPending}>
                Save configuration
              </Button>
              <WriteStatus done={save.isSuccess}>Configuration saved.</WriteStatus>
            </form>
          </Form>
        )}
      </EditSheet>

      {source.status === "active" ? (
        <div className="flex flex-col items-start gap-2">
          <ConfirmAction
            label="Revoke Source"
            consequence="Revoking a Source stops it accepting Events. It cannot be restored, and a replacement is a new Source with a new identifier."
            question={`Revoke the ${source.type} Source ${source.id}? It stops accepting Events and cannot be restored.`}
            confirmLabel={`Revoke ${source.id}`}
            busy={revoke.isPending}
            onConfirm={() => revoke.mutate()}
          />
          <FormError message={formError(asProblem(revoke.error))} />
        </div>
      ) : null}
    </div>
  );
}
