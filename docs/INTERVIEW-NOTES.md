# Microservices Architecture: Technical Interview Reference & Architecture Notes

These notes provide deep, code-backed answers to 15 core architectural questions regarding the design, distributed mechanics, and trade-offs in this repository.

---

### 1. Why Database-per-Service instead of a shared database? How do you enforce it?
**Concept & Rationale:**
A shared database between services creates tight invisible coupling. Schema modifications in one service risk breaking another, queries span multiple domain boundaries bypassing business logic invariants, and database connection pools and table locks become shared bottlenecks.
In this architecture, I strictly enforce **Database-per-Service**:
- `catalog-api` owns `catalogdb` (`src/Shop.Catalog.Api/Data/CatalogDbContext.cs`).
- `ordering-api` owns `orderingdb` (`src/Shop.Ordering.Api/Data/OrderingDbContext.cs`).
- `notifications-worker` owns `notificationsdb` (`src/Shop.Notifications.Worker/Data/NotificationsDbContext.cs`).

**Enforcement:**
1. **Physical Isolation in Docker Compose**: `scripts/init-db.sql` creates 3 separate databases and 3 distinct login roles (`catalog_user`, `ordering_user`, `notifications_user`). Each service receives credentials strictly limited to its own database.
2. **Compile-Time Architecture Tests**: `tests/Shop.ArchitectureTests/ArchitectureTests.cs` scans all project references to ensure no microservice project ever references another microservice project.

---

### 2. When should services communicate synchronously over HTTP vs. asynchronously over messaging?
**Concept & Rationale:**
Synchronous HTTP creates temporal coupling: both caller and callee must be available at the exact same instant, and availability compounds multiplicatively ($A = A_1 \times A_2$).
I established a strict rule (detailed in `docs/adr/0002-synchronous-versus-asynchronous-communication.md`):
1. **Synchronous HTTP** is permitted **only** when an immediate answer is required to proceed:
   - When placing an order, `ordering-api` synchronously calls `catalog-api` (`GET /internal/v1/products/lookup?ids=...`) to validate product existence, obtain the official price snapshot, and verify currency (`src/Shop.Ordering.Api/Clients/CatalogClient.cs`).
   - If Catalog is down, the order is rejected immediately with HTTP 503 (`Ordering.CatalogUnavailable`). No dirty pending order is created.
2. **Asynchronous Events** are used for **all** subsequent operations:
   - Stock reservation, order confirmation/rejection, and customer notification happen asynchronously via RabbitMQ topic events.
   - The client receives HTTP 202 Accepted with a `Location: /api/v1/orders/{id}` header and polls for the terminal state.

---

### 3. How does the Transactional Outbox pattern guarantee zero message loss without 2PC?
**Concept & Rationale:**
In a naive dual-write setup, writing to the database and publishing to RabbitMQ are two distinct operations. If the application crashes after saving the order but before publishing to the broker, the event is permanently lost.
The **Transactional Outbox** solves this by writing the message into an `outbox_messages` table within the **exact same local database transaction** as the business entities (`src/Shop.Ordering.Api/Orders/OrderEndpoints.cs`):
```csharp
db.Orders.Add(order);
db.IdempotencyRecords.Add(idempotencyRecord);
outbox.Enqueue(orderPlacedEvent);
await db.SaveChangesAsync(cancellationToken);
```
Either all three are committed to PostgreSQL, or none are.
A separate background service, `OutboxProcessor<TDbContext>` (`src/Shop.BuildingBlocks.Messaging/Outbox/OutboxProcessor.cs`), queries unprocessed messages and publishes them with publisher confirmations (`PublisherConfirmationsEnabled = true`). The message is marked `processed_on` only after RabbitMQ confirms persistence.

---

### 4. Why use PostgreSQL's `FOR UPDATE SKIP LOCKED` for claiming outbox batches?
**Concept & Rationale:**
When microservices scale horizontally (e.g. `ordering-api` running with 2 replicas in Compose/Aspire), multiple outbox processors poll the `outbox_messages` table concurrently.
- Without locking: both workers query the same batch and publish duplicate messages to RabbitMQ.
- With standard `FOR UPDATE`: worker 2 blocks and waits for worker 1 to finish, creating lock contention and serializing background workers.
- With `FOR UPDATE SKIP LOCKED` (`src/Shop.BuildingBlocks.Messaging/Outbox/OutboxProcessor.cs`):
  Worker 1 locks rows 1-20. Worker 2 skips those locked rows and immediately locks rows 21-40 without blocking.
This delivers concurrent, conflict-free outbox batch claiming across horizontally scaled pods.

---

### 5. How is idempotency implemented on the HTTP checkout endpoint?
**Concept & Rationale:**
Network partitions or dropped client connections can cause clients to retry `POST /api/v1/orders`. Without idempotency, duplicate orders would be created.
Following the Stripe model (`src/Shop.Ordering.Api/Idempotency/IdempotencyRecord.cs` and `docs/adr/0009-idempotency-strategy.md`):
1. The client must supply an `Idempotency-Key` header.
2. `ordering-api` computes a SHA-256 hash of the canonical request payload.
3. If the key exists with the **matching hash**: it replays the cached 202 response body with an `Idempotent-Replayed: true` header without re-running domain logic.
4. If the key exists with a **different payload**: it rejects the request with HTTP 422 (`Ordering.IdempotencyPayloadMismatch`).
5. If new: it inserts the order and the idempotency record atomically. Race conditions are resolved by the primary key constraint on `idempotency_records.key`.

---

### 6. How does the Transactional Inbox pattern prevent duplicate message processing?
**Concept & Rationale:**
Message brokers guarantee **at-least-once delivery**, meaning network retries or consumer restarts will inevitably redeliver events that were already processed.
The **Transactional Inbox** (`src/Shop.BuildingBlocks.Messaging/Inbox/EfInbox.cs`) maintains an `inbox_messages` table with composite key `(message_id, handler_name)`:
1. When a consumer receives an event, it calls `IInbox.TryStartProcessingAsync(messageId, handlerName)`.
2. It attempts to insert the record. If the record already exists (or triggers a unique constraint collision), the event is recognized as a duplicate.
3. The consumer acknowledges the message (`BasicAckAsync`) and skips handler execution.
4. If new, the handler executes, commits its database changes, and calls `MarkProcessedAsync`.

---

### 7. Why use Natural Key Deduplication in Notifications instead of just the transport `MessageId`?
**Concept & Rationale:**
Customer emails are irreversible real-world side-effects; an email sent via SMTP cannot be rolled back.
An inbox relying on transport `MessageId` only protects against identical network redeliveries. If an upstream service re-emits a new event during a manual replay or saga retry, the event has a *new* `MessageId`. An inbox would treat it as a new event and email the customer again.
In `Shop.Notifications.Worker/Consumers/OrderConfirmedNotificationHandler.cs` (and `docs/adr/0016-notifications-natural-key-vs-inbox.md`), I enforce **Natural Key Deduplication**:
The `notifications` table has primary key `(order_id, type)`. Before sending, the worker checks if `(eventMessage.OrderId, "OrderConfirmed")` exists. Regardless of how many times events are replayed or how many different message IDs arrive, the customer receives exactly one confirmation email per order.

---

### 8. How does the Choreographed Saga work, and why choose it over Orchestration?
**Concept & Rationale:**
A choreographed saga coordinates distributed transactions without a central controller:
1. `ordering-api`: Creates order as `Pending` -> emits `OrderPlaced`.
2. `catalog-api`: Consumes `OrderPlaced`. Executes all-or-nothing stock deduction. Emits `StockReserved` if successful; emits `StockRejected` if unavailable.
3. `ordering-api`: Consumes result. Moves order to `Confirmed` on `StockReserved` (emits `OrderConfirmed`); moves order to `Rejected` on `StockRejected` (emits `OrderRejected`).
4. `notifications-worker`: Consumes terminal order events and sends customer emails.

**Why Choreography?**
For this business domain, the sequence is linear and self-contained. Choreography avoids a central orchestrator dependency, reduces latency, and allows each service to remain completely autonomous. Compensation is built naturally into the state machine: if stock is unavailable, `catalog-api` rejects, and `ordering-api` moves `Pending` -> `Rejected` without needing to revert inventory.

---

### 9. How is stock reservation handled to prevent overselling?
**Concept & Rationale:**
In `src/Shop.Catalog.Api/Consumers/OrderPlacedHandler.cs`:
1. All requested items for an order are queried within a single database transaction.
2. **All-or-Nothing Rule**: If *any* product has `AvailableStock < requestedQuantity`, no stock is deducted for *any* product in the order.
3. Each product entity contains an application-managed concurrency token `Version` (`src/Shop.Catalog.Api/Products/Product.cs`). If concurrent orders attempt to reserve the same inventory item simultaneously, EF Core detects the concurrency token change, preventing race conditions.
4. If successful, `AvailableStock` is decremented and `StockReserved` is queued to the outbox.

---

### 10. How does distributed tracing propagate across outboxes and brokers?
**Concept & Rationale:**
Standard HTTP tracing breaks when an operation transitions to an asynchronous outbox table and message broker.
In `src/Shop.BuildingBlocks.Messaging/RabbitMq/RabbitMqEventPublisher.cs` and `src/Shop.BuildingBlocks.Messaging/Consumers/EventConsumer.cs`:
1. **Producer**: When the outbox processor publishes to RabbitMQ, it captures the ambient `Activity.Current` and serializes W3C headers (`traceparent` and `tracestate`) into `BasicProperties.Headers`.
2. **Consumer**: When `EventConsumer` receives the message, it extracts `traceparent`, parses the `ActivityContext`, and initializes a consumer span (`Shop.Messaging`) linked as a child of the original trace.
This creates an unbroken trace waterfall spanning Gateway -> Ordering API -> Outbox -> RabbitMQ -> Catalog Consumer -> Notifications Worker.

---

### 11. What is the RabbitMQ exchange and dead-letter topology?
**Concept & Rationale:**
Documented in `src/Shop.BuildingBlocks.Messaging/RabbitMq/RabbitMqTopology.cs`:
- **Topic Exchange (`shop.events`)**: Durable topic exchange. Routing keys follow `<domain>.<event>.v<version>`.
- **Dead-Letter Exchange (`shop.events.dlx`)**: Durable topic exchange for poison messages.
- **Queue Configuration**: Each consumer queue declares `x-dead-letter-exchange: shop.events.dlx` and `x-dead-letter-routing-key: <queue>.dlq`.
- **Dead-Letter Queue (`<queue>.dlq`)**: Bound to `shop.events.dlx`.
When a consumer encounters a malformed payload or exhausts retries, it issues `BasicRejectAsync(requeue: false)`. RabbitMQ automatically routes the message into the DLQ without blocking subsequent messages.

---

### 12. Why hand-roll the messaging building block rather than using MassTransit or NServiceBus?
**Concept & Rationale:**
Third-party messaging frameworks introduce heavy magic, proprietary licenses, and opinionated abstractions that hide the distributed-systems primitives.
By implementing `Shop.BuildingBlocks.Messaging` using the official `RabbitMQ.Client` 7.x async driver and EF Core:
- Every line of code demonstrates explicit understanding of publisher confirms, mandatory routing, channel recovery, prefetch QoS, dead-letter routing, `SKIP LOCKED` SQL, and inbox deduplication.
- Zero paid or restrictive third-party dependencies.
- Perfect transparency for technical interviews and architectural evaluation.

---

### 13. How does the YARP Gateway protect internal microservices?
**Concept & Rationale:**
In `src/Shop.Gateway/appsettings.json`:
1. **Surface Minimization**: Only public routes (`/api/v1/products/**` and `/api/v1/orders/**`) are routed downstream.
2. **Internal Route Blocking**: Internal endpoints (such as `GET /internal/v1/products/lookup`) have no route in YARP and return HTTP 404 from the gateway. Verified by architecture test `Gateway_Blocks_Access_To_Internal_Routes`.
3. **Defense-in-Depth**: YARP enforces rate limiting (10 requests/10s per IP), request timeouts (30s), security headers (HSTS, nosniff, frame-ancestors 'none'), and stamps correlation headers (`X-Trace-Id`).

---

### 14. How are resilience policies tuned for internal synchronous HTTP calls?
**Concept & Rationale:**
Configured in `src/Shop.Ordering.Api/Program.cs` via `Microsoft.Extensions.Http.Resilience`:
- **Attempt Timeout (2s)**: Internal cluster HTTP calls must respond quickly; if stalled, the attempt is cancelled to allow retry.
- **Total Request Timeout (10s)**: Bounds the total lifespan across all retries.
- **Exponential Backoff with Jitter (3 attempts, 200ms base)**: Retries only transient HTTP 5xx or network errors. Jitter decorrelates concurrent retries to prevent retry storms.
- **Circuit Breaker (50% failure ratio over 10s, minimum throughput 5, 15s break duration)**: If Catalog is failing repeatedly, the circuit opens for 15 seconds, failing fast with HTTP 503 and `Retry-After: 15` rather than overwhelming the struggling service.

---

### 15. What is the consistency model, and how do clients handle eventual consistency?
**Concept & Rationale:**
The system uses an **eventually consistent** model:
1. When placing an order, product validity and pricing are verified synchronously.
2. The order is committed as `Pending` and returned with HTTP 202 Accepted and a `Location: /api/v1/orders/{id}` header.
3. The client does not block on inventory reservation or email sending. Instead, the client polls `GET /api/v1/orders/{id}` until the `status` changes from `Pending` to `Confirmed` or `Rejected`.
This decouples client response latency from broker queues and downstream service availability.
