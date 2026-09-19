using System.Text.Json;
using Integrios.Application.Authoring.Connectors;
using Integrios.Application.Authoring.Topics;
using Integrios.Application.Transforms;
using Integrios.Domain.Entities;
using Integrios.Domain.Enums;
using Integrios.Domain.ValueObjects;
using MediatR;

namespace Integrios.Application.Authoring.Sources;

public sealed record CreateSourceCommand(
    Guid TenantId,
    Guid ConnectorId,
    Guid TopicId,
    string? Name,
    SourceType Type,
    JsonElement Configuration,
    SourceVerificationInput? Verification,
    JsonElement? InputRequirements,
    SourceMapping? Mapping,
    SourceEventIdentityRule? EventIdentityRule,
    IReadOnlyList<string>? EventTypes) : IRequest<SourceDto>;

internal sealed class CreateSourceCommandHandler(
    ISourceRepository sourceRepository,
    IConnectorReader connectorReader,
    ITopicRepository topicRepository,
    IAuthoringLock authoringLock,
    ITransformEvaluator evaluator)
    : IRequestHandler<CreateSourceCommand, SourceDto>
{
    public async Task<SourceDto> Handle(CreateSourceCommand command, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.Name))
            throw new SourceValidationException("Name is required.", "name");

        await using IAsyncDisposable lease = await authoringLock.AcquireAsync(
            AuthoringResource.Topic,
            [command.TopicId],
            cancellationToken);
        Topic topic = await topicRepository.GetByIdAsync(command.TenantId, command.TopicId, cancellationToken)
            ?? throw new SourceValidationException("Source Topic must exist in the same Tenant.");
        Connector connector = await connectorReader.GetByIdAsync(command.ConnectorId, cancellationToken)
            ?? throw new SourceValidationException("The specified Connector does not exist.");
        SourceAuthoringValidator.Validate(command.Type, command.Configuration, command.Verification, connector);
        SourceAuthoringValidator.ValidateRuntimeContract(
            command.Type, command.InputRequirements, command.Mapping, command.EventIdentityRule, evaluator);
        IReadOnlyList<string> eventTypes = SourceAuthoringValidator.ValidateEventTypes(command.EventTypes);
        SourceAuthoringValidator.ValidateEventIdentityRule(command.Type, command.EventIdentityRule);

        TopicEventTypes.EnsureSameSpelling(
            eventTypes,
            await topicRepository.ListSourceDeclarationsAsync(command.TenantId, [command.TopicId], cancellationToken));

        var now = DateTimeOffset.UtcNow;
        JsonElement configuration = command.Type == SourceType.Webhook
            ? WebhookCallbackConfiguration.WithCallbackId(command.Configuration, Guid.NewGuid())
            : command.Configuration.Clone();
        var source = new Source
        {
            Id = Guid.NewGuid(),
            TenantId = command.TenantId,
            ConnectorId = command.ConnectorId,
            TopicId = command.TopicId,
            Name = command.Name.Trim(),
            Type = command.Type,
            EventTypes = eventTypes,
            Configuration = configuration,
            Verification = SourceAuthoringValidator.ToVerification(command.Verification),
            InputRequirements = command.InputRequirements?.Clone(),
            Mapping = command.Mapping,
            EventIdentityRule = command.EventIdentityRule,
            Revision = Guid.NewGuid().ToString("N"),
            // Declared and authorable first; intake opens only when an Operator enables it.
            Status = OperationalStatus.Inactive,
            CreatedAt = now,
            UpdatedAt = now,
        };
        return SourceDto.From(await sourceRepository.CreateAsync(source, cancellationToken));
    }
}
