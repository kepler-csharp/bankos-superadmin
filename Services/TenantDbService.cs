using System.Text.Json;
using System.Text.RegularExpressions;
using BankOsAdmin.Models;
using Npgsql;
using NpgsqlTypes;

namespace BankOsAdmin.Services;

/// <summary>
/// Direct PostgreSQL access to the BankOS databases.
/// • Read-only on each tenant DB for the DB Viewer and live statistics (SELECT only, validated identifiers).
/// • A small set of validated, parameterised writes used to reach full parity with the Laravel SuperAdmin
///   panel: provisioning the first administrador user in the tenant DB, updating the financial config and
///   registering the tenant domain in the central (landlord) DB. Everything degrades gracefully: if the
///   database can't be reached the tenant lifecycle still works through the API.
/// </summary>
public partial class TenantDbService
{
    private readonly IConfiguration _config;
    private readonly ILogger<TenantDbService> _logger;

    public TenantDbService(IConfiguration config, ILogger<TenantDbService> logger)
    {
        _config = config;
        _logger = logger;
    }

    [GeneratedRegex("^[a-zA-Z0-9_]+$")]
    private static partial Regex IdentifierRegex();

    private string ConnString(string tenantId)
    {
        var host = _config["Database:Host"] ?? "bank-os.duckdns.org";
        var port = _config["Database:Port"] ?? "5433";
        var user = _config["Database:User"] ?? "bankos";
        var pass = _config["Database:Password"] ?? "secret";
        var prefix = _config["Database:TenantDbPrefix"] ?? "tenant_";
        var db = $"{prefix}{tenantId}";
        return $"Host={host};Port={port};Database={db};Username={user};Password={pass};" +
               "SSL Mode=Prefer;Trust Server Certificate=true;Timeout=10;Command Timeout=15";
    }

    /// <summary>Connection to the central (landlord) database that holds tenants, tenant_configs and domains.</summary>
    private string CentralConnString()
    {
        var host = _config["Database:Host"] ?? "bank-os.duckdns.org";
        var port = _config["Database:Port"] ?? "5433";
        var user = _config["Database:User"] ?? "bankos";
        var pass = _config["Database:Password"] ?? "secret";
        var db = _config["Database:CentralDb"] ?? "bankos_central";
        return $"Host={host};Port={port};Database={db};Username={user};Password={pass};" +
               "SSL Mode=Prefer;Trust Server Certificate=true;Timeout=10;Command Timeout=15";
    }

    public async Task<(List<string> Tables, string? Error)> GetTablesAsync(string tenantId)
    {
        try
        {
            await using var conn = new NpgsqlConnection(ConnString(tenantId));
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                "SELECT tablename FROM pg_tables WHERE schemaname = 'public' ORDER BY tablename", conn);
            await using var reader = await cmd.ExecuteReaderAsync();
            var tables = new List<string>();
            while (await reader.ReadAsync()) tables.Add(reader.GetString(0));
            return (tables, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DB tables query failed for {tenant}", tenantId);
            return (new(), ex.Message);
        }
    }

    public async Task<(List<string> Columns, List<Dictionary<string, object?>> Rows, long Total, string? Error)>
        GetRowsAsync(string tenantId, string table, int limit = 100)
    {
        if (!IdentifierRegex().IsMatch(table))
            return (new(), new(), 0, "Nombre de tabla inválido.");

        try
        {
            await using var conn = new NpgsqlConnection(ConnString(tenantId));
            await conn.OpenAsync();

            await using var countCmd = new NpgsqlCommand($"SELECT COUNT(*) FROM \"{table}\"", conn);
            var total = Convert.ToInt64(await countCmd.ExecuteScalarAsync() ?? 0L);

            var orderBy = "";
            await using (var colCmd = new NpgsqlCommand(
                "SELECT 1 FROM information_schema.columns WHERE table_name=@t AND column_name='created_at' LIMIT 1", conn))
            {
                colCmd.Parameters.AddWithValue("t", table);
                if (await colCmd.ExecuteScalarAsync() != null) orderBy = "ORDER BY created_at DESC";
            }

            await using var cmd = new NpgsqlCommand($"SELECT * FROM \"{table}\" {orderBy} LIMIT {limit}", conn);
            await using var reader = await cmd.ExecuteReaderAsync();

            var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToList();
            var rows = new List<Dictionary<string, object?>>();
            while (await reader.ReadAsync())
            {
                var row = new Dictionary<string, object?>();
                for (int i = 0; i < reader.FieldCount; i++)
                    row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                rows.Add(row);
            }
            return (columns, rows, total, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DB rows query failed for {tenant}.{table}", tenantId, table);
            return (new(), new(), 0, ex.Message);
        }
    }

    /// <summary>Best-effort live metrics. Returns Reachable=false if the DB can't be queried.</summary>
    public async Task<TenantStats> GetStatsAsync(string tenantId)
    {
        var stats = new TenantStats();
        try
        {
            await using var conn = new NpgsqlConnection(ConnString(tenantId));
            await conn.OpenAsync();
            stats.Reachable = true;

            // discover tables once
            var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using (var cmd = new NpgsqlCommand(
                "SELECT tablename FROM pg_tables WHERE schemaname='public'", conn))
            await using (var r = await cmd.ExecuteReaderAsync())
                while (await r.ReadAsync()) present.Add(r.GetString(0));
            stats.TableCount = present.Count;

            stats.Users = await CountIfExists(conn, present, "users");
            stats.Accounts = await CountIfExists(conn, present, "accounts");
            stats.Transactions = await CountIfExists(conn, present, "transactions");
            stats.Cards = await CountIfExists(conn, present, "cards");
        }
        catch (Exception ex)
        {
            stats.Reachable = false;
            stats.Error = ex.Message;
        }
        return stats;
    }

    private static async Task<long> CountIfExists(NpgsqlConnection conn, HashSet<string> present, string table)
    {
        if (!present.Contains(table)) return 0;
        try
        {
            await using var cmd = new NpgsqlCommand($"SELECT COUNT(*) FROM \"{table}\"", conn);
            return Convert.ToInt64(await cmd.ExecuteScalarAsync() ?? 0L);
        }
        catch { return 0; }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // Provisioning writes — mirror what the Laravel SuperAdmin Livewire panel does.
    // All are best-effort: on failure they return an error string and the caller
    // continues (the tenant itself was already created through the API).
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Creates the first <c>administrador</c> user inside the freshly-provisioned tenant database,
    /// using a bcrypt hash compatible with Laravel's <c>Hash::check</c>. Retries briefly because the
    /// tenant DB is created synchronously by the API right before this runs.
    /// </summary>
    public async Task<string?> CreateAdminUserAsync(string tenantId, string name, string email, string password)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            return "Faltan datos del administrador (correo o contraseña).";

        var hash = BCrypt.Net.BCrypt.HashPassword(password, workFactor: 12);
        Exception? last = null;

        for (var attempt = 1; attempt <= 4; attempt++)
        {
            try
            {
                await using var conn = new NpgsqlConnection(ConnString(tenantId));
                await conn.OpenAsync();

                // Does a users.status column exist? (added by a later migration)
                var hasStatus = false;
                await using (var cc = new NpgsqlCommand(
                    "SELECT 1 FROM information_schema.columns WHERE table_name='users' AND column_name='status' LIMIT 1", conn))
                    hasStatus = await cc.ExecuteScalarAsync() != null;

                var sql = hasStatus
                    ? @"INSERT INTO users (id, name, email, password, role, status, created_at, updated_at)
                        VALUES (@id, @name, @email, @password, 'administrador', 'active', NOW(), NOW())
                        ON CONFLICT (email) DO NOTHING"
                    : @"INSERT INTO users (id, name, email, password, role, created_at, updated_at)
                        VALUES (@id, @name, @email, @password, 'administrador', NOW(), NOW())
                        ON CONFLICT (email) DO NOTHING";

                await using var cmd = new NpgsqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("id", Guid.NewGuid());
                cmd.Parameters.AddWithValue("name", string.IsNullOrWhiteSpace(name) ? "Administrador" : name);
                cmd.Parameters.AddWithValue("email", email.Trim());
                cmd.Parameters.AddWithValue("password", hash);
                var rows = await cmd.ExecuteNonQueryAsync();
                return rows == 0 ? "Ya existía un usuario con ese correo en el banco." : null;
            }
            catch (Exception ex)
            {
                last = ex;
                await Task.Delay(attempt * 600); // brief back-off while the new DB finishes migrating
            }
        }

        _logger.LogWarning(last, "Could not create admin user for tenant {tenant}", tenantId);
        return $"No se pudo crear el usuario administrador en la base del banco: {last?.Message}";
    }

    /// <summary>Returns the email of the first <c>administrador</c> in the tenant DB, or null if unavailable.</summary>
    public async Task<string?> GetAdminEmailAsync(string tenantId)
    {
        try
        {
            await using var conn = new NpgsqlConnection(ConnString(tenantId));
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                "SELECT email FROM users WHERE role = 'administrador' ORDER BY created_at LIMIT 1", conn);
            var result = await cmd.ExecuteScalarAsync();
            return result as string;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not resolve admin email for tenant {tenant}", tenantId);
            return null;
        }
    }

    /// <summary>
    /// Updates the tenant's financial configuration in the central <c>tenant_configs</c> table.
    /// The SuperAdmin API only updates name/status, so this fills the gap to match the Laravel panel.
    /// </summary>
    public async Task<string?> UpdateConfigAsync(
        string tenantId, string currency, decimal maxAmount, string feeType, decimal feeValue,
        Dictionary<string, decimal>? exchangeRates, string? webhookUrl)
    {
        try
        {
            await using var conn = new NpgsqlConnection(CentralConnString());
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                @"UPDATE tenant_configs
                     SET currency = @currency,
                         max_transaction_amount = @maxAmount,
                         transfer_fee_type = @feeType,
                         transfer_fee_value = @feeValue,
                         exchange_rates = @rates,
                         webhook_url = @webhook,
                         updated_at = NOW()
                   WHERE tenant_id = @tenantId", conn);

            cmd.Parameters.AddWithValue("currency", currency);
            cmd.Parameters.AddWithValue("maxAmount", maxAmount);
            cmd.Parameters.AddWithValue("feeType", feeType);
            cmd.Parameters.AddWithValue("feeValue", feeValue);
            cmd.Parameters.AddWithValue("rates",
                NpgsqlDbType.Jsonb, JsonSerializer.Serialize(exchangeRates ?? new()));
            cmd.Parameters.AddWithValue("webhook", (object?)webhookUrl ?? DBNull.Value);
            cmd.Parameters.AddWithValue("tenantId", tenantId);

            var rows = await cmd.ExecuteNonQueryAsync();
            return rows == 0 ? "No se encontró la configuración del banco para actualizar." : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not update config for tenant {tenant}", tenantId);
            return $"No se pudo actualizar la configuración financiera: {ex.Message}";
        }
    }

    /// <summary>Registers the tenant subdomain in the central <c>domains</c> table (idempotent).</summary>
    public async Task<string?> CreateDomainAsync(string tenantId, string domain)
    {
        try
        {
            await using var conn = new NpgsqlConnection(CentralConnString());
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                @"INSERT INTO domains (domain, tenant_id, created_at, updated_at)
                  VALUES (@domain, @tenantId, NOW(), NOW())
                  ON CONFLICT (domain) DO NOTHING", conn);
            cmd.Parameters.AddWithValue("domain", domain);
            cmd.Parameters.AddWithValue("tenantId", tenantId);
            await cmd.ExecuteNonQueryAsync();
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not register domain for tenant {tenant}", tenantId);
            return ex.Message;
        }
    }
}
