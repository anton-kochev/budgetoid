using Aspire.Hosting.Azure;
using Azure.Provisioning;
using Azure.Provisioning.PostgreSql;
using Projects;

IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder(args);

IResourceBuilder<ProjectResource> api = builder.AddProject<Api>("api");

if (builder.ExecutionContext.IsPublishMode)
{
    // In publish mode azd provisions a real Azure Database for PostgreSQL Flexible Server (no
    // RunAsContainer here). No WithPasswordAuthentication call: that is what leaves Aspire's
    // default in place, which is Microsoft Entra authentication only
    // (activeDirectoryAuth Enabled, passwordAuth Disabled in the generated Bicep). Per
    // docs/decisions/0007 there is no longer a database password anywhere in production — not for
    // the application role, not for a server administrator, and none in Key Vault for an operator.
    //
    // ADR 0001 rejected this path once, because Aspire emitted a connection string of bare
    // Host;Database and the app connected as the container's OS user without SSL. What changed is
    // the client side: the API now uses Aspire's *Azure* Npgsql integration, which attaches an
    // Entra token provider, and the connection string below names the role explicitly instead of
    // letting Npgsql default the username. The server-side gap was never the problem.
    //
    // Also note what is deliberately absent: the API does not WithReference this server (see the
    // connection string below), and for this resource type a reference is what registers the
    // referencing compute resource's managed identity as a full Entra *administrator* of the
    // server — azure_pg_admin, CREATEROLE, CREATEDB. A request-serving process must never hold
    // that, and the role it does connect as is bound to its identity by the deploy-time
    // provisioning tool instead.
    IResourceBuilder<ParameterResource> pipelinePrincipalId = builder.AddParameter("pipeline-principal-id");
    IResourceBuilder<ParameterResource> pipelinePrincipalName = builder.AddParameter("pipeline-principal-name");

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

            // The deploy pipeline's service principal, registered as a Microsoft Entra
            // administrator of the server. Somebody has to be able to migrate the schema and bind
            // the application role to its identity, and with Entra-only auth that somebody must be
            // an Entra admin: only an Entra administrator can create or label Entra principals in
            // the database, and membership in azure_pg_admin alone does not confer it.
            //
            // Azure matches an access token to a database role by the principal's object id rather
            // than by name, which is why the resource *name* here is the object id and the display
            // name is only a property. Both arrive as azd parameters
            // (infra.parameters.pipeline_principal_id / _name) so nothing about the pipeline's
            // identity is checked into the repo.
            // AsProvisioningParameter rather than a bare ProvisioningParameter: it registers the
            // parameter in the app model as well as in this module, so azd learns it has to supply a
            // value (from AZURE_PIPELINE_PRINCIPAL_ID / _NAME). A raw Bicep parameter would appear in
            // the module and in nothing else, leaving azd to deploy it unset.
            infrastructure.Add(new PostgreSqlFlexibleServerActiveDirectoryAdministrator("postgres_pipeline_admin")
            {
                Parent = flexibleServer,
                Name = pipelinePrincipalId.AsProvisioningParameter(infrastructure),
                PrincipalType = PostgreSqlFlexibleServerPrincipalType.ServicePrincipal,
                PrincipalName = pipelinePrincipalName.AsProvisioningParameter(infrastructure),
            });
        })
        // Without this, Aspire emits a postgres-roles Bicep module whose administrators resource
        // takes its name from a principalId parameter that nothing fills — no compute resource
        // references this server, deliberately (see the connection string below) — and ARM refuses a
        // resource with an empty name, so azd provision fails outright. WithPasswordAuthentication
        // used to clear these annotations as a side effect, which is why the module only appeared
        // once that call went away. The pipeline administrator this deployment does want is the
        // explicit one above, not a default role assignment.
        .ClearDefaultRoleAssignments();

    // The name is the database name, which the connection string below spells as Database=budgetoid.
    // WaitFor is a run-mode orchestration primitive, so it's omitted for this provisioned resource.
    postgres.AddDatabase("budgetoid");

    // The api deliberately does not WithReference that database. Two independent reasons, and both
    // still hold now that there is no password to leak:
    //
    // First, for this resource type a reference registers the referencing compute resource's
    // managed identity as a full Microsoft Entra *administrator* of the server — azure_pg_admin,
    // CREATEROLE, CREATEDB. The whole grant matrix and every row-level security policy would be
    // decoration: an administrator is not subject to them. The API must reach the database as
    // budgetoid_app and as nothing else.
    //
    // Second, Aspire's own connection string for an Entra server is bare Host= with no username,
    // which is ADR 0001's original failure: Npgsql then defaults the username to the container's OS
    // user ("app" in the noble-chiseled image) and Azure rejects it. Naming the role explicitly here
    // is the fix, and it is required rather than cosmetic — Aspire's Azure Npgsql integration can
    // only infer a username from token claims, which never yields a custom role name like
    // budgetoid_app.
    //
    // There is no Password=, and its absence is load-bearing: that is precisely what makes the
    // client integration attach its Entra token provider instead of standing aside. The credential
    // the API presents is a short-lived access token fetched from its own managed identity, which
    // the deploy-time provisioning tool has bound to this role by object id. Nothing secret is
    // injected into the container, so nothing about the database can leak from its environment.
    //
    // SslMode stays in Api/Program.cs as the single source of TLS config; this host/user/database
    // shape matches what that code expects. ConnectionStrings:budgetoid-admin is asked for only
    // inside the Development startup check, and in the deployed app that key has no value to find.
    api.WithEnvironment("ConnectionStrings__budgetoid", ReferenceExpression.Create(
        $"Host={postgres.GetOutput("hostName")};Username=budgetoid_app;Database=budgetoid"));

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

    // The container account is the superuser, so the connection WithReference wires up is the
    // *admin* one — name it accordingly. The API uses it only under its Development startup check,
    // to migrate the schema and then provision the least-privilege role; requests are served on the
    // connection below.
    api.WithReference(db, connectionName: "budgetoid-admin").WaitFor(db);

    // The application connects as budgetoid_app, which that same startup step creates — or, on a
    // persistent data volume, re-passwords — with exactly this value on every boot, so the role and
    // the connection string cannot drift apart. A literal is adequate here because the container is
    // reachable only from this machine and its superuser credentials are already a fixed local
    // default; it must stay inside the alphabet DatabaseProvisioning allows for role passwords.
    //
    // The identity is swapped by appending to the container's own connection string rather than by
    // composing a new one: the container's port is assigned at run time, so the host half can only
    // come from the resource. ADO.NET connection strings take the last occurrence of a duplicated
    // key, so the appended pair wins over the superuser credentials in front of them.
    const string devAppRolePassword = "budgetoid-app-dev";
    api.WithEnvironment("ConnectionStrings__budgetoid", ReferenceExpression.Create(
        $"{db.Resource.ConnectionStringExpression};Username=budgetoid_app;Password={devAppRolePassword}"));
}

api.WithExternalHttpEndpoints();

builder.Build().Run();
