// Deploy-time database provisioning: migrates the schema, provisions the application role with its
// grants and row-level security policies, and verifies that the policies cover every budget-owned
// table. Runs from the deploy pipeline on every push to main; DEPLOYMENT.md's break-glass recipe
// documents running it by hand.
//
// Input comes from environment variables only, never from argv: the admin connection string is a
// database administrator credential, and argv is visible to every process on the machine.
//
//   DBPROVISION_ADMIN_CONNECTION_STRING  Npgsql connection string for the schema-owning admin role
//   DBPROVISION_APP_ROLE_PASSWORD        password to assign to the least-privilege application role
//
// Exit codes:
//   0  provisioned and coverage verified
//   1  provisioning failed
//   2  a required environment variable is missing or empty

using Infrastructure.Persistence.Provisioning;

const string adminConnectionStringVariable = "DBPROVISION_ADMIN_CONNECTION_STRING";
const string appRolePasswordVariable = "DBPROVISION_APP_ROLE_PASSWORD";

string? adminConnectionString = Environment.GetEnvironmentVariable(adminConnectionStringVariable);
string? appRolePassword = Environment.GetEnvironmentVariable(appRolePasswordVariable);

if (string.IsNullOrEmpty(adminConnectionString) || string.IsNullOrEmpty(appRolePassword))
{
    await Console.Error.WriteLineAsync(
        $"usage: set {adminConnectionStringVariable} and {appRolePasswordVariable} in the "
        + "environment, then run this tool with no arguments. Both are required and neither is "
        + "read from the command line.");
    return 2;
}

try
{
    await DeploymentDatabaseProvisioning.ProvisionAsync(
        adminConnectionString, appRolePassword, Console.WriteLine);
    return 0;
}
catch (Exception exception)
{
    // Everything is caught, and only the message is printed. This is a deploy step whose failure is
    // an exit code, so there is no exception worth crashing on rather than reporting; and a stack
    // trace here would be logged by the pipeline, where an Npgsql frame can carry the admin
    // connection string into build output that outlives the deploy.
    await Console.Error.WriteLineAsync(exception.Message);
    return 1;
}
