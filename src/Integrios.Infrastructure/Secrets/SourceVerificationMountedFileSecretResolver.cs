using Integrios.Application.Secrets;

namespace Integrios.Infrastructure.Secrets;

internal sealed class SourceVerificationMountedFileSecretResolver(string root) : ISourceVerificationSecretResolver
{
    public static string DefaultRoot { get; } = OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Integrios", "secrets", "source")
        : "/run/secrets/integrios/source";

    public string ProviderName => "file";

    public Task<string> ResolveAsync(
        TenantSecretScope tenant,
        string secretReference,
        CancellationToken cancellationToken = default) =>
        MountedFileSecretReader.ReadAsync(root, tenant, secretReference, ProviderName, cancellationToken);
}
