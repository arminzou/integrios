using System.Text.Json;
using Integrios.Application.Authoring.Connectors;
using Integrios.Application.Authoring.Topics;
using Integrios.Application.Transforms;
using Integrios.Domain.Entities;
using Integrios.Domain.ValueObjects;
using MediatR;

namespace Integrios.Application.Authoring.Sources;

public sealed record UpdateSourceCommand(
    Guid TenantId,
    Guid Id,
    string? Name,
    JsonElement Configuration,
    SourceVerificationInput? Verification,
    JsonElement? InputRequirements,
    SourceMapping? Mapping,
    SourceEventIdentityRule? EventIdentityRule,
    IReadOnlyList<string>? EventTypes) : IRequest<SourceDto?>;

internal sealed class UpdateSourceCommandHandler(
    ISourceRepository sourceRepository,
    IConnectorReader connectorReader,
    ITopicRepository topicRepository,
    ITransformEvaluator evaluator)
    : IRequestHandler<UpdateSourceCommand, SourceDto?>
{
    public async Task<SourceDto?> Handle(UpdateSourceCommand command, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.Name))
            throw new SourceValidationException("Name is required.", "name");

        Source? source = await sourceRepository.GetByIdAsync(command.TenantId, command.Id, cancellationToken);
        if (source is null || source.Status != Domain.Enums.SourceStatus.Active)
            return null;
        Connector connector = await connectorReader.GetByIdAsync(source.ConnectorId, cancellationToken)
            ?? throw new SourceValidationException("The Source's Connector does not exist.");
        SourceAuthoringValidator.Validate(source.Type, command.Configuration, command.Verification, connector);
        SourceAuthoringValidator.ValidateRuntimeContract(
            source.Type, command.InputRequirements, command.Mapping, command.EventIdentityRule, evaluator);
        IReadOnlyList<string> eventTypes = SourceAuthoringValidator.ValidateEventTypes(command.EventTypes);
        SourceAuthoringValidator.ValidateEventIdentityRule(source.Type, command.EventIdentityRule);
        IReadOnlyList<SourceDeclaration> otherSources = (await topicRepository.ListSourceDeclarationsAsync(
                command.TenantId, [source.TopicId], cancellationToken))
            .Where(declaration => declaration.SourceId != source.Id)
            .ToList();
        TopicEventTypes.EnsureSameSpelling(eventTypes, otherSources);
        TopicEventTypes.EnsureNoSelectionLosesItsDeclaration(
            source.EventTypes.Where(previous => !eventTypes.Contains(previous, StringComparer.OrdinalIgnoreCase)),
            otherSources,
            await topicRepository.ListSubscriptionSelectionsAsync(command.TenantId, source.TopicId, cancellationToken));

        JsonElement configuration = source.Type == Domain.Enums.SourceType.Webhook
            ? WebhookCallbackConfiguration.WithCallbackId(
                command.Configuration,
                WebhookCallbackConfiguration.ExistingOrNew(source.Configuration))
            : command.Configuration.Clone();
        Source? updated = await sourceRepository.UpdateAsync(
            command.TenantId,
            command.Id,
            command.Name.Trim(),
            configuration,
            SourceAuthoringValidator.ToVerification(command.Verification),
            command.InputRequirements?.Clone(),
            command.Mapping,
            command.EventIdentityRule,
            eventTypes,
            cancellationToken);
        return updated is null ? null : SourceDto.From(updated);
    }

}
