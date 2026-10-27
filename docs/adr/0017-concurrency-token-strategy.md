# ADR 0017: Optimistic Concurrency Token Strategy

## Status
Accepted

## Context
In high-throughput microservices handling stock mutations and status updates, concurrent write conflicts must be detected to prevent lost updates.

Section 5.1 of the architecture design considered using PostgreSQL's internal `xmin` system column mapped to a `uint` property configured via `.IsRowVersion()`. However, testing the official Npgsql EF Core provider revealed a known pitfall: generating migrations with `.IsRowVersion()` scaffolds `xmin` as a column definition inside `CreateTable`, which conflicts directly with PostgreSQL's reserved system column name when executed against PostgreSQL (`column name "xmin" conflicts with a system column name`).

## Decision
I chose an **application-managed integer version property** (`Version`) configured as an EF Core concurrency token:
```csharp
public int Version { get; private set; } = 1;
```
Configured in EF Core via:
```csharp
b.Property(p => p.Version).IsConcurrencyToken().IsRequired();
```
Whenever an entity mutates (`Update`, `Restock`, `Reserve`), the domain method increments `Version++`.

When `SaveChangesAsync` generates the SQL `UPDATE` statement, EF Core includes the current version in the `WHERE` clause:
```sql
UPDATE products SET ..., version = @p1 WHERE id = @p0 AND version = @p2;
```
If another transaction modified the row concurrently, the row count is 0, and EF Core throws a `DbUpdateConcurrencyException`, triggering our consumer retry policy.

## Consequences
### Positive
- Completely database provider agnostic: functions identically in PostgreSQL, SQLite, and In-Memory test providers without provider-specific hacks.
- Zero conflicts with PostgreSQL system columns during migration execution.
- Deterministic and testable in domain unit tests without requiring a running database engine.

### Negative / Trade-offs
- Requires explicitly incrementing the `Version` counter in entity mutation methods.
