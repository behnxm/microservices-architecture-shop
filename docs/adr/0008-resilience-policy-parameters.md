# ADR 0008: Resilience Policy Parameters

## Status
Accepted

## Context
When `ordering-api` performs synchronous lookups against `catalog-api`, network blips, pod restarts, or transient service overloads can cause individual HTTP requests to fail. Uncontrolled retries risk creating a "retry storm" (thundering herd) that overwhelms a recovering service. Conversely, failing immediately on a transient 20-millisecond network timeout damages user experience.

I needed well-tuned resilience policies governed by empirical defaults that protect both caller and callee.

## Decision
I used `Microsoft.Extensions.Http.Resilience` (the modern Polly v8 integration in .NET 10) to configure a standard resilience pipeline on the typed `CatalogClient`:

```csharp
builder.Services.AddHttpClient<ICatalogClient, CatalogClient>(client =>
{
    client.BaseAddress = new Uri("http://catalog-api");
})
.AddStandardResilienceHandler(options =>
{
    options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(10);
    options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(2);
    options.Retry.MaxRetryAttempts = 3;
    options.Retry.BackoffType = DelayBackoffType.Exponential;
    options.Retry.UseJitter = true;
    options.Retry.Delay = TimeSpan.FromMilliseconds(200);
    options.CircuitBreaker.FailureRatio = 0.5;
    options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(10);
    options.CircuitBreaker.MinimumThroughput = 5;
    options.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(15);
});
```

### Parameter Rationale
1. **Total Request Timeout (10s)**: Bounds the entire execution lifespan across all retry attempts. Prevents upstream HTTP gateway connections from hanging indefinitely.
2. **Attempt Timeout (2s)**: An individual internal HTTP call between microservices in the same cluster should finish in single-digit milliseconds. If it exceeds 2 seconds, the connection or host is stalled, and the attempt is cancelled to allow retry.
3. **Retry Strategy (3 attempts, Exponential Backoff with Jitter, 200ms base)**:
   - Only retries transient failures (HTTP 5xx, 408, or network/socket exceptions).
   - HTTP 4xx (client errors such as 400 or 404) are never retried.
   - Jitter decorrelates concurrent retry attempts across replicas, preventing synchronized wave spikes on `catalog-api`.
4. **Circuit Breaker (50% failure ratio, 10s sampling, minimum throughput 5, 15s break duration)**:
   - If `catalog-api` is crashing or down hard, continuing to send retries exhausts resources.
   - When 50% of at least 5 requests fail within 10 seconds, the circuit opens for 15 seconds.
   - During the 15-second break duration, requests fail fast with `CatalogUnavailableException`, which translates immediately to HTTP 503 (`Ordering.CatalogUnavailable`) and `Retry-After: 15`.

## Consequences
### Positive
- Transient network hiccups and rolling deployments are absorbed invisibly without user-visible errors.
- Downstream services under heavy stress are given breathing room by circuit breaker trips rather than subjected to retry floods.
- Explicit telemetry and metrics are emitted for retries, circuit state transitions, and timeouts via OpenTelemetry metrics (`Microsoft.Extensions.Http.Resilience`).

### Negative / Trade-offs
- In the worst case (3 slow attempts of 2s each), order placement can take up to ~6 seconds before returning HTTP 503 to the client. This is still within the 10-second total timeout and the 30-second gateway request timeout.
