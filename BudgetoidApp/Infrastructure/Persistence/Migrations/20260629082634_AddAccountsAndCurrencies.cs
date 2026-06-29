using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAccountsAndCurrencies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Create currencies first so the accounts.currency_code FK has a valid principal, then
            // seed it before any account row can reference it.
            migrationBuilder.CreateTable(
                name: "currencies",
                columns: table => new
                {
                    code = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    symbol = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    minor_unit = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_currencies", x => x.code);
                });

            migrationBuilder.InsertData(
                table: "currencies",
                columns: ["code", "name", "symbol", "minor_unit"],
                values: new object[,]
                {
                    { "AUD", "Australian Dollar", "A$", 2 },
                    { "BHD", "Bahraini Dinar", "BD", 3 },
                    { "CAD", "Canadian Dollar", "C$", 2 },
                    { "CHF", "Swiss Franc", "Fr", 2 },
                    { "EUR", "Euro", "€", 2 },
                    { "GBP", "Pound Sterling", "£", 2 },
                    { "JPY", "Japanese Yen", "¥", 0 },
                    { "KWD", "Kuwaiti Dinar", "KD", 3 },
                    { "NOK", "Norwegian Krone", "kr", 2 },
                    { "PLN", "Polish Zloty", "zł", 2 },
                    { "SEK", "Swedish Krona", "kr", 2 },
                    { "UAH", "Ukrainian Hryvnia", "₴", 2 },
                    { "USD", "US Dollar", "$", 2 }
                });

            // accounts is created fresh here, already carrying currency_code + its FK to currencies
            // (Restrict), so no backfill or DELETE FROM accounts is required.
            migrationBuilder.CreateTable(
                name: "accounts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "case_insensitive"),
                    type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    opening_balance = table.Column<decimal>(type: "numeric(14,2)", nullable: false),
                    currency_code = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_accounts", x => x.id);
                    table.ForeignKey(
                        name: "FK_accounts_currencies_currency_code",
                        column: x => x.currency_code,
                        principalTable: "currencies",
                        principalColumn: "code",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_accounts_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_accounts_currency_code",
                table: "accounts",
                column: "currency_code");

            migrationBuilder.CreateIndex(
                name: "IX_accounts_user_id_name",
                table: "accounts",
                columns: ["user_id", "name"],
                unique: true);

            // Dev database migration: existing transactions cannot be backfilled because accounts
            // did not exist before this slice. This intentionally discards them before adding the
            // required account_id FK column. Down is therefore lossy and cannot restore deleted rows.
            migrationBuilder.Sql("DELETE FROM transactions;");

            migrationBuilder.AddColumn<Guid>(
                name: "account_id",
                table: "transactions",
                type: "uuid",
                nullable: false);

            migrationBuilder.CreateIndex(
                name: "IX_transactions_account_id",
                table: "transactions",
                column: "account_id");

            migrationBuilder.AddForeignKey(
                name: "FK_transactions_accounts_account_id",
                table: "transactions",
                column: "account_id",
                principalTable: "accounts",
                principalColumn: "id",
                onDelete: ReferentialAction.NoAction);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_transactions_accounts_account_id",
                table: "transactions");

            migrationBuilder.DropTable(
                name: "accounts");

            migrationBuilder.DropTable(
                name: "currencies");

            migrationBuilder.DropIndex(
                name: "IX_transactions_account_id",
                table: "transactions");

            migrationBuilder.DropColumn(
                name: "account_id",
                table: "transactions");
        }
    }
}
