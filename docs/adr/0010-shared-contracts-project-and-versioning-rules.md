# ADR 0010: Shared Contracts Project and Versioning Rules

## Status
Accepted

## Context
In an asynchronous event-driven architecture, producer and consumer services must agree on message structures. Sharing a heavy common library containing domain entities or business logic introduces binary coupling across microservice boundaries, which undermines independent deployments.

## Decision
I created a lightweight `Shop.Contracts` library containing exclusively immutable C# sealed records representing integration events.

Rules governing `Shop.Contracts`:
1. **Zero External Dependencies**: The project references only standard .NET primitive types (`Guid`, `DateTimeOffset`, `decimal`, `string`, `IReadOnlyList<T>`).
2. **Immutable Sealed Records**: Every event is declared as `public sealed record ...` to ensure immutability and value equality.
3. **Explicit Contract Name Attributes**: Every event carries `[IntegrationEvent("<routing-key>")]` (for example `ordering.order-placed.v1`).
4. **Additive Versioning Policy**:
   - Within a major version (e.g. `.v1`), only non-breaking additive changes are permitted (adding optional or nullable properties with default values).
   - Properties are never renamed or removed.
   - Breaking semantic changes require introducing a new version record alongside the existing one (e.g. `OrderPlacedIntegrationEventV2` decorated with `ordering.order-placed.v2`).
5. **Tolerant Reader Pattern**: Consumers configure `System.Text.Json` to ignore unexpected JSON properties and use case-insensitive property matching, ensuring older consumers do not fail when a newer producer adds fields.
6. **Machine-Readable Documentation**: All event contracts are documented in `docs/asyncapi.yaml` and validated against JSON fixtures in `docs/contracts/events/`.

## Consequences
### Positive
- Strict compile-time typing for integration events without domain model coupling.
- Guaranteed backward and forward compatibility during phased rollouts.
- Architectural test automation ensures no event name collisions and verifies that code matches AsyncAPI documentation.

### Negative / Trade-offs
- Duplication of data structures between internal service entities (e.g. Catalog's `Product` entity) and external event contracts (e.g. `OrderLineItemContract`). This duplication is intentional: it protects internal domain logic from external contract changes.
