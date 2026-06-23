using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTransactionsUsersForeignKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Remove historical transactions created before real app users existed. These orphaned
            // rows cannot be associated with a users.id and would prevent the FK from being added.
            migrationBuilder.Sql(
                """
                delete from transactions t
                where not exists (
                    select 1
                    from users app_user
                    where app_user.id = t.user_id
                )
                """);

            migrationBuilder.AddForeignKey(
                name: "FK_transactions_users_user_id",
                table: "transactions",
                column: "user_id",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_transactions_users_user_id",
                table: "transactions");
        }
    }
}
