namespace BankOsAdmin.Models;

/// <summary>A bank/tenant as exposed by the BankOS SuperAdmin API.</summary>
public class TenantModel
{
    public string Id { get; set; } = "";        // slug, e.g. "banco-colombia"
    public string Name { get; set; } = "";
    public string Status { get; set; } = "active";
    public string Currency { get; set; } = "COP";
    public decimal MaxTransactionAmount { get; set; }
    public string TransferFeeType { get; set; } = "percentage"; // percentage | fixed
    public decimal TransferFeeValue { get; set; }
    public string? WebhookUrl { get; set; }
    public string? AdminEmail { get; set; }
    public DateTime? CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public TenantConfig? Config { get; set; }

    /// <summary>Convention used by the platform: every tenant gets a dedicated DB "tenant_{slug}".</summary>
    public string DbName => $"tenant_{Id}";
    public bool IsActive => string.Equals(Status, "active", StringComparison.OrdinalIgnoreCase);

    /// <summary>Live figures read directly from the tenant database (best-effort, may be null).</summary>
    public TenantStats? Stats { get; set; }
}

public class TenantConfig
{
    public string TenantId { get; set; } = "";
    public string Currency { get; set; } = "COP";
    public decimal MaxTransactionAmount { get; set; }
    public string TransferFeeType { get; set; } = "percentage";
    public decimal TransferFeeValue { get; set; }
    public string? WebhookUrl { get; set; }
    public Dictionary<string, decimal>? ExchangeRates { get; set; }
}

/// <summary>Aggregated, read-only metrics computed from the tenant's PostgreSQL database.</summary>
public class TenantStats
{
    public long Users { get; set; }
    public long Accounts { get; set; }
    public long Transactions { get; set; }
    public long Cards { get; set; }
    public int TableCount { get; set; }
    public bool Reachable { get; set; }
    public string? Error { get; set; }
}

public class CreateTenantViewModel
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Currency { get; set; } = "COP";
    public decimal MaxTransactionAmount { get; set; } = 10_000_000;
    public string TransferFeeType { get; set; } = "percentage";
    public decimal TransferFeeValue { get; set; } = 1;
    public string? WebhookUrl { get; set; }
    public string AdminEmail { get; set; } = "";
    public string AdminPassword { get; set; } = "";
    public string AdminName { get; set; } = "";
    public decimal ExchangeUSD { get; set; } = 4200;
    public decimal ExchangeEUR { get; set; } = 4500;
    public decimal ExchangeGBP { get; set; } = 5300;
    public bool SendWelcomeEmail { get; set; } = true;
}

public class EditTenantViewModel
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Status { get; set; } = "active";
    public string Currency { get; set; } = "COP";
    public decimal MaxTransactionAmount { get; set; } = 10_000_000;
    public string TransferFeeType { get; set; } = "percentage";
    public decimal TransferFeeValue { get; set; } = 1;
    public string? WebhookUrl { get; set; }
    public string? AdminEmail { get; set; }
    public bool NotifyByEmail { get; set; } = true;
}

// ── API envelope ─────────────────────────────────────────────────────────────

public class ApiResponse<T>
{
    public bool Success { get; set; }
    public T? Data { get; set; }
    public ApiError? Error { get; set; }
    public ApiMeta? Meta { get; set; }
}

public class ApiError
{
    public string Code { get; set; } = "";
    public string Message { get; set; } = "";
}

public class ApiMeta
{
    public int? CurrentPage { get; set; }
    public int? PerPage { get; set; }
    public int? Total { get; set; }
    public int? LastPage { get; set; }
}

// ── Chatbot ──────────────────────────────────────────────────────────────────

public class ChatMessage
{
    public string Role { get; set; } = "user";
    public string Content { get; set; } = "";
}
