using Azure.AI.OpenAI;
using ExpenseClassifier.Agents;
using ExpenseClassifier.Models;
using ExpenseClassifier.Services;
using ExpenseClassifier.Tools;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.AI;

var builder = WebApplication.CreateBuilder(args);

// --- Dependency Injection & Services Setup ---

// 1. In-Memory Caching (Required for caching LLM responses)
builder.Services.AddMemoryCache();

// 2. Policy & Spending Limit Tools
builder.Services.AddSingleton<CompanyPolicyTool>();
builder.Services.AddSingleton<SpendingLimitTool>();

// 3. AI Client Setup (Supports live Azure OpenAI or offline SimulationChatClient)
var useSimulation = builder.Configuration.GetValue<bool>("ExpenseClassifier:UseSimulation");
if (useSimulation)
{
    builder.Services.AddSingleton<IChatClient, SimulationChatClient>();
}
else
{
    builder.Services.AddScoped<AzureOpenAIClient>(sp =>
    {
        var config = sp.GetRequiredService<IConfiguration>();
        var endpoint = config["AzureOpenAI:Endpoint"] ?? throw new InvalidOperationException("AzureOpenAI:Endpoint is not configured.");
        var apiKey = config["AzureOpenAI:ApiKey"] ?? throw new InvalidOperationException("AzureOpenAI:ApiKey is not configured.");

        return new AzureOpenAIClient(new Uri(endpoint), new System.ClientModel.ApiKeyCredential(apiKey));
    });

    builder.Services.AddScoped<IChatClient>(sp =>
    {
        var config = sp.GetRequiredService<IConfiguration>();
        var deploymentName = config["AzureOpenAI:DeploymentName"] ?? throw new InvalidOperationException("AzureOpenAI:DeploymentName is not configured.");

        var client = sp.GetRequiredService<AzureOpenAIClient>();
        return client.GetChatClient(deploymentName).AsIChatClient();
    });
}

// 4. Intelligent Expense Classifier Agent Registration
builder.Services.AddScoped<IExpenseClassifierAgent, ExpenseClassifierAgent>();

var app = builder.Build();

// --- API Endpoints ---

// Single Expense Classification Endpoint
app.MapPost("/classify", async (IExpenseClassifierAgent agent, RequestDto request, CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request?.Description))
    {
        return Results.BadRequest(new ProblemDetails
        {
            Title = "Invalid Request Payload",
            Detail = "Expense description must not be empty or whitespace.",
            Status = StatusCodes.Status400BadRequest
        });
    }

    var result = await agent.Classify(request.Description, cancellationToken);
    return Results.Ok(result);
});

// Batch Expense Classification Endpoint
app.MapPost("/classify/batch", async (IExpenseClassifierAgent agent, BatchExpenseRequest request, CancellationToken cancellationToken) =>
{
    if (request?.Items == null || request.Items.Count == 0)
    {
        return Results.BadRequest(new ProblemDetails
        {
            Title = "Invalid Batch Request Payload",
            Detail = "Batch items list cannot be null or empty.",
            Status = StatusCodes.Status400BadRequest
        });
    }

    var result = await agent.ClassifyBatch(request, cancellationToken);
    return Results.Ok(result);
});

await app.RunAsync();
