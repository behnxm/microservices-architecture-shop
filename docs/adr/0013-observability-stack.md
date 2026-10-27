# ADR 0013: Observability Stack

## Status
Accepted

## Context
Monitoring distributed microservices across HTTP gateways, database queries, and RabbitMQ message flows requires unified metrics, structured logs, and distributed traces.

Proprietary APM vendors (Datadog, Dynatrace, New Relic) introduce licensing fees, vendor lock-in, and proprietary SDKs.

## Decision
I selected **OpenTelemetry (OTel)** with the **.NET Aspire Dashboard**:
1. **OpenTelemetry Instrumentation**:
   - Tracing: `OpenTelemetry.Instrumentation.AspNetCore`, `OpenTelemetry.Instrumentation.Http`, and `Npgsql.OpenTelemetry`.
   - Custom ActivitySources: `Shop.Messaging` for outbox and consumer spans; `RabbitMQ.Client.Publisher` and `RabbitMQ.Client.Subscriber` for AMQP protocol spans.
   - Metrics: OpenTelemetry runtime and custom meters (`Shop.Messaging` tracking outbox publish counts and consumer durations).
   - Structured logging: emitted via OTLP with trace context enrichment.
2. **Aspire Dashboard as OTLP Collector**:
   - Runs as a container (`mcr.microsoft.com/dotnet/aspire-dashboard:9.5`).
   - Listens on standard OTLP ports (4317 gRPC, 4318 HTTP).
   - Provides a real-time web UI on port 18888 visualizing distributed trace waterfalls, metrics histograms, and structured log streams across all microservices.

## Consequences
### Positive
- 100% vendor-neutral standard: services can switch backends (e.g. to Prometheus/Grafana or Honeycomb) without changing any application code.
- Out-of-the-box integration with .NET Aspire in local development and Docker Compose.

### Negative / Trade-offs
- The standalone Aspire dashboard is an in-memory visualization tool optimized for local development and technical demonstrations; long-term multi-month log and trace retention in enterprise production requires forwarding to Grafana Tempo/Loki or Elastic.
