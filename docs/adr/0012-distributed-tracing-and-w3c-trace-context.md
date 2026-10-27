# ADR 0012: Distributed Tracing and W3C Trace Context

## Status
Accepted

## Context
In a distributed microservices architecture where client requests transition between synchronous HTTP calls and asynchronous RabbitMQ event choreographies across outboxes and inboxes, maintaining an unbroken causal trace is critical for debugging, latency analysis, and observability.

Without explicit context propagation, the asynchronous trace breaks at the database outbox boundary: the consumer creates an orphan trace with no link to the originating HTTP checkout request.

## Decision
I implemented end-to-end distributed tracing using OpenTelemetry and the W3C Trace Context specification (`traceparent` and `tracestate`):
1. **Gateway Ingress**:
   - The Gateway checks incoming HTTP requests for an existing `traceparent` or correlation ID.
   - It stamps `X-Trace-Id` on responses and forwards the active trace to downstream services.
2. **Outbox Ingestion and Broker Publishing**:
   - When `RabbitMqEventPublisher` sends a message to RabbitMQ, it captures the ambient `Activity.Current` (representing the active span from the HTTP request or outbox processor).
   - It injects `traceparent` (`${version}-${traceId}-${spanId}-${traceFlags}`) and `tracestate` into the AMQP `BasicProperties.Headers` dictionary.
3. **Consumer Extraction**:
   - When `EventConsumer` receives a delivery from RabbitMQ, it extracts `traceparent` and `tracestate` from `ea.BasicProperties.Headers`.
   - It parses the string into an `ActivityContext` via `ActivityContext.TryParse(tpStr, null, out var parentContext)`.
   - It starts a new OpenTelemetry `Activity` with `ActivityKind.Consumer`, explicitly setting `parentContext` as the parent.
4. **ActivitySource and Exporter Wiring**:
   - Telemetry source `Shop.Messaging` is registered in `Shop.ServiceDefaults` and emitted via OpenTelemetry protocol (OTLP) to the Aspire Dashboard.

## Consequences
### Positive
- End-to-end distributed trace continuity from client HTTP POST -> Gateway -> Ordering API -> Outbox -> RabbitMQ Broker -> Catalog API Consumer -> Outbox -> RabbitMQ -> Notifications Worker.
- Visualizable in Aspire Dashboard and OpenTelemetry-compliant APMs (Jaeger, Grafana Tempo, Datadog) as a unified causal trace waterfall.

### Negative / Trade-offs
- Slight message overhead for serializing and parsing AMQP header dictionaries.
