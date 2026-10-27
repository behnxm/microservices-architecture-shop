# ADR 0007: Code-Based YARP Gateway

## Status
Accepted

## Context
A microservices system requires a single entry point for external traffic to handle routing, protocol translation, client rate limiting, security headers, CORS, and distributed tracing correlation.

I evaluated two architectural options for the gateway:
1. Aspire's managed YARP container resource or generic off-the-shelf reverse proxy containers (e.g. Envoy, NGINX).
2. A custom code-based ASP.NET Core application powered by Microsoft's YARP (`Yarp.ReverseProxy`).

## Decision
I chose to build a dedicated code-based ASP.NET Core gateway using `Yarp.ReverseProxy` and `Microsoft.Extensions.ServiceDiscovery.Yarp`.

I configured:
- Routing rules matching `/api/v1/products/{**catch-all}` and `/api/v1/orders/{**catch-all}`.
- Strict isolation of internal endpoints: routes matching `/internal/**` are deliberately omitted from proxy route configuration, ensuring they cannot be reached externally.
- Per-route IP-based rate limiting via ASP.NET Core RateLimiter (`standard-policy` for read traffic, and a strict `orders-policy` for checkout mutation).
- Service discovery resolution so cluster destinations (`http://catalog-api`, `http://ordering-api`) are resolved seamlessly both in Aspire and Docker Compose without hardcoded IP addresses.
- Security headers middleware (`X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`, `Referrer-Policy: strict-origin-when-cross-origin`, CSP).
- Transparent pass-through of critical headers such as `Idempotency-Key` and `X-Trace-Id`.

## Consequences
### Positive
- Deep integration with .NET 10 OpenTelemetry, structured logging, and health checking (`Shop.ServiceDefaults`).
- Custom middleware can inspect, enrich, or reject requests in C# with full type safety before proxying.
- No need to maintain complex third-party proxy configuration DSLs.
- Architectural unit tests can verify proxy policies directly in C# using `WebApplicationFactory`.

### Negative / Trade-offs
- The gateway is a .NET application consuming .NET runtime memory, unlike a lightweight C/Go proxy such as Envoy or Envoy Gateway. For our throughput requirements, the observability and developer experience advantages heavily outweigh this cost.
