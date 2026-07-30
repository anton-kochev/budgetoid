using Aspire.Hosting.Azure;
using Aspire.Hosting.Azure.AppContainers;
using Azure.Core;
using Azure.Provisioning;
using Azure.Provisioning.AppContainers;
using Azure.Provisioning.Expressions;
using Azure.Provisioning.Network;
using Azure.Provisioning.PostgreSql;
using Azure.Provisioning.PrivateDns;
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

    // The network the API and the database share privately. It exists so that the PostgreSQL server
    // can carry no standing firewall rule at all: the API reaches it over a private endpoint, and the
    // public endpoint is opened only for the couple of minutes the deploy pipeline needs to migrate.
    // Aspire has no API for any of this in 13.4.6 — only internal annotations — so it is written
    // directly against Azure.Provisioning. See docs/decisions/0009.
    //
    // A standalone module rather than a callback on either consumer: the Container Apps environment
    // needs the delegated subnet and the database needs the private-endpoint subnet and the DNS zone,
    // so putting the network inside either one would make the other depend on it for no reason.
    var network = builder.AddAzureInfrastructure("network", infrastructure =>
    {
        VirtualNetwork virtualNetwork = new("virtualNetwork")
        {
            AddressSpace = new VirtualNetworkAddressSpace { AddressPrefixes = { "10.0.0.0/16" } },
            Subnets =
            {
                // Delegation is declared here and nowhere else. The annotation further down tells the
                // environment which subnet to use; it does not delegate the subnet, and an
                // undelegated one is rejected at creation. /27 is the documented minimum for a
                // workload-profiles environment.
                new SubnetResource("containerAppsSubnet")
                {
                    Name = "container-apps",
                    AddressPrefix = "10.0.0.0/27",
                    Delegations =
                    {
                        new ServiceDelegation { Name = "container-apps", ServiceName = "Microsoft.App/environments" },
                    },
                },
                // Private endpoints take no delegation; one address is enough, and /28 is the
                // smallest subnet Azure accepts.
                new SubnetResource("privateEndpointSubnet")
                {
                    Name = "private-endpoints",
                    AddressPrefix = "10.0.1.0/28",
                },
            },
        };
        infrastructure.Add(virtualNetwork);

        // This zone is what makes the private endpoint usable without changing a single connection
        // string. The server's public FQDN keeps resolving publicly everywhere else, and resolves to
        // the private address inside this network, because the zone group below publishes the record
        // here and the link makes this network consult the zone. Private DNS zones are global.
        PrivateDnsZone postgresPrivateDnsZone = new("postgresPrivateDnsZone")
        {
            Name = "privatelink.postgres.database.azure.com",
            Location = new AzureLocation("global"),
        };
        infrastructure.Add(postgresPrivateDnsZone);
        infrastructure.Add(new VirtualNetworkLink("postgresPrivateDnsZoneLink")
        {
            Parent = postgresPrivateDnsZone,
            Name = "virtual-network",
            Location = new AzureLocation("global"),
            VirtualNetworkId = virtualNetwork.Id,
            RegistrationEnabled = false,
        });

        // Subnet ids are composed from the network's id rather than read back off the inline subnet
        // resources: the subnets are declared inside the virtual network, so they have no independent
        // resource of their own to reference.
        infrastructure.Add(new ProvisioningOutput("containerAppsSubnetId", typeof(string))
        {
            Value = BicepFunction.Interpolate($"{virtualNetwork.Id}/subnets/container-apps"),
        });
        infrastructure.Add(new ProvisioningOutput("privateEndpointSubnetId", typeof(string))
        {
            Value = BicepFunction.Interpolate($"{virtualNetwork.Id}/subnets/private-endpoints"),
        });
        infrastructure.Add(new ProvisioningOutput("postgresPrivateDnsZoneId", typeof(string))
        {
            Value = postgresPrivateDnsZone.Id,
        });
    });

    // The AppHost owns the Container Apps environment, where azd used to generate it. That is not a
    // preference: a virtual network can only be attached to an environment as it is created, and an
    // environment azd generates never has one. Taking ownership is what makes the subnet below
    // reachable, and it is why this change recreates the environment and moves the API to a new
    // hostname.
    //
    // WithAzdResourceNaming keeps azd's own naming for the registry, workspace and identity, so the
    // container registry keeps the images it already holds rather than starting empty.
    IResourceBuilder<AzureContainerAppEnvironmentResource> containerAppEnvironment = builder
        .AddAzureContainerAppEnvironment("cae")
        .WithAzdResourceNaming();

    // The three values the network module publishes. They are constructed rather than read through
    // GetOutput because that extension is declared on IResourceBuilder<AzureBicepResource>, and
    // IResourceBuilder is invariant, so a builder of a derived resource does not satisfy it.
    BicepOutputReference containerAppsSubnetId = new("containerAppsSubnetId", network.Resource);
    BicepOutputReference privateEndpointSubnetId = new("privateEndpointSubnetId", network.Resource);
    BicepOutputReference postgresPrivateDnsZoneId = new("postgresPrivateDnsZoneId", network.Resource);

    // Aspire reads this annotation when it generates the environment and puts the subnet in
    // properties.vnetConfiguration.infrastructureSubnetId. The annotation type is public; nothing
    // public attaches it, so it is attached by hand.
    containerAppEnvironment.WithAnnotation(new DelegatedSubnetAnnotation(
        ReferenceExpression.Create($"{containerAppsSubnetId}")));

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

            // Aspire adds an "allow all Azure IPs" firewall rule of its own, and offers no way to
            // decline it. That rule is precisely the one ADR 0006 rejected: it admits every Azure
            // tenant's egress, permanently, in place of the two-minute single-address window the
            // deploy pipeline opens. Removing the provisionable is the only lever available, so the
            // rules are matched by name — a count would silently start deleting the wrong thing if
            // Aspire ever emits a different set.
            //
            // Deleting it from the template does not delete it from a server that already has it:
            // ARM deployments are incremental and never remove resources. The live rule is deleted
            // once, by hand, and DEPLOYMENT.md carries the verification that it is gone.
            foreach (PostgreSqlFlexibleServerFirewallRule firewallRule in infrastructure
                         .GetProvisionableResources()
                         .OfType<PostgreSqlFlexibleServerFirewallRule>()
                         .ToList())
            {
                infrastructure.Remove(firewallRule);
            }

            // Public access stays on, and that is a decision rather than an oversight. The deploy
            // pipeline runs on a GitHub-hosted runner with no route into the network above, so the
            // transient firewall window is its only way in — turning public access off would take
            // migrations with it. What changes is that nothing stands open between deploys.
            flexibleServer.Network = new PostgreSqlFlexibleServerNetwork
            {
                PublicNetworkAccess = PostgreSqlFlexibleServerPublicNetworkAccessState.Enabled,
            };

            // The private path itself. GroupIds names the sub-resource being linked — "postgresqlServer"
            // is the only one a flexible server offers — and the zone group is what writes the server's
            // record into the private zone, which is what lets the connection string keep naming the
            // public FQDN.
            PrivateEndpoint privateEndpoint = new("postgresPrivateEndpoint")
            {
                Location = flexibleServer.Location,
                Subnet = new SubnetResource("postgresPrivateEndpointSubnet")
                {
                    Id = privateEndpointSubnetId.AsProvisioningParameter(infrastructure),
                },
                PrivateLinkServiceConnections =
                {
                    new NetworkPrivateLinkServiceConnection
                    {
                        Name = "postgres",
                        PrivateLinkServiceId = flexibleServer.Id,
                        GroupIds = { "postgresqlServer" },
                    },
                },
            };
            infrastructure.Add(privateEndpoint);
            infrastructure.Add(new PrivateDnsZoneGroup("postgresPrivateEndpointDnsZoneGroup")
            {
                Parent = privateEndpoint,
                Name = "default",
                PrivateDnsZoneConfigs =
                {
                    new PrivateDnsZoneConfig
                    {
                        Name = "postgres",
                        PrivateDnsZoneId = postgresPrivateDnsZoneId.AsProvisioningParameter(infrastructure),
                    },
                },
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

    // Scale to zero at rest, two replicas at most. This used to be an `az containerapp update` step
    // in the deploy workflow, because the generated container app defaults to a warm replica and the
    // AppHost had no say over it while azd owned the environment. It owns the environment now, so the
    // setting belongs in the model, where it is applied by the same deployment that creates the app
    // rather than by a step that could be reordered away from it.
    api.PublishAsAzureContainerApp((_, app) =>
    {
        app.Template.Scale = new ContainerAppScale { MinReplicas = 0, MaxReplicas = 2 };
    });
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
