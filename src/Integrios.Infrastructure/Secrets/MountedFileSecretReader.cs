using Integrios.Application.Secrets;

namespace Integrios.Infrastructure.Secrets;

internal static class MountedFileSecretReader
{
    public static async Task<string> ReadAsync(
        string root,
        TenantSecretScope tenant,
        string secretReference,
        string providerName,
        CancellationToken cancellationToken)
    {
        SecretValueValidator.ValidateScope(tenant, secretReference, providerName);
        string path = Path.Combine(root, tenant.Slug, secretReference);

        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096,
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
                throw new SecretResolutionException(secretReference, providerName);

            string value = SecretValueValidator.StrictUtf8.GetString(bytes, 0, total);
            return SecretValueValidator.ValidateText(value, secretReference, providerName);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (SecretResolutionException)
        {
            throw;
        }
        catch
        {
            throw new SecretResolutionException(secretReference, providerName);
        }
    }
}
