namespace Integrios.Admin.Auth;

public sealed record OperatorPasswordOptions
{
    public const string SectionKey = "Integrios:Admin:Password";
    public const string EnabledKey = SectionKey + ":Enabled";

    public required bool Enabled { get; init; }

    public static OperatorPasswordOptions FromConfiguration(IConfiguration configuration) => new()
    {
        Enabled = configuration.GetValue<bool?>(EnabledKey) ?? false,
    };
}
