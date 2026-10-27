# ADR 0009: Idempotency Strategy

## Status
Accepted

## Context
In distributed systems, network partitions or timeouts frequently lead clients to retry HTTP POST requests. If a client submits an order, the server processes it successfully, but the network drops the HTTP 202 Accepted response, the client will retry the checkout request. Without an idempotency mechanism, duplicate orders will be created, charging the customer twice and doubling stock reservations.

Distributed locking (e.g. via Redis) introduces an extra external dependency, single points of failure, and operational complexity.

## Decision
I designed a database-driven idempotency strategy following the Stripe model:
1. **Header Requirement**: Clients must send an `Idempotency-Key` HTTP header with every mutating checkout request (`POST /api/v1/orders`). If omitted, the request is rejected immediately with HTTP 400 (`Ordering.MissingIdempotencyKey`).
2. **Payload Fingerprinting**: `ordering-api` computes a SHA-256 hash of the canonical request payload.
3. **Database Persistence**: An `idempotency_records` table in `orderingdb` stores:
   - `key`: Primary key (varchar(64)).
   - `request_hash`: Hex-encoded SHA-256 string (char(64)).
   - `order_id`: UUID reference to the created order.
   - `status_code`: HTTP status code returned (202).
   - `response_body`: Serialized JSON payload returned to the client.
   - `created_at`: Timestamp.
4. **Processing Semantics**:
   - On request arrival, check `idempotency_records` by key.
   - If key exists and `request_hash` matches: replay the cached response body and status code, appending the header `Idempotent-Replayed: true`.
   - If key exists but `request_hash` differs: reject with HTTP 422 Unprocessable Entity (`Ordering.IdempotencyPayloadMismatch`), preventing key reuse for different orders.
   - If key does not exist: execute catalog check, insert `orders` entity, `order_lines`, and `idempotency_records` record within a single atomic database transaction.
   - Concurrent duplicate race conditions are resolved by the database primary key constraint: if a second concurrent worker attempts to insert the same key, it catches `DbUpdateException` and queries the existing winner.

## Consequences
### Positive
- Client retries are completely safe and side-effect free.
- The idempotency record is committed in the exact same local database transaction as the order itself, guaranteeing atomic all-or-nothing execution without distributed locks.
- Payload hashing prevents dangerous misuse where a client generates one static key and attempts to reuse it for multiple distinct orders.

### Negative / Trade-offs
- Requires clients to generate and manage unique keys (typically UUIDv4 or UUIDv7).
- In high-throughput systems, old idempotency records need periodic archival or expiration cleanup (e.g. records older than 24-48 hours).
