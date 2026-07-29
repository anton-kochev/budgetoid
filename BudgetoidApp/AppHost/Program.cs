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
    // Admin credentials for password auth. Declaring them as explicit parameters (instead of
    // letting the parameterless overload auto-generate anonymous ones) keeps them addressable: they
    // map to the same manifest params azd already provisions (AZURE_POSTGRES_USERNAME / _PASSWORD),
    // so the stored admin password is unchanged. Their only consumers are the server provisioning
    // below and the Key Vault secrets the generated Bicep writes for the operator; nothing built
    // from them is handed to the running application.
    IResourceBuilder<ParameterResource> postgresUsername = builder.AddParameter("postgres-username");
    IResourceBuilder<ParameterResource> postgresPassword = builder.AddParameter("postgres-password", secret: true);

    // Password for the least-privilege role the application serves requests as (budgetoid_app,
    // created by Infrastructure's grants script). It is a parameter of its own rather than a
    // derivative of the admin password because the two identities are meant to be independently
    // rotatable — that independence is the whole point of the split.
    IResourceBuilder<ParameterResource> postgresAppPassword = builder.AddParameter("postgres-app-password", secret: true);

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
        .WithPasswordAuthentication(postgresUsername, postgresPassword);

    // The name is the database name, which the connection string below spells as Database=budgetoid.
    // WaitFor is a run-mode orchestration primitive, so it's omitted for this provisioned resource.
    // The database resource and the Key Vault secret the operator migrates with are both emitted by
    // AddDatabase into the server's Bicep module, so nothing here depends on the api referencing it.
    postgres.AddDatabase("budgetoid");

    // The api deliberately does not WithReference that database: for this resource a reference
    // injects the *admin* identity into the container. It writes ConnectionStrings__budgetoid
    // (which the explicit override below would win over) but also BUDGETOID_URI, BUDGETOID_USERNAME
    // and BUDGETOID_PASSWORD, all built from the administrator login — which would put the server
    // admin password in a request-serving container's environment under a second set of names, with
    // no consumer. Dropping the reference is what actually keeps admin credentials out; the
    // connection string below is the only one the application reads, and it needs no reference to
    // exist.
    //
    // ROOT FIX for docs/decisions/0001 gotcha #1: WithReference resolves the connection string to a
    // Key Vault secret reference ({postgres-kv.secrets.connectionstrings--budgetoid}), which azd
    // mis-renders into a BARE Container App secret (host only, no credentials) — so the app couldn't
    // reach Postgres until a post-deploy step rewrote the secret. Instead, build the full connection
    // string here from the application-role password parameter + the server host output and inject
    // it directly, so azd emits a complete, self-contained secret and no repair step is needed.
    // SslMode stays in Api/Program.cs as the single source of TLS config; this host/user/password/db
    // shape matches what that code expects.
    //
    // Two identities exist; one of them reaches the container. "budgetoid_app" is the least-privilege
    // role, and the connection string below is the only one injected — the API serves every request
    // on it. The server admin can run DDL, and a request-serving process has no use for DDL rights,
    // so it is never given them: the deploy-time migration and role-provisioning steps run in the
    // deploy pipeline, as the Tools/DbProvision tool, which reads the admin credentials out of Key
    // Vault with the pipeline identity (DEPLOYMENT.md). The API asks for
    // ConnectionStrings:budgetoid-admin only inside its Development startup check, and in the
    // deployed app that key has no value to find.
    api.WithEnvironment("ConnectionStrings__budgetoid", ReferenceExpression.Create(
        $"Host={postgres.GetOutput("hostName")};Username=budgetoid_app;Password={postgresAppPassword.Resource};Database=budgetoid"));

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
