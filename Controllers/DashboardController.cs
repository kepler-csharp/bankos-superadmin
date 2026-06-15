using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using BankOsAdmin.Models;
using BankOsAdmin.Services;

namespace BankOsAdmin.Controllers;

public class DashboardController : Controller
{
    private readonly BankOsApiService _api;
    private readonly EmailService _email;
    private readonly PdfService _pdf;
    private readonly TenantDbService _db;
    private readonly IConfiguration _config;
    private readonly ILogger<DashboardController> _logger;

    public DashboardController(BankOsApiService api, EmailService email, PdfService pdf,
        TenantDbService db, IConfiguration config, ILogger<DashboardController> logger)
    {
        _api = api;
        _email = email;
        _pdf = pdf;
        _db = db;
        _config = config;
        _logger = logger;
    }

    private string? ApiKey => HttpContext.Session.GetString("ApiKey");

    private bool NotAuthed(out IActionResult redirect)
    {
        if (ApiKey == null) { redirect = RedirectToAction("Login", "Auth"); return true; }
        redirect = null!;
        return false;
    }

    // ── Tenant list + KPIs ─────────────────────────────────────────────────────

    public async Task<IActionResult> Index()
    {
        if (NotAuthed(out var r)) return r;

        var (tenants, err) = await _api.GetTenantsAsync(ApiKey!);
        if (err != null && (err.Contains("401") || err.Contains("inválida")))
        {
            HttpContext.Session.Clear();
            return RedirectToAction("Login", "Auth");
        }

        tenants ??= new();
        ViewBag.Error = err;
        ViewBag.Tenants = tenants;
        ViewBag.ActiveCount = tenants.Count(t => t.IsActive);
        ViewBag.InactiveCount = tenants.Count(t => !t.IsActive);
        return View();
    }

    // ── Create ─────────────────────────────────────────────────────────────────

    [HttpGet]
    public IActionResult Create()
    {
        if (NotAuthed(out var r)) return r;
        return View(new CreateTenantViewModel());
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(CreateTenantViewModel vm)
    {
        if (NotAuthed(out var r)) return r;
        if (!ModelState.IsValid) return View(vm);

        // 1) Create the tenant through the API. This also creates + migrates its database synchronously.
        var (_, err) = await _api.CreateTenantAsync(ApiKey!, vm);
        if (err != null)
        {
            ViewBag.Error = err;
            return View(vm);
        }

        var notes = new List<string>();
        var suffix = _config["Database:DomainSuffix"] ?? ".bank.os";
        var domain = $"{vm.Id}{suffix}";

        // 2) Provision the first administrador user inside the tenant DB (parity with the Laravel panel).
        if (!string.IsNullOrWhiteSpace(vm.AdminEmail) && !string.IsNullOrWhiteSpace(vm.AdminPassword))
        {
            var adminErr = await _db.CreateAdminUserAsync(vm.Id, vm.AdminName, vm.AdminEmail, vm.AdminPassword);
            if (adminErr != null) notes.Add($"Usuario administrador: {adminErr}");
        }

        // 3) Register the tenant subdomain in the central domains table (cosmetic, best-effort).
        await _db.CreateDomainAsync(vm.Id, domain);

        // 4) Notify the tenant administrator — always, sent from this MVC app (never from the API).
        if (!string.IsNullOrWhiteSpace(vm.AdminEmail))
        {
            _ = _email.SendTenantCreatedAsync(
                vm.AdminEmail, vm.Name, vm.Id, domain, vm.AdminEmail, vm.AdminPassword,
                vm.Currency, vm.MaxTransactionAmount, vm.TransferFeeType, vm.TransferFeeValue);

            TempData["Success"] = $"Banco «{vm.Name}» creado. Se notificó al administrador en {vm.AdminEmail}.";
        }
        else
        {
            TempData["Success"] = $"Banco «{vm.Name}» creado correctamente.";
        }

        if (notes.Count > 0)
            TempData["Warning"] = string.Join(" · ", notes) +
                " (el banco se creó; estas tareas requieren acceso directo a PostgreSQL).";

        return RedirectToAction("Detail", new { id = vm.Id });
    }

    // ── Detail (with live DB metrics) ───────────────────────────────────────────

    public async Task<IActionResult> Detail(string id)
    {
        if (NotAuthed(out var r)) return r;

        var (tenant, err) = await _api.GetTenantAsync(ApiKey!, id);
        if (err != null || tenant == null)
        {
            TempData["Error"] = err ?? "Tenant no encontrado.";
            return RedirectToAction("Index");
        }

        tenant.Stats = await _db.GetStatsAsync(id);
        return View(tenant);
    }

    // ── Edit ─────────────────────────────────────────────────────────────────

    [HttpGet]
    public async Task<IActionResult> Edit(string id)
    {
        if (NotAuthed(out var r)) return r;

        var (tenant, err) = await _api.GetTenantAsync(ApiKey!, id);
        if (err != null || tenant == null) { TempData["Error"] = err ?? "Tenant no encontrado."; return RedirectToAction("Index"); }

        // The API doesn't expose the admin email; resolve it from the tenant DB (best-effort).
        var adminEmail = tenant.AdminEmail ?? await _db.GetAdminEmailAsync(id);

        return View(new EditTenantViewModel
        {
            Id = tenant.Id,
            Name = tenant.Name,
            Status = tenant.Status,
            Currency = tenant.Currency,
            MaxTransactionAmount = tenant.MaxTransactionAmount,
            TransferFeeType = tenant.TransferFeeType,
            TransferFeeValue = tenant.TransferFeeValue,
            WebhookUrl = tenant.WebhookUrl,
            AdminEmail = adminEmail,
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(EditTenantViewModel vm)
    {
        if (NotAuthed(out var r)) return r;

        var (oldTenant, _) = await _api.GetTenantAsync(ApiKey!, vm.Id);

        // 1) Name + status through the API.
        var err = await _api.UpdateTenantAsync(ApiKey!, vm.Id, vm);
        if (err != null) { ViewBag.Error = err; return View(vm); }

        // 2) Financial config directly in the central DB (the API doesn't cover this).
        var rates = oldTenant?.Config?.ExchangeRates;
        var cfgErr = await _db.UpdateConfigAsync(
            vm.Id, vm.Currency, vm.MaxTransactionAmount, vm.TransferFeeType, vm.TransferFeeValue,
            rates, string.IsNullOrWhiteSpace(vm.WebhookUrl) ? null : vm.WebhookUrl);

        // 3) Build the change diff (mirrors the Laravel notification) and notify the admin — always.
        if (oldTenant != null)
        {
            var changes = new Dictionary<string, (string, string)>();
            if (oldTenant.Name != vm.Name) changes["Nombre"] = (oldTenant.Name, vm.Name);
            if (oldTenant.Status != vm.Status)
                changes["Estado"] = (Label(oldTenant.Status), Label(vm.Status));
            if (!string.Equals(oldTenant.Currency, vm.Currency, StringComparison.OrdinalIgnoreCase))
                changes["Moneda"] = (oldTenant.Currency, vm.Currency);
            if (oldTenant.MaxTransactionAmount != vm.MaxTransactionAmount)
                changes["Límite por transacción"] = ($"{oldTenant.MaxTransactionAmount:N0}", $"{vm.MaxTransactionAmount:N0}");
            if (oldTenant.TransferFeeValue != vm.TransferFeeValue || oldTenant.TransferFeeType != vm.TransferFeeType)
                changes["Comisión"] = (FeeText(oldTenant.TransferFeeType, oldTenant.TransferFeeValue),
                                        FeeText(vm.TransferFeeType, vm.TransferFeeValue));
            if ((oldTenant.WebhookUrl ?? "") != (vm.WebhookUrl ?? ""))
                changes["Webhook URL"] = (string.IsNullOrWhiteSpace(oldTenant.WebhookUrl) ? "(ninguno)" : oldTenant.WebhookUrl,
                                          string.IsNullOrWhiteSpace(vm.WebhookUrl) ? "(ninguno)" : vm.WebhookUrl!);

            var target = vm.AdminEmail ?? oldTenant.AdminEmail ?? await _db.GetAdminEmailAsync(vm.Id);
            if (!string.IsNullOrWhiteSpace(target) && changes.Count > 0)
                _ = _email.SendTenantUpdatedAsync(target, vm.Name, vm.Id, changes);
        }

        TempData["Success"] = $"Banco «{vm.Name}» actualizado correctamente.";
        if (cfgErr != null)
            TempData["Warning"] = $"Nombre y estado actualizados. Configuración financiera: {cfgErr}";

        return RedirectToAction("Detail", new { id = vm.Id });
    }

    // ── Toggle status ───────────────────────────────────────────────────────────

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ToggleStatus(string id, string currentStatus, string? adminEmail, string? tenantName)
    {
        if (NotAuthed(out var r)) return r;

        var err = await _api.ToggleTenantStatusAsync(ApiKey!, id, currentStatus);
        if (err != null) { TempData["Error"] = err; return RedirectToAction("Index"); }

        var newStatus = currentStatus == "active" ? "inactive" : "active";
        var target = !string.IsNullOrWhiteSpace(adminEmail) ? adminEmail : await _db.GetAdminEmailAsync(id);
        if (!string.IsNullOrWhiteSpace(target))
            _ = _email.SendTenantStatusChangedAsync(target, tenantName ?? id, id, newStatus);

        TempData["Success"] = newStatus == "active"
            ? $"Banco «{tenantName ?? id}» reactivado."
            : $"Banco «{tenantName ?? id}» desactivado.";
        return RedirectToAction("Index");
    }

    // ── Delete ─────────────────────────────────────────────────────────────────

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(string id, string? adminEmail, string? tenantName)
    {
        if (NotAuthed(out var r)) return r;

        // Resolve the admin email before deactivating so the notification can still be addressed.
        var target = !string.IsNullOrWhiteSpace(adminEmail) ? adminEmail : await _db.GetAdminEmailAsync(id);

        var err = await _api.DeleteTenantAsync(ApiKey!, id);
        if (err != null) { TempData["Error"] = err; return RedirectToAction("Index"); }

        if (!string.IsNullOrWhiteSpace(target))
            _ = _email.SendTenantDeletedAsync(target, tenantName ?? id, id);

        TempData["Success"] = $"Banco «{tenantName ?? id}» desactivado en el sistema.";
        return RedirectToAction("Index");
    }

    // ── PDF certificate (generated in MVC, not the API) ─────────────────────────

    public async Task<IActionResult> Certificate(string id)
    {
        if (NotAuthed(out var r)) return r;

        var (tenant, err) = await _api.GetTenantAsync(ApiKey!, id);
        if (err != null || tenant == null)
        {
            TempData["Error"] = err ?? "Tenant no encontrado.";
            return RedirectToAction("Index");
        }

        var stats = await _db.GetStatsAsync(id);
        var pdf = _pdf.GenerateTenantCertificate(tenant, tenant.AdminEmail, stats);
        return File(pdf, "application/pdf", $"certificado-{id}-{DateTime.Now:yyyyMMdd}.pdf");
    }

    // ── DB Viewer ────────────────────────────────────────────────────────────────

    public async Task<IActionResult> DbViewer(string id)
    {
        if (NotAuthed(out var r)) return r;
        var (tenant, _) = await _api.GetTenantAsync(ApiKey!, id);
        ViewBag.TenantId = id;
        ViewBag.TenantName = tenant?.Name ?? id;
        return View();
    }

    // ── Chatbot ────────────────────────────────────────────────────────────────

    public IActionResult Chat()
    {
        if (NotAuthed(out var r)) return r;
        return View();
    }

    public record ChatRequest(string Message, List<ChatMessage> History);

    [HttpPost]
    public async Task<IActionResult> ChatSend([FromBody] ChatRequest req)
    {
        if (ApiKey == null) return Json(new { reply = "Sesión expirada. Inicia sesión de nuevo." });

        var (tenants, _) = await _api.GetTenantsAsync(ApiKey);
        tenants ??= new();
        var total = tenants.Count;
        var active = tenants.Count(t => t.IsActive);
        var ctx = string.Join("\n", tenants.Select(t =>
            $"- id:{t.Id} | nombre:{t.Name} | estado:{t.Status} | moneda:{t.Currency} " +
            $"| limite_tx:{t.MaxTransactionAmount:N0} | comision:{FeeText(t.TransferFeeType, t.TransferFeeValue)} " +
            $"| creado:{t.CreatedAt:dd/MM/yyyy}"));

        var systemPrompt =
            "Eres el asistente del panel SuperAdmin de BankOs. Respondes ÚNICAMENTE preguntas sobre los tenants " +
            "(bancos) del sistema: listados, estados, configuración, monedas, comisiones, límites, estadísticas y " +
            "comparaciones. Si te preguntan algo ajeno a los tenants de BankOs, responde exactamente: " +
            "\"Solo puedo ayudarte con preguntas sobre los tenants del sistema BankOs.\" " +
            "Sé claro y conciso, usa español, y no inventes datos: básate solo en la lista siguiente.\n\n" +
            $"TENANTS (total: {total}, activos: {active}):\n{ctx}";

        var openAiKey = _config["OpenAI:ApiKey"] ?? "";
        var model = _config["OpenAI:Model"] ?? "gpt-4o-mini";
        if (string.IsNullOrWhiteSpace(openAiKey) || openAiKey.StartsWith("sk-tu-"))
            return Json(new { reply = "El asistente no está configurado todavía. Agrega tu clave en OpenAI:ApiKey dentro de appsettings.json." });

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(40) };
        http.DefaultRequestHeaders.Add("Authorization", $"Bearer {openAiKey}");

        var messages = new List<object> { new { role = "system", content = systemPrompt } };
        foreach (var m in req.History.TakeLast(10))
            messages.Add(new { role = m.Role, content = m.Content });
        messages.Add(new { role = "user", content = req.Message });

        var payload = new { model, max_tokens = 500, temperature = 0.3, messages };
        var body = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        try
        {
            var resp = await http.PostAsync("https://api.openai.com/v1/chat/completions", body);
            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            if (!resp.IsSuccessStatusCode)
            {
                var msg = doc.RootElement.TryGetProperty("error", out var e) && e.TryGetProperty("message", out var m)
                    ? m.GetString() : $"HTTP {(int)resp.StatusCode}";
                return Json(new { reply = $"No pude consultar al asistente: {msg}" });
            }
            var answer = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
            return Json(new { reply = answer });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Chat error");
            return Json(new { reply = $"Ocurrió un error al consultar al asistente: {ex.Message}" });
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static string Label(string status) => status == "active" ? "Activo" : "Inactivo";
    private static string FeeText(string type, decimal value) => type == "percentage" ? $"{value}%" : $"{value:N0} (fija)";
}
