using Integrios.Domain.Connections;
using Integrios.Domain.Delivery;
using Integrios.Domain.Events;
using Integrios.Domain.Integrations;
using Integrios.Domain.Subscriptions;
using Integrios.Domain.Tenants;
using Integrios.Domain.Topics;
using Integrios.Infrastructure.Outbox;
using Microsoft.EntityFrameworkCore;
using DomainEvent = Integrios.Domain.Events.Event;

namespace Integrios.Infrastructure.Data;

internal sealed class IntegriosDbContext(DbContextOptions<IntegriosDbContext> options) : DbContext(options)
{
    public DbSet<AdminKey> AdminKeys => Set<AdminKey>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
    public DbSet<Connection> Connections => Set<Connection>();
    public DbSet<DeliveryAttempt> DeliveryAttempts => Set<DeliveryAttempt>();
    public DbSet<DomainEvent> Events => Set<DomainEvent>();
    public DbSet<Integration> Integrations => Set<Integration>();
    public DbSet<OutboxEntry> Outboxes => Set<OutboxEntry>();
    public DbSet<SourceEndpoint> SourceEndpoints => Set<SourceEndpoint>();
    public DbSet<Subscription> Subscriptions => Set<Subscription>();
    public DbSet<SubscriptionDelivery> SubscriptionDeliveries => Set<SubscriptionDelivery>();
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<Topic> Topics => Set<Topic>();
    public DbSet<TopicSource> TopicSources => Set<TopicSource>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(IntegriosDbContext).Assembly);
}
