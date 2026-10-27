# ADR 0006: SKIP LOCKED Outbox Claiming

## Status
Accepted

## Context
When microservices are scaled horizontally across multiple instances or pods (such as `ordering-api` running with 2 replicas), each replica runs an instance of `OutboxProcessor`.

If multiple workers execute a naive `SELECT * FROM outbox_messages WHERE processed_on IS NULL ORDER BY occurred_on LIMIT 20`, race conditions emerge:
- Both workers claim the exact same batch of messages.
- Both publish duplicates to RabbitMQ, multiplying broker traffic and consumer load.
- If one worker locks rows with standard `FOR UPDATE`, competing workers block and wait, killing parallelism and causing database lock contention.

## Decision
I utilized PostgreSQL's native `FOR UPDATE SKIP LOCKED` clause in the outbox processor:

```sql
SELECT id AS "Value"
FROM outbox_messages
WHERE processed_on IS NULL
ORDER BY occurred_on
LIMIT {0}
FOR UPDATE SKIP LOCKED
```

### Mechanics
1. A worker opens a transaction and executes the query with `FOR UPDATE SKIP LOCKED`.
2. PostgreSQL locks the matching rows for that worker and immediately returns them.
3. If a second worker concurrently runs the query, PostgreSQL skips the rows locked by the first worker and locks the next available batch without waiting or blocking.
4. The worker publishes the messages via RabbitMQ, updates `processed_on = DateTimeOffset.UtcNow`, and commits the transaction, releasing the row locks.
5. In non-Postgres environments (such as In-Memory EF Core databases used in fast unit tests), the processor seamlessly falls back to standard LINQ querying.

## Consequences
### Positive
- Completely eliminates lock contention and worker stalls across horizontally scaled replicas.
- Zero duplicate claiming among concurrent outbox processors.
- Highly efficient utilization of PostgreSQL row-level locking.

### Negative / Trade-offs
- `SKIP LOCKED` is a relational database-specific feature (PostgreSQL, Oracle, MySQL 8.0+). Changing datastores would require verifying support for lock-skipping semantics.
