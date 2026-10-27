using Projects;

var builder = DistributedApplication.CreateBuilder(args);

// 1. Shared Infrastructure resources
var postgres = builder.AddPostgres("postgres")
    .WithImage("postgres", "17.4-alpine");

var catalogDb = postgres.AddDatabase("catalogdb");
var orderingDb = postgres.AddDatabase("orderingdb");
var notificationsDb = postgres.AddDatabase("notificationsdb");

var rabbitmq = builder.AddRabbitMQ("rabbitmq")
    .WithManagementPlugin()
    .WithImage("rabbitmq", "4.0-management-alpine");

var mailpit = builder.AddContainer("mailpit", "axllent/mailpit", "v1.22")
    .WithHttpEndpoint(targetPort: 8025, port: 8025, name: "ui")
    .WithEndpoint(targetPort: 1025, port: 1025, name: "smtp");

// 2. Microservices
var catalogApi = builder.AddProject<Shop_Catalog_Api>("catalog-api")
    .WithReference(catalogDb)
    .WithReference(rabbitmq)
    .WaitFor(catalogDb)
    .WaitFor(rabbitmq)
    .WithEnvironment("Database:Migrate", "true")
    .WithEnvironment("Database:Seed", "true")
    .WithEnvironment("HealthChecks:Expose", "true")
    .WithHttpHealthCheck("/health");

var orderingApi = builder.AddProject<Shop_Ordering_Api>("ordering-api")
    .WithReference(orderingDb)
    .WithReference(catalogApi)
    .WithReference(rabbitmq)
    .WaitFor(orderingDb)
    .WaitFor(catalogApi)
    .WaitFor(rabbitmq)
    .WithReplicas(2)
    .WithEnvironment("Database:Migrate", "true")
    .WithEnvironment("HealthChecks:Expose", "true")
    .WithHttpHealthCheck("/health");

// 3. Reverse Proxy Gateway (the single external entry point)
builder.AddProject<Shop_Gateway>("gateway")
    .WithReference(catalogApi)
    .WithReference(orderingApi)
    .WaitFor(catalogApi)
    .WaitFor(orderingApi)
    .WithHttpEndpoint(port: 8080, name: "public-gateway")
    .WithHttpHealthCheck("/health");

builder.Build().Run();
