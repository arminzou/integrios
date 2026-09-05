import { useState } from "react";
import { api } from "../api/client";
import { fieldError, formError } from "../api/problem";
import type { components } from "../api/schema";
import { navigate } from "../routes";
import { ConfirmAction, Disclosure, Field, FormError, fieldProps, Link, ListStatus, LoadMore } from "../ui/controls";
import { useAction } from "../ui/useAction";
import { useCursorList } from "../ui/useCursorList";
import { useResource } from "../ui/useResource";
import { SubscriptionsSection } from "./Subscriptions";

type Topic = components["schemas"]["AdminTopicResponse"];

const writeFields = ["name", "description"];

export function TopicsScreen({ tenantId }: { tenantId: string }) {
  const [status, setStatus] = useState("");
  const list = useCursorList<Topic>(
    (after) =>
      api.GET("/admin/tenants/{tenantId}/topics", {
        params: { path: { tenantId }, query: { status: status || undefined, after: after ?? undefined, limit: 20 } },
      }),
    `topics|${tenantId}|${status}`,
  );

  return (
    <>
      <h1>Topics</h1>
      <p>
        In <Link to={`/tenants/${tenantId}`}>this Tenant</Link>.
      </p>

      <Disclosure label="New Topic">
        <CreateTopic tenantId={tenantId} onCreated={list.reload} />
      </Disclosure>

      <h2>All Topics</h2>
      <Field id="topic-status" label="Status">
        <select {...fieldProps("topic-status")} value={status} onChange={(event) => setStatus(event.target.value)}>
          <option value="">Any status</option>
          <option value="active">Active</option>
          <option value="disabled">Disabled</option>
        </select>
      </Field>

      <ListStatus
        busy={list.busy}
        loaded={list.loaded}
        problem={list.problem}
        empty={list.items.length === 0}
        emptyText="This Tenant has no Topics matching this filter."
      />
      {list.items.length > 0 ? (
        <div className="table-card">
          <table>
            <caption>Topics, newest first</caption>
            <thead>
              <tr>
                <th scope="col">Name</th>
                <th scope="col">Status</th>
                <th scope="col">Description</th>
              </tr>
            </thead>
            <tbody>
              {list.items.map((topic) => (
                <tr key={topic.id}>
                  <th scope="row">
                    <Link to={`/tenants/${tenantId}/topics/${topic.id}`}>{topic.name}</Link>
                  </th>
                  <td>{topic.status}</td>
                  <td>{topic.description ?? "—"}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      ) : null}
      <LoadMore cursor={list.cursor} busy={list.busy} onLoadMore={list.loadMore} />
    </>
  );
}

function CreateTopic({ tenantId, onCreated }: { tenantId: string; onCreated: () => void }) {
  const [name, setName] = useState("");
  const [description, setDescription] = useState("");
  const { busy, problem, run } = useAction();

  return (
    <form
      onSubmit={(event) => {
        event.preventDefault();
        void run(
          () =>
            api.POST("/admin/tenants/{tenantId}/topics", {
              params: { path: { tenantId } },
              body: { name, description: description || null },
            }),
          (created) => {
            onCreated();
            if (created) navigate(`/tenants/${tenantId}/topics/${created.id}`);
          },
        );
      }}
    >
      <h2>Create a Topic</h2>
      <FormError message={formError(problem, writeFields)} />
      <Field id="create-topic-name" label="Name" error={fieldError(problem, "name")}>
        <input
          {...fieldProps("create-topic-name", fieldError(problem, "name"))}
          value={name}
          onChange={(event) => setName(event.target.value)}
          required
        />
      </Field>
      <Field id="create-topic-description" label="Description (optional)" error={fieldError(problem, "description")}>
        <input
          {...fieldProps("create-topic-description", fieldError(problem, "description"))}
          value={description}
          onChange={(event) => setDescription(event.target.value)}
        />
      </Field>
      <button type="submit" disabled={busy}>
        Create Topic
      </button>
    </form>
  );
}

export function TopicScreen({ tenantId, topicId }: { tenantId: string; topicId: string }) {
  const topic = useResource<Topic>(
    () => api.GET("/admin/tenants/{tenantId}/topics/{id}", { params: { path: { tenantId, id: topicId } } }),
    `${tenantId}|${topicId}`,
  );

  if (topic.problem)
    return (
      <>
        <h1>Topic</h1>
        <p role="alert">{topic.problem.detail ?? `This Topic could not be read (${topic.problem.status}).`}</p>
      </>
    );
  if (!topic.data) return <p>Loading…</p>;

  const current = topic.data;
  return (
    <>
      <h1>{current.name}</h1>
      <p>
        In <Link to={`/tenants/${tenantId}/topics`}>this Tenant's Topics</Link>.
      </p>
      <dl>
        <dt>Status</dt>
        <dd>{current.status}</dd>
        <dt>Description</dt>
        <dd>{current.description ?? "—"}</dd>
      </dl>

      <EditTopic key={current.updated_at} tenantId={tenantId} topic={current} onSaved={topic.reload} />

      {/* Subscriptions are owned by the Topic in the API, so they are authored where they live
          rather than from a separate Tenant-level list that would have to reintroduce the Topic. */}
      <SubscriptionsSection tenantId={tenantId} topicId={topicId} topicName={current.name} />
    </>
  );
}

function EditTopic({ tenantId, topic, onSaved }: { tenantId: string; topic: Topic; onSaved: () => void }) {
  const [name, setName] = useState(topic.name);
  const [description, setDescription] = useState(topic.description ?? "");
  const { busy, problem, run } = useAction();

  return (
    <>
      <form
        onSubmit={(event) => {
          event.preventDefault();
          void run(
            () =>
              api.PATCH("/admin/tenants/{tenantId}/topics/{id}", {
                params: { path: { tenantId, id: topic.id } },
                body: { name, description: description || null },
              }),
            onSaved,
          );
        }}
      >
        <h2>Edit {topic.name}</h2>
        <FormError message={formError(problem, writeFields)} />
        <Field id="topic-name" label="Name" error={fieldError(problem, "name")}>
          <input
            {...fieldProps("topic-name", fieldError(problem, "name"))}
            value={name}
            onChange={(event) => setName(event.target.value)}
            required
          />
        </Field>
        <Field id="topic-description" label="Description (optional)" error={fieldError(problem, "description")}>
          <input
            {...fieldProps("topic-description", fieldError(problem, "description"))}
            value={description}
            onChange={(event) => setDescription(event.target.value)}
          />
        </Field>
        <button type="submit" disabled={busy}>
          Save changes
        </button>
      </form>

      {topic.status === "active" ? (
        <ConfirmAction
          label="Deactivate Topic"
          question={`Deactivate the Topic "${topic.name}"? Its Subscriptions stop receiving Events.`}
          confirmLabel={`Deactivate ${topic.name}`}
          busy={busy}
          onConfirm={() =>
            void run(
              () =>
                api.POST("/admin/tenants/{tenantId}/topics/{id}/deactivate", {
                  params: { path: { tenantId, id: topic.id } },
                }),
              onSaved,
            )
          }
        />
      ) : null}
    </>
  );
}
