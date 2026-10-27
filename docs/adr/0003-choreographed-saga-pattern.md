# ADR 0003: Choreographed Saga Pattern

## Status
Accepted

## Context
Fulfilling a customer order requires multiple microservices (`ordering-api`, `catalog-api`, `notifications-worker`) to update their respective datastores consistently without distributed 2-Phase Commit (2PC) transactions, which lock database resources and introduce tight temporal coupling and single points of failure.

I needed to choose between an **Orchestrated Saga** (a central coordinator service directing commands and listening for replies) and a **Choreographed Saga** (services reacting autonomously to domain events emitted by peers).

## Decision
I chose a **Choreographed Saga with Compensation by Rejection**:
1. **Flow Description**:
   - `ordering-api` creates the order as `Pending` and publishes `ordering.order-placed.v1`.
   - `catalog-api` consumes `OrderPlaced` and executes all-or-nothing stock reservation within its own local database transaction:
     - If all requested items have sufficient stock, it reserves inventory and publishes `catalog.stock-reserved.v1`.
     - If any item is unavailable or out of stock, no stock is deducted and it publishes `catalog.stock-rejected.v1` (with rejection reason and unavailable product IDs).
   - `ordering-api` consumes the outcome:
     - On `StockReserved`: moves order to `Confirmed` and publishes `ordering.order-confirmed.v1`.
     - On `StockRejected`: moves order to `Rejected` (recording reason) and publishes `ordering.order-rejected.v1`.
   - `notifications-worker` consumes `OrderConfirmed` and `OrderRejected` to dispatch customer emails.
2. **Compensation Mechanics**:
   - Compensation is handled cleanly by the rejection transition: since stock reservation is all-or-nothing and the order begins in `Pending`, a rejection requires no rollback of inventory; it simply transitions the order to terminal state `Rejected`.

## Consequences
### Positive
- No central coordinator service or single point of failure; services remain loosely coupled.
- Clear, linear event choreography that is easy to trace and reason about.
- Scales horizontally: each participant processes events at its own pace without blocking.

### Negative / Trade-offs
- Flow logic is distributed across services rather than localized in a single orchestrator state machine.
- Requires robust event tracing (`X-Trace-Id` and W3C traceparent) to visualize end-to-end execution across services.
