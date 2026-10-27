# The Beginner's Guide to Microservices in .NET: From Monolith to Distributed Systems

If you are coming from building traditional ASP.NET Core Web APIs—whether single-project APIs or layered Clean Architecture solutions—stepping into microservices can feel disorienting at first.

In a traditional application, you have **one** `Program.cs`, **one** database connection string, **one** Entity Framework `DbContext`, and when one part of your app needs to talk to another, you simply inject a service or send an in-memory MediatR command. Everything runs in a single process, and database operations happen inside an atomic, all-or-nothing ACID transaction.

When you look at this microservices repository, almost all of those familiar assumptions look different:
- There is no single `Program.cs` that runs the whole application.
- There is no shared database; each service owns its own database that no other service is allowed to touch.
- In-memory method calls are replaced by HTTP requests and asynchronous RabbitMQ events.
- Database transactions cannot span across services.

I wrote this guide specifically to bridge that gap. If you understand C#, basic ASP.NET Core minimal APIs, and Entity Framework Core, this guide will walk you through the mental shift, explain where the system actually starts, how the pieces fit together, and answer the exact questions that come up when learning distributed systems for the first time.

---

## 1. The Architectural Evolution: Monolith vs. Clean Architecture vs. Microservices

To understand *why* this repository is structured the way it is, let us trace how .NET architectures evolve as systems grow.

### Stage 1: The Single-Project Monolith ("No Architecture")
```
[Client / Browser]
        │
        ▼ (HTTP)
┌──────────────────────────────────────────────┐
│  Single ASP.NET Core Web API Project         │
│  - Controllers / Minimal APIs                │
│  - Business Logic (Services)                 │
│  - EF Core DbContext                         │
└──────────────────────┬───────────────────────┘
                       │
                       ▼
              [Single PostgreSQL DB]
```
- **How it works:** Everything lives in one `.csproj`. Controllers directly instantiate or inject EF Core `DbContext` instances. If the checkout logic needs product details, it queries `db.Products` and `db.Orders` in the same SQL statement with a `JOIN`.
- **Strengths:** Incredibly fast to build; zero network latency between components; trivial local debugging (just press F5).
- **Weaknesses:** As the team and codebase grow, boundaries blur. Spaghetti dependencies accumulate, and a bug in the email sender can crash the entire checkout API.

---

### Stage 2: The Clean Architecture Monolith
```
[Client / Browser]
       │
       ▼ (HTTP)
┌─────────────────────────────────────────────────────────┐
│  Single Web API Host (Presentation Layer)               │
│    │                                                    │
│    ▼                                                    │
│  Application Layer (Commands, Queries, MediatR Handlers)│
│    │                                                    │
│    ▼                                                    │
│  Domain Layer (Entities, Value Objects, Domain Events)  │
│    ▲                                                    │
│    │                                                    │
│  Infrastructure Layer (EF Core, Repositories, SMTP)     │
└────────────────────────────┬────────────────────────────┘
                             │
                             ▼
                    [Single PostgreSQL DB]
```
- **How it works:** The code is cleanly separated into 4 distinct projects (`Domain`, `Application`, `Infrastructure`, `Api`). Dependencies point inward toward the domain. You dispatch an in-memory `CreateOrderCommand` via MediatR; a handler loads entities and calls `await _dbContext.SaveChangesAsync()`.
- **Strengths:** Excellent testability, high maintainability, clear business rules.
- **The Crucial Realization:** Despite having multiple projects in the Visual Studio solution, **this is still a monolith**. When you compile, it produces one executable. It runs as **one operating system process**, connects to **one database**, and shares **one memory space**.

---

### Stage 3: The Microservices Architecture (This Repository)
```
                                [Client / Frontend]
                                         │
                                         ▼ (Public HTTP on :8080)
                       ┌───────────────────────────────────┐
                       │ Shop.Gateway (YARP Reverse Proxy) │
                       └───────────────┬───────────────────┘
                                       │ (Internal Routing)
                     ┌─────────────────┴─────────────────┐
                     ▼                                   ▼
          ┌─────────────────────┐             ┌─────────────────────┐
          │   Shop.Catalog.Api  │             │   Shop.Ordering.Api │
          └──────────┬──────────┘             └──────────┬──────────┘
                     │                                   │
                     ▼                                   ▼
             [catalogdb (DB)]                    [orderingdb (DB)]
                     │                                   │
                     └─────────────┐       ┌─────────────┘
                                   ▼       ▼
                          ┌─────────────────────────┐
                          │   RabbitMQ Event Broker │
                          │     (shop.events)       │
                          └────────────┬────────────┘
                                       │
                                       ▼
                         ┌───────────────────────────┐
                         │ Shop.Notifications.Worker │
                         └─────────────┬─────────────┘
                                       │
                                       ▼
                            [notificationsdb (DB)]
```
- **How it works:** The application is split into independent, autonomous programs. Each service runs in its own process, listens on its own network port, and writes exclusively to its own database.
- **How they communicate:**
  1. **Synchronous HTTP:** Only when an immediate answer is strictly required (for example, `Shop.Ordering.Api` calls `Shop.Catalog.Api` to verify that product IDs exist and fetch their real-time prices).
  2. **Asynchronous Events:** For everything else (stock reservation, payment, confirmation emails). `Shop.Ordering.Api` saves the order, writes an event to its database, and a background publisher sends `ordering.order-placed.v1` to RabbitMQ. The other services react when they receive the event.

### Architectural Comparison Matrix

| Dimension | Single-Project Monolith | Clean Architecture Monolith | Microservices Architecture (This Repo) |
| :--- | :--- | :--- | :--- |
| **Number of OS Processes** | 1 | 1 | 4 (.NET services) + 3 (Infrastructure) |
| **Databases** | 1 shared database | 1 shared database | 3 isolated databases (`catalogdb`, `orderingdb`, `notificationsdb`) |
| **Inter-module Calls** | Direct C# method call | In-memory MediatR / interface | HTTP over network or RabbitMQ JSON events |
| **Data Consistency** | ACID transaction (`db.SaveChanges()`) | ACID transaction (`db.SaveChanges()`) | Eventual consistency via Choreographed Saga |
| **Blast Radius of a Bug** | Entire application goes down | Entire application goes down | Isolated; if notifications crash, checkout still works |
| **Entry Point** | 1 `Program.cs` | 1 `Program.cs` (in Web API project) | Developer: `Shop.AppHost`<br/>Client: `Shop.Gateway` |

---

## 2. The Mystery of the "Entry Point": Where Does It All Start?

When you first open this solution, your eyes naturally search for the startup project. But in a distributed system, you quickly realize: **there is no single program that runs the application**.

Instead, you need to understand that distributed systems have **two distinct kinds of entry points**:
1. **The Developer / Orchestrator Entry Point** (How *you* boot the entire system locally).
2. **The Client / Network Entry Point** (How *users and frontends* talk to the running system).

```
                      +---------------------------------------+
                      |      DEVELOPER ENTRY POINT            |
                      |          Shop.AppHost                 |
                      |  (Runs containers, starts services,   |
                      |    injects connection strings)        |
                      +-------------------+-------------------+
                                          | orchestrates & boots
                                          v
+-----------------------------------------------------------------------------------------+
|                                    RUNNING SYSTEM                                       |
|                                                                                         |
|   +-----------------------+                                                             |
|   |   CLIENT ENTRY POINT  |                                                             |
|   |      Shop.Gateway     |                                                             |
|   |  (:8080 Public Door)  |                                                             |
|   +-----------+-----------+                                                             |
|               |                                                                         |
|               | routes requests internally                                              |
|               v                                                                         |
|       +---------------+       +---------------+       +-----------------------------+   |
|       |  Catalog API  | <---> |  Ordering API | ----> | RabbitMQ -> Notifications   |   |
|       +---------------+       +---------------+       +-----------------------------+   |
+-----------------------------------------------------------------------------------------+
```

### Entry Point 1: The Developer / Orchestrator Entry Point (`Shop.AppHost`)
In a monolith, you right-click the Web API project, choose "Set as Startup Project", and press F5.
In this microservice solution, the startup project is **`Shop.AppHost`** (`src/Shop.AppHost/Program.cs`).

`Shop.AppHost` is built with **.NET Aspire**. It is not an API and it serves no customer traffic. Instead, think of it as the **conductor of the orchestra**:
- It starts a PostgreSQL container and creates three databases (`catalogdb`, `orderingdb`, `notificationsdb`).
- It starts a RabbitMQ message broker container (`rabbitmq:4.0-management`).
- It starts a Mailpit email testing server container (`axllent/mailpit`).
- It launches each of our .NET projects (`Shop.Catalog.Api`, two replicas of `Shop.Ordering.Api`, `Shop.Notifications.Worker`, and `Shop.Gateway`).
- It automatically configures and injects the database connection strings and RabbitMQ credentials into each service's environment variables.
- It launches the **Aspire Dashboard**, giving you a real-time web portal showing all running processes, logs, metrics, and distributed OpenTelemetry traces.

> **Key Takeaway:** To run the entire ecosystem on your machine during development, you run `Shop.AppHost`.

---

### Entry Point 2: The Client / Network Entry Point (`Shop.Gateway`)
Once all the services are running, where do clients (such as your React frontend, mobile app, or Postman) send their HTTP requests?

They talk **only** to **`Shop.Gateway`** (`src/Shop.Gateway/Program.cs`), which listens on port **8080**.

`Shop.Gateway` is built with **YARP (Yet Another Reverse Proxy)**. In a microservices architecture, you never expose your internal microservices directly to the public internet. If you did:
- The frontend would have to know 5 different IP addresses and ports.
- You couldn't easily rename or refactor internal services without breaking clients.
- Every individual microservice would have to duplicate CORS rules, rate limiting, and public authentication.

Instead, the Gateway acts as the single front door:
- When a client sends `GET http://localhost:8080/api/v1/products`, YARP checks its routing table and forwards the request internally to `Shop.Catalog.Api`.
- When a client sends `POST http://localhost:8080/api/v1/orders`, YARP forwards the request internally to `Shop.Ordering.Api`.
- If someone tries to call `GET http://localhost:8080/internal/v1/products/lookup`, YARP blocks it with HTTP 404 because `/internal/` routes are private and strictly reserved for service-to-service communication.

---

## 3. Aspire vs. Docker Compose: Which Should You Use?

In this repository, you will notice both a `src/Shop.AppHost/` project and a root `compose.yaml` file. Why do both exist, and when should you choose one over the other?

| Feature / Scenario | .NET Aspire (`Shop.AppHost`) | Docker Compose (`compose.yaml`) |
| :--- | :--- | :--- |
| **Primary Audience** | **You, the developer**, writing code on your machine. | **CI/CD pipelines, staging environments, production testing.** |
| **How Code Runs** | Runs your C# projects directly on your host machine via .NET SDK (`dotnet run`). Infrastructure (Postgres, RabbitMQ) runs in Docker containers. | Builds multi-stage Linux container images for **every** project and runs everything inside isolated Docker containers. |
| **Hot Reload & Edit** | **Yes.** You can edit C# code, save, and see changes instantly without rebuilding containers. | **No.** Every single code change requires rebuilding Docker images (`docker compose build`). |
| **Debugging** | **Seamless.** Set breakpoints in Visual Studio or VS Code across any service and step through execution. | Requires attaching remote debuggers inside running containers. |
| **Observability** | Built-in Aspire Dashboard showing live console logs, structured traces, and health status out of the box. | Uses container logs (`docker logs`) and exports traces to a standalone dashboard container. |
| **Command to Run** | `dotnet run --project src/Shop.AppHost` | `docker compose up -d --build` |

### The Golden Rule
- **Use .NET Aspire for your daily development work.** It is fast, allows live debugging, supports hot reload, and requires zero Docker builds for your C# code.
- **Use Docker Compose when you want to verify container builds**, run automated smoke tests (`bash scripts/smoke-test.sh`), or deploy to an environment where the .NET SDK is not installed.

---

## 4. What Is the Very First Thing to Do Right Now? (Step-by-Step)

Let us walk through running this entire distributed system from scratch on your machine.

### Prerequisites
Before running, ensure you have:
1. **.NET 10 SDK** installed (`dotnet --version`).
2. **Docker Desktop** installed and running (Aspire needs Docker to spin up PostgreSQL, RabbitMQ, and Mailpit containers).

---

### Step 1: Start Docker Desktop
Make sure the Docker daemon is active on your machine. You can verify this by opening a terminal and running:
```bash
docker info
```
If this prints system details without error, your container runtime is ready.

---

### Step 2: Boot the System via Aspire
In your terminal, navigate to the root of the repository and run:
```bash
dotnet run --project src/Shop.AppHost
```

---

### Step 3: What Happens Under the Hood
When you execute that command, watch the terminal output. Aspire performs several steps:
1. **Pulls and starts container images:**
   - PostgreSQL (`postgres:17-alpine`) on a dynamic port.
   - RabbitMQ (`rabbitmq:4.0-management-alpine`) on dynamic ports (AMQP + Management UI).
   - Mailpit (`axllent/mailpit:latest`) on ports for SMTP (1025) and Web UI (8025).
2. **Launches the microservice projects:**
   - Starts `Shop.Catalog.Api`.
   - Starts two instances of `Shop.Ordering.Api` (demonstrating round-robin load balancing).
   - Starts `Shop.Notifications.Worker`.
   - Starts `Shop.Gateway` on port **8080**.
3. **Applies Database Migrations:**
   - Each API applies its own Entity Framework Core migrations automatically on startup (`CatalogDbContext`, `OrderingDbContext`, `NotificationsDbContext`).
   - Catalog seeds initial sample products into `catalogdb`.
4. **Launches the Aspire Dashboard:**
   - Aspire prints a dashboard URL in the terminal (typically `http://localhost:18888` or with a login token).

Click or open the dashboard URL in your browser.

---

### Step 4: Explore the Aspire Dashboard
The Aspire Dashboard is your command center. Spend a moment looking at the tabs:
- **Resources:** You will see a list of all 4 .NET projects and 3 containers. All should display a green `Running` status badge. You can see the internal URLs, process IDs, and health checks for each service.
- **Console Logs:** Click on any service (for example, `ordering-api-1` or `notifications-worker`) to inspect live console logs streamed in real time.
- **Structured Logs:** Search and filter log events across the entire solution by log level or message content.
- **Traces:** This is where the magic of OpenTelemetry distributed tracing lives. When an HTTP request or RabbitMQ message flows through the system, a trace records every single step across all services.

---

### Step 5: Make Your First Request (Browse Catalog)
Open a new terminal or your favorite HTTP client (you can also use the ready-made `.http` files inside the `requests/` folder).

Query the catalog through the Gateway:
```bash
curl -i http://localhost:8080/api/v1/products
```

**What you will see:**
The Gateway routes the request to `Shop.Catalog.Api`, which queries `catalogdb` and returns a JSON array of seeded products:
```json
[
  {
    "id": "11111111-1111-1111-1111-111111111111",
    "name": "Mechanical Keyboard",
    "description": "Tactile wireless mechanical keyboard",
    "price": 129.99,
    "currency": "USD",
    "availableStock": 50
  },
  {
    "id": "22222222-2222-2222-2222-222222222222",
    "name": "Ergonomic Mouse",
    "description": "Precision wireless mouse with thumb rest",
    "price": 79.99,
    "currency": "USD",
    "availableStock": 100
  }
]
```

Notice the response headers:
- `X-Trace-Id`: A unique W3C trace ID injected by the Gateway.
- `X-RateLimit-Limit`: Rate limiting headers managed at the gateway layer.

---

### Step 6: Place an Order (Experience the Asynchronous Flow)
Now, let us place an order. In microservices, order placement is not a simple database insert—it triggers an asynchronous distributed workflow.

Run this POST request:
```bash
curl -i -X POST http://localhost:8080/api/v1/orders \
  -H "Content-Type: application/json" \
  -H "Idempotency-Key: 99999999-9999-9999-9999-999999999999" \
  -d '{
    "customerId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
    "customerEmail": "customer@example.com",
    "items": [
      {
        "productId": "11111111-1111-1111-1111-111111111111",
        "quantity": 2
      }
    ]
  }'
```

**Look closely at the HTTP status code returned:**
```http
HTTP/1.1 202 Accepted
Location: /api/v1/orders/a1b2c3d4-...
```

**Why `202 Accepted` instead of `201 Created`?**
Because in a distributed system, reserving stock and confirming payment happen asynchronously! The Ordering API validates the request, records the order in state `Pending`, and returns `202 Accepted` immediately. It gives the client a `Location` header so the client can poll or query the order status.

---

### Step 7: Watch the Distributed Saga in the Traces Tab
Now switch back to your browser and open the **Traces** tab in the Aspire Dashboard.
Click on the trace corresponding to your `POST /api/v1/orders` call.

You will see an end-to-end visualization showing:
1. `Shop.Gateway` receives the POST request.
2. `Shop.Gateway` forwards to `Shop.Ordering.Api`.
3. `Shop.Ordering.Api` performs a synchronous HTTP call to `Shop.Catalog.Api` to validate prices.
4. `Shop.Ordering.Api` commits the order and enqueues an Outbox record.
5. The `OutboxProcessor` publishes the message to RabbitMQ.
6. `Shop.Catalog.Api` consumes the message from RabbitMQ, reserves stock, and publishes `catalog.stock-reserved.v1`.
7. `Shop.Ordering.Api` consumes `catalog.stock-reserved.v1` and updates the order status to `Confirmed`.
8. `Shop.Notifications.Worker` consumes `ordering.order-confirmed.v1` and dispatches an email via Mailpit.

Finally, open Mailpit at `http://localhost:8025` in your browser. You will see the actual confirmation email delivered to `customer@example.com`!

---

## 5. Frequently Asked Questions (The Junior Developer's Survival Guide)

Here are the most common questions, doubts, and points of confusion that arise when transitioning from Clean Architecture to microservices.

---

### Q1: Why can't `Shop.Ordering.Api` just query `catalogdb` directly with EF Core? Wouldn't that be much faster and simpler?
**Answer:**
This is the single most important rule in microservices: **Database-per-Service**.

If `Shop.Ordering.Api` were allowed to connect to `catalogdb` and run queries against its tables:
1. **Schema Coupling:** If the Catalog team renames a column from `price` to `unit_price`, the Ordering API silently breaks.
2. **Locking & Performance Bottlenecks:** A long-running analytics query in Ordering could place row or table locks in Catalog's database, preventing customers from browsing products.
3. **Bypassing Business Rules:** Catalog enforces invariants (such as stock validation and price formatting). Directly modifying Catalog's tables from another service bypasses that logic.

By enforcing private databases, `Shop.Catalog.Api` completely encapsulates its data. The only way any other service can interact with product data is through Catalog's public API or its published events.

---

### Q2: What on earth is an `Idempotency-Key` and why is it mandatory when placing an order?
**Answer:**
In a distributed system, **networks are unreliable**.

Imagine this real-world scenario:
1. Your client sends a POST request to place an order for \$200.
2. The server receives the request, charges your card, creates the order in the database, and prepares to return `HTTP 200 OK`.
3. Just before the response reaches your phone, your cellular connection drops.
4. Your phone sees a network timeout error. It doesn't know whether the order succeeded or failed.
5. The user hits the "Submit Order" button again.

Without idempotency, the server would treat the second click as a brand new order and charge the customer twice (\$400 total)!

With an **Idempotency-Key**:
- The client generates a unique UUID (for example, `99999999-9999-9999-9999-999999999999`) and sends it in the `Idempotency-Key` header.
- The server checks its database: *Have I seen this key before?*
- If **no**, it processes the order and caches the response.
- If **yes**, it immediately returns the saved response without re-executing the checkout logic.

The customer can hit the button 10 times, but the order is only placed once.

---

### Q3: What is the "Transactional Outbox" pattern, and why can't I just call `channel.BasicPublishAsync()` inside my endpoint?
**Answer:**
This addresses the famous **Dual-Write Problem**.

Consider what happens if you write this naive code inside your controller:
```csharp
// NAIVE APPROACH - DO NOT DO THIS
await db.Orders.AddAsync(order);
await db.SaveChangesAsync(); // Step 1: Save to Postgres

await rabbitChannel.BasicPublishAsync(orderPlacedEvent); // Step 2: Publish to broker
```

What happens if the power cuts out or your application container crashes between Step 1 and Step 2?
The order is permanently stored in your database, but the event was **never published** to RabbitMQ. Catalog never reserves stock, the customer never gets an email, and the order is stuck in limbo forever.

What if you swap the order?
```csharp
await rabbitChannel.BasicPublishAsync(orderPlacedEvent); // Step 1: Publish to broker
await db.SaveChangesAsync(); // Step 2: Save to Postgres fails!
```
If `SaveChangesAsync()` throws a database exception, the message is already out in RabbitMQ! Catalog reserves stock for an order that doesn't even exist in the database!

**The Outbox Solution:**
Instead of talking to RabbitMQ directly in the endpoint, the application inserts the event into an `outbox_messages` table **inside the exact same local PostgreSQL database transaction** as the order:
```csharp
using var transaction = await db.Database.BeginTransactionAsync();

db.Orders.Add(order);
db.OutboxMessages.Add(new OutboxMessage { ... });

await db.SaveChangesAsync(); // BOTH succeed or BOTH fail atomically!
await transaction.CommitAsync();
```
A reliable background worker (`OutboxProcessor`) periodically reads unhandled messages from `outbox_messages` and publishes them to RabbitMQ. If RabbitMQ is down, the worker simply retries later. Zero messages are ever lost.

---

### Q4: What is the "Transactional Inbox" pattern?
**Answer:**
Message brokers like RabbitMQ guarantee **at-least-once delivery**, not *exactly-once delivery*.

If a consumer processes a message successfully and attempts to acknowledge it (`basic.ack`), but a network hiccup kills the connection before RabbitMQ receives the acknowledgment, RabbitMQ will redeliver that same message to another worker.

The **Transactional Inbox** ensures that duplicate deliveries are completely harmless:
- When a consumer receives an event, it checks an `inbox_messages` table using the unique `MessageId`.
- If the `MessageId` is already present, the consumer knows it has already processed this event. It immediately acknowledges the message and skips re-executing the business logic.

---

### Q5: What is a "Choreographed Saga"? Why not use an Orchestrator?
**Answer:**
When a business process spans multiple services (Ordering -> Catalog -> Notifications), you have two choices for coordination:

1. **Orchestration:** A central coordinator service acts as the boss. It calls Catalog: "Reserve stock." If Catalog replies "OK", it calls Payment: "Charge card." If payment fails, it calls Catalog: "Cancel stock."
2. **Choreography (Used Here):** Services react to events autonomously, like dancers responding to music.
   - Ordering publishes: `OrderPlaced`.
   - Catalog hears `OrderPlaced`, reserves stock, and publishes `StockReserved` (or `StockRejected`).
   - Ordering hears `StockReserved` and transitions the order to `Confirmed`.
   - Notifications hears `OrderConfirmed` and sends an email.

In smaller systems, choreography avoids introducing a single point of failure and keeps individual services decoupled. Each service only knows what events it cares about.

---

### Q6: Why did we build Outbox and Inbox by hand instead of using MassTransit or Wolverine?
**Answer:**
In enterprise production applications, using established messaging frameworks like MassTransit or Wolverine is common and often recommended.

However, in this reference implementation, I deliberately wrote the Outbox, Inbox, and RabbitMQ publisher/consumers by hand using the official `RabbitMQ.Client` 7.x async API.

**Why?**
When you use a high-level framework, the underlying mechanics are hidden behind magic abstractions (`IConsumer<T>`, auto-inbox, auto-outbox). When something goes wrong in production—such as deadlocks during high concurrency or unacknowledged message storms—developers who only know the framework struggle to diagnose the root cause.

By building it from scratch, you can inspect:
- Exactly how PostgreSQL's `FOR UPDATE SKIP LOCKED` prevents multiple worker replicas from competing for the same outbox rows.
- How RabbitMQ Publisher Confirms work under the hood.
- How dead-letter exchanges (`shop.events.dlx`) capture poison messages after retries are exhausted.

Once you understand the low-level mechanics demonstrated here, high-level frameworks will make total sense.

---

### Q7: Why did you use Feature Folders instead of 4-Project Clean Architecture inside each microservice?
**Answer:**
In a monolith, Clean Architecture is valuable because you need strong physical boundaries to stop developers from tangling UI logic with database queries across hundreds of features.

However, when you slice a system into microservices, **the microservice itself is the boundary**.

`Shop.Catalog.Api` only has a few endpoints: browse products, lookup products, and reserve stock. If you split a 3-endpoint microservice into 4 separate `.csproj` projects (`Catalog.Domain`, `Catalog.Application`, `Catalog.Infrastructure`, `Catalog.Api`), you end up with 16 projects across the solution! You create massive ceremony, endless DTO mapping, and navigating the codebase becomes painful.

Instead, I used **Vertical Slice / Feature Folders** inside each service:
```
src/Shop.Catalog.Api/
├── Products/
│   ├── Product.cs               (Domain Entity)
│   ├── ProductEndpoints.cs      (Minimal API routes)
│   └── ProductDto.cs            (Request/Response contracts)
├── Data/
│   ├── CatalogDbContext.cs      (EF Core)
│   └── Migrations/
└── Messaging/
    └── OrderPlacedConsumer.cs   (RabbitMQ Background Consumer)
```
Everything related to the Catalog feature is located in one clean, cohesive place.

---

### Q8: What happens if `Shop.Catalog.Api` goes down when a customer places an order?
**Answer:**
`Shop.Ordering.Api` uses **resilient HTTP** (powered by `Microsoft.Extensions.Http.Resilience` and Polly) to call Catalog.

1. **Transient Network Blip:** The client automatically retries 3 times with exponential backoff and jitter. If Catalog recovers within a second, the call succeeds seamlessly.
2. **Catalog Is Down Hard:** If Catalog continues to fail, the **Circuit Breaker** trips open. Ordering stops hammering the dead Catalog service and immediately returns:
   ```http
   HTTP/503 Service Unavailable
   Retry-After: 15
   ```
   ```json
   {
     "type": "https://datatracker.ietf.org/doc/html/rfc9110#section-15.6.4",
     "title": "Catalog Service Unavailable",
     "status": 503,
     "detail": "The catalog service is currently unavailable. Please retry later."
   }
   ```
Notice what did **not** happen:
- The system did not hang indefinitely.
- It did not create a broken, half-initialized order in the database.
- It failed cleanly and defensively.

---

## 6. The Project Directory Tour

Here is a map of the solution and where each concept is implemented in the code:

```
MicroservicesShop/
├── src/
│   ├── Shop.AppHost/                 <-- [START HERE] The .NET Aspire orchestrator
│   │   └── Program.cs                <-- Wires up Postgres, RabbitMQ, and starts all APIs
│   │
│   ├── Shop.Gateway/                 <-- YARP Reverse Proxy (:8080)
│   │   └── appsettings.json          <-- Routing rules (/api/v1/products, /api/v1/orders)
│   │
│   ├── Shop.Catalog.Api/             <-- Owns catalogdb & product inventory
│   │   ├── Products/                 <-- Product endpoints and domain logic
│   │   └── Messaging/                <-- Consumes OrderPlaced, reserves stock
│   │
│   ├── Shop.Ordering.Api/            <-- Owns orderingdb & order lifecycle
│   │   ├── Orders/                   <-- POST /api/v1/orders, idempotency handling
│   │   ├── Clients/                  <-- Resilient HTTP CatalogClient
│   │   └── Messaging/                <-- Consumes StockReserved / StockRejected
│   │
│   ├── Shop.Notifications.Worker/    <-- Owns notificationsdb & email dispatch
│   │   └── Messaging/                <-- Consumes OrderConfirmed, sends Mailpit SMTP email
│   │
│   ├── Shop.BuildingBlocks.Messaging/<-- Shared resilient messaging engine
│   │   ├── Outbox/                   <-- OutboxProcessor with SKIP LOCKED batching
│   │   ├── Inbox/                    <-- InboxConsumer with idempotency deduplication
│   │   └── RabbitMq/                 <-- Channel pooling, publisher confirms, retry & DLQ
│   │
│   ├── Shop.Contracts/               <-- Pure C# record event contracts (No dependencies)
│   └── Shop.ServiceDefaults/         <-- OpenTelemetry, health checks, and service discovery
│
├── tests/
│   ├── Shop.ArchitectureTests/       <-- Ensures no microservice references another
│   ├── Shop.Catalog.Tests/           <-- Catalog unit and slice tests
│   ├── Shop.Ordering.Tests/          <-- Ordering unit, client resilience, and idempotency tests
│   ├── Shop.BuildingBlocks.Tests/    <-- Outbox, inbox, and DLQ behavior tests
│   ├── Shop.Notifications.Tests/     <-- Deduplication and email rendering tests
│   └── Shop.EndToEndTests/           <-- Aspire AppHost integration tests
│
├── docs/                             <-- Architecture Decision Records (ADRs) & documentation
├── requests/                         <-- Ready-to-run .http requests for VS Code & Visual Studio
├── compose.yaml                      <-- Production-like Docker Compose file
└── Dockerfile                        <-- Production multi-stage Dockerfile
```

---

## 7. Recommended Learning Path

To get the most out of this codebase, I recommend following this path:

1. **Run the system locally:** Follow the steps in [Section 4](#4-what-is-the-very-first-thing-to-do-right-now-step-by-step) to boot `Shop.AppHost`, explore the Aspire Dashboard, and send requests from `requests/Catalog.http` and `requests/Ordering.http`.
2. **Inspect the Gateway:** Open `src/Shop.Gateway/appsettings.json` and see how YARP maps incoming paths to internal service clusters.
3. **Trace the Order Flow:** Open `src/Shop.Ordering.Api/Orders/OrderEndpoints.cs` to see how an order is saved and enqueued into the Outbox within a single database transaction.
4. **Follow the Saga:** Trace how `Shop.Catalog.Api/Messaging/OrderPlacedConsumer.cs` receives the event and atomically decrements inventory using optimistic concurrency tokens.
5. **Read the Architecture Decision Records (ADRs):** Open `docs/adr/` to understand why specific distributed design choices were made (such as `0001` for Database-per-Service, `0005` for hand-written messaging, and `0006` for `SKIP LOCKED`).
6. **Dive into the Interview Notes:** Read `docs/INTERVIEW-NOTES.md` for deep-dive explanations of concurrency, idempotency, and failure modes.
