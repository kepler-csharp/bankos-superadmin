using BankOsAdmin.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews();

// Session holds the master API key after login (server-side, http-only cookie reference)
builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(opts =>
{
    opts.IdleTimeout = TimeSpan.FromHours(8);
    opts.Cookie.Name = ".BankOs.Session";
    opts.Cookie.HttpOnly = true;
    opts.Cookie.IsEssential = true;
    opts.Cookie.SameSite = SameSiteMode.Lax;
});

// Typed HttpClient against the BankOS (Laravel) SuperAdmin API
builder.Services.AddHttpClient<BankOsApiService>();

// Email notifications (tenant created / updated / status / deleted)
builder.Services.AddScoped<EmailService>();

// PDF certificate generation (runs in the MVC app, not the API)
builder.Services.AddSingleton<PdfService>();

// Direct PostgreSQL reader for the per-tenant DB Viewer + live stats
builder.Services.AddScoped<TenantDbService>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseStaticFiles();
app.UseRouting();
app.UseSession();
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

// Attribute-routed API controllers (DB Viewer AJAX endpoints under /api/db)
app.MapControllers();

app.Run();
