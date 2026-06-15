using Microsoft.AspNetCore.Mvc;
using BankOsAdmin.Services;

namespace BankOsAdmin.Controllers;

public class HomeController : Controller
{
    private readonly BankOsApiService _api;
    public HomeController(BankOsApiService api) => _api = api;

    public async Task<IActionResult> Index()
    {
        // Public landing — show how many banks already run on the platform.
        var banks = await _api.GetPublicBanksAsync();
        ViewBag.BankCount = banks.Count;
        return View();
    }

    public IActionResult Privacy() => View();

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        ViewBag.RequestId = HttpContext.TraceIdentifier;
        return View();
    }
}
