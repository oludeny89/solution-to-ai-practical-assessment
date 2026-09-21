using ExpenseClassifier.Agents;
using ExpenseClassifier.Models;
using ExpenseClassifier.Services;
using ExpenseClassifier.Tools;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ExpenseClassifier.Solution.Tests;

public class ExpenseClassifierAgentTests
{
    private readonly ExpenseClassifierAgent _agent;
    private readonly IMemoryCache _cache;

    public ExpenseClassifierAgentTests()
    {
        _cache = new MemoryCache(new MemoryCacheOptions());
        var simulationClient = new SimulationChatClient();
        var policyTool = new CompanyPolicyTool();
        var spendingLimitTool = new SpendingLimitTool();
        var logger = NullLogger<ExpenseClassifierAgent>.Instance;

        _agent = new ExpenseClassifierAgent(
            simulationClient,
            policyTool,
            spendingLimitTool,
            _cache,
            logger);
    }

    [Fact]
    public async Task Classify_PolicyMatch_ReturnsPolicyAgentStage()
    {
        var description = "Bolt ride from Murtala Muhammed Airport to Victoria Island office costing ₦15,000";

        var result = await _agent.Classify(description);

        Assert.NotNull(result);
        Assert.Equal("Transportation", result.Category);
        Assert.Equal("Bolt", result.Merchant);
        Assert.Equal(15000m, result.ExtractedAmount);
        Assert.Equal("NGN", result.Currency);
        Assert.Equal("Compliant", result.ComplianceStatus);
        Assert.Equal("PolicyAgentWithTools", result.ClassificationStage);
    }

    [Fact]
    public async Task Classify_UnmatchedExpense_TriggersFallbackGeneralAgent()
    {
        var description = "Annual JetBrains Rider IDE team license renewal for software engineers $650 USD";

        var result = await _agent.Classify(description);

        Assert.NotNull(result);
        Assert.Equal("Software Subscriptions", result.Category);
        Assert.Equal("JetBrains", result.Merchant);
        Assert.Equal(650m, result.ExtractedAmount);
        Assert.Equal("USD", result.Currency);
        Assert.Equal("FallbackGeneralAgent", result.ClassificationStage);
    }

    [Fact]
    public async Task Classify_SubsequentCall_ReturnsCacheHit()
    {
        var first = "Ikeja Electric (IKEDC) prepaid meter token recharge for ₦50,000";
        var second = "   ikeja electric (ikedc) prepaid meter token recharge for ₦50,000   ";

        var initialResult = await _agent.Classify(first);
        Assert.Equal("PolicyAgentWithTools", initialResult.ClassificationStage);

        var cachedResult = await _agent.Classify(second);
        Assert.Equal("CacheHit", cachedResult.ClassificationStage);
        Assert.Equal("Utilities", cachedResult.Category);
        Assert.Equal(50000m, cachedResult.ExtractedAmount);
    }

    [Fact]
    public async Task Classify_ExcessiveExpense_FlagsPolicyViolation()
    {
        var description = "Client dinner meeting at Terra Kulture Restaurant Victoria Island Lagos for $180 USD";

        var result = await _agent.Classify(description);

        Assert.NotNull(result);
        Assert.Equal("Food", result.Category);
        Assert.Equal("PolicyViolation", result.ComplianceStatus);
        Assert.Contains("maximum allowable", result.ComplianceNotes);
    }

    [Fact]
    public async Task Classify_OverThresholdMeal_FlagsRequiresManagerApproval()
    {
        var description = "Team lunch at Bukka Hut Lekki costing ₦40,000";

        var result = await _agent.Classify(description);

        Assert.NotNull(result);
        Assert.Equal("Food", result.Category);
        Assert.Equal("RequiresManagerApproval", result.ComplianceStatus);
        Assert.Contains("manager sign-off", result.ComplianceNotes);
    }

    [Fact]
    public async Task Classify_InvalidInput_ThrowsArgumentException()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _agent.Classify("   "));
    }

    [Fact]
    public async Task ClassifyBatch_ProcessesItemsConcurrentlyAndAggregatesSummary()
    {
        var request = new BatchExpenseRequest
        {
            Department = "Engineering",
            EmployeeId = "EMP-9402",
            Items =
            [
                new ExpenseItemRequest { Id = "1", Description = "Bolt ride to office ₦8,500" },
                new ExpenseItemRequest { Id = "2", Description = "Team lunch at Bukka Hut ₦42,000" },
                new ExpenseItemRequest { Id = "3", Description = "Monthly MTN 5G Broadband data subscription ₦35,000" },
                new ExpenseItemRequest { Id = "4", Description = "GitHub Copilot monthly subscription $19 USD" }
            ]
        };

        var response = await _agent.ClassifyBatch(request);

        Assert.NotNull(response);
        Assert.Equal(4, response.Summary.TotalItems);
        Assert.Equal(3, response.Summary.CompliantCount);
        Assert.Equal(1, response.Summary.RequiresApprovalCount);
        Assert.Equal(0, response.Summary.PolicyViolationCount);
        Assert.Equal(4, response.Results.Count);

        Assert.True(response.Summary.TotalAmountByCurrency.ContainsKey("NGN"));
        Assert.True(response.Summary.TotalAmountByCurrency.ContainsKey("USD"));
        Assert.Equal(85500m, response.Summary.TotalAmountByCurrency["NGN"]);
        Assert.Equal(19m, response.Summary.TotalAmountByCurrency["USD"]);
    }
}
