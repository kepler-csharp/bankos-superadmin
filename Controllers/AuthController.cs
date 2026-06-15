using Microsoft.AspNetCore.Mvc;
using BankOsAdmin.Services;

namespace BankOsAdmin.Controllers;

public class AuthController : Controller
{
    private readonly BankOsApiService _api;
    public AuthController(BankOsApiService api) => _api = api;

    [HttpGet]
    public IActionResult Login()
    {
        if (HttpContext.Session.GetString("ApiKey") != null)
            return RedirectToAction("Index", "Dashboard");
        return View();
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            ViewBag.Error = "Ingresa la Master API Key para continuar.";
            return View();
        }

        var err = await _api.ValidateKeyAsync(apiKey.Trim());
        if (err != null)
        {
            ViewBag.Error = err.Contains("401") || err.Contains("403") || err.Contains("inválida")
                ? "Master API Key inválida. Verifica la clave configurada en el servidor."
                : $"No se pudo conectar con la API de BankOs: {err}";
            return View();
        }

        HttpContext.Session.SetString("ApiKey", apiKey.Trim());
        return RedirectToAction("Index", "Dashboard");
    }

    public IActionResult Logout()
    {
        HttpContext.Session.Clear();
        return RedirectToAction("Login");
    }
}
