using Integrios.Application.Secrets;

namespace Integrios.Infrastructure.Secrets;

internal sealed class DestinationAuthenticationMountedFileSecretResolver(string root)
    : IDestinationAuthenticationSecretResolver
{
    public static string DefaultRoot { get; } = OperatingSystem.IsWindows()
        ? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Integrios",
            "secrets",
            "destination")
        : "/run/secrets/integrios/destination";

    public string ProviderName => "file";

    public Task<string> ResolveAsync(
        TenantSecretScope tenant,
        string secretReference,
        CancellationToken cancellationToken) =>
        MountedFileSecretReader.ReadAsync(root, tenant, secretReference, ProviderName, cancellationToken);
}
