using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using BankOsAdmin.Models;

namespace BankOsAdmin.Services;

/// <summary>
/// Talks to the BankOS (Laravel) SuperAdmin REST API using the Master API Key.
/// Only SuperAdmin-scoped endpoints (tenant lifecycle + public bank list) are used.
/// </summary>
public class BankOsApiService
{
    private readonly HttpClient _http;
    private readonly ILogger<BankOsApiService> _logger;

    public BankOsApiService(HttpClient http, IConfiguration config, ILogger<BankOsApiService> logger)
    {
        _http = http;
        _logger = logger;

        var baseUrl = config["BankOS:ApiBaseUrl"] ?? "http://bank-os.duckdns.org:8080";
        _http.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
        _http.Timeout = TimeSpan.FromSeconds(60);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    private void UseKey(string apiKey)
    {
        // The SuperAdmin endpoints are guarded by the master.key middleware, which checks the
        // X-API-Key header only. No JWT bearer is involved for these routes.
        _http.DefaultRequestHeaders.Remove("X-API-Key");
        _http.DefaultRequestHeaders.Add("X-API-Key", apiKey);
    }

    // ── Auth check ───────────────────────────────────────────────────────────

    /// <summary>Returns null on success, or an error message if the key is invalid / unreachable.</summary>
    public async Task<string?> ValidateKeyAsync(string apiKey)
    {
        var (_, err) = await GetTenantsAsync(apiKey, perPage: 1);
        return err;
    }

    // ── Tenants ────────────────────────────────────────────────────────────────

    public async Task<(List<TenantModel>? Tenants, string? Error)> GetTenantsAsync(string apiKey, int perPage = 100)
    {
        UseKey(apiKey);
        try
        {
            var resp = await _http.GetAsync($"api/v1/tenants?per_page={perPage}");
            var json = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode)
                return (null, ExtractError(json, (int)resp.StatusCode));

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.False)
                return (null, "La API respondió success=false.");

            // data may be an array, or a paginator object with an "items"/"data" array.
            var dataEl = root.TryGetProperty("data", out var d) ? d : root;
            JsonElement arrayEl = dataEl;
            if (dataEl.ValueKind == JsonValueKind.Object)
            {
                if (dataEl.TryGetProperty("items", out var items)) arrayEl = items;
                else if (dataEl.TryGetProperty("data", out var inner)) arrayEl = inner;
            }

            var tenants = new List<TenantModel>();
            if (arrayEl.ValueKind == JsonValueKind.Array)
                foreach (var item in arrayEl.EnumerateArray())
                    tenants.Add(ParseTenant(item, item));

            return (tenants, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching tenants");
            return (null, ex.Message);
        }
    }

    public async Task<(TenantModel? Tenant, string? Error)> GetTenantAsync(string apiKey, string slug)
    {
        UseKey(apiKey);
        try
        {
            var resp = await _http.GetAsync($"api/v1/tenants/{slug}");
            var json = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode)
                return (null, ExtractError(json, (int)resp.StatusCode));

            using var doc = JsonDocument.Parse(json);
            var data = doc.RootElement.TryGetProperty("data", out var d) ? d : doc.RootElement;

            // Response can be { data: { tenant: {...}, config: {...} } } OR { data: {...flat...} }
            var tenantEl = data.TryGetProperty("tenant", out var te) ? te : data;
            var configEl = data.TryGetProperty("config", out var ce) ? ce :
                           (tenantEl.TryGetProperty("config", out var ce2) ? ce2 : default);

            return (ParseTenant(tenantEl, configEl), null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching tenant {slug}", slug);
            return (null, ex.Message);
        }
    }

    public async Task<(TenantModel? Tenant, string? Error)> CreateTenantAsync(string apiKey, CreateTenantViewModel vm)
    {
        UseKey(apiKey);

        // exchange_rates is required by the API and every value must be numeric >= 0.
        // Always include the base currency at 1, plus any foreign rates the operator entered.
        var rates = new Dictionary<string, decimal> { [vm.Currency] = 1m };
        void AddRate(string code, decimal value)
        {
            if (value > 0 && !rates.ContainsKey(code)) rates[code] = value;
        }
        AddRate("USD", vm.ExchangeUSD);
        AddRate("EUR", vm.ExchangeEUR);
        AddRate("GBP", vm.ExchangeGBP);

        // Only the fields the SuperAdmin endpoint validates are sent. The admin user is
        // provisioned separately (direct DB), exactly as the Laravel panel does.
        var payload = new
        {
            id = vm.Id,
            name = vm.Name,
            currency = vm.Currency,
            max_transaction_amount = vm.MaxTransactionAmount,
            transfer_fee_type = vm.TransferFeeType,
            transfer_fee_value = vm.TransferFeeValue,
            exchange_rates = rates,
            webhook_url = string.IsNullOrWhiteSpace(vm.WebhookUrl) ? null : vm.WebhookUrl,
        };

        var body = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        try
        {
            var resp = await _http.PostAsync("api/v1/tenants", body);
            var json = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode)
                return (null, ExtractError(json, (int)resp.StatusCode));

            // Re-fetch to obtain the canonical record (create returns tenant+config).
            var (t, err) = await GetTenantAsync(apiKey, vm.Id);
            if (t != null) t.AdminEmail ??= vm.AdminEmail;
            return (t, err);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating tenant");
            return (null, ex.Message);
        }
    }

    public async Task<string?> UpdateTenantAsync(string apiKey, string slug, EditTenantViewModel vm)
    {
        UseKey(apiKey);
        // The SuperAdmin update endpoint only accepts name + status.
        // Financial config is updated directly in the central DB (see TenantDbService).
        var payload = new
        {
            name = vm.Name,
            status = vm.Status,
        };
        return await SendPatchAsync(slug, payload);
    }

    public async Task<string?> ToggleTenantStatusAsync(string apiKey, string slug, string currentStatus)
    {
        UseKey(apiKey);
        var newStatus = currentStatus == "active" ? "inactive" : "active";
        return await SendPatchAsync(slug, new { status = newStatus });
    }

    public async Task<string?> DeleteTenantAsync(string apiKey, string slug)
    {
        UseKey(apiKey);
        try
        {
            var resp = await _http.DeleteAsync($"api/v1/tenants/{slug}");
            if (!resp.IsSuccessStatusCode)
                return ExtractError(await resp.Content.ReadAsStringAsync(), (int)resp.StatusCode);
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    // ── Public (no key) ─────────────────────────────────────────────────────────

    public async Task<List<TenantModel>> GetPublicBanksAsync()
    {
        try
        {
            var resp = await _http.GetAsync("api/v1/banks");
            if (!resp.IsSuccessStatusCode) return [];
            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var data = doc.RootElement.TryGetProperty("data", out var d) ? d : doc.RootElement;
            var result = new List<TenantModel>();
            if (data.ValueKind == JsonValueKind.Array)
                foreach (var item in data.EnumerateArray())
                    result.Add(new TenantModel
                    {
                        Id = GetStr(item, "id", "slug"),
                        Name = GetStr(item, "name"),
                    });
            return result;
        }
        catch { return []; }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task<string?> SendPatchAsync(string slug, object payload)
    {
        var body = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Patch, $"api/v1/tenants/{slug}") { Content = body };
            var resp = await _http.SendAsync(req);
            if (!resp.IsSuccessStatusCode)
                return ExtractError(await resp.Content.ReadAsStringAsync(), (int)resp.StatusCode);
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    private static TenantModel ParseTenant(JsonElement tenantEl, JsonElement configEl)
    {
        var t = new TenantModel
        {
            Id = GetStr(tenantEl, "id", "slug"),
            Name = GetStr(tenantEl, "name"),
            Status = GetStr(tenantEl, "status", fallback: "active"),
            AdminEmail = NullIfEmpty(GetStr(tenantEl, "admin_email", "adminEmail")),
        };

        if (TryDate(tenantEl, "created_at", out var ca)) t.CreatedAt = ca;
        if (TryDate(tenantEl, "updated_at", out var ua)) t.UpdatedAt = ua;

        if (configEl.ValueKind == JsonValueKind.Object)
        {
            var cfg = new TenantConfig
            {
                TenantId = t.Id,
                Currency = GetStr(configEl, "currency", fallback: "COP"),
                MaxTransactionAmount = GetDec(configEl, "max_transaction_amount"),
                TransferFeeType = GetStr(configEl, "transfer_fee_type", fallback: "percentage"),
                TransferFeeValue = GetDec(configEl, "transfer_fee_value"),
                WebhookUrl = NullIfEmpty(GetStr(configEl, "webhook_url")),
            };
            if (configEl.TryGetProperty("exchange_rates", out var rates) && rates.ValueKind == JsonValueKind.Object)
            {
                cfg.ExchangeRates = new();
                foreach (var r in rates.EnumerateObject())
                    if (r.Value.ValueKind == JsonValueKind.Number) cfg.ExchangeRates[r.Name] = r.Value.GetDecimal();
            }
            t.Config = cfg;
            t.Currency = cfg.Currency;
            t.MaxTransactionAmount = cfg.MaxTransactionAmount;
            t.TransferFeeType = cfg.TransferFeeType;
            t.TransferFeeValue = cfg.TransferFeeValue;
            t.WebhookUrl = cfg.WebhookUrl;
        }
        else
        {
            // flat fields directly on tenant
            t.Currency = GetStr(tenantEl, "currency", fallback: "COP");
            t.MaxTransactionAmount = GetDec(tenantEl, "max_transaction_amount");
            t.TransferFeeType = GetStr(tenantEl, "transfer_fee_type", fallback: "percentage");
            t.TransferFeeValue = GetDec(tenantEl, "transfer_fee_value");
            t.WebhookUrl = NullIfEmpty(GetStr(tenantEl, "webhook_url"));
        }

        return t;
    }

    private static string ExtractError(string json, int status)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var err) && err.TryGetProperty("message", out var m))
            {
                var raw = m.GetString() ?? $"HTTP {status}";
                if (raw.Contains("master API key", StringComparison.OrdinalIgnoreCase))
                    return "Master API Key inválida o ausente. Verifica el valor de X-API-Key.";
                return raw;
            }
            if (root.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.String &&
                !root.TryGetProperty("errors", out _))
                return msg.GetString() ?? $"HTTP {status}";
            if (root.TryGetProperty("errors", out var errs) && errs.ValueKind == JsonValueKind.Object)
            {
                var parts = new List<string>();
                foreach (var f in errs.EnumerateObject())
                    if (f.Value.ValueKind == JsonValueKind.Array)
                        foreach (var v in f.Value.EnumerateArray())
                            parts.Add(v.GetString() ?? "");
                if (parts.Count > 0) return string.Join(" · ", parts);
            }
        }
        catch { /* not json */ }
        return status switch
        {
            401 => "Master API Key inválida (401).",
            403 => "Acceso denegado para esta clave (403).",
            404 => "Recurso no encontrado (404).",
            422 => "Datos inválidos (422).",
            _ => $"HTTP {status}"
        };
    }

    private static string GetStr(JsonElement el, string name, string? alt = null, string fallback = "")
    {
        if (el.ValueKind == JsonValueKind.Object)
        {
            if (el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String) return v.GetString() ?? fallback;
            if (alt != null && el.TryGetProperty(alt, out var v2) && v2.ValueKind == JsonValueKind.String) return v2.GetString() ?? fallback;
        }
        return fallback;
    }

    private static decimal GetDec(JsonElement el, string name)
    {
        if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v))
        {
            if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d)) return d;
            if (v.ValueKind == JsonValueKind.String && decimal.TryParse(v.GetString(), out var ds)) return ds;
        }
        return 0;
    }

    private static bool TryDate(JsonElement el, string name, out DateTime value)
    {
        value = default;
        if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) &&
            v.ValueKind == JsonValueKind.String && DateTime.TryParse(v.GetString(), out var dt))
        {
            value = dt;
            return true;
        }
        return false;
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
