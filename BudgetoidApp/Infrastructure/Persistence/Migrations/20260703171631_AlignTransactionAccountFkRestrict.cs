using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AlignTransactionAccountFkRestrict : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_transactions_accounts_account_id",
                table: "transactions");

            migrationBuilder.AddForeignKey(
                name: "FK_transactions_accounts_account_id",
                table: "transactions",
                column: "account_id",
                principalTable: "accounts",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_transactions_accounts_account_id",
                table: "transactions");

            migrationBuilder.AddForeignKey(
                name: "FK_transactions_accounts_account_id",
                table: "transactions",
                column: "account_id",
                principalTable: "accounts",
                principalColumn: "id");
        }
    }
}
