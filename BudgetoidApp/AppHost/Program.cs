using Aspire.Hosting.Azure;
using Azure.Provisioning.PostgreSql;
using Projects;

IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder(args);

IResourceBuilder<ProjectResource> api = builder.AddProject<Api>("api");

if (builder.ExecutionContext.IsPublishMode)
{
    // In publish mode azd provisions a real Azure Database for PostgreSQL Flexible Server (no
    // RunAsContainer here). Aspire's default auth model is Microsoft Entra / managed identity, but
    // that path produced an incomplete connection string end-to-end (the app connected as OS user
    // "app" without SSL and was rejected). Per docs/decisions/0001 we switch to password
    // authentication: the parameterless WithPasswordAuthentication() auto-generates the admin
    // username and a random password stored as a secure parameter, which azd surfaces as a
    // Container App secret and Aspire wires into a complete connection string.
    IResourceBuilder<AzurePostgresFlexibleServerResource> postgres = builder
        .AddAzurePostgresFlexibleServer("postgres")
        .ConfigureInfrastructure(infrastructure =>
        {
            // Pin the cheapest tier that still gets automated backups: Burstable Standard_B1ms,
            // 32 GB storage, 7-day backup retention, geo-redundant backup off. These map straight
            // to the generated server Bicep.
            PostgreSqlFlexibleServer flexibleServer = infrastructure
                .GetProvisionableResources()
                .OfType<PostgreSqlFlexibleServer>()
                .Single();

            flexibleServer.Sku = new PostgreSqlFlexibleServerSku
            {
                Name = "Standard_B1ms",
                Tier = PostgreSqlFlexibleServerSkuTier.Burstable,
            };
            flexibleServer.Storage = new PostgreSqlFlexibleServerStorage
            {
                StorageSizeInGB = 32,
            };
            flexibleServer.Backup = new PostgreSqlFlexibleServerBackupProperties
            {
                BackupRetentionDays = 7,
                GeoRedundantBackup = PostgreSqlFlexibleServerGeoRedundantBackupEnum.Disabled,
            };
        })
        .WithPasswordAuthentication();

    // Keep the connection name "budgetoid"; the API reads GetConnectionString("budgetoid").
    // WaitFor is a run-mode orchestration primitive, so it's omitted for this provisioned resource.
    IResourceBuilder<AzurePostgresFlexibleServerDatabaseResource> db = postgres.AddDatabase("budgetoid");

    api.WithReference(db);

    // Deploy-time azd parameters (non-secret) baked into the generated Bicep; azd provision prompts
    // for them. ASP.NET binds the double-underscore/index env-var names to configuration keys, so
    // these back the Google client id and the CORS allowed-origins list the API requires at boot.
    IResourceBuilder<ParameterResource> googleClientId = builder.AddParameter("google-client-id");
    IResourceBuilder<ParameterResource> frontendOrigin = builder.AddParameter("frontend-origin");

    api
        .WithEnvironment("Authentication__Google__ClientId", googleClientId)
        .WithEnvironment("Cors__AllowedOrigins__0", frontendOrigin);
}
else
{
    // Local dev: run Azure Postgres as a persistent container with a data volume and pgAdmin.
    IResourceBuilder<AzurePostgresFlexibleServerResource> postgres = builder
        .AddAzurePostgresFlexibleServer("postgres")
        .RunAsContainer(container => container
            .WithDataVolume()
            .WithLifetime(ContainerLifetime.Persistent)
            .WithPgAdmin());

    IResourceBuilder<AzurePostgresFlexibleServerDatabaseResource> db = postgres.AddDatabase("budgetoid");

    api.WithReference(db).WaitFor(db);
}

api.WithExternalHttpEndpoints();

builder.Build().Run();
