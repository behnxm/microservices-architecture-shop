# ADR 0005: Hand-Written Outbox and Inbox Patterns

## Status
Accepted

## Context
Third-party messaging frameworks (MassTransit, NServiceBus, Wolverine, Rebus) provide out-of-the-box implementations of the outbox and inbox patterns. However, they introduce heavy abstractions, complex configuration conventions, licensing considerations, and proprietary abstractions that obscure the fundamental distributed-systems mechanisms.

To create an educational, transparent, interview-ready reference architecture without forbidden frameworks or paid dependencies, I evaluated writing the transactional outbox and inbox directly on the official `RabbitMQ.Client` 7.x async driver and EF Core.

## Decision
I implemented both patterns by hand inside `Shop.BuildingBlocks.Messaging`:
1. **Transactional Outbox**:
   - `OutboxMessage` entity with `outbox_messages` table in each service's database: `(id, message_type, routing_key, payload, occurred_on, processed_on, retry_count, last_error)`.
   - Domain operations add messages to `IOutbox` within the local database transaction: the business mutation (e.g. creating an `Order`) and the outbox message commit atomically or roll back together.
   - An `OutboxProcessor<TDbContext>` background service periodically claims batches of unprocessed messages, publishes them to RabbitMQ with publisher confirmations (`PublisherConfirmationsEnabled = true`), and marks them processed upon broker ack.
2. **Transactional Inbox**:
   - `InboxMessage` entity with `inbox_messages` table: `(message_id, handler_name, received_on, processed_on)`.
   - Before a consumer handler processes a message, `IInbox.TryStartProcessingAsync` attempts to insert the record. If the record already exists (or triggers a primary key conflict), the message is recognized as a duplicate and acknowledged immediately without reprocessing.
   - Upon successful handler completion, `MarkProcessedAsync` stamps the `processed_on` timestamp.

## Consequences
### Positive
- Zero third-party framework overhead, black-box reflection, or licensing constraints.
- Total control over SQL statements, locking semantics, batch sizes, retry intervals, and OpenTelemetry instrumentation.
- High pedagogical and interview value: demonstrates exact mastery of the underlying distributed patterns.

### Negative / Trade-offs
- Maintenance responsibility rests on the repository rather than a maintained open-source framework community.
- Features like scheduled message delivery or complex sagas must be written explicitly if needed in the future.
