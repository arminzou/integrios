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
                name: "connectors",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    key = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    contract_version = table.Column<int>(type: "int", nullable: false),
                    manifest_schema_version = table.Column<int>(type: "int", nullable: false),
                    name = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    direction = table.Column<string>(type: "nvarchar(max)", nullable: false),
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
                });

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
                    table.CheckConstraint("chk_tenants_slug_dns_label", "LEN(slug) BETWEEN 1 AND 63 AND slug COLLATE Latin1_General_100_BIN2 NOT LIKE '%[^a-z0-9-]%' AND LEFT(slug, 1) <> '-' AND RIGHT(slug, 1) <> '-'");
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    display_name = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    email = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    last_signed_in_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("users_pkey", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "destinations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    connector_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    name = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    configuration = table.Column<string>(type: "nvarchar(max)", nullable: false, defaultValueSql: "N'{}'"),
                    authentication = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    status = table.Column<string>(type: "nvarchar(max)", nullable: false, defaultValueSql: "N'active'"),
                    environment = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    updated_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    description = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("destinations_pkey", x => x.id);
                    table.UniqueConstraint("uq_destinations_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_destinations_authentication_object", "authentication IS NULL OR ISJSON(authentication, OBJECT) = 1");
                    table.CheckConstraint("ck_destinations_configuration_json", "ISJSON(configuration, VALUE) = 1");
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
                name: "topics",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    key = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    name = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    status = table.Column<string>(type: "nvarchar(max)", nullable: false, defaultValueSql: "N'active'"),
                    created_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    updated_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    description = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pipelines_pkey", x => x.id);
                    table.UniqueConstraint("uq_topics_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.UniqueConstraint("uq_topics_tenant_key", x => new { x.tenant_id, x.key });
                    table.CheckConstraint("chk_topics_key_dns_label", "LEN([key]) BETWEEN 1 AND 63 AND [key] COLLATE Latin1_General_100_BIN2 NOT LIKE '%[^a-z0-9-]%' AND LEFT([key], 1) <> '-' AND RIGHT([key], 1) <> '-'");
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
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    user_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    issuer = table.Column<string>(type: "nvarchar(450)", nullable: false, collation: "Latin1_General_BIN2"),
                    subject = table.Column<string>(type: "nvarchar(450)", nullable: false, collation: "Latin1_General_BIN2"),
                    created_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false, defaultValueSql: "SYSUTCDATETIME()")
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
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    connector_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    topic_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    name = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    type = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    configuration = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    verification = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    input_requirements = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    mapping = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    event_identity_rule = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    revision = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    status = table.Column<string>(type: "nvarchar(max)", nullable: false, defaultValueSql: "N'active'"),
                    created_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    updated_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    revoked_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("sources_pkey", x => x.id);
                    table.UniqueConstraint("uq_sources_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_sources_configuration_json", "ISJSON(configuration, VALUE) = 1");
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
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    topic_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    name = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    match_rules = table.Column<string>(type: "nvarchar(max)", nullable: false, defaultValueSql: "N'{}'"),
                    destination_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    mapping_config = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    http_delivery = table.Column<string>(type: "nvarchar(max)", nullable: false, defaultValueSql: "N'{\"body\": \"json\", \"method\": \"POST\", \"headers\": {}, \"version\": 1}'"),
                    http_success = table.Column<string>(type: "nvarchar(max)", nullable: true),
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
                    table.CheckConstraint("ck_subscriptions_http_success_json", "http_success IS NULL OR ISJSON(http_success, OBJECT) = 1");
                    table.CheckConstraint("ck_subscriptions_mapping_config_json", "mapping_config IS NULL OR ISJSON(mapping_config, VALUE) = 1");
                    table.CheckConstraint("ck_subscriptions_match_rules_json", "ISJSON(match_rules, VALUE) = 1");
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
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    topic_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    source_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    source_event_id = table.Column<string>(type: "nvarchar(450)", nullable: true, collation: "Latin1_General_100_BIN2"),
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
                    table.CheckConstraint("ck_events_source_required", "source_id IS NOT NULL");
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
                    event_delivery_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    attempt_number = table.Column<int>(type: "int", nullable: false),
                    status = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    request_payload = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    response_status_code = table.Column<int>(type: "int", nullable: true),
                    response_body = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    response_body_truncated = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    error_message = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    completed_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    failure_phase = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("delivery_attempts_pkey", x => x.id);
                    table.UniqueConstraint("uq_delivery_attempts_delivery_id", x => new { x.event_delivery_id, x.id });
                    table.UniqueConstraint("uq_delivery_attempts_delivery_number", x => new { x.event_delivery_id, x.attempt_number });
                    table.CheckConstraint("ck_delivery_attempts_completion", "((status = 'in_progress' AND completed_at IS NULL) OR (status <> 'in_progress' AND completed_at IS NOT NULL))");
                    table.CheckConstraint("ck_delivery_attempts_failure_phase", "((status = 'failed' AND failure_phase IS NOT NULL AND failure_phase IN ('transform', 'secret_resolution', 'request_construction', 'http')) OR (status <> 'failed' AND failure_phase IS NULL))");
                    table.CheckConstraint("ck_delivery_attempts_number_positive", "attempt_number > 0");
                    table.CheckConstraint("ck_delivery_attempts_request_payload_json", "request_payload IS NULL OR ISJSON(request_payload, VALUE) = 1");
                    table.CheckConstraint("ck_delivery_attempts_status", "status IN ('in_progress', 'succeeded', 'failed', 'indeterminate')");
                });

            migrationBuilder.CreateTable(
                name: "event_deliveries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWID()"),
                    event_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    subscription_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    destination_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    status = table.Column<string>(type: "nvarchar(450)", nullable: false, defaultValueSql: "N'pending'"),
                    lifetime_attempt_count = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    retry_cycle_attempt_count = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    deliver_after = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    processed_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    failed_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    updated_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    mapping_config_snapshot = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    traceparent = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    connector_key = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    active_attempt_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    lease_expires_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    http_execution_snapshot = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("event_deliveries_pkey", x => x.id);
                    table.UniqueConstraint("uq_event_deliveries_event_subscription", x => new { x.event_id, x.subscription_id });
                    table.CheckConstraint("ck_event_deliveries_attempt_counts_nonnegative", "lifetime_attempt_count >= 0 AND retry_cycle_attempt_count >= 0 AND retry_cycle_attempt_count <= lifetime_attempt_count");
                    table.CheckConstraint("ck_event_deliveries_http_execution_snapshot_json", "ISJSON(http_execution_snapshot, VALUE) = 1");
                    table.CheckConstraint("ck_event_deliveries_lease_state", "((status = 'in_flight' AND active_attempt_id IS NOT NULL AND lease_expires_at IS NOT NULL) OR (status IN ('pending', 'succeeded', 'dead_lettered') AND active_attempt_id IS NULL AND lease_expires_at IS NULL))");
                    table.CheckConstraint("ck_event_deliveries_mapping_config_snapshot_json", "mapping_config_snapshot IS NULL OR ISJSON(mapping_config_snapshot, VALUE) = 1");
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
                filter: "(status IN (N'pending', N'in_flight'))");

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
                name: "idx_events_source_event_id",
                table: "events",
                columns: new[] { "source_id", "source_event_id" },
                filter: "(source_event_id IS NOT NULL)");

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
                filter: "(processed_at IS NULL)");

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
