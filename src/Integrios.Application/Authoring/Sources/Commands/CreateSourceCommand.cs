using System.Text.Json;
using Integrios.Application.Authoring.Connectors;
using Integrios.Application.Authoring.Topics;
using Integrios.Domain.Entities;
using Integrios.Domain.Enums;
using Integrios.Domain.ValueObjects;
using Integrios.Application.Transforms;
using MediatR;

namespace Integrios.Application.Authoring.Sources;

public sealed record CreateSourceCommand(
    Guid TenantId,
    Guid ConnectorId,
    Guid TopicId,
    SourceType Type,
    JsonElement Configuration,
    SourceVerificationInput? Verification,
    JsonElement? InputRequirements,
    SourceMapping? Mapping,
    SourceEventIdentityRule? EventIdentityRule) : IRequest<SourceDto>;

internal sealed class CreateSourceCommandHandler(
    ISourceRepository sourceRepository,
    IConnectorReader connectorReader,
    ITopicRepository topicRepository,
    ITransformEvaluator evaluator)
    : IRequestHandler<CreateSourceCommand, SourceDto>
{
    public async Task<SourceDto> Handle(CreateSourceCommand command, CancellationToken cancellationToken)
    {
        Topic topic = await topicRepository.GetByIdAsync(command.TenantId, command.TopicId, cancellationToken)
            ?? throw new SourceValidationException("Source Topic must exist in the same Tenant.");
        if (topic.Status != OperationalStatus.Active)
            throw new SourceValidationException("Source Topic must be active.");
        Connector connector = await connectorReader.GetByIdAsync(command.ConnectorId, cancellationToken)
            ?? throw new SourceValidationException("The specified Connector does not exist.");
        SourceAuthoringValidator.Validate(command.Type, command.Configuration, command.Verification, connector);
        SourceAuthoringValidator.ValidateRuntimeContract(
            command.Type, command.InputRequirements, command.Mapping, command.EventIdentityRule, evaluator);

        var now = DateTimeOffset.UtcNow;
        JsonElement configuration = command.Type == SourceType.Webhook
            ? WebhookCallbackConfiguration.WithCallbackId(command.Configuration, Guid.NewGuid())
            : command.Configuration.Clone();
        var source = new Source
        {
            Id = Guid.NewGuid(), TenantId = command.TenantId, ConnectorId = command.ConnectorId, TopicId = command.TopicId,
            Type = command.Type, Configuration = configuration,
            Verification = SourceAuthoringValidator.ToVerification(command.Verification),
            InputRequirements = command.InputRequirements?.Clone(),
            Mapping = command.Mapping,
            EventIdentityRule = command.EventIdentityRule,
            Revision = Guid.NewGuid().ToString("N"), Status = SourceStatus.Active,
            CreatedAt = now, UpdatedAt = now,
        };
        return SourceDto.From(await sourceRepository.CreateAsync(source, cancellationToken));
    }
}
