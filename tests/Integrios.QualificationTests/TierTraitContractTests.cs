using System.Reflection;

namespace Integrios.QualificationTests;

/// <summary>
/// CI selects qualification tiers by the <c>Tier</c> trait, so a test method that declares no
/// tier is invisible to every tier filter and would silently run only in the unfiltered nightly
/// and release runs. That is exactly how three classes previously became release-only, so the
/// invariant is enforced here rather than trusted to a hand-maintained filter in the workflow.
/// </summary>
[Trait("Category", "Qualification")]
[Trait("Tier", "database")]
public sealed class TierTraitContractTests
{
    private static readonly HashSet<string> KnownTiers = ["database", "smoke", "deep"];

    [Fact]
    public void EveryQualificationTestDeclaresExactlyOneKnownTier()
    {
        var violations = new List<string>();

        foreach (MethodInfo method in typeof(TierTraitContractTests).Assembly
                     .GetTypes()
                     .Where(type => type.IsClass && !type.IsAbstract)
                     .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                     .Where(IsTestMethod))
        {
            string[] tiers = [.. TierValues(method).Concat(TierValues(method.DeclaringType!))];
            string name = $"{method.DeclaringType!.Name}.{method.Name}";

            if (tiers.Length == 0)
                violations.Add($"{name} declares no Tier trait");
            else if (tiers.Length > 1)
                violations.Add($"{name} declares {tiers.Length} Tier traits: {string.Join(", ", tiers)}");
            else if (!KnownTiers.Contains(tiers[0]))
                violations.Add($"{name} declares unknown Tier '{tiers[0]}'");
        }

        Assert.True(
            violations.Count == 0,
            $"""
             Every qualification test must declare exactly one Tier from [{string.Join(", ", KnownTiers.Order(StringComparer.Ordinal))}],
             on the method or its class. Add the trait, and register any new tier in both this test
             and the tier selection in .github/workflows/ci.yml.

             {string.Join(Environment.NewLine, violations)}
             """);
    }

    private static bool IsTestMethod(MethodInfo method) =>
        method.GetCustomAttributes<FactAttribute>(inherit: true).Any()
        || method.GetCustomAttributes<TheoryAttribute>(inherit: true).Any();

    private static IEnumerable<string> TierValues(MemberInfo target) => target
        .GetCustomAttributesData()
        .Where(attribute => attribute.AttributeType == typeof(TraitAttribute)
            && attribute.ConstructorArguments.Count == 2
            && (string?)attribute.ConstructorArguments[0].Value == "Tier")
        .Select(attribute => (string)attribute.ConstructorArguments[1].Value!);
}
