namespace Integrios.Admin.Auth;

public sealed class AdminAuthOptions
{
    public const string SectionName = "AdminAuth";

    public string? Token { get; init; }
}
