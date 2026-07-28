using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace Integrios.QualificationTests;

// Shared by the database lifecycle test classes. Those classes are split so xUnit can run them as
// parallel collections; the helpers they have in common live here rather than being duplicated.
internal static class DatabaseLifecycleAssertions
{
    public static async Task ExecuteAsync(QualificationDatabase database, string sql)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    public static Task<long> CountAsync(
        QualificationDatabase database,
        string table,
        string where = "TRUE") =>
        DatabaseLifecycleFixture.ScalarAsync<long>(database, $"SELECT COUNT(*) FROM {table} WHERE {where}");

    public static Task<string> ColumnShapeAsync(
        QualificationDatabase database,
        string table,
        string column) =>
        DatabaseLifecycleFixture.ScalarAsync<string>(
            database,
            $"SELECT data_type || '|' || is_nullable FROM information_schema.columns WHERE table_schema = 'public' AND table_name = '{table}' AND column_name = '{column}'");

    public static Task<long> CountColumnsAsync(
        QualificationDatabase database,
        string table,
        params string[] columns)
    {
        string names = string.Join(", ", columns.Select(column => $"'{column}'"));
        return DatabaseLifecycleFixture.ScalarAsync<long>(
            database,
            $"SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = 'public' AND table_name = '{table}' AND column_name IN ({names})");
    }

    public static string Hash(string secret) =>
        "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret))).ToLowerInvariant();
}
