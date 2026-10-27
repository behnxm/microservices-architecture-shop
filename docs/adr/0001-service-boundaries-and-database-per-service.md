# ADR 0001: Service Boundaries and Database Per Service

## Status
Accepted

## Context
When designing the MicroservicesShop architecture, I needed to define clean service boundaries that reflect distinct business capabilities and enforce independent evolvability and deployability.

A common pitfall in distributed systems is sharing databases across microservices. While sharing a database eases ad-hoc queries, it creates invisible coupling: schema changes by one service risk breaking another service, transactions span multiple domains causing lock contention and distributed transaction overhead, and deployment autonomy is lost.

## Decision
I established strict service boundaries aligned with domain subdomains:
1. **Catalog Service (`catalog-api`)**: Owns the product catalog, SKU master records, pricing, and available stock inventory. Its datastore is `catalogdb`.
2. **Ordering Service (`ordering-api`)**: Owns customer orders, checkout state transitions, price snapshots, and customer order history. Its datastore is `orderingdb`.
3. **Notifications Service (`notifications-worker`)**: Owns outbound customer communications and delivery attempts. Its datastore is `notificationsdb`.
4. **Gateway (`gateway`)**: The sole public reverse proxy; owns no persistent state.

I strictly enforce the **Database-per-Service** pattern:
- Each service has its own dedicated PostgreSQL database and its own connection string.
- No service may read or write another service's database directly.
- In Docker Compose, each service connects using a distinct PostgreSQL login role with grants restricted strictly to its own database.
- Any cross-service data dependency (e.g. Ordering needing product prices during checkout) must be fulfilled via explicitly documented APIs or integration events.

## Consequences
### Positive
- Schema migrations in Catalog or Ordering can be executed completely independently without risk of cross-service side effects.
- No cross-service database locks or distributed deadlocks.
- Failure of one database instance does not automatically bring down unrelated services.
- Microservices can be scaled and tuned according to their specific read/write access patterns.

### Negative / Trade-offs
- Queries that span products and orders cannot use simple SQL joins; they require either an API call (e.g., synchronous lookup during checkout) or eventual consistency through asynchronous event projection.
- Cross-service consistency requires choreographing sagas rather than atomic ACID database transactions.
