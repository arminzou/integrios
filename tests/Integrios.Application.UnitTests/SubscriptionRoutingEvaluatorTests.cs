using Integrios.Application.Delivery;

namespace Integrios.Application.UnitTests;

public sealed class SubscriptionRoutingEvaluatorTests
{
    [Fact]
    public void SelectTargets_MatchesCurrentRule_CaseInsensitively()
    {
        var candidate = Candidate("""{"event_type":"Payment.Created"}""");

        var target = SubscriptionRoutingEvaluator.SelectTargets("payment.created", [candidate]).ShouldHaveSingleItem();

        target.SubscriptionId.ShouldBe(candidate.SubscriptionId);
    }

    [Fact]
    public void SelectTargets_MatchesLegacyArrayRule()
    {
        var candidate = Candidate("""{"event_types":["payment.updated","payment.created"]}""");

        var target = SubscriptionRoutingEvaluator.SelectTargets("PAYMENT.CREATED", [candidate]).ShouldHaveSingleItem();

        target.SubscriptionId.ShouldBe(candidate.SubscriptionId);
    }

    [Fact]
    public void SelectTargets_CurrentRuleTakesPrecedenceOverLegacyCompatibilityRule()
    {
        var candidate = Candidate(
            """{"event_type":"payment.updated","event_types":["payment.created"]}""");

        var targets = SubscriptionRoutingEvaluator.SelectTargets("payment.created", [candidate]);

        targets.ShouldBeEmpty();
    }

    [Fact]
    public void SelectTargets_IgnoresMalformedAndNonMatchingRules()
    {
        var targets = SubscriptionRoutingEvaluator.SelectTargets(
            "payment.created",
            [
                Candidate("not-json"),
                Candidate("[]"),
                Candidate("{}"),
                Candidate("""{"event_types":[42,null]}""")
            ]);

        targets.ShouldBeEmpty();
    }

    [Fact]
    public void SelectTargets_OrdersDeterministicallyByOrderIndexThenSubscriptionId()
    {
        var firstId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var secondId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var candidates = new[]
        {
            Candidate("""{"event_type":"payment.created"}""", secondId, orderIndex: 5),
            Candidate("""{"event_type":"payment.created"}""", Guid.NewGuid(), orderIndex: 10),
            Candidate("""{"event_type":"payment.created"}""", firstId, orderIndex: 5)
        };

        var targets = SubscriptionRoutingEvaluator.SelectTargets("payment.created", candidates);

        targets.Select(target => target.SubscriptionId).ShouldBe([firstId, secondId, candidates[1].SubscriptionId]);
    }

    private static SubscriptionRoutingCandidate Candidate(
        string? matchRulesJson,
        Guid? subscriptionId = null,
        int orderIndex = 0)
        => new(
            subscriptionId ?? Guid.NewGuid(),
            Guid.NewGuid(),
            orderIndex,
            matchRulesJson,
            """{"engine":"jsonata","version":"1","expression":"$"}""",
            "webhook",
            """{"version":1,"base_uri":"https://example.test/deliver","request":{"version":1,"method":"POST","headers":{},"body":"json"}}""");
}
