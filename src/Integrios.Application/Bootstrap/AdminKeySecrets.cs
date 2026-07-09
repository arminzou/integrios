using System.Security.Cryptography;
using System.Text;

namespace Integrios.Application.Bootstrap;

internal static class AdminKeySecrets
{
    public static string Generate() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    public static string Hash(string secret) =>
        "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret))).ToLowerInvariant();
}
