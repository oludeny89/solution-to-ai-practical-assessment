# Expense Classifier — Implementation Notes

This document answers the architecture questions in the assessment using the current implementation. It explains the decisions visible in the code, their trade-offs, and improvements that have not yet been implemented.

The service converts an employee's expense description into structured financial information. For example, `Bolt ride to office ₦8,500` becomes a Transportation expense with amount `8500`, currency `NGN`, and merchant `Bolt`. It also returns a compliance status indicating whether the expense fits spending rules or needs review.

The [README](README.md) contains the assessment requirements. These notes describe the implementation as it currently stands; they do not claim that every requirement is complete.

## 1. Multi-Stage AI Agent Architecture & Orchestration

### Structure and initialization

[ExpenseClassifierAgent](ExpenseClassifier.Solution/Services/ExpenseClassifierAgent.cs) implements `IExpenseClassifierAgent`, which exposes two operations:

- `Classify`: processes one expense description.
- `ClassifyBatch`: processes multiple descriptions and calculates summary totals.

The constructor receives an `IChatClient`, the company policy and spending limit tools, an `IMemoryCache`, and a logger through dependency injection. This keeps the classification workflow separate from the choice of AI provider and lets tests use the local simulator.

The agent coordinates these steps:

```text
Validate description
    → Normalize description and check cache
    → Return cached result if available
    → Otherwise call the primary stage with tools
    → Call the fallback stage if the primary result is missing or Other
    → Set response metadata, cache the result, and return it
```

The two stages use the same chat client with different instructions and options. They are not separate AI services or separately instantiated agent-framework agents.

### Tools and the primary stage

The constructor wraps `CompanyPolicyTool.GetPolicy` and `SpendingLimitTool.GetSpendingLimits` using `AIFunctionFactory.Create`. These function definitions are assigned to the primary stage's `ChatOptions.Tools` collection.

The primary system prompt instructs the model to consult both tools, choose an official category, extract financial details, and evaluate compliance. The tools return policy text; they do not calculate compliance themselves.

**Current limitation:** exposing tool definitions does not demonstrate that tool requests are executed. The live client setup contains no explicit function-invocation wrapper or tool-execution loop. The live tool workflow needs implementation or verification before it can be described as complete. The simulator only checks whether tools are present and does not invoke them.

### Fallback behavior

If the primary result is `null` or its category equals `Other`, the agent makes a second call with a broader classification prompt and no tools. This can identify categories outside the standard company policy.

The final result comes from the fallback if available, otherwise from the primary result. If both are missing, the agent creates an `Other` response with compliance status `Unverifiable`.

The current condition does not check whether other category names belong to the official category list. An unexpected category other than `Other` therefore bypasses fallback.

### Reuse and thread safety

Prompts are constants, and tool definitions and options are built once per agent instance. This avoids rebuilding them for every item within a batch. However, the agent is scoped to an HTTP request, so construction and tool reflection still occur for each new request.

Each classification builds its own message list and response objects. The options are shared within the agent and are not modified by the classification methods. The tools return static text and have no mutable request state. Concurrent safety also depends on the selected chat client supporting concurrent calls with those options.

### Structured output

Both prompts ask for a JSON object. `RequestClassificationAsync` removes surrounding Markdown code fences if present and uses `System.Text.Json` to deserialize the text into `ResponseDto`. Property matching is case-insensitive.

This creates a typed C# result, but it does not guarantee valid business data or deterministic live AI output. The implementation does not configure a strict response schema or validate category names, compliance statuses, confidence ranges, or currency codes after deserialization.

There is also a contract mismatch: `ResponseDto.ExpenseDescription` is required, but the JSON examples in the prompts omit it. A live response following those examples can fail required-property deserialization before the agent assigns the description later. The simulator includes this property, so its responses do not expose that issue.

A future improvement is to deserialize into a dedicated model-output type, validate it, and then construct the API response with application-owned metadata.

## 2. Dependency Injection & Service Lifetime Strategy

Dependency injection means the application creates and supplies the objects a class needs. Registrations are configured in [Program.cs](ExpenseClassifier.Solution/Program.cs).

| Service | Current lifetime | Purpose and trade-off |
|---|---|---|
| `IMemoryCache` | Singleton, through `AddMemoryCache()` | Shares cached results across requests within one application process. |
| `CompanyPolicyTool` | Singleton | Reuses a stateless source of category rules. |
| `SpendingLimitTool` | Singleton | Reuses a stateless source of spending rules. |
| `IChatClient` in simulation mode | Singleton | Reuses the simulator, which performs classification with local data. |
| `AzureOpenAIClient` in live mode | Scoped | Creates a client per request scope; this is simple but does not reuse the application-level client across requests. |
| `IChatClient` in live mode | Scoped | Adapts the scoped Azure client for the configured deployment. |
| `IExpenseClassifierAgent` | Scoped | Supplies one coordinator per request, shared by the items in that request's batch. |

A singleton lives for the application lifetime. A scoped service normally lives for one HTTP request. The current registrations do not make a singleton capture a scoped classifier or live chat client.

### Offline development and testing

`ExpenseClassifier:UseSimulation` selects the chat client. It is currently `true` in [appsettings.json](ExpenseClassifier.Solution/appsettings.json).

[SimulationChatClient](ExpenseClassifier.Solution/Services/SimulationChatClient.cs) implements `IChatClient` using keyword matching, regular expressions for amount extraction, and hardcoded compliance rules. It makes no external AI request and requires no Azure credentials. Its predictable responses make it useful for local development and automated tests.

Simulation verifies the surrounding workflow, but does not prove real model behavior, tool execution, or complete policy coverage. For example, it deliberately routes software subscriptions through fallback and does not implement all spending rules described by the policy tools. Its confidence scores are fixed values, not calibrated probabilities.

## 3. High-Performance Caching & Concurrency

### Cache-aside strategy

Cache-aside means the agent checks the cache first and computes an answer only when the answer is absent.

The cache key starts with `expense_class_` and includes a normalized description. Normalization trims leading and trailing whitespace, converts letters to lowercase, and collapses repeated whitespace.

For example, these descriptions share a key:

```text
"  Bolt ride to office ₦8,500  "
"bolt ride to office ₦8,500"
```

Punctuation is preserved. The implementation does not tokenize descriptions or perform semantic matching.

Results use a 30-minute absolute expiration: a cache hit does not restart that lifetime. On a hit, the agent returns a copy marked `CacheHit`, preserving the stored object's classification stage and using the current caller's trimmed description.

### Cache limitations

- `IMemoryCache` supports concurrent access, but the lookup, classification, and insertion sequence is not atomic. Simultaneous misses for the same description can make duplicate AI calls. No cache-stampede prevention is implemented.
- Every final result is cached, including the default result created after failures. Successful deserialization alone does not establish that a result is valid.
- On a miss, the returned result is the same mutable object placed in the cache. A direct in-process caller could modify it. Copying on storage or using immutable results would improve isolation.
- Cached answers disappear when the application restarts and are not shared across servers.
- Keys do not include a policy version, so a changed policy can leave old decisions cached until expiration.
- Cache lifetime is hardcoded despite the similarly named configuration setting.

A future improvement is to coordinate simultaneous requests for each key, cache only validated results, and include policy version information in the key.

### Bounded batch concurrency

`ClassifyBatch` uses `Parallel.ForEachAsync` with `MaxDegreeOfParallelism = 5`. This permits up to five classifications in progress within one batch, limiting simultaneous work compared with launching every item at once.

Each item calls the existing `Classify` method. Results are written into separate array positions using the original item index, preserving input order even when items finish out of order. Summary calculation runs after all items finish, so it does not require concurrent updates to the totals dictionary.

The summary contains item count, compliance counts, totals by currency, and elapsed processing time. Amounts in different currencies are kept separate; there is no exchange-rate conversion. The timer stops before summary aggregation, so it does not measure the entire HTTP response duration.

The concurrency limit applies per batch, not across all requests or application instances. It is hardcoded rather than read from `MaxBatchConcurrency` in configuration. The response also has no separate count for `Unverifiable` results, and item IDs are not returned.

## 4. Resilience, Error Handling & API Robustness

### Input validation and ProblemDetails

The single-expense endpoint rejects blank descriptions. The batch endpoint rejects a missing or empty items collection. Both return HTTP 400 with a `ProblemDetails` object containing `Title`, `Detail`, and `Status`.

For example, the explicit single-expense validation returns these fields:

```json
{
  "title": "Invalid Request Payload",
  "detail": "Expense description must not be empty or whitespace.",
  "status": 400
}
```

The agent also checks single descriptions and batch collection validity when called directly. However, the batch endpoint does not validate every item before starting work. A null item or invalid item description can fail processing rather than produce a controlled item-level response.

There is no centralized application exception handler that consistently maps internal and upstream failures to ProblemDetails responses. Full error-response coverage is therefore incomplete.

### AI failures and malformed responses

`RequestClassificationAsync` catches exceptions, logs a warning, and returns `null`. Empty response text also produces `null`. A primary-stage failure triggers fallback. If both stages fail, the caller receives an `Other` / `Unverifiable` default response, normally with HTTP 200.

This keeps some failures from escaping the classification flow, but hides the distinction between an unknown expense and an unavailable AI service. The default response is also cached.

The application code does not define an explicit retry strategy, rate-limit handling policy, circuit breaker, or classification deadline. Any provider SDK behavior is separate from the application's own implementation. Fallback is a second classification attempt, not a dedicated transient-error retry policy.

### Cancellation

Cancellation tokens flow from the endpoints into the agent, the chat client call, and `Parallel.ForEachAsync`. However, the broad exception handler also catches cancellation exceptions from the chat call, and the simulator does not explicitly check cancellation. Cancellation is therefore passed through but not consistently honored end to end.

### Improvements still needed

1. Validate every batch item before processing and set request-size limits.
2. Let cancellation exceptions propagate instead of converting them into classification failures.
3. Distinguish malformed model output, temporary upstream failures, and legitimate unknown categories.
4. Add bounded retries for appropriate transient failures, explicit deadlines, and consistent upstream error responses.
5. Validate structured results before returning or caching them.
6. Enforce financial thresholds in deterministic application code after extraction, with explicit handling of unsupported currencies and missing context such as number of people or nights.

### Verification status

[ExpenseClassifierAgentTests.cs](ExpenseClassifier.Solution.Tests/ExpenseClassifierAgentTests.cs) contains tests for policy classification, fallback, cache reuse, meal compliance, invalid single input, and batch totals using the simulator. The batch test checks results and totals; it does not directly measure the concurrency limit.

These notes are based on source inspection. Tests and live Azure calls were not run for this documentation update. Live tool execution, failure handling, and policy boundary cases need additional verification.

## 5. Production & Enterprise Scale Roadmap

The following are proposed changes, not features already implemented. At a target such as 100,000 claims per day, capacity planning would need to consider peak traffic, model latency, provider quotas, and average calls per expense, including fallback.

| Area | Proposed change | Reason |
|---|---|---|
| Classification correctness | Complete the live tool workflow, enforce an output contract, and validate extracted fields. | Ensure live responses can be processed reliably. |
| Financial policy | Use versioned, deterministic compliance rules after extraction. | Make threshold decisions consistent and auditable. |
| Configuration | Bind and validate cache lifetime, batch size, concurrency, and timeout options. | Make settings effective without code changes. |
| Shared caching | Consider Redis with validated results and policy-aware keys. | Share answers across application instances. |
| Duplicate work | Coordinate requests for identical keys and give submitted jobs stable identifiers. | Reduce duplicate model calls and repeated processing. |
| Large batches | Consider a durable queue such as Azure Service Bus with background workers and job-status endpoints. | Absorb bursts without keeping long-running HTTP requests open. |
| Rate limits | Coordinate model-call concurrency across workers and use bounded retry policies. | Keep total traffic within provider limits. |
| Access and secrets | Add authentication, authorization, tenant isolation, and managed identity where supported. | Restrict access to financial information and avoid embedding credentials. |
| Observability | Add metrics and traces for latency, cache hits, fallback frequency, failures, and model usage. | Identify bottlenecks and track operating cost. |
| Audit history | Store policy/model versions and classification outcomes with appropriate access and retention controls. | Support investigation and review of financial decisions. |
| Testing | Add boundary, failure, cancellation, concurrent-load, and live integration tests. | Verify behavior beyond predictable simulation examples. |

Semantic caching would need special care. Similar sentences can contain different amounts, currencies, or business contexts and require different compliance decisions. Exact normalized matching is a simpler starting point; broader reuse would need safeguards to preserve those distinctions.
