using Aspire.Hosting.Azure;
using Projects;

IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder(args);

IResourceBuilder<AzurePostgresFlexibleServerResource> postgres = builder
    .AddAzurePostgresFlexibleServer("postgres")
    .RunAsContainer(container => container
        .WithDataVolume()
        .WithLifetime(ContainerLifetime.Persistent)
        .WithPgAdmin());

IResourceBuilder<AzurePostgresFlexibleServerDatabaseResource> db = postgres.AddDatabase("budgetoid");

builder
    .AddProject<Api>("api")
    .WithReference(db)
    .WaitFor(db)
    .WithExternalHttpEndpoints();

builder.Build().Run();
