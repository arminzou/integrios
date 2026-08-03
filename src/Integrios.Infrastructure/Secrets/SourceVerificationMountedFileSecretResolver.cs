using Integrios.Application.Secrets;

namespace Integrios.Infrastructure.Secrets;

internal sealed class SourceVerificationMountedFileSecretResolver(string root) : ISourceVerificationSecretResolver
{
    public static string DefaultRoot { get; } = OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Integrios", "secrets", "source")
        : "/run/secrets/integrios/source";

    public string ProviderName => "file";

    public async Task<string> ResolveAsync(TenantSecretScope tenant, string secretReference, CancellationToken cancellationToken = default)
    {
        SecretValueValidator.ValidateScope(tenant, secretReference, ProviderName);
        string path = Path.Combine(root, tenant.Slug, secretReference);

        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            byte[] bytes = new byte[SecretValueValidator.MaxBytes + 1];
            int total = 0;
            while (total < bytes.Length)
            {
                int read = await stream.ReadAsync(bytes.AsMemory(total), cancellationToken);
                if (read == 0)
                    break;
                total += read;
            }
            if (total == 0 || total > SecretValueValidator.MaxBytes)
                throw new SecretResolutionException(secretReference, ProviderName);
            string value = SecretValueValidator.StrictUtf8.GetString(bytes, 0, total);
            return SecretValueValidator.ValidateText(value, secretReference, ProviderName);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (SecretResolutionException) { throw; }
        catch { throw new SecretResolutionException(secretReference, ProviderName); }
    }
}
