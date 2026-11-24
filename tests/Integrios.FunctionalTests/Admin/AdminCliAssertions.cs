using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace Integrios.FunctionalTests.Admin;

internal static class AdminCliAssertions
{
    public static async Task ExecuteAsync(CliDatabase database, string sql)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    public static Task<long> CountAsync(
        CliDatabase database,
        string table,
        string where = "TRUE") =>
        AdminCliFixture.ScalarAsync<long>(database, $"SELECT COUNT(*) FROM {table} WHERE {where}");

    public static Task<string> ColumnShapeAsync(
        CliDatabase database,
        string table,
        string column) =>
        AdminCliFixture.ScalarAsync<string>(
            database,
            $"SELECT data_type || '|' || is_nullable FROM information_schema.columns WHERE table_schema = 'public' AND table_name = '{table}' AND column_name = '{column}'");

    public static Task<long> CountColumnsAsync(
        CliDatabase database,
        string table,
        params string[] columns)
    {
        string names = string.Join(", ", columns.Select(column => $"'{column}'"));
        return AdminCliFixture.ScalarAsync<long>(
            database,
            $"SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = 'public' AND table_name = '{table}' AND column_name IN ({names})");
    }

    public static string Hash(string secret) =>
        "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret))).ToLowerInvariant();
}
