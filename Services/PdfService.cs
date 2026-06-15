using BankOsAdmin.Models;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace BankOsAdmin.Services;

/// <summary>
/// Generates the official tenant + bank certificate as a PDF, entirely server-side
/// (the BankOS API is never asked to render documents). Uses QuestPDF.
/// </summary>
public class PdfService
{
    private readonly byte[]? _logo;

    // Brand palette sampled from the BankOs logo
    private const string Navy = "#0c1f6e";
    private const string NavyDeep = "#0a1652";
    private const string Indigo = "#221a8c";
    private const string Purple = "#7c12fd";
    private const string Blue = "#0463fd";
    private const string Cyan = "#00a8e8";
    private const string Green = "#22c55e";
    private const string Ink = "#0f172a";
    private const string Slate = "#475569";
    private const string Muted = "#94a3b8";
    private const string Line = "#e8edf6";
    private const string Soft = "#f6f8fc";

    public PdfService(IWebHostEnvironment env)
    {
        QuestPDF.Settings.License = LicenseType.Community;
        var logoPath = Path.Combine(env.WebRootPath ?? "wwwroot", "img", "logo.png");
        if (File.Exists(logoPath)) _logo = File.ReadAllBytes(logoPath);
    }

    public byte[] GenerateTenantCertificate(TenantModel t, string? adminEmail = null, TenantStats? stats = null)
    {
        var reference = $"BANKOS-{t.Id.ToUpperInvariant()}-{DateTime.Now:yyyyMMdd-HHmm}";

        var doc = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(0);
                page.DefaultTextStyle(x => x.FontFamily("Helvetica").FontColor(Ink).LineHeight(1.25f));

                page.Content().Column(col =>
                {
                    col.Item().Element(c => Header(c, reference));
                    col.Item().PaddingHorizontal(48).PaddingTop(30).Element(c => Body(c, t, adminEmail, stats, reference));
                    col.Item().Extend();
                    col.Item().Element(Footer);
                });
            });
        });

        return doc.GeneratePdf();
    }

    // ── Header ───────────────────────────────────────────────────────────────

    private void Header(IContainer c, string reference)
    {
        c.Column(h =>
        {
            h.Item().Background(Navy).PaddingHorizontal(48).PaddingTop(40).PaddingBottom(30).Row(row =>
            {
                row.RelativeItem().Column(left =>
                {
                    left.Item().Row(brand =>
                    {
                        if (_logo != null)
                            brand.ConstantItem(54).Height(54).AlignMiddle().Image(_logo).FitArea();
                        brand.ConstantItem(14);
                        brand.AutoItem().AlignMiddle().Column(w =>
                        {
                            w.Item().Text(txt =>
                            {
                                txt.Span("Bank").FontColor("#ffffff").FontSize(30).Bold();
                                txt.Span("O").FontColor("#c084fc").FontSize(30).Bold();
                                txt.Span("s").FontColor("#4ade80").FontSize(30).Bold();
                            });
                            w.Item().Text("Una plataforma. Todos los bancos.")
                                .FontColor("#9bb4ff").FontSize(10);
                        });
                    });

                    left.Item().PaddingTop(26).Text("CERTIFICADO DE TENANT Y BANCO")
                        .FontColor("#eaf1ff").FontSize(19).Bold().LetterSpacing(0.3f);
                    left.Item().PaddingTop(3).Text("Documento oficial de registro en el ecosistema BankOs")
                        .FontColor("#9bb4ff").FontSize(11);
                });

                row.ConstantItem(120).AlignTop().Column(seal =>
                {
                    seal.Item().Border(1.5f).BorderColor("#3b56c9")
                        .Background("#101f5e").Padding(12).Column(s =>
                    {
                        s.Item().AlignCenter().Text("✓").FontColor("#4ade80").FontSize(30).Bold();
                        s.Item().AlignCenter().PaddingTop(2).Text("VERIFICADO").FontColor("#ffffff").FontSize(8).Bold().LetterSpacing(1.5f);
                        s.Item().AlignCenter().Text("BankOs").FontColor("#9bb4ff").FontSize(7).LetterSpacing(2);
                    });
                });
            });

            // brand spectrum rail
            h.Item().Element(SpectrumRail);
        });
    }

    /// <summary>A thin band approximating the BankOs spectrum gradient using interpolated segments.</summary>
    private void SpectrumRail(IContainer c)
    {
        // gradient stops: navy → indigo → purple → blue → cyan → green
        var stops = new (float Pos, (int R, int G, int B) Rgb)[]
        {
            (0.00f, (0x0c, 0x1f, 0x6e)),
            (0.22f, (0x3a, 0x1d, 0x9e)),
            (0.42f, (0x7c, 0x12, 0xfd)),
            (0.62f, (0x04, 0x63, 0xfd)),
            (0.80f, (0x00, 0xa8, 0xe8)),
            (1.00f, (0x22, 0xc5, 0x5e)),
        };
        const int segments = 60;
        c.Height(6).Row(row =>
        {
            for (int i = 0; i < segments; i++)
            {
                var pos = i / (float)(segments - 1);
                row.RelativeItem().Background(Interpolate(stops, pos));
            }
        });
    }

    private static string Interpolate((float Pos, (int R, int G, int B) Rgb)[] stops, float pos)
    {
        for (int i = 1; i < stops.Length; i++)
        {
            if (pos <= stops[i].Pos)
            {
                var (p0, c0) = stops[i - 1];
                var (p1, c1) = stops[i];
                var t = (pos - p0) / Math.Max(0.0001f, p1 - p0);
                int r = (int)(c0.R + (c1.R - c0.R) * t);
                int g = (int)(c0.G + (c1.G - c0.G) * t);
                int b = (int)(c0.B + (c1.B - c0.B) * t);
                return $"#{r:x2}{g:x2}{b:x2}";
            }
        }
        var last = stops[^1].Rgb;
        return $"#{last.R:x2}{last.G:x2}{last.B:x2}";
    }

    // ── Body ─────────────────────────────────────────────────────────────────

    private void Body(IContainer c, TenantModel t, string? adminEmail, TenantStats? stats, string reference)
    {
        c.Column(b =>
        {
            // Statement
            b.Item().Border(1).BorderColor(Line).Background(Soft).Padding(20).Text(txt =>
            {
                txt.DefaultTextStyle(s => s.FontSize(12.5f).FontColor(Slate).LineHeight(1.5f));
                txt.Span("BankOs certifica que el banco ");
                txt.Span(t.Name).Bold().FontColor(Navy);
                txt.Span(", identificado con el tenant ");
                txt.Span(t.Id).Bold().FontColor(Purple).FontFamily("Courier");
                txt.Span(", se encuentra debidamente registrado y aprovisionado en la plataforma, con una base de datos dedicada y aislada.");
            });

            Section(b, "Identidad del banco");
            b.Item().PaddingTop(14).Row(row =>
            {
                row.RelativeItem().Column(l =>
                {
                    Field(l, "Nombre del banco", t.Name);
                    Field(l, "Identificador (Tenant ID)", t.Id, mono: true);
                    Field(l, "Estado", t.IsActive ? "Activo" : "Inactivo", color: t.IsActive ? Green : "#dc2626");
                });
                row.ConstantItem(28);
                row.RelativeItem().Column(r =>
                {
                    Field(r, "Base de datos dedicada", t.DbName, mono: true);
                    Field(r, "Correo del administrador", string.IsNullOrWhiteSpace(adminEmail) ? "—" : adminEmail!);
                    Field(r, "Webhook configurado", string.IsNullOrWhiteSpace(t.WebhookUrl) ? "No" : "Sí");
                });
            });

            Section(b, "Configuración financiera");
            b.Item().PaddingTop(14).Row(row =>
            {
                row.RelativeItem().Column(l =>
                {
                    Field(l, "Moneda principal", t.Currency);
                    Field(l, "Límite por transacción", $"{t.MaxTransactionAmount:N0} {t.Currency}");
                });
                row.ConstantItem(28);
                row.RelativeItem().Column(r =>
                {
                    Field(r, "Tipo de comisión", t.TransferFeeType == "percentage" ? "Porcentual" : "Fija");
                    Field(r, "Valor de comisión",
                        t.TransferFeeType == "percentage" ? $"{t.TransferFeeValue}%" : $"{t.TransferFeeValue:N2} {t.Currency}");
                });
            });

            // Exchange rates
            if (t.Config?.ExchangeRates is { Count: > 0 } rates)
            {
                Section(b, "Tasas de cambio configuradas");
                b.Item().PaddingTop(12).Table(table =>
                {
                    table.ColumnsDefinition(cols => { cols.RelativeColumn(); cols.RelativeColumn(); });
                    table.Header(hd =>
                    {
                        hd.Cell().Background(Soft).Padding(8).Text("Moneda").FontSize(10).Bold().FontColor(Slate);
                        hd.Cell().Background(Soft).Padding(8).Text($"Tasa (1 unidad → {t.Currency})").FontSize(10).Bold().FontColor(Slate);
                    });
                    foreach (var r in rates)
                    {
                        table.Cell().BorderBottom(1).BorderColor(Line).Padding(8).Text(r.Key).FontSize(11);
                        table.Cell().BorderBottom(1).BorderColor(Line).Padding(8).Text($"{r.Value:N2}").FontSize(11);
                    }
                });
            }

            // Live metrics
            if (stats is { Reachable: true })
            {
                Section(b, "Métricas de la base de datos (en vivo)");
                b.Item().PaddingTop(12).Row(row =>
                {
                    Metric(row, "Usuarios", stats.Users.ToString("N0"), Blue);
                    row.ConstantItem(12);
                    Metric(row, "Cuentas", stats.Accounts.ToString("N0"), Purple);
                    row.ConstantItem(12);
                    Metric(row, "Transacciones", stats.Transactions.ToString("N0"), Cyan);
                    row.ConstantItem(12);
                    Metric(row, "Tablas", stats.TableCount.ToString("N0"), Green);
                });
            }

            // Dates
            Section(b, "Vigencia y emisión");
            b.Item().PaddingTop(14).Row(row =>
            {
                row.RelativeItem().Column(l =>
                {
                    Field(l, "Fecha de creación", t.CreatedAt?.ToString("dd 'de' MMMM 'de' yyyy, HH:mm") ?? "—");
                    Field(l, "Emisión del certificado", DateTime.Now.ToString("dd 'de' MMMM 'de' yyyy, HH:mm"));
                });
                row.ConstantItem(28);
                row.RelativeItem().Column(r =>
                {
                    Field(r, "Validez", "Vigente mientras el tenant esté activo");
                    Field(r, "Emitido por", "BankOs · Panel SuperAdmin");
                });
            });

            // Seal
            b.Item().PaddingTop(26).Border(1).BorderColor(Line).Background(Soft)
                .Padding(16).Row(row =>
            {
                row.RelativeItem().Column(s =>
                {
                    s.Item().Text("SELLO DIGITAL").FontSize(9).Bold().FontColor(Slate).LetterSpacing(1);
                    s.Item().PaddingTop(4).Text(reference).FontFamily("Courier").FontSize(10).FontColor(Purple);
                    s.Item().PaddingTop(4).Text("Documento generado electrónicamente. Su autenticidad puede verificarse con la referencia anterior en el panel SuperAdmin de BankOs.")
                        .FontSize(8.5f).FontColor(Muted).LineHeight(1.4f);
                });
            });
        });
    }

    private void Footer(IContainer c) =>
        c.Background("#0a1230").PaddingVertical(16).PaddingHorizontal(48).Row(row =>
        {
            row.RelativeItem().AlignMiddle().Text($"BankOs © {DateTime.Now.Year} · Sistema Bancario Multi-Tenant")
                .FontColor("#8ea2d8").FontSize(9);
            row.RelativeItem().AlignMiddle().AlignRight().Text("No requiere firma física")
                .FontColor("#56689f").FontSize(9);
        });

    // ── Atoms ────────────────────────────────────────────────────────────────

    private void Section(ColumnDescriptor col, string title)
    {
        col.Item().PaddingTop(26).Text(title.ToUpperInvariant())
            .FontSize(10).Bold().FontColor(Navy).LetterSpacing(1.3f);
        col.Item().PaddingTop(6).Height(1).Background(Line);
    }

    private static void Field(ColumnDescriptor col, string label, string value, bool mono = false, string? color = null)
    {
        col.Item().PaddingBottom(13).Column(item =>
        {
            item.Item().Text(label).FontSize(8.5f).Bold().FontColor(Muted).LetterSpacing(0.4f);
            var t = item.Item().PaddingTop(2).Text(value).FontSize(12).FontColor(color ?? Ink);
            if (mono) t.FontFamily("Courier");
        });
    }

    private static void Metric(RowDescriptor row, string label, string value, string color)
    {
        row.RelativeItem().Border(1).BorderColor(Line).Padding(14).Column(m =>
        {
            m.Item().Text(value).FontSize(22).Bold().FontColor(color);
            m.Item().PaddingTop(2).Text(label).FontSize(9).FontColor(Muted);
        });
    }
}