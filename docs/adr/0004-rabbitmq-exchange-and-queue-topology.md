# ADR 0004: RabbitMQ Exchange and Queue Topology

## Status
Accepted

## Context
A microservices messaging backbone requires a resilient, predictable topology that isolates subscriber queues, prevents noisy-neighbor interference, handles message routing flexibility, and safely routes unprocessable poison messages without blocking queue progression.

## Decision
I designed a topic-based RabbitMQ topology:
1. **Primary Exchange (`shop.events`)**:
   - Type: `topic`
   - Durable: `true`
   - Auto-delete: `false`
   - Routing keys follow the pattern `<producer-domain>.<event-name>.v<version>` (e.g. `ordering.order-placed.v1`).
2. **Dead-Letter Exchange (`shop.events.dlx`)**:
   - Type: `topic`
   - Durable: `true`
   - Auto-delete: `false`
3. **Queue Naming and Binding**:
   - Each consumer gets its own dedicated durable queue named `<consumer-service>.<event-short-name>` (e.g. `catalog.order-placed`, `ordering.stock-reserved`, `ordering.stock-rejected`, `notifications.order-events`).
   - Each primary queue is bound to `shop.events` with its respective routing key.
   - Each primary queue is configured with dead-letter arguments:
     - `x-dead-letter-exchange`: `shop.events.dlx`
     - `x-dead-letter-routing-key`: `<queue-name>.dlq`
4. **Dead-Letter Queues (DLQs)**:
   - For every primary queue, a corresponding dead-letter queue `<queue-name>.dlq` is declared and bound to `shop.events.dlx` with routing key `<queue-name>.dlq`.
   - When a consumer exhausts in-process retries or encounters a non-recoverable error (e.g. deserialization failure), it executes `BasicRejectAsync(deliveryTag, requeue: false)`, causing RabbitMQ to automatically route the rejected message into the DLQ.

## Consequences
### Positive
- Publishers only know about the `shop.events` exchange and versioned routing keys, with no knowledge of consumer queues.
- Adding a new consumer service (e.g. Analytics) only requires binding a new queue to `shop.events` without altering publishers.
- Poison messages never block main queue consumption; they are isolated in DLQs for offline inspection and manual re-driving.

### Negative / Trade-offs
- Dead-letter queues require operational alerting and tooling to notify engineers of unhandled processing failures.
