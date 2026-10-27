# ADR 0014: Internal Endpoints and Health Endpoint Exposure

## Status
Accepted

## Context
Microservices need both internal diagnostic endpoints (readiness and liveness health checks) and service-to-service communication endpoints (such as synchronous product lookups). Exposing these endpoints indiscriminately to the public Internet presents an attack surface and risks leaking operational infrastructure status.

## Decision
I established the following endpoint exposure policies:

1. **Internal Service-to-Service Endpoints**:
   - Internal APIs are mounted under the route prefix `/internal/v1/...` (e.g. `/internal/v1/products/lookup`).
   - The YARP reverse proxy exclusively defines routes matching `/api/v1/...`. Because there is no route mapping for `/internal/**`, the gateway returns 404 for any external attempt to reach internal endpoints.
   - Internal endpoints are accessible only within the private container network.

2. **Liveness (`/alive`) vs Readiness (`/health`) Separation**:
   - `/alive` performs only minimal in-process health checks (`self` tagged `live`). It verifies that the ASP.NET Core process is responsive and able to handle HTTP requests.
   - `/health` executes all registered dependency health checks (e.g. database connectivity, broker connection). It verifies that the service is ready to accept production workload.

3. **Gated Health Check Exposure**:
   - Gated via configuration setting `HealthChecks:Expose`.
   - In Development, Aspire AppHost, and Docker Compose environments, `HealthChecks:Expose` is set to `true`.
   - In production environments, it defaults to `false` unless explicitly opted-in, preventing public reconnaissance of downstream infrastructure dependencies.

4. **CLI-Based Container Health Checking**:
   - Implemented a lightweight `--healthcheck` command-line switch directly in `Shop.ServiceDefaults.HealthCheckCli`.
   - Container engines (Docker, Podman, Kubernetes) execute `dotnet /app/Shop.Catalog.Api.dll --healthcheck` which issues a local HTTP request against `http://localhost:8080/alive`.
   - This eliminates the need for `curl`, `wget`, or a full shell in production container images, allowing the use of ultra-minimal chiseled runtime images.

## Consequences
### Positive
- Zero external exposure of internal lookup APIs or infrastructure health probes.
- Minimal container image footprint and reduced CVE surface area.
- Strict separation between container restarts (liveness) and traffic routing (readiness).

### Negative / Trade-offs
- Developers must remember to set `HealthChecks:Expose=true` when running services outside the default orchestrators if they wish to inspect readiness via a browser.
