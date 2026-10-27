using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Shop.ServiceDefaults;

/// <summary>
/// Middleware to propagate the current OpenTelemetry Trace ID in the HTTP response headers
/// and enrich the logger with a structured TraceId scope.
/// </summary>
public sealed class TraceMiddleware
{
    public const string HeaderName = "X-Trace-Id";
    private readonly RequestDelegate _next;

    public TraceMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var traceId = Activity.Current?.TraceId.ToString()
            ?? (!string.IsNullOrWhiteSpace(context.TraceIdentifier) ? context.TraceIdentifier : ActivityTraceId.CreateRandom().ToString());

        context.Response.OnStarting(() =>
        {
            if (!context.Response.Headers.ContainsKey(HeaderName))
            {
                context.Response.Headers[HeaderName] = traceId;
            }
            return Task.CompletedTask;
        });

        var logger = context.RequestServices.GetRequiredService<ILogger<TraceMiddleware>>();
        using (logger.BeginScope(new Dictionary<string, object> { ["TraceId"] = traceId }))
        {
            await _next(context);
        }
    }
}
