# ADR 0011: Asynchronous Messaging Technology

## Status
Accepted

## Context
A message broker is the backbone of our asynchronous choreography, outbox publishing, and dead-letter routing.
I needed to select an open-source, production-proven message broker with native support for topic-based pub/sub routing, publisher confirms, dead-letter exchanges, and high-performance .NET client libraries.

I evaluated:
1. **RabbitMQ**: AMQP 0-9-1 broker with topic exchanges, publisher confirmations, native dead-lettering, lightweight footprint, and official async .NET client (`RabbitMQ.Client` 7.x).
2. **Apache Kafka**: Log-based distributed event stream. Outstanding for event sourcing and stream processing, but introduces high operational overhead (ZooKeeper/KRaft), complex partition-based rebalancing, and lacks native selective routing keys and dead-letter exchanges without extra consumer-side topics.
3. **Azure Service Bus / AWS SQS**: Cloud-proprietary managed brokers; violates our requirement for a local, portable development and Docker Compose setup.

## Decision
I chose **RabbitMQ 4.x** (`rabbitmq:4.0-management-alpine`):
- Flexible routing via topic exchanges (`shop.events`).
- Native dead-letter exchange bindings (`shop.events.dlx`) for poison-message isolation.
- Built-in management plugin on port 15672 for visual queue monitoring.
- Official async .NET 10 client (`RabbitMQ.Client` 7.x) offering pure `ValueTask`-based APIs with cancellation token propagation.

## Consequences
### Positive
- Fully containerized and lightweight for local Aspire and Compose development.
- Native topic routing decouples publishers from consumers.
- Explicit publisher confirmations ensure zero message loss across the broker boundary.

### Negative / Trade-offs
- Unlike Kafka, messages are deleted upon consumer acknowledgement; it is not a replayable audit log (which is why domain audit state is retained in service databases).
