// Deploy-time database provisioning: migrates the schema, provisions the application role with its
// grants and row-level security policies, verifies that the policies cover every budget-owned
// table, and binds the role to the API's managed identity. Runs from the deploy pipeline on every
// push to main; DEPLOYMENT.md's break-glass recipe documents running it by hand.
//
// Input comes from environment variables only, never from argv: the admin connection string is a
// database administrator credential — in the pipeline a short-lived Microsoft Entra access token —
// and argv is visible to every process on the machine.
//
//   DBPROVISION_ADMIN_CONNECTION_STRING  Npgsql connection string for a Microsoft Entra
//                                        administrator of the server. Password= carries the access
//                                        token; to PostgreSQL a token *is* the password.
//   DBPROVISION_APP_IDENTITY_OBJECT_ID   object (principal) id of the API's user-assigned managed
//                                        identity, as a GUID. The application role is bound to this
//                                        principal, and Azure matches tokens to roles by object id
//                                        rather than by name — which is why this, and not a name, is
//                                        the input.
//
// Exit codes:
//   0  provisioned, coverage verified, and the application role bound to the identity
//   1  provisioning failed
//   2  a required environment variable is missing, empty, or malformed

using Infrastructure.Persistence.Provisioning;

const string adminConnectionStringVariable = "DBPROVISION_ADMIN_CONNECTION_STRING";
const string appIdentityObjectIdVariable = "DBPROVISION_APP_IDENTITY_OBJECT_ID";

string? adminConnectionString = Environment.GetEnvironmentVariable(adminConnectionStringVariable);
string? appIdentityObjectIdValue = Environment.GetEnvironmentVariable(appIdentityObjectIdVariable);

if (string.IsNullOrEmpty(adminConnectionString) || string.IsNullOrEmpty(appIdentityObjectIdValue))
{
    await Console.Error.WriteLineAsync(
        $"usage: set {adminConnectionStringVariable} and {appIdentityObjectIdVariable} in the "
        + "environment, then run this tool with no arguments. Both are required and neither is "
        + "read from the command line.");
    return 2;
}

// Parsed here rather than deeper down, and reported as a usage error rather than a failure: an
// unresolved Azure CLI query yields an empty or literal-null string, and a malformed principal id
// is a pipeline misconfiguration to fix, not a provisioning run that got as far as the database.
if (!Guid.TryParse(appIdentityObjectIdValue, out Guid appIdentityObjectId))
{
    await Console.Error.WriteLineAsync(
        $"{appIdentityObjectIdVariable} is not a GUID. It must be the object (principal) id of the "
        + "API's user-assigned managed identity — see DEPLOYMENT.md.");
    return 2;
}

try
{
    await DeploymentDatabaseProvisioning.ProvisionAsync(adminConnectionString, Console.WriteLine);

    // Strictly after provisioning, because the role has to exist before it can be labelled. This
    // step is separate from ProvisionAsync on purpose: skipping it is not silent — the application
    // simply cannot authenticate (28P01) — whereas the row-level security coverage ProvisionAsync
    // verifies fails open and reports nothing.
    Console.WriteLine(
        $"Binding role {DatabaseProvisioning.AppRoleName} to managed identity "
        + $"{appIdentityObjectId:D}.");
    await DatabaseProvisioning.AttachAppRoleIdentityAsync(adminConnectionString, appIdentityObjectId);
    Console.WriteLine(
        $"Role {DatabaseProvisioning.AppRoleName} authenticates as the managed identity; its "
        + "password has been cleared.");

    return 0;
}
catch (Exception exception)
{
    // Everything is caught, and only the message is printed. This is a deploy step whose failure is
    // an exit code, so there is no exception worth crashing on rather than reporting; and a stack
    // trace here would be logged by the pipeline, where an Npgsql frame can carry the admin
    // connection string — which is an access token — into build output that outlives the deploy.
    await Console.Error.WriteLineAsync(exception.Message);
    return 1;
}
