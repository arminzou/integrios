using Integrios.Domain.Entities;
using Integrios.Domain.Enums;
using Integrios.Domain.ValueObjects;
using Integrios.Infrastructure.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using DomainEvent = Integrios.Domain.Entities.Event;

namespace Integrios.Infrastructure.Data;

internal sealed class IntegriosDbContext(DbContextOptions<IntegriosDbContext> options) : DbContext(options)
{
    public DbSet<OperatorKey> OperatorKeys => Set<OperatorKey>();
    public DbSet<TenantApiKey> TenantApiKeys => Set<TenantApiKey>();
    public DbSet<Connection> Connections => Set<Connection>();
    public DbSet<DeliveryAttempt> DeliveryAttempts => Set<DeliveryAttempt>();
    public DbSet<DomainEvent> Events => Set<DomainEvent>();
    public DbSet<Connector> Connectors => Set<Connector>();
    public DbSet<OutboxEntry> Outboxes => Set<OutboxEntry>();
    public DbSet<Source> Sources => Set<Source>();
    public DbSet<SourceEndpoint> SourceEndpoints => Set<SourceEndpoint>();
    public DbSet<Subscription> Subscriptions => Set<Subscription>();
    public DbSet<EventDelivery> EventDeliveries => Set<EventDelivery>();
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<Topic> Topics => Set<Topic>();
    public DbSet<TopicSource> TopicSources => Set<TopicSource>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Conventions.Remove<ForeignKeyIndexConvention>();
        configurationBuilder.Properties<OperationalStatus>()
            .HaveConversion<SnakeCaseEnumConverter<OperationalStatus>>();
        configurationBuilder.Properties<TopicSourceStatus>()
            .HaveConversion<SnakeCaseEnumConverter<TopicSourceStatus>>();
        configurationBuilder.Properties<SourceStatus>()
            .HaveConversion<SnakeCaseEnumConverter<SourceStatus>>();
        configurationBuilder.Properties<SourceType>()
            .HaveConversion<SnakeCaseEnumConverter<SourceType>>();
        configurationBuilder.Properties<DeliveryFailurePhase>()
            .HaveConversion<SnakeCaseEnumConverter<DeliveryFailurePhase>>();
        configurationBuilder.Properties<DeliveryAttemptStatus>()
            .HaveConversion<SnakeCaseEnumConverter<DeliveryAttemptStatus>>();
        configurationBuilder.Properties<ConnectorDirection>()
            .HaveConversion<SnakeCaseEnumConverter<ConnectorDirection>>();
        configurationBuilder.Properties<EventDeliveryStatus>()
            .HaveConversion<SnakeCaseEnumConverter<EventDeliveryStatus>>();
        configurationBuilder.Properties<ConnectionSchemeSelection>()
            .HaveConversion<StoredJsonConverter<ConnectionSchemeSelection>>();
        configurationBuilder.Properties<ConnectorManifest>()
            .HaveConversion<StoredJsonConverter<ConnectorManifest>>();
        configurationBuilder.Properties<HttpDeliveryConfiguration>()
            .HaveConversion<StoredJsonConverter<HttpDeliveryConfiguration>>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(IntegriosDbContext).Assembly);

        if (DatabaseProviders.FromContext(Database) == DatabaseProvider.SqlServer)
            ApplySqlServerOverrides(modelBuilder);
    }

    private static void ApplySqlServerOverrides(ModelBuilder modelBuilder)
    {
        const string currentTimestamp = "SYSUTCDATETIME()";
        const string generatedGuid = "NEWID()";
        const string jsonType = "nvarchar(max)";
        static string TextDefault(string value) => $"N'{value}'";
        static string JsonDefault(string value) =>
            $"N'{value.Replace("'", "''", StringComparison.Ordinal)}'";

        modelBuilder.Entity<OperatorKey>(entity =>
        {
            entity.Property(e => e.Id).HasDefaultValueSql(generatedGuid);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql(currentTimestamp);
        });

        modelBuilder.Entity<TenantApiKey>(entity =>
        {
            entity.Property(e => e.CreatedAt).HasDefaultValueSql(currentTimestamp);
            entity.Property(e => e.Status).HasDefaultValueSql(TextDefault("active"));
        });

        modelBuilder.Entity<Connection>(entity =>
        {
            entity.ToTable("connections", table =>
            {
                table.HasCheckConstraint("ck_connections_config_json", "ISJSON(config, VALUE) = 1");
                table.HasCheckConstraint(
                    "ck_connections_source_verification_object",
                    "source_verification IS NULL OR ISJSON(source_verification, OBJECT) = 1");
                table.HasCheckConstraint(
                    "ck_connections_destination_authentication_object",
                    "destination_authentication IS NULL OR ISJSON(destination_authentication, OBJECT) = 1");
            });
            entity.Property(e => e.Config).HasDefaultValueSql(JsonDefault("{}")).HasColumnType(jsonType);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql(currentTimestamp);
            entity.Property(e => e.DestinationAuthentication).HasColumnType(jsonType);
            entity.Property(e => e.SourceVerification).HasColumnType(jsonType);
            entity.Property(e => e.Status).HasDefaultValueSql(TextDefault("active"));
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql(currentTimestamp);
        });

        modelBuilder.Entity<DeliveryAttempt>(entity =>
        {
            entity.ToTable("delivery_attempts", table => table.HasCheckConstraint(
                "ck_delivery_attempts_request_payload_json",
                "request_payload IS NULL OR ISJSON(request_payload, VALUE) = 1"));
            entity.Property(e => e.Id).HasDefaultValueSql(generatedGuid);
            entity.Property(e => e.RequestPayload).HasColumnType(jsonType);
            entity.Property(e => e.StartedAt).HasDefaultValueSql(currentTimestamp);
        });

        modelBuilder.Entity<DomainEvent>(entity =>
        {
            entity.ToTable("events", table =>
            {
                table.UseSqlOutputClause(false);
                table.HasCheckConstraint("ck_events_source_required", "source_id IS NOT NULL");
                table.HasCheckConstraint("ck_events_payload_json", "ISJSON(payload, VALUE) = 1");
                table.HasCheckConstraint(
                    "ck_events_metadata_json",
                    "metadata IS NULL OR ISJSON(metadata, VALUE) = 1");
            });
            entity.Property(e => e.AcceptedAt).HasDefaultValueSql(currentTimestamp);
            entity.Property(e => e.Metadata).HasColumnType(jsonType);
            entity.Property(e => e.Payload).HasColumnType(jsonType);
            entity.Property(e => e.Status).HasDefaultValueSql(TextDefault("accepted"));
        });

        modelBuilder.Entity<Connector>(entity =>
        {
            entity.ToTable("connectors", table =>
            {
                table.UseSqlOutputClause(false);
                table.HasCheckConstraint("ck_connectors_manifest_object", "ISJSON(manifest, OBJECT) = 1");
                table.HasCheckConstraint(
                    "ck_connectors_manifest_identity",
                    "JSON_VALUE(manifest, '$.key') = [key] "
                    + "AND TRY_CONVERT(int, JSON_VALUE(manifest, '$.contract_version')) = contract_version "
                    + "AND TRY_CONVERT(int, JSON_VALUE(manifest, '$.manifest_schema_version')) = manifest_schema_version");
            });
            entity.Property(e => e.CreatedAt).HasDefaultValueSql(currentTimestamp);
            entity.Property(e => e.Manifest).HasColumnType(jsonType);
            entity.Property(e => e.Status).HasDefaultValueSql(TextDefault("active"));
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql(currentTimestamp);
        });

        modelBuilder.Entity<OutboxEntry>(entity =>
        {
            entity.ToTable("outbox", table =>
                table.HasCheckConstraint("ck_outbox_payload_json", "ISJSON(payload, VALUE) = 1"));
            entity.HasIndex(e => new { e.DeliverAfter, e.CreatedAt }, "idx_outbox_pending")
                .Metadata.RemoveAnnotation("Npgsql:IndexNullSortOrder");
            entity.Property(e => e.Id).HasDefaultValueSql(generatedGuid);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql(currentTimestamp);
            entity.Property(e => e.Payload).HasColumnType(jsonType);
        });

        modelBuilder.Entity<SourceEndpoint>(entity =>
        {
            entity.Property<string>("Status").HasDefaultValueSql(TextDefault("active"));
            entity.HasIndex("TenantId", "TopicId", "ConnectionId")
                .HasDatabaseName("uq_source_endpoints_active_association")
                .HasFilter("(status = N'active')");
            entity.Property(e => e.Id).HasDefaultValueSql(generatedGuid);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql(currentTimestamp);
        });

        modelBuilder.Entity<Source>(entity =>
        {
            entity.ToTable("sources", table =>
            {
                table.HasCheckConstraint("ck_sources_configuration_json", "ISJSON(configuration, VALUE) = 1");
            });
            entity.Property(e => e.Configuration).HasColumnType(jsonType);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql(currentTimestamp);
            entity.Property(e => e.Status).HasDefaultValueSql(TextDefault("active"));
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql(currentTimestamp);
        });

        modelBuilder.Entity<Subscription>(entity =>
        {
            entity.ToTable("subscriptions", table =>
            {
                table.HasCheckConstraint("ck_subscriptions_match_rules_json", "ISJSON(match_rules, VALUE) = 1");
                table.HasCheckConstraint("ck_subscriptions_http_delivery_json", "ISJSON(http_delivery, VALUE) = 1");
                table.HasCheckConstraint(
                    "ck_subscriptions_mapping_config_json",
                    "mapping_config IS NULL OR ISJSON(mapping_config, VALUE) = 1");
            });
            entity.Property(e => e.CreatedAt).HasDefaultValueSql(currentTimestamp);
            entity.Property(e => e.HttpDelivery)
                .HasDefaultValueSql(JsonDefault("{\"body\": \"json\", \"method\": \"POST\", \"headers\": {}, \"version\": 1}"))
                .HasColumnType(jsonType);
            entity.Property(e => e.MatchRules).HasDefaultValueSql(JsonDefault("{}")).HasColumnType(jsonType);
            entity.Property(e => e.Status).HasDefaultValueSql(TextDefault("active"));
            entity.Property(e => e.MappingConfig).HasColumnType(jsonType);
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql(currentTimestamp);
        });

        modelBuilder.Entity<EventDelivery>(entity =>
        {
            entity.ToTable("event_deliveries", table =>
            {
                table.HasCheckConstraint(
                    "ck_event_deliveries_http_execution_snapshot_json",
                    "ISJSON(http_execution_snapshot, VALUE) = 1");
                table.HasCheckConstraint(
                    "ck_event_deliveries_mapping_config_snapshot_json",
                    "mapping_config_snapshot IS NULL OR ISJSON(mapping_config_snapshot, VALUE) = 1");
            });
            entity.HasIndex(
                    e => new { e.Status, e.LeaseExpiresAt, e.DeliverAfter, e.CreatedAt },
                    "idx_event_deliveries_claimable")
                .HasFilter("(status IN (N'pending', N'in_flight'))");
            entity.Property(e => e.Id).HasDefaultValueSql(generatedGuid);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql(currentTimestamp);
            entity.Property(e => e.HttpExecutionSnapshot).HasColumnType(jsonType);
            entity.Property(e => e.Status).HasDefaultValueSql(TextDefault("pending"));
            entity.Property(e => e.MappingConfigSnapshot).HasColumnType(jsonType);
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql(currentTimestamp);
        });

        modelBuilder.Entity<Tenant>(entity =>
        {
            entity.ToTable("tenants", table => table.HasCheckConstraint(
                "chk_tenants_slug_dns_label",
                "LEN(slug) BETWEEN 1 AND 63 AND slug NOT LIKE '%[^a-z0-9-]%' "
                + "AND LEFT(slug, 1) <> '-' AND RIGHT(slug, 1) <> '-'"));
            entity.Property(e => e.CreatedAt).HasDefaultValueSql(currentTimestamp);
            entity.Property(e => e.Status).HasDefaultValueSql(TextDefault("active"));
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql(currentTimestamp);
        });

        modelBuilder.Entity<Topic>(entity =>
        {
            entity.Property(e => e.CreatedAt).HasDefaultValueSql(currentTimestamp);
            entity.Property(e => e.Status).HasDefaultValueSql(TextDefault("active"));
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql(currentTimestamp);
        });

        modelBuilder.Entity<TopicSource>(entity =>
        {
            entity.Property(e => e.CreatedAt).HasDefaultValueSql(currentTimestamp);
            entity.Property(e => e.Status).HasDefaultValueSql(TextDefault("active"));
        });

        foreach (var property in modelBuilder.Model.GetEntityTypes().SelectMany(entity => entity.GetProperties()))
        {
            if (property.ClrType == typeof(System.Text.Json.JsonElement))
                property.SetValueConverter(new JsonElementStoredConverter());
            else if (property.ClrType == typeof(System.Text.Json.JsonElement?))
                property.SetValueConverter(new NullableJsonElementStoredConverter());
        }
    }
}
