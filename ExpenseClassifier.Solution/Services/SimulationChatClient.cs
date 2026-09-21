using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using ExpenseClassifier.Models;
using ExpenseClassifier.Tools;
using Microsoft.Extensions.AI;

namespace ExpenseClassifier.Services;

/// <summary>
/// A deterministic in-memory simulation of <see cref="IChatClient"/> for local testing and CI
/// when live Azure OpenAI credentials are unavailable or unnecessary.
/// </summary>
public partial class SimulationChatClient : IChatClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public ChatClientMetadata Metadata => new("simulation-openai", new Uri("http://localhost:5000"), "gpt-4o-simulated");

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var messages = chatMessages.ToList();
        var lastUserMessage = messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? string.Empty;

        // Perform deterministic simulation extraction
        var result = SimulateClassification(lastUserMessage, options);

        var json = JsonSerializer.Serialize(result, JsonOptions);
        var responseMessage = new ChatMessage(ChatRole.Assistant, json);

        var response = new ChatResponse(responseMessage)
        {
            ModelId = "gpt-4o-simulated"
        };

        return Task.FromResult(response);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(chatMessages, options, cancellationToken);
        var text = response.Messages.FirstOrDefault()?.Text ?? string.Empty;
        yield return new ChatResponseUpdate
        {
            Role = ChatRole.Assistant,
            Contents = [new TextContent(text)]
        };
    }

    public void Dispose()
    {
        // No unmanaged resources
        GC.SuppressFinalize(this);
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        return serviceType.IsInstanceOfType(this) ? this : null;
    }

    private static ResponseDto SimulateClassification(string text, ChatOptions? options)
    {
        var (amount, currency) = ExtractAmountAndCurrency(text);
        var (category, subCategory, merchant, isPolicyMatch) = ClassifyHeuristic(text);

        var hasTools = options?.Tools != null && options.Tools.Count > 0;
        var effectiveCategory = isPolicyMatch || !hasTools ? category : "Other";
        var stage = hasTools ? "PolicyAgentWithTools" : "FallbackGeneralAgent";

        var (compliance, notes) = EvaluateCompliance(effectiveCategory, amount, currency);

        return new ResponseDto
        {
            Category = effectiveCategory,
            SubCategory = subCategory,
            ExtractedAmount = amount,
            Currency = currency,
            Merchant = merchant,
            ConfidenceScore = isPolicyMatch ? 0.98 : 0.88,
            ComplianceStatus = compliance,
            ComplianceNotes = notes,
            ClassificationStage = stage,
            ExpenseDescription = text.Trim()
        };
    }

    private static (decimal? Amount, string? Currency) ExtractAmountAndCurrency(string text)
    {
        // Try matching patterns like ₦45,000, $50.00, 120 USD, 30000 NGN, €250, etc.
        var symbolMatch = AmountWithSymbolRegex().Match(text);
        if (symbolMatch.Success)
        {
            var symbol = symbolMatch.Groups[1].Value.Trim();
            var rawNum = symbolMatch.Groups[2].Value.Replace(",", "");
            if (decimal.TryParse(rawNum, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed))
            {
                var curr = symbol switch
                {
                    "₦" or "NGN" => "NGN",
                    "$" or "USD" => "USD",
                    "€" or "EUR" => "EUR",
                    "£" or "GBP" => "GBP",
                    _ => "USD"
                };
                return (parsed, curr);
            }
        }

        var suffixMatch = AmountWithSuffixRegex().Match(text);
        if (suffixMatch.Success)
        {
            var rawNum = suffixMatch.Groups[1].Value.Replace(",", "");
            var curr = suffixMatch.Groups[2].Value.ToUpperInvariant();
            if (decimal.TryParse(rawNum, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed))
            {
                return (parsed, curr);
            }
        }

        return (null, null);
    }

    private static (string Category, string? SubCategory, string? Merchant, bool IsPolicyMatch) ClassifyHeuristic(string text)
    {
        var lower = text.ToLowerInvariant();

        if (lower.Contains("bolt") || lower.Contains("uber") || lower.Contains("taxi") || lower.Contains("airport") || lower.Contains("transit") || lower.Contains("flight"))
        {
            string? merchant = lower.Contains("bolt") ? "Bolt" : lower.Contains("uber") ? "Uber" : null;
            string subCategory = lower.Contains("flight") ? "Flight" : lower.Contains("airport") ? "Airport Transfer" : "Rideshare";
            return ("Transportation", subCategory, merchant, true);
        }

        if (lower.Contains("bukka") || lower.Contains("restaurant") || lower.Contains("lunch") || lower.Contains("dinner") || lower.Contains("food") || lower.Contains("kulture") || lower.Contains("meal"))
        {
            string? merchant = lower.Contains("bukka hut") ? "Bukka Hut" : lower.Contains("terra kulture") ? "Terra Kulture" : null;
            string subCategory = lower.Contains("dinner") || lower.Contains("lunch") ? "Client Dining" : "Team Meal";
            return ("Food", subCategory, merchant, true);
        }

        if (lower.Contains("hotel") || lower.Contains("suites") || lower.Contains("accommodation") || lower.Contains("lodging") || lower.Contains("stay") || lower.Contains("retreat"))
        {
            string? merchant = lower.Contains("eko hotel") ? "Eko Hotel & Suites" : null;
            return ("Accommodation", "Hotel Lodging", merchant, true);
        }

        if (lower.Contains("ikedc") || lower.Contains("electric") || lower.Contains("broadband") || lower.Contains("mtn") || lower.Contains("airtel") || lower.Contains("starlink") || lower.Contains("meter token"))
        {
            string? merchant = lower.Contains("ikedc") || lower.Contains("ikeja electric") ? "Ikeja Electric (IKEDC)" : lower.Contains("mtn") ? "MTN" : null;
            string subCategory = lower.Contains("electric") || lower.Contains("meter") ? "Electricity" : "Internet Subscription";
            return ("Utilities", subCategory, merchant, true);
        }

        if (lower.Contains("jetbrains") || lower.Contains("rider") || lower.Contains("slack") || lower.Contains("aws") || lower.Contains("license") || lower.Contains("subscription") || lower.Contains("github"))
        {
            string? merchant = lower.Contains("jetbrains") ? "JetBrains" : lower.Contains("github") ? "GitHub" : lower.Contains("slack") ? "Slack" : null;
            return ("Software Subscriptions", "Developer Tooling", merchant, false);
        }

        if (lower.Contains("paper") || lower.Contains("stationery") || lower.Contains("keyboard") || lower.Contains("desk"))
        {
            return ("Office Supplies", "Stationery", null, false);
        }

        return ("Other", "General Expense", null, false);
    }

    private static (string Status, string? Notes) EvaluateCompliance(string category, decimal? amount, string? currency)
    {
        if (amount == null)
        {
            return ("Unverifiable", "Amount not specified in expense description; unable to verify threshold compliance.");
        }

        var amt = amount.Value;
        var isNgn = string.Equals(currency, "NGN", StringComparison.OrdinalIgnoreCase);
        var isUsd = string.Equals(currency, "USD", StringComparison.OrdinalIgnoreCase);

        return category switch
        {
            "Food" when (isNgn && amt > 100000) || (isUsd && amt > 150) =>
                ("PolicyViolation", "Exceeds maximum allowable entertainment limit per policy without prior executive approval."),
            "Food" when (isNgn && amt > 35000) || (isUsd && amt > 50) =>
                ("RequiresManagerApproval", "Meal expense exceeds standard ₦35,000 / $50 per-person allowance; requires manager sign-off."),
            "Transportation" when (isNgn && amt > 50000) || (isUsd && amt > 60) =>
                ("RequiresManagerApproval", "Transit fare exceeds standard limit; requires manager justification."),
            "Accommodation" when (isNgn && amt > 300000) || (isUsd && amt > 350) =>
                ("RequiresManagerApproval", "Hotel rate exceeds standard ₦180,000 / $200 per night threshold."),
            _ => ("Compliant", "Expense is within standard company policy limits.")
        };
    }

    [GeneratedRegex(@"(₦|\$|EUR|USD|NGN|€|£)\s*(\d{1,3}(?:,\d{3})*(?:\.\d+)?|\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex AmountWithSymbolRegex();

    [GeneratedRegex(@"(\d{1,3}(?:,\d{3})*(?:\.\d+)?|\d+)\s*(USD|NGN|EUR|GBP)", RegexOptions.IgnoreCase)]
    private static partial Regex AmountWithSuffixRegex();
}
