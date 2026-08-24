using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Integrios.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class SqlServerBaseline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "operator_keys",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWID()"),
                    public_key = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    secret_hash = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    name = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    revoked_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("operator_keys_pkey", x => x.id);
                    table.UniqueConstraint("operator_keys_public_key_key", x => x.public_key);
                });

            migrationBuilder.CreateTable(
                name: "connectors",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    key = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    contract_version = table.Column<int>(type: "int", nullable: false),
                    manifest_schema_version = table.Column<int>(type: "int", nullable: false),
                    name = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    direction = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    supported_auth_schemes = table.Column<string>(type: "nvarchar(max)", nullable: false, defaultValueSql: "N'[]'"),
                    status = table.Column<string>(type: "nvarchar(max)", nullable: false, defaultValueSql: "N'active'"),
                    created_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    updated_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    manifest = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("connectors_pkey", x => x.id);
                    table.UniqueConstraint("uq_connectors_key_contract_version", x => new { x.key, x.contract_version });
                    table.CheckConstraint("ck_connectors_contract_version_positive", "contract_version > 0");
                    table.CheckConstraint("ck_connectors_manifest_identity", "JSON_VALUE(manifest, '$.key') = [key] AND TRY_CONVERT(int, JSON_VALUE(manifest, '$.contract_version')) = contract_version AND TRY_CONVERT(int, JSON_VALUE(manifest, '$.manifest_schema_version')) = manifest_schema_version");
                    table.CheckConstraint("ck_connectors_manifest_object", "ISJSON(manifest, OBJECT) = 1");
                    table.CheckConstraint("ck_connectors_manifest_schema_version_positive", "manifest_schema_version > 0");
                    table.CheckConstraint("ck_connectors_supported_auth_schemes_json", "ISJSON(supported_auth_schemes, VALUE) = 1");
                });

            migrationBuilder.CreateTable(
                name: "tenants",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    slug = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    name = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    status = table.Column<string>(type: "nvarchar(max)", nullable: false, defaultValueSql: "N'active'"),
                    environment = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    updated_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    description = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("tenants_pkey", x => x.id);
                    table.UniqueConstraint("tenants_slug_key", x => x.slug);
                    table.CheckConstraint("chk_tenants_slug_dns_label", "LEN(slug) BETWEEN 1 AND 63 AND slug NOT LIKE '%[^a-z0-9-]%' AND LEFT(slug, 1) <> '-' AND RIGHT(slug, 1) <> '-'");
                });

            migrationBuilder.CreateTable(
                name: "tenant_api_keys",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    name = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    key_prefix = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    key_hash = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    status = table.Column<string>(type: "nvarchar(max)", nullable: false, defaultValueSql: "N'active'"),
                    created_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    expires_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    last_used_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    revoked_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    description = table.Column<string>(type: "nvarchar(max)", nullable: true)
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
                name: "connections",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    connector_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    name = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    config = table.Column<string>(type: "nvarchar(max)", nullable: false, defaultValueSql: "N'{}'"),
                    source_verification = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    destination_authentication = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    status = table.Column<string>(type: "nvarchar(max)", nullable: false, defaultValueSql: "N'active'"),
                    environment = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    updated_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    description = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("connections_pkey", x => x.id);
                    table.UniqueConstraint("uq_connections_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.UniqueConstraint("uq_connections_tenant_name", x => new { x.tenant_id, x.name });
                    table.CheckConstraint("ck_connections_config_json", "ISJSON(config, VALUE) = 1");
                    table.CheckConstraint("ck_connections_destination_authentication_object", "destination_authentication IS NULL OR ISJSON(destination_authentication, OBJECT) = 1");
                    table.CheckConstraint("ck_connections_source_verification_object", "source_verification IS NULL OR ISJSON(source_verification, OBJECT) = 1");
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
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    name = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    status = table.Column<string>(type: "nvarchar(max)", nullable: false, defaultValueSql: "N'active'"),
                    created_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    updated_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    description = table.Column<string>(type: "nvarchar(max)", nullable: true)
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
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    topic_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    name = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    match_rules = table.Column<string>(type: "nvarchar(max)", nullable: false, defaultValueSql: "N'{}'"),
                    destination_connection_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    transform_config = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    http_delivery = table.Column<string>(type: "nvarchar(max)", nullable: false, defaultValueSql: "N'{\"body\": \"json\", \"method\": \"POST\", \"headers\": {}, \"version\": 1}'"),
                    status = table.Column<string>(type: "nvarchar(max)", nullable: false, defaultValueSql: "N'active'"),
                    order_index = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    created_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    updated_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    description = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("routes_pkey", x => x.id);
                    table.CheckConstraint("ck_subscriptions_http_delivery_json", "ISJSON(http_delivery, VALUE) = 1");
                    table.CheckConstraint("ck_subscriptions_match_rules_json", "ISJSON(match_rules, VALUE) = 1");
                    table.CheckConstraint("ck_subscriptions_transform_config_json", "transform_config IS NULL OR ISJSON(transform_config, VALUE) = 1");
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
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    topic_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    connection_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    status = table.Column<string>(type: "nvarchar(max)", nullable: false, defaultValueSql: "N'active'"),
                    created_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    inactive_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
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
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    topic_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    source_connection_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    source_event_id = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    event_type = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    payload = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    metadata = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    idempotency_key = table.Column<string>(type: "nvarchar(450)", nullable: true),
                    status = table.Column<string>(type: "nvarchar(max)", nullable: false, defaultValueSql: "N'accepted'"),
                    accepted_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    processed_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    failed_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("events_pkey", x => x.id);
                    table.CheckConstraint("ck_events_metadata_json", "metadata IS NULL OR ISJSON(metadata, VALUE) = 1");
                    table.CheckConstraint("ck_events_payload_json", "ISJSON(payload, VALUE) = 1");
                    table.CheckConstraint("ck_events_source_connection_required", "source_connection_id IS NOT NULL");
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
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWID()"),
                    callback_path = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    connection_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    status = table.Column<string>(type: "nvarchar(max)", nullable: false, defaultValueSql: "N'active'"),
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    topic_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
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
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWID()"),
                    event_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    payload = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    processed_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    attempt_count = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    deliver_after = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    traceparent = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("outbox_pkey", x => x.id);
                    table.CheckConstraint("ck_outbox_payload_json", "ISJSON(payload, VALUE) = 1");
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
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWID()"),
                    subscription_delivery_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    attempt_number = table.Column<int>(type: "int", nullable: false),
                    status = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    request_payload = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    response_status_code = table.Column<int>(type: "int", nullable: true),
                    response_body = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    error_message = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    completed_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    failure_phase = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("delivery_attempts_pkey", x => x.id);
                    table.UniqueConstraint("uq_delivery_attempts_delivery_id", x => new { x.subscription_delivery_id, x.id });
                    table.UniqueConstraint("uq_delivery_attempts_delivery_number", x => new { x.subscription_delivery_id, x.attempt_number });
                    table.CheckConstraint("ck_delivery_attempts_completion", "((status = 'in_progress' AND completed_at IS NULL) OR (status <> 'in_progress' AND completed_at IS NOT NULL))");
                    table.CheckConstraint("ck_delivery_attempts_failure_phase", "((status = 'failed' AND failure_phase IS NOT NULL AND failure_phase IN ('transform', 'secret_resolution', 'request_construction', 'http')) OR (status <> 'failed' AND failure_phase IS NULL))");
                    table.CheckConstraint("ck_delivery_attempts_number_positive", "attempt_number > 0");
                    table.CheckConstraint("ck_delivery_attempts_request_payload_json", "request_payload IS NULL OR ISJSON(request_payload, VALUE) = 1");
                    table.CheckConstraint("ck_delivery_attempts_status", "status IN ('in_progress', 'succeeded', 'failed', 'indeterminate')");
                });

            migrationBuilder.CreateTable(
                name: "subscription_deliveries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWID()"),
                    event_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    subscription_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    destination_connection_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    status = table.Column<string>(type: "nvarchar(450)", nullable: false, defaultValueSql: "N'pending'"),
                    lifetime_attempt_count = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    retry_cycle_attempt_count = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    deliver_after = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    processed_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    failed_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    updated_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    transform_config_snapshot = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    traceparent = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    connector_key = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    active_attempt_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    lease_expires_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    http_execution_snapshot = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("subscription_deliveries_pkey", x => x.id);
                    table.UniqueConstraint("uq_subscription_deliveries_event_subscription", x => new { x.event_id, x.subscription_id });
                    table.CheckConstraint("ck_subscription_deliveries_attempt_counts_nonnegative", "lifetime_attempt_count >= 0 AND retry_cycle_attempt_count >= 0 AND retry_cycle_attempt_count <= lifetime_attempt_count");
                    table.CheckConstraint("ck_subscription_deliveries_http_execution_snapshot_json", "ISJSON(http_execution_snapshot, VALUE) = 1");
                    table.CheckConstraint("ck_subscription_deliveries_lease_state", "((status = 'in_flight' AND active_attempt_id IS NOT NULL AND lease_expires_at IS NOT NULL) OR (status IN ('pending', 'succeeded', 'dead_lettered') AND active_attempt_id IS NULL AND lease_expires_at IS NULL))");
                    table.CheckConstraint("ck_subscription_deliveries_transform_config_snapshot_json", "transform_config_snapshot IS NULL OR ISJSON(transform_config_snapshot, VALUE) = 1");
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
                name: "idx_operator_keys_lookup",
                table: "operator_keys",
                column: "public_key",
                filter: "(revoked_at IS NULL)");

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
                filter: "(processed_at IS NULL)");

            migrationBuilder.CreateIndex(
                name: "idx_source_endpoints_association",
                table: "source_endpoints",
                columns: new[] { "tenant_id", "topic_id", "connection_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "uq_source_endpoints_active_association",
                table: "source_endpoints",
                columns: new[] { "tenant_id", "topic_id", "connection_id" },
                unique: true,
                filter: "(status = N'active')");

            migrationBuilder.CreateIndex(
                name: "idx_subscription_deliveries_claimable",
                table: "subscription_deliveries",
                columns: new[] { "status", "lease_expires_at", "deliver_after", "created_at" },
                filter: "(status IN (N'pending', N'in_flight'))");

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

            migrationBuilder.Sql(
                """
                CREATE TRIGGER connectors_reject_functional_update
                ON connectors
                AFTER UPDATE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    IF EXISTS (
                        SELECT id, [key], contract_version, manifest_schema_version, direction,
                               supported_auth_schemes, JSON_MODIFY(manifest, '$.presentation', NULL)
                        FROM inserted
                        EXCEPT
                        SELECT id, [key], contract_version, manifest_schema_version, direction,
                               supported_auth_schemes, JSON_MODIFY(manifest, '$.presentation', NULL)
                        FROM deleted
                    ) OR EXISTS (
                        SELECT id, [key], contract_version, manifest_schema_version, direction,
                               supported_auth_schemes, JSON_MODIFY(manifest, '$.presentation', NULL)
                        FROM deleted
                        EXCEPT
                        SELECT id, [key], contract_version, manifest_schema_version, direction,
                               supported_auth_schemes, JSON_MODIFY(manifest, '$.presentation', NULL)
                        FROM inserted
                    )
                        THROW 51000, 'Connector functional contracts are immutable; apply a new contract_version', 1;
                END
                """);

            migrationBuilder.Sql(
                """
                CREATE TRIGGER events_require_active_topic_source
                ON events
                AFTER INSERT, UPDATE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    IF (NOT EXISTS (SELECT 1 FROM deleted)
                        OR UPDATE(tenant_id) OR UPDATE(topic_id) OR UPDATE(source_connection_id))
                    AND EXISTS (
                        SELECT 1 FROM inserted i
                        WHERE i.topic_id IS NOT NULL
                          AND NOT EXISTS (
                              SELECT 1 FROM topic_sources ts
                              WHERE ts.tenant_id=i.tenant_id AND ts.topic_id=i.topic_id
                                AND ts.connection_id=i.source_connection_id AND ts.status=N'active'
                          )
                    )
                        THROW 51001, 'An Event source Connection must be actively associated with its Topic.', 1;
                END
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS events_require_active_topic_source");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS connectors_reject_functional_update");

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
                name: "operator_keys");

            migrationBuilder.DropTable(
                name: "tenant_api_keys");

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
