// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Npgsql;

namespace Vais.Agents.Runtime.Host;

internal static class ConnectionStringDisplay
{
    /// <summary>Returns a compact <c>user@host:port</c> display for an Npgsql connection string.</summary>
    public static string ForPostgres(string connectionString)
    {
        try
        {
            var b = new NpgsqlConnectionStringBuilder(connectionString);
            var user = string.IsNullOrEmpty(b.Username) ? "" : $"{b.Username}@";
            var port = b.Port > 0 ? $":{b.Port}" : "";
            return $"{user}{b.Host}{port}";
        }
        catch
        {
            return "<invalid>";
        }
    }

    /// <summary>Returns the connection string with the password field cleared.</summary>
    public static string RedactPostgres(string connectionString)
    {
        try
        {
            var b = new NpgsqlConnectionStringBuilder(connectionString) { Password = null };
            return b.ConnectionString;
        }
        catch
        {
            return connectionString;
        }
    }
}
