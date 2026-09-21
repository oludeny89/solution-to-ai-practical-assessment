namespace ExpenseClassifier.Models;

/// <summary>
/// Request payload for a single expense classification.
/// </summary>
public class RequestDto
{
    public required string Description { get; set; }
}

/// <summary>
/// Detailed classification and compliance result for an expense line item.
/// </summary>
public class ResponseDto
{
    public required string Category { get; set; }

    public string? SubCategory { get; set; }

    public decimal? ExtractedAmount { get; set; }

    public string? Currency { get; set; }

    public string? Merchant { get; set; }

    public double ConfidenceScore { get; set; } = 1.0;

    public string ComplianceStatus { get; set; } = "Compliant"; // Compliant, RequiresManagerApproval, PolicyViolation, Unverifiable

    public string? ComplianceNotes { get; set; }

    public string ClassificationStage { get; set; } = "PolicyAgentWithTools"; // PolicyAgentWithTools, FallbackGeneralAgent, CacheHit

    public required string ExpenseDescription { get; set; }
}

/// <summary>
/// An individual item in a batch expense submission.
/// </summary>
public class ExpenseItemRequest
{
    public string? Id { get; set; }

    public required string Description { get; set; }
}

/// <summary>
/// Request payload for processing a batch of expense claims.
/// </summary>
public class BatchExpenseRequest
{
    public string? Department { get; set; }

    public string? EmployeeId { get; set; }

    public required List<ExpenseItemRequest> Items { get; set; } = [];
}

/// <summary>
/// Aggregate summary metrics for a batch expense submission.
/// </summary>
public class BatchSummary
{
    public int TotalItems { get; set; }

    public int CompliantCount { get; set; }

    public int RequiresApprovalCount { get; set; }

    public int PolicyViolationCount { get; set; }

    public Dictionary<string, decimal> TotalAmountByCurrency { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public long ProcessingTimeMs { get; set; }
}

/// <summary>
/// Response payload for a batch expense classification operation.
/// </summary>
public class BatchExpenseResponse
{
    public required BatchSummary Summary { get; set; }

    public required List<ResponseDto> Results { get; set; } = [];
}
