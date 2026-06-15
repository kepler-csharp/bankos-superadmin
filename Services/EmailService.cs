using System.Net;
using System.Net.Mail;

namespace BankOsAdmin.Services;

/// <summary>
/// Transactional emails for the tenant lifecycle. Sent over SMTP.
/// Templates use email-safe inline styles and approximate the BankOs wordmark
/// (navy "Bank" + purple "O" + green "s") since gradient text is unreliable in mail clients.
/// </summary>
public class EmailService
{
    private readonly IConfiguration _config;
    private readonly ILogger<EmailService> _logger;

    public EmailService(IConfiguration config, ILogger<EmailService> logger)
    {
        _config = config;
        _logger = logger;
    }

    private bool Enabled => bool.TryParse(_config["Email:Enabled"], out var e) && e;
    private string From => _config["Email:From"] ?? _config["Email:Username"] ?? "noreply@bankos.com";
    private string FromName => _config["Email:FromName"] ?? "BankOs";
    private string Portal => _config["Branding:PortalUrl"] ?? "http://bank-os.duckdns.org:8080";
    private string Support => _config["Branding:SupportEmail"] ?? "soporte@bankos.com";

    private SmtpClient BuildClient() => new(_config["Email:Host"] ?? "smtp.gmail.com")
    {
        Port = int.Parse(_config["Email:Port"] ?? "587"),
        Credentials = new NetworkCredential(_config["Email:Username"], _config["Email:Password"]),
        EnableSsl = true
    };

    // ── Public senders ───────────────────────────────────────────────────────

    public Task SendTenantCreatedAsync(string to, string tenantName, string tenantId, string domain,
        string adminEmail, string adminPassword, string currency, decimal maxAmount, string feeType, decimal feeValue)
    {
        var fee = feeType == "percentage" ? $"{feeValue}%" : $"{feeValue:N0} {currency} (fija)";
        var body = Shell(
            accent: "#22c55e",
            badge: "TENANT ACTIVO",
            heading: $"¡{System.Net.WebUtility.HtmlEncode(tenantName)} ya está en línea!",
            intro: "Tu banco digital fue creado en la plataforma BankOs. Aquí están las credenciales del administrador para el primer acceso.",
            inner: $"""
                {Panel("Credenciales de acceso", "#0463fd", $"""
                    {Row("Banco", System.Net.WebUtility.HtmlEncode(tenantName))}
                    {Row("URL de acceso", Mono(System.Net.WebUtility.HtmlEncode(domain)))}
                    {Row("Tenant ID", Mono(tenantId))}
                    {Row("Email", System.Net.WebUtility.HtmlEncode(adminEmail))}
                    {Row("Contraseña temporal", Mono(System.Net.WebUtility.HtmlEncode(adminPassword)), "#7c12fd")}
                """)}
                {Panel("Configuración del banco", "#22c55e", $"""
                    {Row("Moneda principal", currency)}
                    {Row("Límite por transacción", $"{maxAmount:N0} {currency}")}
                    {Row("Comisión por transferencia", fee)}
                """)}
                <p style="color:#475569;font-size:13px;line-height:1.6;margin:24px 0 0">
                  <b>Importante:</b> por seguridad, cambia la contraseña en el primer inicio de sesión y guarda
                  estas credenciales en un lugar seguro.
                </p>
            """);
        return SendAsync(to, $"¡Bienvenido a BankOs! Tu plataforma {tenantName} está lista", body);
    }

    public Task SendTenantUpdatedAsync(string to, string tenantName, string tenantId,
        Dictionary<string, (string Old, string New)> changes)
    {
        var rows = string.Concat(changes.Select(c =>
            $"""
            <tr>
              <td style="padding:8px 0;color:#64748b;font-size:13px;width:40%">{System.Net.WebUtility.HtmlEncode(c.Key)}</td>
              <td style="padding:8px 0;font-size:13px">
                <span style="color:#ef4444;text-decoration:line-through">{System.Net.WebUtility.HtmlEncode(c.Value.Old)}</span>
                &nbsp;→&nbsp;
                <span style="color:#16a34a;font-weight:600">{System.Net.WebUtility.HtmlEncode(c.Value.New)}</span>
              </td>
            </tr>
            """));
        var body = Shell(
            accent: "#0463fd",
            badge: "ACTUALIZACIÓN",
            heading: "Se actualizó tu plataforma",
            intro: $"Se aplicaron cambios en la configuración de <b>{System.Net.WebUtility.HtmlEncode(tenantName)}</b>.",
            inner: $"""
                {Panel("Detalle de cambios", "#0463fd",
                    $"<table style='width:100%;border-collapse:collapse'>{rows}</table>")}
                {IdBlock(tenantId)}
            """);
        return SendAsync(to, $"[BankOs] Cambios en {tenantName}", body);
    }

    public Task SendTenantStatusChangedAsync(string to, string tenantName, string tenantId, string newStatus)
    {
        var deactivated = newStatus == "inactive";
        var body = Shell(
            accent: deactivated ? "#f59e0b" : "#22c55e",
            badge: deactivated ? "DESACTIVADO" : "REACTIVADO",
            heading: deactivated ? "Tu plataforma fue desactivada" : "Tu plataforma fue reactivada",
            intro: deactivated
                ? $"El banco <b>{System.Net.WebUtility.HtmlEncode(tenantName)}</b> quedó <b>desactivado</b> temporalmente. Las operaciones están en pausa hasta que se reactive."
                : $"El banco <b>{System.Net.WebUtility.HtmlEncode(tenantName)}</b> fue <b>reactivado</b> y vuelve a estar operativo.",
            inner: IdBlock(tenantId));
        return SendAsync(to,
            deactivated ? $"[BankOs] {tenantName} desactivado" : $"[BankOs] {tenantName} reactivado",
            body);
    }

    public Task SendTenantDeletedAsync(string to, string tenantName, string tenantId)
    {
        var body = Shell(
            accent: "#ef4444",
            badge: "DADO DE BAJA",
            heading: "Tu plataforma fue dada de baja",
            intro: $"El banco <b>{System.Net.WebUtility.HtmlEncode(tenantName)}</b> fue <b>desactivado</b> en el ecosistema BankOs. Los usuarios no podrán iniciar sesión ni operar mientras esté en este estado. Tu información se conserva y la plataforma puede reactivarse.",
            inner: $"""
                {IdBlock(tenantId)}
                <p style="color:#475569;font-size:13px;line-height:1.6;margin:20px 0 0">
                  Si esto no fue solicitado por tu organización, contacta de inmediato a
                  <a href="mailto:{Support}" style="color:#0463fd">{Support}</a>.
                </p>
            """);
        return SendAsync(to, $"[BankOs] Tu plataforma {tenantName} ha sido desactivada", body);
    }

    // ── Transport ──────────────────────────────────────────────────────────────

    private async Task SendAsync(string to, string subject, string html)
    {
        if (string.IsNullOrWhiteSpace(to)) return;
        if (!Enabled)
        {
            _logger.LogInformation("[Email disabled] Would send to {to}: {subject}", to, subject);
            return;
        }
        try
        {
            using var client = BuildClient();
            using var msg = new MailMessage
            {
                From = new MailAddress(From, FromName),
                Subject = subject,
                Body = html,
                IsBodyHtml = true
            };
            msg.To.Add(to);
            await client.SendMailAsync(msg);
            _logger.LogInformation("Email sent to {to}: {subject}", to, subject);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send email to {to}", to);
        }
    }

    // ── Template building blocks ─────────────────────────────────────────────

    private string Shell(string accent, string badge, string heading, string intro, string inner) => $$"""
        <!DOCTYPE html>
        <html lang="es">
        <head><meta charset="UTF-8"><meta name="viewport" content="width=device-width,initial-scale=1"></head>
        <body style="margin:0;padding:32px 16px;background:#eef1f8;font-family:'Segoe UI',-apple-system,BlinkMacSystemFont,Roboto,Helvetica,Arial,sans-serif">
          <table role="presentation" width="100%" cellpadding="0" cellspacing="0">
            <tr><td align="center">
              <table role="presentation" width="600" cellpadding="0" cellspacing="0" style="max-width:600px;width:100%">
                <!-- header -->
                <tr><td style="background:#0c1f6e;background:linear-gradient(135deg,#0a1652 0%,#0c1f6e 55%,#221a8c 100%);border-radius:18px 18px 0 0;padding:34px 40px 0">
                  <div style="font-size:26px;font-weight:800;letter-spacing:-0.5px">
                    <span style="color:#ffffff">Bank</span><span style="color:#a855f7">O</span><span style="color:#34d399">s</span>
                  </div>
                  <div style="color:#9bb4ff;font-size:12px;margin-top:2px">Una plataforma. Todos los bancos.</div>
                  <table role="presentation" cellpadding="0" cellspacing="0" style="margin:18px 0 0"><tr>
                    <td style="background:rgba(255,255,255,0.14);border:1px solid rgba(255,255,255,0.2);border-radius:50px;padding:5px 14px;color:#ffffff;font-size:11px;font-weight:700;letter-spacing:1px">{{badge}}</td>
                  </tr></table>
                  <div style="height:34px"></div>
                </td></tr>
                <!-- accent rail -->
                <tr><td style="height:5px;background:{{accent}};background:linear-gradient(90deg,#0c1f6e,#7c12fd 42%,#0463fd 62%,#00a8e8 80%,#22c55e)"></td></tr>
                <!-- body -->
                <tr><td style="background:#ffffff;border-radius:0 0 18px 18px;padding:36px 40px;box-shadow:0 16px 48px rgba(12,31,110,0.10)">
                  <h1 style="color:#0f172a;font-size:21px;font-weight:800;margin:0 0 10px">{{heading}}</h1>
                  <p style="color:#475569;font-size:14px;line-height:1.6;margin:0 0 8px">{{intro}}</p>
                  {{inner}}
                  <hr style="border:none;border-top:1px solid #e8edf6;margin:28px 0 16px">
                  <p style="color:#94a3b8;font-size:11px;line-height:1.6;margin:0">
                    BankOs · Sistema Bancario Multi-Tenant · <a href="{{Portal}}" style="color:#0463fd;text-decoration:none">Abrir portal</a><br>
                    Este es un mensaje automático. ¿Dudas? Escríbenos a <a href="mailto:{{Support}}" style="color:#0463fd;text-decoration:none">{{Support}}</a>.
                  </p>
                </td></tr>
              </table>
            </td></tr>
          </table>
        </body>
        </html>
        """;

    private static string Panel(string title, string color, string content) => $"""
        <div style="background:#f6f8fc;border:1px solid #e8edf6;border-left:4px solid {color};border-radius:12px;padding:18px 20px;margin:20px 0">
          <div style="color:#475569;font-size:11px;font-weight:700;text-transform:uppercase;letter-spacing:1px;margin:0 0 12px">{title}</div>
          {content}
        </div>
        """;

    private static string Row(string label, string value, string? valueColor = null) => $"""
        <table role="presentation" width="100%" cellpadding="0" cellspacing="0"><tr>
          <td style="padding:4px 0;color:#64748b;font-size:13px;width:42%">{label}</td>
          <td style="padding:4px 0;font-size:13px;font-weight:600;color:{valueColor ?? "#0f172a"}">{value}</td>
        </tr></table>
        """;

    private static string Mono(string v) =>
        $"<span style=\"font-family:'Courier New',monospace;background:#eef2ff;color:#312e81;padding:2px 7px;border-radius:5px\">{v}</span>";

    private static string IdBlock(string tenantId) => $"""
        <div style="background:#f6f8fc;border:1px solid #e8edf6;border-radius:12px;padding:16px 20px;margin:20px 0">
          <span style="color:#64748b;font-size:12px">Tenant ID</span><br>
          {Mono(System.Net.WebUtility.HtmlEncode(tenantId))}
        </div>
        """;
}
