using System;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Integrios.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    internal partial class PostgresBaseline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "admin_keys",
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
                    table.PrimaryKey("admin_keys_pkey", x => x.id);
                    table.UniqueConstraint("admin_keys_public_key_key", x => x.public_key);
                });

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
                    supported_auth_schemes = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
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
                name: "api_keys",
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
                    table.PrimaryKey("api_credentials_pkey", x => x.id);
                    table.UniqueConstraint("api_credentials_key_id_key", x => x.key_prefix);
                    table.ForeignKey(
                        name: "api_credentials_tenant_id_fkey",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "connections",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    connector_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    config = table.Column<JsonElement>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    source_verification = table.Column<string>(type: "jsonb", nullable: true),
                    destination_authentication = table.Column<string>(type: "jsonb", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false, defaultValueSql: "'active'::text"),
                    environment = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    description = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("connections_pkey", x => x.id);
                    table.UniqueConstraint("uq_connections_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.UniqueConstraint("uq_connections_tenant_name", x => new { x.tenant_id, x.name });
                    table.CheckConstraint("ck_connections_destination_authentication_object", "destination_authentication IS NULL OR jsonb_typeof(destination_authentication) = 'object'");
                    table.CheckConstraint("ck_connections_source_verification_object", "source_verification IS NULL OR jsonb_typeof(source_verification) = 'object'");
                    table.ForeignKey(
                        name: "connections_connector_id_fkey",
                        column: x => x.connector_id,
                        principalTable: "connectors",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "connections_tenant_id_fkey",
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
                name: "subscriptions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    topic_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    match_rules = table.Column<JsonElement>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    destination_connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    transform_config = table.Column<JsonElement>(type: "jsonb", nullable: true),
                    http_delivery = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{\"body\": \"json\", \"method\": \"POST\", \"headers\": {}, \"version\": 1}'::jsonb"),
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
                        name: "fk_subscriptions_destination_connection_tenant",
                        columns: x => new { x.tenant_id, x.destination_connection_id },
                        principalTable: "connections",
                        principalColumns: new[] { "tenant_id", "id" });
                    table.ForeignKey(
                        name: "fk_subscriptions_topic_tenant",
                        columns: x => new { x.tenant_id, x.topic_id },
                        principalTable: "topics",
                        principalColumns: new[] { "tenant_id", "id" });
                });

            migrationBuilder.CreateTable(
                name: "topic_sources",
                columns: table => new
                {
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    topic_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    inactive_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false, defaultValueSql: "'active'::text")
                },
                constraints: table =>
                {
                    table.PrimaryKey("topic_sources_pkey", x => new { x.tenant_id, x.topic_id, x.connection_id });
                    table.CheckConstraint("ck_topic_sources_inactive_at", "((status = 'active' AND inactive_at IS NULL) OR (status = 'inactive' AND inactive_at IS NOT NULL))");
                    table.CheckConstraint("ck_topic_sources_status", "status IN ('active', 'inactive')");
                    table.ForeignKey(
                        name: "fk_topic_sources_connection_tenant",
                        columns: x => new { x.tenant_id, x.connection_id },
                        principalTable: "connections",
                        principalColumns: new[] { "tenant_id", "id" });
                    table.ForeignKey(
                        name: "fk_topic_sources_topic_tenant",
                        columns: x => new { x.tenant_id, x.topic_id },
                        principalTable: "topics",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    topic_id = table.Column<Guid>(type: "uuid", nullable: true),
                    source_connection_id = table.Column<Guid>(type: "uuid", nullable: true),
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
                        name: "fk_events_source_connection_tenant",
                        columns: x => new { x.tenant_id, x.source_connection_id },
                        principalTable: "connections",
                        principalColumns: new[] { "tenant_id", "id" });
                    table.ForeignKey(
                        name: "fk_events_topic_source",
                        columns: x => new { x.tenant_id, x.topic_id, x.source_connection_id },
                        principalTable: "topic_sources",
                        principalColumns: new[] { "tenant_id", "topic_id", "connection_id" });
                    table.ForeignKey(
                        name: "fk_events_topic_tenant",
                        columns: x => new { x.tenant_id, x.topic_id },
                        principalTable: "topics",
                        principalColumns: new[] { "tenant_id", "id" });
                });

            migrationBuilder.CreateTable(
                name: "source_endpoints",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    callback_path = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false, defaultValueSql: "'active'::text"),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    topic_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("source_endpoints_pkey", x => x.id);
                    table.UniqueConstraint("source_endpoints_callback_path_key", x => x.callback_path);
                    table.CheckConstraint("ck_source_endpoints_revoked_at", "((status = 'active' AND revoked_at IS NULL) OR (status = 'revoked' AND revoked_at IS NOT NULL))");
                    table.CheckConstraint("ck_source_endpoints_status", "status IN ('active', 'revoked')");
                    table.ForeignKey(
                        name: "fk_source_endpoints_topic_source",
                        columns: x => new { x.tenant_id, x.topic_id, x.connection_id },
                        principalTable: "topic_sources",
                        principalColumns: new[] { "tenant_id", "topic_id", "connection_id" });
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
                    subscription_delivery_id = table.Column<Guid>(type: "uuid", nullable: false),
                    attempt_number = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    request_payload = table.Column<JsonElement>(type: "jsonb", nullable: true),
                    response_status_code = table.Column<int>(type: "integer", nullable: true),
                    response_body = table.Column<string>(type: "text", nullable: true),
                    error_message = table.Column<string>(type: "text", nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    failure_phase = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("delivery_attempts_pkey", x => x.id);
                    table.UniqueConstraint("uq_delivery_attempts_delivery_id", x => new { x.subscription_delivery_id, x.id });
                    table.UniqueConstraint("uq_delivery_attempts_delivery_number", x => new { x.subscription_delivery_id, x.attempt_number });
                    table.CheckConstraint("ck_delivery_attempts_completion", "((status = 'in_progress' AND completed_at IS NULL) OR (status <> 'in_progress' AND completed_at IS NOT NULL))");
                    table.CheckConstraint("ck_delivery_attempts_failure_phase", "((status = 'failed' AND failure_phase IS NOT NULL AND failure_phase IN ('transform', 'secret_resolution', 'request_construction', 'http')) OR (status <> 'failed' AND failure_phase IS NULL))");
                    table.CheckConstraint("ck_delivery_attempts_number_positive", "attempt_number > 0");
                    table.CheckConstraint("ck_delivery_attempts_status", "status IN ('in_progress', 'succeeded', 'failed', 'indeterminate')");
                });

            migrationBuilder.CreateTable(
                name: "subscription_deliveries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subscription_id = table.Column<Guid>(type: "uuid", nullable: false),
                    destination_connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false, defaultValueSql: "'pending'::text"),
                    lifetime_attempt_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    retry_cycle_attempt_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    deliver_after = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    failed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    transform_config_snapshot = table.Column<JsonElement>(type: "jsonb", nullable: true),
                    traceparent = table.Column<string>(type: "text", nullable: true),
                    connector_key = table.Column<string>(type: "text", nullable: false),
                    active_attempt_id = table.Column<Guid>(type: "uuid", nullable: true),
                    lease_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    http_execution_snapshot = table.Column<JsonElement>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("subscription_deliveries_pkey", x => x.id);
                    table.UniqueConstraint("uq_subscription_deliveries_event_subscription", x => new { x.event_id, x.subscription_id });
                    table.CheckConstraint("ck_subscription_deliveries_attempt_counts_nonnegative", "lifetime_attempt_count >= 0 AND retry_cycle_attempt_count >= 0 AND retry_cycle_attempt_count <= lifetime_attempt_count");
                    table.CheckConstraint("ck_subscription_deliveries_lease_state", "((status = 'in_flight' AND active_attempt_id IS NOT NULL AND lease_expires_at IS NOT NULL) OR (status IN ('pending', 'succeeded', 'dead_lettered') AND active_attempt_id IS NULL AND lease_expires_at IS NULL))");
                    table.ForeignKey(
                        name: "fk_subscription_deliveries_active_attempt",
                        columns: x => new { x.id, x.active_attempt_id },
                        principalTable: "delivery_attempts",
                        principalColumns: new[] { "subscription_delivery_id", "id" });
                    table.ForeignKey(
                        name: "subscription_deliveries_destination_connection_id_fkey",
                        column: x => x.destination_connection_id,
                        principalTable: "connections",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "subscription_deliveries_event_id_fkey",
                        column: x => x.event_id,
                        principalTable: "events",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "subscription_deliveries_subscription_id_fkey",
                        column: x => x.subscription_id,
                        principalTable: "subscriptions",
                        principalColumn: "id");
                });

            migrationBuilder.CreateIndex(
                name: "idx_admin_keys_lookup",
                table: "admin_keys",
                column: "public_key",
                filter: "(revoked_at IS NULL)");

            migrationBuilder.CreateIndex(
                name: "idx_api_keys_key_hash",
                table: "api_keys",
                column: "key_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_api_keys_tenant_id",
                table: "api_keys",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "idx_connections_tenant_id",
                table: "connections",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "idx_delivery_attempts_delivery",
                table: "delivery_attempts",
                columns: new[] { "subscription_delivery_id", "attempt_number" });

            migrationBuilder.CreateIndex(
                name: "idx_events_idempotency",
                table: "events",
                columns: new[] { "tenant_id", "idempotency_key" },
                unique: true,
                filter: "(idempotency_key IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "idx_events_tenant_id",
                table: "events",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "idx_outbox_pending",
                table: "outbox",
                columns: new[] { "deliver_after", "created_at" },
                filter: "(processed_at IS NULL)")
                .Annotation("Npgsql:IndexNullSortOrder", new[] { NullSortOrder.NullsFirst, NullSortOrder.NullsLast });

            migrationBuilder.CreateIndex(
                name: "idx_source_endpoints_association",
                table: "source_endpoints",
                columns: new[] { "tenant_id", "topic_id", "connection_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "uq_source_endpoints_active_association",
                table: "source_endpoints",
                columns: new[] { "tenant_id", "topic_id", "connection_id" },
                unique: true,
                filter: "(status = 'active'::text)");

            migrationBuilder.CreateIndex(
                name: "idx_subscription_deliveries_claimable",
                table: "subscription_deliveries",
                columns: new[] { "status", "lease_expires_at", "deliver_after", "created_at" },
                filter: "(status = ANY (ARRAY['pending'::text, 'in_flight'::text]))");

            migrationBuilder.CreateIndex(
                name: "idx_subscription_deliveries_event_id",
                table: "subscription_deliveries",
                column: "event_id");

            migrationBuilder.CreateIndex(
                name: "idx_subscription_deliveries_subscription_id",
                table: "subscription_deliveries",
                column: "subscription_id");

            migrationBuilder.CreateIndex(
                name: "idx_subscriptions_topic_id",
                table: "subscriptions",
                column: "topic_id");

            migrationBuilder.CreateIndex(
                name: "idx_topic_sources_connection_id",
                table: "topic_sources",
                column: "connection_id");

            migrationBuilder.CreateIndex(
                name: "idx_topic_sources_topic_id",
                table: "topic_sources",
                column: "topic_id");

            migrationBuilder.CreateIndex(
                name: "idx_topics_tenant_id",
                table: "topics",
                column: "tenant_id");

            migrationBuilder.AddForeignKey(
                name: "delivery_attempts_subscription_delivery_id_fkey",
                table: "delivery_attempts",
                column: "subscription_delivery_id",
                principalTable: "subscription_deliveries",
                principalColumn: "id");

            // Current runtime invariants that require PostgreSQL triggers are retained below.
            migrationBuilder.Sql(
                """
                ALTER TABLE events
                    ADD CONSTRAINT ck_events_source_connection_required
                    CHECK (source_connection_id IS NOT NULL) NOT VALID;

                CREATE FUNCTION reject_connector_functional_update()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $$
                BEGIN
                    IF NEW.id IS DISTINCT FROM OLD.id
                       OR NEW.key IS DISTINCT FROM OLD.key
                       OR NEW.contract_version IS DISTINCT FROM OLD.contract_version
                       OR NEW.manifest_schema_version IS DISTINCT FROM OLD.manifest_schema_version
                       OR NEW.direction IS DISTINCT FROM OLD.direction
                       OR NEW.supported_auth_schemes IS DISTINCT FROM OLD.supported_auth_schemes
                       OR (NEW.manifest - 'presentation') IS DISTINCT FROM (OLD.manifest - 'presentation') THEN
                        RAISE EXCEPTION 'Connector functional contracts are immutable; apply a new contract_version';
                    END IF;

                    RETURN NEW;
                END;
                $$;

                CREATE TRIGGER connectors_reject_functional_update
                BEFORE UPDATE ON connectors
                FOR EACH ROW
                EXECUTE FUNCTION reject_connector_functional_update();

                CREATE FUNCTION events_require_active_topic_source()
                RETURNS trigger AS $$
                BEGIN
                    IF NEW.topic_id IS NOT NULL AND NOT EXISTS (
                        SELECT 1
                        FROM topic_sources ts
                        WHERE ts.tenant_id = NEW.tenant_id
                          AND ts.topic_id = NEW.topic_id
                          AND ts.connection_id = NEW.source_connection_id
                          AND ts.status = 'active'
                    ) THEN
                        RAISE EXCEPTION USING
                            ERRCODE = '23503',
                            CONSTRAINT = 'fk_events_topic_source_active',
                            MESSAGE = 'An Event source Connection must be actively associated with its Topic.';
                    END IF;

                    RETURN NEW;
                END
                $$ LANGUAGE plpgsql;

                CREATE TRIGGER trg_events_require_active_topic_source
                BEFORE INSERT OR UPDATE OF tenant_id, topic_id, source_connection_id ON events
                FOR EACH ROW
                EXECUTE FUNCTION events_require_active_topic_source();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP TRIGGER IF EXISTS trg_events_require_active_topic_source ON events;
                DROP FUNCTION IF EXISTS events_require_active_topic_source();
                DROP TRIGGER IF EXISTS connectors_reject_functional_update ON connectors;
                DROP FUNCTION IF EXISTS reject_connector_functional_update();
                """);

            migrationBuilder.DropForeignKey(
                name: "connections_tenant_id_fkey",
                table: "connections");

            migrationBuilder.DropForeignKey(
                name: "events_tenant_id_fkey",
                table: "events");

            migrationBuilder.DropForeignKey(
                name: "pipelines_tenant_id_fkey",
                table: "topics");

            migrationBuilder.DropForeignKey(
                name: "connections_connector_id_fkey",
                table: "connections");

            migrationBuilder.DropForeignKey(
                name: "delivery_attempts_subscription_delivery_id_fkey",
                table: "delivery_attempts");

            migrationBuilder.DropTable(
                name: "admin_keys");

            migrationBuilder.DropTable(
                name: "api_keys");

            migrationBuilder.DropTable(
                name: "outbox");

            migrationBuilder.DropTable(
                name: "source_endpoints");

            migrationBuilder.DropTable(
                name: "tenants");

            migrationBuilder.DropTable(
                name: "connectors");

            migrationBuilder.DropTable(
                name: "subscription_deliveries");

            migrationBuilder.DropTable(
                name: "delivery_attempts");

            migrationBuilder.DropTable(
                name: "events");

            migrationBuilder.DropTable(
                name: "subscriptions");

            migrationBuilder.DropTable(
                name: "topic_sources");

            migrationBuilder.DropTable(
                name: "connections");

            migrationBuilder.DropTable(
                name: "topics");
        }
    }
}
