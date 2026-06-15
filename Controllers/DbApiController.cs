using Microsoft.AspNetCore.Mvc;
using BankOsAdmin.Services;

namespace BankOsAdmin.Controllers;

/// <summary>AJAX endpoints used by the DB Viewer. Read-only, session-protected.</summary>
[Route("api/db")]
[ApiController]
public class DbApiController : ControllerBase
{
    private readonly TenantDbService _db;
    public DbApiController(TenantDbService db) => _db = db;

    private bool Authed => HttpContext.Session.GetString("ApiKey") != null;

    [HttpGet("tables")]
    public async Task<IActionResult> GetTables([FromQuery] string tenantId)
    {
        if (!Authed) return Unauthorized();
        if (string.IsNullOrWhiteSpace(tenantId)) return BadRequest();
        var (tables, error) = await _db.GetTablesAsync(tenantId);
        return Ok(new { tables, error });
    }

    [HttpGet("rows")]
    public async Task<IActionResult> GetRows([FromQuery] string tenantId, [FromQuery] string table)
    {
        if (!Authed) return Unauthorized();
        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(table)) return BadRequest();
        var (columns, rows, total, error) = await _db.GetRowsAsync(tenantId, table);
        return Ok(new { columns, rows, total, error });
    }
}
