using System;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Integrios.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class PostgresBaseline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "connectors",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "text", nullable: false),
                    contract_version = table.Column<int>(type: "integer", nullable: false),
                    manifest_schema_version = table.Column<int>(type: "integer", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    direction = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false, defaultValueSql: "'active'::text"),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    description = table.Column<string>(type: "text", nullable: true),
                    manifest = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("connectors_pkey", x => x.id);
                    table.UniqueConstraint("uq_connectors_key_contract_version", x => new { x.key, x.contract_version });
                    table.CheckConstraint("ck_connectors_contract_version_positive", "contract_version > 0");
                    table.CheckConstraint("ck_connectors_manifest_identity", "manifest->>'key' = key AND (manifest->>'contract_version')::INTEGER = contract_version AND (manifest->>'manifest_schema_version')::INTEGER = manifest_schema_version");
                    table.CheckConstraint("ck_connectors_manifest_object", "jsonb_typeof(manifest) = 'object'");
                    table.CheckConstraint("ck_connectors_manifest_schema_version_positive", "manifest_schema_version > 0");
                });

            migrationBuilder.CreateTable(
                name: "operator_keys",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    public_key = table.Column<string>(type: "text", nullable: false),
                    secret_hash = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("operator_keys_pkey", x => x.id);
                    table.UniqueConstraint("operator_keys_public_key_key", x => x.public_key);
                });

            migrationBuilder.CreateTable(
                name: "tenants",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    slug = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false, defaultValueSql: "'active'::text"),
                    environment = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    description = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("tenants_pkey", x => x.id);
                    table.UniqueConstraint("tenants_slug_key", x => x.slug);
                    table.CheckConstraint("chk_tenants_slug_dns_label", "slug ~ '^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$'");
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    display_name = table.Column<string>(type: "text", nullable: false),
                    email = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    last_signed_in_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("users_pkey", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "destinations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    connector_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    configuration = table.Column<JsonElement>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    authentication = table.Column<string>(type: "jsonb", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false, defaultValueSql: "'active'::text"),
                    environment = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    description = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("destinations_pkey", x => x.id);
                    table.UniqueConstraint("uq_destinations_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.UniqueConstraint("uq_destinations_tenant_name", x => new { x.tenant_id, x.name });
                    table.CheckConstraint("ck_destinations_authentication_object", "authentication IS NULL OR jsonb_typeof(authentication) = 'object'");
                    table.CheckConstraint("ck_destinations_configuration_json", "jsonb_typeof(configuration) = 'object'");
                    table.ForeignKey(
                        name: "destinations_connector_id_fkey",
                        column: x => x.connector_id,
                        principalTable: "connectors",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "destinations_tenant_id_fkey",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "tenant_api_keys",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    key_prefix = table.Column<string>(type: "text", nullable: false),
                    key_hash = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false, defaultValueSql: "'active'::text"),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    description = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("tenant_api_keys_pkey", x => x.id);
                    table.UniqueConstraint("tenant_api_keys_key_prefix_key", x => x.key_prefix);
                    table.ForeignKey(
                        name: "tenant_api_keys_tenant_id_fkey",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "topics",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false, defaultValueSql: "'active'::text"),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    description = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pipelines_pkey", x => x.id);
                    table.UniqueConstraint("uq_topics_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.UniqueConstraint("uq_topics_tenant_name", x => new { x.tenant_id, x.name });
                    table.ForeignKey(
                        name: "pipelines_tenant_id_fkey",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "operator_identities",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    issuer = table.Column<string>(type: "text", nullable: false),
                    subject = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("operator_identities_pkey", x => x.id);
                    table.ForeignKey(
                        name: "operator_identities_user_id_fkey",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "sources",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    connector_id = table.Column<Guid>(type: "uuid", nullable: false),
                    topic_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "text", nullable: false),
                    configuration = table.Column<JsonElement>(type: "jsonb", nullable: false),
                    verification = table.Column<string>(type: "jsonb", nullable: true),
                    input_requirements = table.Column<JsonElement>(type: "jsonb", nullable: true),
                    mapping = table.Column<string>(type: "jsonb", nullable: true),
                    event_identity_rule = table.Column<string>(type: "jsonb", nullable: true),
                    revision = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false, defaultValueSql: "'active'::text"),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("sources_pkey", x => x.id);
                    table.UniqueConstraint("uq_sources_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_sources_revoked_at", "((status = 'active' AND revoked_at IS NULL) OR (status = 'revoked' AND revoked_at IS NOT NULL))");
                    table.CheckConstraint("ck_sources_status", "status IN ('active', 'revoked')");
                    table.CheckConstraint("ck_sources_type", "type IN ('event_api', 'webhook', 'queue')");
                    table.ForeignKey(
                        name: "fk_sources_connector",
                        column: x => x.connector_id,
                        principalTable: "connectors",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_sources_tenant",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_sources_topic_tenant",
                        columns: x => new { x.tenant_id, x.topic_id },
                        principalTable: "topics",
                        principalColumns: new[] { "tenant_id", "id" });
                });

            migrationBuilder.CreateTable(
                name: "subscriptions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    topic_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    match_rules = table.Column<JsonElement>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    destination_id = table.Column<Guid>(type: "uuid", nullable: false),
                    mapping_config = table.Column<JsonElement>(type: "jsonb", nullable: true),
                    http_delivery = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{\"body\": \"json\", \"method\": \"POST\", \"headers\": {}, \"version\": 1}'::jsonb"),
                    http_success = table.Column<string>(type: "jsonb", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false, defaultValueSql: "'active'::text"),
                    order_index = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    description = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("routes_pkey", x => x.id);
                    table.ForeignKey(
                        name: "fk_subscriptions_destination_tenant",
                        columns: x => new { x.tenant_id, x.destination_id },
                        principalTable: "destinations",
                        principalColumns: new[] { "tenant_id", "id" });
                    table.ForeignKey(
                        name: "fk_subscriptions_topic_tenant",
                        columns: x => new { x.tenant_id, x.topic_id },
                        principalTable: "topics",
                        principalColumns: new[] { "tenant_id", "id" });
                });

            migrationBuilder.CreateTable(
                name: "events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    topic_id = table.Column<Guid>(type: "uuid", nullable: true),
                    source_id = table.Column<Guid>(type: "uuid", nullable: true),
                    source_event_id = table.Column<string>(type: "text", nullable: true),
                    event_type = table.Column<string>(type: "text", nullable: false),
                    payload = table.Column<JsonElement>(type: "jsonb", nullable: false),
                    metadata = table.Column<JsonElement>(type: "jsonb", nullable: true),
                    idempotency_key = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false, defaultValueSql: "'accepted'::text"),
                    accepted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    failed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("events_pkey", x => x.id);
                    table.ForeignKey(
                        name: "events_tenant_id_fkey",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_events_source_tenant",
                        columns: x => new { x.tenant_id, x.source_id },
                        principalTable: "sources",
                        principalColumns: new[] { "tenant_id", "id" });
                    table.ForeignKey(
                        name: "fk_events_topic_tenant",
                        columns: x => new { x.tenant_id, x.topic_id },
                        principalTable: "topics",
                        principalColumns: new[] { "tenant_id", "id" });
                });

            migrationBuilder.CreateTable(
                name: "outbox",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    payload = table.Column<JsonElement>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    attempt_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    deliver_after = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    traceparent = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("outbox_pkey", x => x.id);
                    table.ForeignKey(
                        name: "outbox_event_id_fkey",
                        column: x => x.event_id,
                        principalTable: "events",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "delivery_attempts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    event_delivery_id = table.Column<Guid>(type: "uuid", nullable: false),
                    attempt_number = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    request_payload = table.Column<JsonElement>(type: "jsonb", nullable: true),
                    response_status_code = table.Column<int>(type: "integer", nullable: true),
                    response_body = table.Column<string>(type: "text", nullable: true),
                    response_body_truncated = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    error_message = table.Column<string>(type: "text", nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    failure_phase = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("delivery_attempts_pkey", x => x.id);
                    table.UniqueConstraint("uq_delivery_attempts_delivery_id", x => new { x.event_delivery_id, x.id });
                    table.UniqueConstraint("uq_delivery_attempts_delivery_number", x => new { x.event_delivery_id, x.attempt_number });
                    table.CheckConstraint("ck_delivery_attempts_completion", "((status = 'in_progress' AND completed_at IS NULL) OR (status <> 'in_progress' AND completed_at IS NOT NULL))");
                    table.CheckConstraint("ck_delivery_attempts_failure_phase", "((status = 'failed' AND failure_phase IS NOT NULL AND failure_phase IN ('transform', 'secret_resolution', 'request_construction', 'http')) OR (status <> 'failed' AND failure_phase IS NULL))");
                    table.CheckConstraint("ck_delivery_attempts_number_positive", "attempt_number > 0");
                    table.CheckConstraint("ck_delivery_attempts_status", "status IN ('in_progress', 'succeeded', 'failed', 'indeterminate')");
                });

            migrationBuilder.CreateTable(
                name: "event_deliveries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subscription_id = table.Column<Guid>(type: "uuid", nullable: false),
                    destination_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false, defaultValueSql: "'pending'::text"),
                    lifetime_attempt_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    retry_cycle_attempt_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    deliver_after = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    failed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    mapping_config_snapshot = table.Column<JsonElement>(type: "jsonb", nullable: true),
                    traceparent = table.Column<string>(type: "text", nullable: true),
                    connector_key = table.Column<string>(type: "text", nullable: false),
                    active_attempt_id = table.Column<Guid>(type: "uuid", nullable: true),
                    lease_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    http_execution_snapshot = table.Column<JsonElement>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("event_deliveries_pkey", x => x.id);
                    table.UniqueConstraint("uq_event_deliveries_event_subscription", x => new { x.event_id, x.subscription_id });
                    table.CheckConstraint("ck_event_deliveries_attempt_counts_nonnegative", "lifetime_attempt_count >= 0 AND retry_cycle_attempt_count >= 0 AND retry_cycle_attempt_count <= lifetime_attempt_count");
                    table.CheckConstraint("ck_event_deliveries_lease_state", "((status = 'in_flight' AND active_attempt_id IS NOT NULL AND lease_expires_at IS NOT NULL) OR (status IN ('pending', 'succeeded', 'dead_lettered') AND active_attempt_id IS NULL AND lease_expires_at IS NULL))");
                    table.ForeignKey(
                        name: "event_deliveries_destination_id_fkey",
                        column: x => x.destination_id,
                        principalTable: "destinations",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "event_deliveries_event_id_fkey",
                        column: x => x.event_id,
                        principalTable: "events",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "event_deliveries_subscription_id_fkey",
                        column: x => x.subscription_id,
                        principalTable: "subscriptions",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_event_deliveries_active_attempt",
                        columns: x => new { x.id, x.active_attempt_id },
                        principalTable: "delivery_attempts",
                        principalColumns: new[] { "event_delivery_id", "id" });
                });

            migrationBuilder.CreateIndex(
                name: "idx_delivery_attempts_delivery",
                table: "delivery_attempts",
                columns: new[] { "event_delivery_id", "attempt_number" });

            migrationBuilder.CreateIndex(
                name: "idx_destinations_tenant_id",
                table: "destinations",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "idx_event_deliveries_claimable",
                table: "event_deliveries",
                columns: new[] { "status", "lease_expires_at", "deliver_after", "created_at" },
                filter: "(status = ANY (ARRAY['pending'::text, 'in_flight'::text]))");

            migrationBuilder.CreateIndex(
                name: "idx_event_deliveries_event_id",
                table: "event_deliveries",
                column: "event_id");

            migrationBuilder.CreateIndex(
                name: "idx_event_deliveries_subscription_id",
                table: "event_deliveries",
                column: "subscription_id");

            migrationBuilder.CreateIndex(
                name: "idx_events_idempotency",
                table: "events",
                columns: new[] { "tenant_id", "idempotency_key" },
                unique: true,
                filter: "(idempotency_key IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "idx_events_tenant_accepted",
                table: "events",
                columns: new[] { "tenant_id", "accepted_at", "id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "uq_operator_identities_issuer_subject",
                table: "operator_identities",
                columns: new[] { "issuer", "subject" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_operator_keys_lookup",
                table: "operator_keys",
                column: "public_key",
                filter: "(revoked_at IS NULL)");

            migrationBuilder.CreateIndex(
                name: "idx_outbox_event_id",
                table: "outbox",
                column: "event_id");

            migrationBuilder.CreateIndex(
                name: "idx_outbox_pending",
                table: "outbox",
                columns: new[] { "deliver_after", "created_at" },
                filter: "(processed_at IS NULL)")
                .Annotation("Npgsql:IndexNullSortOrder", new[] { NullSortOrder.NullsFirst, NullSortOrder.NullsLast });

            migrationBuilder.CreateIndex(
                name: "idx_sources_connector_id",
                table: "sources",
                column: "connector_id");

            migrationBuilder.CreateIndex(
                name: "idx_sources_tenant_created",
                table: "sources",
                columns: new[] { "tenant_id", "created_at", "id" });

            migrationBuilder.CreateIndex(
                name: "idx_sources_topic_id",
                table: "sources",
                column: "topic_id");

            migrationBuilder.CreateIndex(
                name: "idx_subscriptions_topic_id",
                table: "subscriptions",
                column: "topic_id");

            migrationBuilder.CreateIndex(
                name: "idx_tenant_api_keys_key_hash",
                table: "tenant_api_keys",
                column: "key_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_tenant_api_keys_tenant_id",
                table: "tenant_api_keys",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "idx_topics_tenant_id",
                table: "topics",
                column: "tenant_id");

            migrationBuilder.AddForeignKey(
                name: "delivery_attempts_event_delivery_id_fkey",
                table: "delivery_attempts",
                column: "event_delivery_id",
                principalTable: "event_deliveries",
                principalColumn: "id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "delivery_attempts_event_delivery_id_fkey",
                table: "delivery_attempts");

            migrationBuilder.DropTable(
                name: "operator_identities");

            migrationBuilder.DropTable(
                name: "operator_keys");

            migrationBuilder.DropTable(
                name: "outbox");

            migrationBuilder.DropTable(
                name: "tenant_api_keys");

            migrationBuilder.DropTable(
                name: "users");

            migrationBuilder.DropTable(
                name: "event_deliveries");

            migrationBuilder.DropTable(
                name: "events");

            migrationBuilder.DropTable(
                name: "subscriptions");

            migrationBuilder.DropTable(
                name: "delivery_attempts");

            migrationBuilder.DropTable(
                name: "sources");

            migrationBuilder.DropTable(
                name: "destinations");

            migrationBuilder.DropTable(
                name: "topics");

            migrationBuilder.DropTable(
                name: "connectors");

            migrationBuilder.DropTable(
                name: "tenants");
        }
    }
}
