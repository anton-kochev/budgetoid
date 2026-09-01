using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class InitialCreate : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AlterDatabase()
            .Annotation("Npgsql:CollationDefinition:case_insensitive", "und-u-ks-level2,und-u-ks-level2,icu,False");

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
                table.CheckConstraint("CK_currencies_code", "code ~ '^[A-Z]{3}$'");
                table.CheckConstraint("CK_currencies_minor_unit", "minor_unit between 0 and 4");
            });

        migrationBuilder.CreateTable(
            name: "users",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false, collation: "case_insensitive"),
                created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_users", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "webauthn_challenges",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                challenge = table.Column<byte[]>(type: "bytea", nullable: false),
                ceremony = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                expires_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_webauthn_challenges", x => x.id);
                table.CheckConstraint("CK_webauthn_challenges_ceremony", "ceremony in ('registration', 'authentication', 'reauthentication', 'account_registration')");
                table.CheckConstraint("CK_webauthn_challenges_length", "length(challenge) = 32");
                table.CheckConstraint("CK_webauthn_challenges_lifetime", "expires_at_utc > created_at_utc");
            });

        migrationBuilder.CreateTable(
            name: "budgets",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                user_id = table.Column<Guid>(type: "uuid", nullable: false),
                name = table.Column<byte[]>(type: "bytea", nullable: true),
                base_currency_code = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: true),
                created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_budgets", x => x.id);
                table.CheckConstraint("CK_budgets_name_length", "length(name) between 29 and 1024");
                table.CheckConstraint("CK_budgets_name_version", "substring(name from 1 for 1) = '\\x01'::bytea");
                table.ForeignKey(
                    name: "FK_budgets_currencies_base_currency_code",
                    column: x => x.base_currency_code,
                    principalTable: "currencies",
                    principalColumn: "code",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_budgets_users_user_id",
                    column: x => x.user_id,
                    principalTable: "users",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "credentials",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                user_id = table.Column<Guid>(type: "uuid", nullable: false),
                type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                provider = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                subject = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_credentials", x => x.id);
                table.UniqueConstraint("AK_credentials_id_user_id_type", x => new { x.id, x.user_id, x.type });
                table.CheckConstraint("CK_credentials_provider", "provider is null or provider in ('google')");
                table.CheckConstraint("CK_credentials_type", "type in ('passkey', 'federated', 'recovery_codes')");
                table.CheckConstraint("CK_credentials_type_shape", "(type = 'federated' and provider is not null and subject is not null and length(subject) > 0) or (type = 'passkey' and provider is null and subject is null) or (type = 'recovery_codes' and provider is null and subject is null)");
                table.ForeignKey(
                    name: "FK_credentials_users_user_id",
                    column: x => x.user_id,
                    principalTable: "users",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "accounts",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                budget_id = table.Column<Guid>(type: "uuid", nullable: false),
                name = table.Column<byte[]>(type: "bytea", nullable: false),
                name_key = table.Column<byte[]>(type: "bytea", nullable: false),
                type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                opening_balance = table.Column<decimal>(type: "numeric(14,4)", nullable: false),
                currency_code = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_accounts", x => x.id);
                table.UniqueConstraint("AK_accounts_id_budget_id", x => new { x.id, x.budget_id });
                table.CheckConstraint("CK_accounts_name_key_length", "length(name_key) = 32");
                table.CheckConstraint("CK_accounts_name_length", "length(name) between 29 and 1024");
                table.CheckConstraint("CK_accounts_name_version", "substring(name from 1 for 1) = '\\x01'::bytea");
                table.CheckConstraint("CK_accounts_opening_balance", "abs(opening_balance) <= 1000000000");
                table.CheckConstraint("CK_accounts_type", "type in ('Checking', 'Savings', 'Cash', 'CreditCard')");
                table.ForeignKey(
                    name: "FK_accounts_budgets_budget_id",
                    column: x => x.budget_id,
                    principalTable: "budgets",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_accounts_currencies_currency_code",
                    column: x => x.currency_code,
                    principalTable: "currencies",
                    principalColumn: "code",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "category_groups",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                budget_id = table.Column<Guid>(type: "uuid", nullable: false),
                name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "case_insensitive"),
                description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                position = table.Column<int>(type: "integer", nullable: false),
                created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_category_groups", x => x.id);
                table.UniqueConstraint("AK_category_groups_id_budget_id", x => new { x.id, x.budget_id });
                table.CheckConstraint("CK_category_groups_position", "position >= 0");
                table.ForeignKey(
                    name: "FK_category_groups_budgets_budget_id",
                    column: x => x.budget_id,
                    principalTable: "budgets",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "payees",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                budget_id = table.Column<Guid>(type: "uuid", nullable: false),
                name = table.Column<byte[]>(type: "bytea", nullable: false),
                name_key = table.Column<byte[]>(type: "bytea", nullable: false),
                created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_payees", x => x.id);
                table.UniqueConstraint("AK_payees_id_budget_id", x => new { x.id, x.budget_id });
                table.CheckConstraint("CK_payees_name_key_length", "length(name_key) = 32");
                table.CheckConstraint("CK_payees_name_length", "length(name) between 29 and 1024");
                table.CheckConstraint("CK_payees_name_version", "substring(name from 1 for 1) = '\\x01'::bytea");
                table.ForeignKey(
                    name: "FK_payees_budgets_budget_id",
                    column: x => x.budget_id,
                    principalTable: "budgets",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "passkey_public_keys",
            columns: table => new
            {
                credential_id = table.Column<Guid>(type: "uuid", nullable: false),
                user_id = table.Column<Guid>(type: "uuid", nullable: false),
                credential_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                webauthn_credential_id = table.Column<byte[]>(type: "bytea", nullable: false),
                public_key_cose = table.Column<byte[]>(type: "bytea", nullable: false),
                cose_algorithm = table.Column<int>(type: "integer", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_passkey_public_keys", x => x.credential_id);
                table.CheckConstraint("CK_passkey_public_keys_cose_algorithm", "cose_algorithm in (-7, -257)");
                table.CheckConstraint("CK_passkey_public_keys_credential_type", "credential_type = 'passkey'");
                table.CheckConstraint("CK_passkey_public_keys_public_key_length", "length(public_key_cose) between 1 and 1024");
                table.CheckConstraint("CK_passkey_public_keys_webauthn_credential_id_length", "length(webauthn_credential_id) between 16 and 1023");
                table.ForeignKey(
                    name: "FK_passkey_public_keys_credentials",
                    columns: x => new { x.credential_id, x.user_id, x.credential_type },
                    principalTable: "credentials",
                    principalColumns: new[] { "id", "user_id", "type" },
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "passkey_signature_counters",
            columns: table => new
            {
                credential_id = table.Column<Guid>(type: "uuid", nullable: false),
                user_id = table.Column<Guid>(type: "uuid", nullable: false),
                credential_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                signature_counter = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_passkey_signature_counters", x => x.credential_id);
                table.CheckConstraint("CK_passkey_signature_counters_credential_type", "credential_type = 'passkey'");
                table.CheckConstraint("CK_passkey_signature_counters_value", "signature_counter >= 0 and signature_counter <= 4294967295");
                table.ForeignKey(
                    name: "FK_passkey_signature_counters_credentials",
                    columns: x => new { x.credential_id, x.user_id, x.credential_type },
                    principalTable: "credentials",
                    principalColumns: new[] { "id", "user_id", "type" },
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "recovery_code_hashes",
            columns: table => new
            {
                verifier_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                credential_id = table.Column<Guid>(type: "uuid", nullable: false),
                user_id = table.Column<Guid>(type: "uuid", nullable: false),
                credential_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_recovery_code_hashes", x => x.verifier_hash);
                table.CheckConstraint("CK_recovery_code_hashes_credential_type", "credential_type = 'recovery_codes'");
                table.CheckConstraint("CK_recovery_code_hashes_verifier_hash_length", "length(verifier_hash) = 32");
                table.ForeignKey(
                    name: "FK_recovery_code_hashes_credentials",
                    columns: x => new { x.credential_id, x.user_id, x.credential_type },
                    principalTable: "credentials",
                    principalColumns: new[] { "id", "user_id", "type" },
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "sessions",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                user_id = table.Column<Guid>(type: "uuid", nullable: false),
                credential_id = table.Column<Guid>(type: "uuid", nullable: false),
                credential_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                expires_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                revoked_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_sessions", x => x.id);
                table.UniqueConstraint("AK_sessions_id_user_id", x => new { x.id, x.user_id });
                table.CheckConstraint("CK_sessions_kind", "kind in ('full', 'locked')");
                table.CheckConstraint("CK_sessions_kind_matches_credential", "(kind = 'full') = (credential_type in ('passkey', 'recovery_codes'))");
                table.CheckConstraint("CK_sessions_lifetime", "expires_at_utc > created_at_utc");
                table.ForeignKey(
                    name: "FK_sessions_credentials_credential_id_user_id_credential_type",
                    columns: x => new { x.credential_id, x.user_id, x.credential_type },
                    principalTable: "credentials",
                    principalColumns: new[] { "id", "user_id", "type" },
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "wrapped_account_keys",
            columns: table => new
            {
                factor_id = table.Column<Guid>(type: "uuid", nullable: false),
                credential_id = table.Column<Guid>(type: "uuid", nullable: false),
                user_id = table.Column<Guid>(type: "uuid", nullable: false),
                credential_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                wrapped_content_key = table.Column<byte[]>(type: "bytea", nullable: false),
                wrapped_index_key = table.Column<byte[]>(type: "bytea", nullable: false),
                created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_wrapped_account_keys", x => x.factor_id);
                table.CheckConstraint("CK_wrapped_account_keys_credential_type", "credential_type in ('passkey', 'recovery_codes')");
                table.CheckConstraint("CK_wrapped_account_keys_wrapped_content_key_length", "length(wrapped_content_key) = 61");
                table.CheckConstraint("CK_wrapped_account_keys_wrapped_content_key_version", "get_byte(wrapped_content_key, 0) = 1");
                table.CheckConstraint("CK_wrapped_account_keys_wrapped_index_key_length", "length(wrapped_index_key) = 61");
                table.CheckConstraint("CK_wrapped_account_keys_wrapped_index_key_version", "get_byte(wrapped_index_key, 0) = 1");
                table.ForeignKey(
                    name: "FK_wrapped_account_keys_credentials",
                    columns: x => new { x.credential_id, x.user_id, x.credential_type },
                    principalTable: "credentials",
                    principalColumns: new[] { "id", "user_id", "type" },
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "categories",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                budget_id = table.Column<Guid>(type: "uuid", nullable: false),
                category_group_id = table.Column<Guid>(type: "uuid", nullable: false),
                name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "case_insensitive"),
                description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                position = table.Column<int>(type: "integer", nullable: false),
                created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_categories", x => x.id);
                table.UniqueConstraint("AK_categories_id_budget_id", x => new { x.id, x.budget_id });
                table.CheckConstraint("CK_categories_position", "position >= 0");
                table.ForeignKey(
                    name: "FK_categories_budgets_budget_id",
                    column: x => x.budget_id,
                    principalTable: "budgets",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_categories_category_groups_category_group_id_budget_id",
                    columns: x => new { x.category_group_id, x.budget_id },
                    principalTable: "category_groups",
                    principalColumns: new[] { "id", "budget_id" },
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "session_tokens",
            columns: table => new
            {
                token_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                session_id = table.Column<Guid>(type: "uuid", nullable: false),
                user_id = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_session_tokens", x => x.token_hash);
                table.CheckConstraint("CK_session_tokens_token_hash_length", "length(token_hash) = 32");
                table.ForeignKey(
                    name: "FK_session_tokens_sessions",
                    columns: x => new { x.session_id, x.user_id },
                    principalTable: "sessions",
                    principalColumns: new[] { "id", "user_id" },
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "transactions",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                budget_id = table.Column<Guid>(type: "uuid", nullable: false),
                account_id = table.Column<Guid>(type: "uuid", nullable: false),
                amount = table.Column<decimal>(type: "numeric(14,4)", nullable: false),
                date = table.Column<DateOnly>(type: "date", nullable: false),
                description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                payee_id = table.Column<Guid>(type: "uuid", nullable: true),
                category_id = table.Column<Guid>(type: "uuid", nullable: true),
                created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_transactions", x => x.id);
                table.CheckConstraint("CK_transactions_amount", "abs(amount) <= 1000000000");
                table.ForeignKey(
                    name: "FK_transactions_accounts_account_id_budget_id",
                    columns: x => new { x.account_id, x.budget_id },
                    principalTable: "accounts",
                    principalColumns: new[] { "id", "budget_id" },
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_transactions_budgets_budget_id",
                    column: x => x.budget_id,
                    principalTable: "budgets",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_transactions_categories_category_id_budget_id",
                    columns: x => new { x.category_id, x.budget_id },
                    principalTable: "categories",
                    principalColumns: new[] { "id", "budget_id" },
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_transactions_payees_payee_id_budget_id",
                    columns: x => new { x.payee_id, x.budget_id },
                    principalTable: "payees",
                    principalColumns: new[] { "id", "budget_id" },
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.InsertData(
            table: "currencies",
            columns: new[] { "code", "minor_unit", "name", "symbol" },
            values: new object[,]
            {
                { "AUD", 2, "Australian Dollar", "A$" },
                { "BHD", 3, "Bahraini Dinar", "BD" },
                { "CAD", 2, "Canadian Dollar", "C$" },
                { "CHF", 2, "Swiss Franc", "Fr" },
                { "EUR", 2, "Euro", "€" },
                { "GBP", 2, "Pound Sterling", "£" },
                { "JPY", 0, "Japanese Yen", "¥" },
                { "KWD", 3, "Kuwaiti Dinar", "KD" },
                { "NOK", 2, "Norwegian Krone", "kr" },
                { "PLN", 2, "Polish Zloty", "zł" },
                { "SEK", 2, "Swedish Krona", "kr" },
                { "UAH", 2, "Ukrainian Hryvnia", "₴" },
                { "USD", 2, "US Dollar", "$" }
            });

        migrationBuilder.CreateIndex(
            name: "IX_accounts_budget_id_name_key",
            table: "accounts",
            columns: new[] { "budget_id", "name_key" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_accounts_currency_code",
            table: "accounts",
            column: "currency_code");

        migrationBuilder.CreateIndex(
            name: "IX_budgets_base_currency_code",
            table: "budgets",
            column: "base_currency_code");

        migrationBuilder.CreateIndex(
            name: "IX_budgets_user_id_name",
            table: "budgets",
            columns: new[] { "user_id", "name" },
            unique: true)
            .Annotation("Npgsql:NullsDistinct", false);

        migrationBuilder.CreateIndex(
            name: "IX_categories_budget_id_name",
            table: "categories",
            columns: new[] { "budget_id", "name" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_categories_category_group_id_budget_id",
            table: "categories",
            columns: new[] { "category_group_id", "budget_id" });

        migrationBuilder.CreateIndex(
            name: "IX_categories_category_group_id_position",
            table: "categories",
            columns: new[] { "category_group_id", "position" });

        migrationBuilder.CreateIndex(
            name: "IX_category_groups_budget_id_name",
            table: "category_groups",
            columns: new[] { "budget_id", "name" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_category_groups_budget_id_position",
            table: "category_groups",
            columns: new[] { "budget_id", "position" });

        migrationBuilder.CreateIndex(
            name: "IX_credentials_provider_subject",
            table: "credentials",
            columns: new[] { "provider", "subject" },
            unique: true,
            filter: "type = 'federated'");

        migrationBuilder.CreateIndex(
            name: "IX_credentials_user_id",
            table: "credentials",
            column: "user_id");

        migrationBuilder.CreateIndex(
            name: "IX_credentials_user_id_federated",
            table: "credentials",
            column: "user_id",
            unique: true,
            filter: "type = 'federated'");

        migrationBuilder.CreateIndex(
            name: "IX_credentials_user_id_recovery_codes",
            table: "credentials",
            column: "user_id",
            unique: true,
            filter: "type = 'recovery_codes'");

        migrationBuilder.CreateIndex(
            name: "IX_passkey_public_keys_credential_id_user_id_credential_type",
            table: "passkey_public_keys",
            columns: new[] { "credential_id", "user_id", "credential_type" });

        migrationBuilder.CreateIndex(
            name: "IX_passkey_public_keys_webauthn_credential_id",
            table: "passkey_public_keys",
            column: "webauthn_credential_id",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_passkey_signature_counters_credential_id_user_id_credential_type",
            table: "passkey_signature_counters",
            columns: new[] { "credential_id", "user_id", "credential_type" });

        migrationBuilder.CreateIndex(
            name: "IX_passkey_signature_counters_user_id",
            table: "passkey_signature_counters",
            column: "user_id");

        migrationBuilder.CreateIndex(
            name: "IX_payees_budget_id_name_key",
            table: "payees",
            columns: new[] { "budget_id", "name_key" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_recovery_code_hashes_credential_id_user_id_credential_type",
            table: "recovery_code_hashes",
            columns: new[] { "credential_id", "user_id", "credential_type" });

        migrationBuilder.CreateIndex(
            name: "IX_session_tokens_session_id_user_id",
            table: "session_tokens",
            columns: new[] { "session_id", "user_id" });

        migrationBuilder.CreateIndex(
            name: "IX_sessions_credential_id_user_id_credential_type",
            table: "sessions",
            columns: new[] { "credential_id", "user_id", "credential_type" });

        migrationBuilder.CreateIndex(
            name: "IX_sessions_user_id",
            table: "sessions",
            column: "user_id");

        migrationBuilder.CreateIndex(
            name: "IX_transactions_account_id_budget_id",
            table: "transactions",
            columns: new[] { "account_id", "budget_id" });

        migrationBuilder.CreateIndex(
            name: "IX_transactions_budget_id_date_created_at_utc",
            table: "transactions",
            columns: new[] { "budget_id", "date", "created_at_utc" },
            descending: new[] { false, true, true });

        migrationBuilder.CreateIndex(
            name: "IX_transactions_category_id_budget_id",
            table: "transactions",
            columns: new[] { "category_id", "budget_id" });

        migrationBuilder.CreateIndex(
            name: "IX_transactions_payee_id_budget_id",
            table: "transactions",
            columns: new[] { "payee_id", "budget_id" });

        migrationBuilder.CreateIndex(
            name: "IX_users_email",
            table: "users",
            column: "email",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_webauthn_challenges_challenge",
            table: "webauthn_challenges",
            column: "challenge",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_webauthn_challenges_expires_at_utc",
            table: "webauthn_challenges",
            column: "expires_at_utc");

        migrationBuilder.CreateIndex(
            name: "IX_wrapped_account_keys_credential_id_user_id_credential_type",
            table: "wrapped_account_keys",
            columns: new[] { "credential_id", "user_id", "credential_type" });

        migrationBuilder.CreateIndex(
            name: "IX_wrapped_account_keys_user_id",
            table: "wrapped_account_keys",
            column: "user_id");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "passkey_public_keys");

        migrationBuilder.DropTable(
            name: "passkey_signature_counters");

        migrationBuilder.DropTable(
            name: "recovery_code_hashes");

        migrationBuilder.DropTable(
            name: "session_tokens");

        migrationBuilder.DropTable(
            name: "transactions");

        migrationBuilder.DropTable(
            name: "webauthn_challenges");

        migrationBuilder.DropTable(
            name: "wrapped_account_keys");

        migrationBuilder.DropTable(
            name: "sessions");

        migrationBuilder.DropTable(
            name: "accounts");

        migrationBuilder.DropTable(
            name: "categories");

        migrationBuilder.DropTable(
            name: "payees");

        migrationBuilder.DropTable(
            name: "credentials");

        migrationBuilder.DropTable(
            name: "category_groups");

        migrationBuilder.DropTable(
            name: "budgets");

        migrationBuilder.DropTable(
            name: "currencies");

        migrationBuilder.DropTable(
            name: "users");
    }
}
