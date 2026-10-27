# ADR 0002: Synchronous vs Asynchronous Communication

## Status
Accepted

## Context
A recurring dilemma in microservices design is deciding when services should communicate synchronously (e.g. over HTTP REST) versus asynchronously (via message broker events).

If every operation is synchronous, the system suffers from cascading latency, temporal coupling (all downstream services must be available at the exact same moment), and reduced availability: availability of a call chain of $N$ services with 99.9% availability each degrades to $0.999^N$.
Conversely, if everything is asynchronous, simple user operations like submitting an order become opaque, requiring clients to poll or maintain WebSockets before knowing if the product exists or if the price is valid.

## Decision
I established a strict rule governing communication styles:
1. **Synchronous HTTP** is permitted **only** when an answer is immediately required to proceed and cannot be derived locally:
   - When placing an order, `ordering-api` makes a single synchronous HTTP call to `catalog-api` (`GET /internal/v1/products/lookup?ids=...`) to validate product existence, obtain the official price snapshot, and verify currency.
   - If `catalog-api` is unavailable, the order is rejected immediately with HTTP 503 (`Ordering.CatalogUnavailable`) and a `Retry-After: 15` header. No pending order is persisted.
   - The synchronous HTTP call is made resilient with retry, timeout, and circuit breaker policies (`Microsoft.Extensions.Http.Resilience`).
2. **Asynchronous Events** are used for **all** subsequent operations and side effects:
   - Stock reservation is asynchronous: `ordering-api` persists the order as `Pending` and publishes `OrderPlaced` to RabbitMQ via the Transactional Outbox.
   - Stock confirmation/rejection, customer notifications, and status transitions occur asynchronously via choreographed events (`StockReserved`, `StockRejected`, `OrderConfirmed`, `OrderRejected`).
   - The client receives HTTP 202 Accepted with a `Location: /api/v1/orders/{id}` header and polls the status.

## Consequences
### Positive
- Client gets fast, authoritative feedback on product validity and price without waiting for stock locks, broker round-trips, or downstream workers.
- The checkout pipeline does not hold distributed locks or block on inventory reservation.
- Temporary spikes in traffic are smoothed by RabbitMQ queues rather than exhausting HTTP thread pools.

### Negative / Trade-offs
- Clients must understand eventual consistency and poll `GET /api/v1/orders/{id}` to verify final confirmation or rejection.
- Requires robust outbox, inbox, and choreographed saga infrastructure to guarantee message delivery and handle compensation.
