using Integrios.Application.Delivery;

namespace Integrios.Application.UnitTests;

public sealed class SubscriptionRoutingEvaluatorTests
{
    [Fact]
    public void SelectTargets_MatchesASelectedType_CaseInsensitively()
    {
        var candidate = Candidate(["Payment.Created"]);

        var target = SubscriptionRoutingEvaluator.SelectTargets("payment.created", [candidate]).ShouldHaveSingleItem();

        target.SubscriptionId.ShouldBe(candidate.SubscriptionId);
    }

    [Fact]
    public void SelectTargets_MatchesAnyOfSeveralSelectedTypes()
    {
        var candidate = Candidate(["payment.updated", "payment.created"]);

        var target = SubscriptionRoutingEvaluator.SelectTargets("PAYMENT.CREATED", [candidate]).ShouldHaveSingleItem();

        target.SubscriptionId.ShouldBe(candidate.SubscriptionId);
    }

    [Fact]
    public void SelectTargets_IgnoresSubscriptionsSelectingOtherTypes()
    {
        var targets = SubscriptionRoutingEvaluator.SelectTargets(
            "payment.created",
            [Candidate(["payment.updated"]), Candidate(["payment.created.v2", "created"])]);

        targets.ShouldBeEmpty();
    }

    [Fact]
    public void SelectTargets_OrdersDeterministicallyByOrderIndexThenSubscriptionId()
    {
        var firstId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var secondId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var candidates = new[]
        {
            Candidate(["payment.created"], secondId, orderIndex: 5),
            Candidate(["payment.created"], Guid.NewGuid(), orderIndex: 10),
            Candidate(["payment.created"], firstId, orderIndex: 5)
        };

        var targets = SubscriptionRoutingEvaluator.SelectTargets("payment.created", candidates);

        targets.Select(target => target.SubscriptionId).ShouldBe([firstId, secondId, candidates[1].SubscriptionId]);
    }

    private static SubscriptionRoutingCandidate Candidate(
        IReadOnlyList<string> eventTypes,
        Guid? subscriptionId = null,
        int orderIndex = 0)
        => new(
            subscriptionId ?? Guid.NewGuid(),
            Guid.NewGuid(),
            orderIndex,
            eventTypes,
            """{"engine":"jsonata","version":"1","expression":"$"}""",
            "webhook",
            """{"version":1,"base_uri":"https://example.test/deliver","request":{"version":1,"method":"POST","headers":{},"body":"json"}}""");
}
