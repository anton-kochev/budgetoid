using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class ConstrainCredentialProviderAndFederatedShape : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(
            name: "CK_credentials_type_shape",
            table: "credentials");

        migrationBuilder.CreateIndex(
            name: "IX_credentials_user_id_federated",
            table: "credentials",
            column: "user_id",
            unique: true,
            filter: "type = 'federated'");

        migrationBuilder.AddCheckConstraint(
            name: "CK_credentials_provider",
            table: "credentials",
            sql: "provider is null or provider in ('google')");

        migrationBuilder.AddCheckConstraint(
            name: "CK_credentials_type_shape",
            table: "credentials",
            sql: "(type = 'federated' and provider is not null and subject is not null and length(subject) > 0) or (type = 'passkey' and provider is null and subject is null)");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_credentials_user_id_federated",
            table: "credentials");

        migrationBuilder.DropCheckConstraint(
            name: "CK_credentials_provider",
            table: "credentials");

        migrationBuilder.DropCheckConstraint(
            name: "CK_credentials_type_shape",
            table: "credentials");

        migrationBuilder.AddCheckConstraint(
            name: "CK_credentials_type_shape",
            table: "credentials",
            sql: "(type = 'federated' and provider is not null and subject is not null) or (type = 'passkey' and provider is null and subject is null)");
    }
}
