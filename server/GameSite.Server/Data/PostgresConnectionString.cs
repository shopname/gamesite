using Npgsql;

namespace GameSite.Server.Data;

/// <summary>
/// Neon や Render がくれる URL 形式（postgresql://user:pass@host/db?sslmode=require）を、
/// Npgsql が読める形式（Host=...;Username=...）に変換する。すでに Npgsql 形式ならそのまま返す。
/// </summary>
public static class PostgresConnectionString
{
    public static string Normalize(string value)
    {
        value = value.Trim();
        if (!value.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) &&
            !value.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
            return value;

        var uri = new Uri(value);
        var userInfo = uri.UserInfo.Split(':', 2);
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.IsDefaultPort || uri.Port <= 0 ? 5432 : uri.Port,
            Database = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/')),
            Username = Uri.UnescapeDataString(userInfo[0]),
            Password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : null,
            SslMode = SslMode.Require, // クラウドの DB は暗号化接続が前提
        };

        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = pair.Split('=', 2);
            if (kv.Length == 2 && kv[0].Equals("sslmode", StringComparison.OrdinalIgnoreCase) &&
                Enum.TryParse<SslMode>(kv[1].Replace("-", ""), ignoreCase: true, out var mode))
            {
                builder.SslMode = mode;
            }
        }
        return builder.ConnectionString;
    }
}
