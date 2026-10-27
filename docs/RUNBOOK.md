# Operational Runbook: MicroservicesShop

This runbook documents how to run, inspect, test, and troubleshoot the MicroservicesShop architecture in local development and production-like environments.

---

## 1. Prerequisites

- **.NET 10 SDK** (`10.0.403` or later)
- **Docker & Docker Compose** (Docker Desktop, Rancher Desktop, or Podman)
- **Git**
- Optional: `curl` and `jq` for command-line smoke testing

---

## 2. Option A: Development Run with .NET Aspire

.NET Aspire orchestrates all microservices, PostgreSQL databases, RabbitMQ, and Mailpit containers with automatic service discovery and a centralized telemetry dashboard.

### Starting the System
```bash
dotnet run --project src/Shop.AppHost
```

### Endpoints and Dashboards
- **Aspire Dashboard**: URL displayed in console output (e.g. `http://localhost:15000` or similar). Provides real-time distributed traces, metrics, console logs, and resource states.
- **Gateway (Public Entry Point)**: `http://localhost:8080`
- **Catalog Scalar UI**: `http://localhost:<catalog-port>/scalar/v1`
- **Ordering Scalar UI**: `http://localhost:<ordering-port>/scalar/v1`
- **Mailpit Web UI**: `http://localhost:8025`
- **RabbitMQ Management**: `http://localhost:15672` (Credentials: `guest` / `guest`)

---

## 3. Option B: Production-like Run with Docker Compose

Docker Compose runs the fully isolated multi-container architecture, enforcing independent PostgreSQL login roles, private networks, and health checks.

### Launching the Stack
```bash
# 1. Build and start containers in detached mode
docker compose up -d --build

# 2. Check health status of all containers
docker compose ps
```

### Verifying Service Health
All services include Docker health checks using the `--healthcheck` CLI flag:
```bash
# Verify all containers are reporting (healthy)
docker compose ps --format "table {{.Name}}\t{{.Status}}\t{{.Ports}}"
```

### Executing the Automated Smoke Test
```bash
bash scripts/smoke-test.sh
```

The script exercises:
1. Gateway health check (`GET /health`).
2. Catalog product query (`GET /api/v1/products`).
3. Order checkout (`POST /api/v1/orders` with `Idempotency-Key`).
4. Idempotency replay verification (verifies cached 202 and `Idempotent-Replayed: true` header).
5. Polling order status until `Confirmed`.
6. Mailpit email receipt verification.

### Stopping the Stack
```bash
docker compose down -v
```

---

## 4. Running Automated Tests

```bash
# 1. Run all Unit, Component, and Architecture tests (fast, no Docker required)
dotnet test Shop.slnx -c Release --filter "Category!=E2E"

# 2. Run Architecture tests only
dotnet test tests/Shop.ArchitectureTests

# 3. Run Building Blocks (Outbox/Inbox/RabbitMQ) tests
dotnet test tests/Shop.BuildingBlocks.Tests

# 4. Verify code formatting and style
dotnet format --verify-no-changes
```

---

## 5. Troubleshooting Guide

### 1. RabbitMQ Connection Failure
- **Symptom**: `RabbitMQ connection refused` in service logs.
- **Remedy**: Verify RabbitMQ container is healthy:
  ```bash
  docker compose exec rabbitmq rabbitmq-diagnostics -q ping
  ```
  Ensure port 5672 is not in use by a local RabbitMQ instance.

### 2. Database Role Permission Errors
- **Symptom**: `permission denied for table ...` on database migration.
- **Remedy**: Ensure `scripts/init-db.sql` was executed on initial database creation. If PostgreSQL data volume persisted an older schema:
  ```bash
  docker compose down -v
  docker compose up -d postgres
  ```

### 3. Inspecting Dead-Letter Queues (DLQs)
- **Symptom**: Order placed but never confirmed or rejected.
- **Remedy**: Open RabbitMQ Management UI at `http://localhost:15672` (`guest`/`guest`). Navigate to **Queues** and inspect:
  - `catalog.order-placed.dlq`
  - `ordering.stock-reserved.dlq`
  - `ordering.stock-rejected.dlq`
  - `notifications.order-confirmed.dlq`
  If messages are present, inspect their `x-death` headers to identify the root cause.

### 4. Inspecting End-to-End Traces
- Open the Aspire Dashboard (or Compose Aspire Dashboard at `http://localhost:18888`).
- Click **Traces**.
- Locate a `POST /api/v1/orders` trace. Click on it to expand the complete trace waterfall:
  1. `gateway`: `POST /api/v1/orders`
  2. `ordering-api`: `CreateOrderAsync` -> `ICatalogClient.GetProductsAsync`
  3. `ordering-api`: `Outbox.Publish` -> `RabbitMQ.BasicPublish`
  4. `catalog-api`: `OrderPlacedHandler.Consume` -> Stock reservation transaction
  5. `catalog-api`: `Outbox.Publish` -> `RabbitMQ.BasicPublish` (`catalog.stock-reserved.v1`)
  6. `ordering-api`: `StockReservedHandler.Consume` -> Order status transition to `Confirmed`
  7. `notifications-worker`: `OrderConfirmedNotificationHandler.Consume` -> SMTP Send to Mailpit
