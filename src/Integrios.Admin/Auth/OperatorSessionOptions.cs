using Microsoft.Extensions.Configuration;

namespace Integrios.Admin.Auth;

/// Browser session configuration for human members of the Operator.
///
/// Lifetime is fixed and non-sliding, and it is the documented upper bound on how long an already
/// issued session survives after the identity provider deprovisions someone: removing the provider
/// or application assignment prevents the next sign-in but cannot recall a live cookie.
public sealed record OperatorSessionOptions
{
    public const string SectionKey = "Integrios:Admin:Session";
    public const string LifetimeKey = SectionKey + ":Lifetime";
    public const string CookieName = "integrios_operator_session";

    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromHours(8);

    public required TimeSpan Lifetime { get; init; }

    public static OperatorSessionOptions FromConfiguration(IConfiguration configuration)
    {
        string? configured = configuration[LifetimeKey];
        if (string.IsNullOrWhiteSpace(configured))
            return new OperatorSessionOptions { Lifetime = DefaultLifetime };

        if (!TimeSpan.TryParse(configured, out TimeSpan lifetime))
            throw new InvalidOperationException($"{LifetimeKey} must be a TimeSpan value.");
        if (lifetime <= TimeSpan.Zero)
            throw new InvalidOperationException($"{LifetimeKey} must be greater than zero.");

        return new OperatorSessionOptions { Lifetime = lifetime };
    }
}
