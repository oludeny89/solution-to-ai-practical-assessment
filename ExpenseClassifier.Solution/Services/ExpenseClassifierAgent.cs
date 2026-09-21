using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using ExpenseClassifier.Models;
using ExpenseClassifier.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace ExpenseClassifier.Agents;

/// <summary>
/// Core contract for the intelligent expense classification service.
/// </summary>
public interface IExpenseClassifierAgent
{
    /// <summary>
    /// Classifies a single expense description using a two-stage agentic workflow:
    /// 1. Primary Policy Agent (with tool calling: <see cref="CompanyPolicyTool"/> and <see cref="SpendingLimitTool"/>).
    /// 2. Fallback General Agent (without tools) if the primary stage results in "Other" or an unrecognized policy category.
    /// Incorporates cache-aside caching to avoid redundant LLM invocations.
    /// </summary>
    Task<ResponseDto> Classify(string expenseDescription, CancellationToken cancellationToken = default);

    /// <summary>
    /// Classifies a collection of expense line items concurrently with bounded parallelism
    /// and generates aggregate financial metrics and compliance statistics.
    /// </summary>
    Task<BatchExpenseResponse> ClassifyBatch(BatchExpenseRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Complete reference implementation of <see cref="IExpenseClassifierAgent"/> featuring:
/// - Cache-aside caching with normalized keys
/// - Two-stage multi-tool agentic pipeline with automated fallback
/// - Financial entity extraction (amount, ISO currency, merchant)
/// - Bounded-concurrency batch processing with summary aggregation
/// </summary>
public partial class ExpenseClassifierAgent : IExpenseClassifierAgent
{
    private readonly IChatClient _chatClient;
    private readonly CompanyPolicyTool _policyTool;
    private readonly SpendingLimitTool _spendingLimitTool;
    private readonly IMemoryCache _cache;
    private readonly ILogger<ExpenseClassifierAgent> _logger;

    private readonly ChatOptions _policyChatSettings;
    private readonly ChatOptions _generalChatSettings;

    private static readonly TimeSpan ClassificationCacheLifetime = TimeSpan.FromMinutes(30);
    private const int BatchWorkerLimit = 5;

    private static readonly JsonSerializerOptions ResponseJsonSettings = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private const string PolicyClassificationInstructions = """
        You are the Enterprise Expense Classification & Financial Compliance Auditor.
        Your task is to analyze employee expense claims, determine their corporate GL category, extract key financial entities, and evaluate spending limit compliance.

        Instructions:
        1. Always query the 'GetPolicy' tool to determine official company policy categories (Transportation, Food, Accommodation, Utilities, Office Supplies, Software Subscriptions).
        2. Always query the 'GetSpendingLimits' tool to evaluate if the expense amount complies with limits, requires manager approval, or is a policy violation.
        3. Extract financial entities: numeric amount, standard 3-letter currency code (NGN, USD, EUR, GBP), merchant/vendor name, and subcategory.
        4. If the expense does not match official company policy, set category to "Other".
        5. If amount is not specified in the description, set complianceStatus to "Unverifiable".
        6. Output strictly valid JSON matching this schema:
        {
          "category": "string",
          "subCategory": "string",
          "extractedAmount": 0.0,
          "currency": "string",
          "merchant": "string",
          "confidenceScore": 0.95,
          "complianceStatus": "Compliant" | "RequiresManagerApproval" | "PolicyViolation" | "Unverifiable",
          "complianceNotes": "string"
        }
        Return only the raw JSON object. Do not wrap with markdown code blocks.
        """;

    private const string GeneralClassificationInstructions = """
        You are the Enterprise Fallback Expense Classifier.
        Your task is to classify an expense that did not match standard corporate policy into a broader business category (such as Software Subscriptions, Office Supplies, Health & Wellness, Training & Education, Marketing, Equipment, or Other).
        Extract amount, ISO currency code, merchant, subcategory, and evaluate general compliance.
        Output strictly valid JSON matching this schema:
        {
          "category": "string",
          "subCategory": "string",
          "extractedAmount": 0.0,
          "currency": "string",
          "merchant": "string",
          "confidenceScore": 0.85,
          "complianceStatus": "Compliant" | "RequiresManagerApproval" | "PolicyViolation" | "Unverifiable",
          "complianceNotes": "string"
        }
        Return only the raw JSON object. Do not wrap with markdown code blocks.
        """;

    public ExpenseClassifierAgent(
        IChatClient chatClient,
        CompanyPolicyTool policyTool,
        SpendingLimitTool spendingLimitTool,
        IMemoryCache cache,
        ILogger<ExpenseClassifierAgent> logger)
    {
        _chatClient = chatClient;
        _policyTool = policyTool;
        _spendingLimitTool = spendingLimitTool;
        _cache = cache;
        _logger = logger;

        // Pre-build reusable tool definitions & ChatOptions to eliminate per-request reflection overhead
        var policyFunctions = new List<AITool>
        {
            AIFunctionFactory.Create(_policyTool.GetPolicy, "GetPolicy"),
            AIFunctionFactory.Create(_spendingLimitTool.GetSpendingLimits, "GetSpendingLimits")
        };

        _policyChatSettings = new ChatOptions
        {
            Tools = policyFunctions
        };

        _generalChatSettings = new ChatOptions();
    }

    /// <inheritdoc/>
    public async Task<ResponseDto> Classify(string expenseDescription, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(expenseDescription))
        {
            throw new ArgumentException("Expense description cannot be null or whitespace.", nameof(expenseDescription));
        }

        var normalizedExpenseText = NormalizeExpenseText(expenseDescription);
        var classificationCacheKey = $"expense_class_{normalizedExpenseText}";

        // Check Cache-Aside Layer
        if (_cache.TryGetValue(classificationCacheKey, out ResponseDto? storedClassification) && storedClassification != null)
        {
            _logger.LogInformation("Cache hit for expense: {Description}", expenseDescription);
            return CopyClassificationResult(storedClassification, "CacheHit", expenseDescription);
        }

        _logger.LogInformation("Cache miss. Executing agent classification for: {Description}", expenseDescription);

        // Stage 1: Primary Policy-Driven Agent with Tool Calling
        var policyClassification = await RequestClassificationAsync(
            PolicyClassificationInstructions,
            expenseDescription,
            _policyChatSettings,
            cancellationToken);

        ResponseDto resolvedClassification;

        // Stage 2: Fallback General Agent if Primary returned "Other" or failed to match
        if (policyClassification == null || string.Equals(policyClassification.Category, "Other", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("Primary agent returned 'Other'. Triggering Fallback General Agent for: {Description}", expenseDescription);

            var generalClassification = await RequestClassificationAsync(
                GeneralClassificationInstructions,
                expenseDescription,
                _generalChatSettings,
                cancellationToken);

            resolvedClassification = generalClassification ?? policyClassification ?? CreateUnverifiableResult(expenseDescription, "Other", "FallbackGeneralAgent");
            resolvedClassification.ClassificationStage = "FallbackGeneralAgent";
        }
        else
        {
            resolvedClassification = policyClassification;
            resolvedClassification.ClassificationStage = "PolicyAgentWithTools";
        }

        resolvedClassification.ExpenseDescription = expenseDescription.Trim();

        // Store valid result in cache
        _cache.Set(classificationCacheKey, resolvedClassification, ClassificationCacheLifetime);

        return resolvedClassification;
    }

    /// <inheritdoc/>
    public async Task<BatchExpenseResponse> ClassifyBatch(BatchExpenseRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Items == null || request.Items.Count == 0)
        {
            throw new ArgumentException("Batch expense items list cannot be null or empty.", nameof(request));
        }

        var batchTimer = Stopwatch.StartNew();
        var itemClassifications = new ResponseDto[request.Items.Count];

        var batchExecutionOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = BatchWorkerLimit,
            CancellationToken = cancellationToken
        };

        var positionedExpenses = request.Items.Select((expenseItem, position) => (expenseItem, position));

        await Parallel.ForEachAsync(positionedExpenses, batchExecutionOptions, async (positionedExpense, itemCancellation) =>
        {
            var itemClassification = await Classify(positionedExpense.expenseItem.Description, itemCancellation);
            itemClassifications[positionedExpense.position] = itemClassification;
        });

        batchTimer.Stop();

        // Calculate aggregate metrics
        var batchTotals = new BatchSummary
        {
            TotalItems = itemClassifications.Length,
            CompliantCount = itemClassifications.Count(expenseResult => string.Equals(expenseResult.ComplianceStatus, "Compliant", StringComparison.OrdinalIgnoreCase)),
            RequiresApprovalCount = itemClassifications.Count(expenseResult => string.Equals(expenseResult.ComplianceStatus, "RequiresManagerApproval", StringComparison.OrdinalIgnoreCase)),
            PolicyViolationCount = itemClassifications.Count(expenseResult => string.Equals(expenseResult.ComplianceStatus, "PolicyViolation", StringComparison.OrdinalIgnoreCase)),
            ProcessingTimeMs = batchTimer.ElapsedMilliseconds
        };

        foreach (var expenseResult in itemClassifications)
        {
            if (expenseResult.ExtractedAmount.HasValue && !string.IsNullOrWhiteSpace(expenseResult.Currency))
            {
                var currencyCode = expenseResult.Currency.ToUpperInvariant();
                batchTotals.TotalAmountByCurrency.TryAdd(currencyCode, 0m);
                batchTotals.TotalAmountByCurrency[currencyCode] += expenseResult.ExtractedAmount.Value;
            }
        }

        return new BatchExpenseResponse
        {
            Summary = batchTotals,
            Results = [.. itemClassifications]
        };
    }

    private async Task<ResponseDto?> RequestClassificationAsync(
        string classificationInstructions,
        string claimText,
        ChatOptions chatSettings,
        CancellationToken cancellationToken)
    {
        try
        {
            var conversationMessages = new List<ChatMessage>
            {
                new(ChatRole.System, classificationInstructions),
                new(ChatRole.User, claimText)
            };

            var chatReply = await _chatClient.GetResponseAsync(conversationMessages, chatSettings, cancellationToken);
            var replyText = chatReply.Messages.FirstOrDefault()?.Text;

            if (string.IsNullOrWhiteSpace(replyText))
            {
                return null;
            }

            // Strip any accidental markdown formatting
            var jsonPayload = RemoveJsonCodeFences(replyText);
            return JsonSerializer.Deserialize<ResponseDto>(jsonPayload, ResponseJsonSettings);
        }
        catch (Exception classificationError)
        {
            _logger.LogWarning(classificationError, "Agent execution encountered an error for description: {Description}", claimText);
            return null;
        }
    }

    private static string NormalizeExpenseText(string claimDescription)
    {
        var normalizedText = claimDescription.Trim().ToLowerInvariant();
        return RepeatedWhitespacePattern().Replace(normalizedText, " ");
    }

    private static string RemoveJsonCodeFences(string chatReply)
    {
        var normalizedText = chatReply.Trim();
        if (normalizedText.StartsWith("```json", StringComparison.OrdinalIgnoreCase))
        {
            normalizedText = normalizedText[7..];
        }
        else if (normalizedText.StartsWith("```", StringComparison.OrdinalIgnoreCase))
        {
            normalizedText = normalizedText[3..];
        }

        if (normalizedText.EndsWith("```", StringComparison.OrdinalIgnoreCase))
        {
            normalizedText = normalizedText[..^3];
        }

        return normalizedText.Trim();
    }

    private static ResponseDto CopyClassificationResult(ResponseDto originalClassification, string processingStage, string claimDescription)
    {
        return new ResponseDto
        {
            Category = originalClassification.Category,
            SubCategory = originalClassification.SubCategory,
            ExtractedAmount = originalClassification.ExtractedAmount,
            Currency = originalClassification.Currency,
            Merchant = originalClassification.Merchant,
            ConfidenceScore = originalClassification.ConfidenceScore,
            ComplianceStatus = originalClassification.ComplianceStatus,
            ComplianceNotes = originalClassification.ComplianceNotes,
            ClassificationStage = processingStage,
            ExpenseDescription = claimDescription.Trim()
        };
    }

    private static ResponseDto CreateUnverifiableResult(string claimDescription, string expenseCategory, string processingStage)
    {
        return new ResponseDto
        {
            Category = expenseCategory,
            SubCategory = "General Expense",
            ConfidenceScore = 0.5,
            ComplianceStatus = "Unverifiable",
            ComplianceNotes = "Automated fallback classification.",
            ClassificationStage = processingStage,
            ExpenseDescription = claimDescription.Trim()
        };
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex RepeatedWhitespacePattern();
}
