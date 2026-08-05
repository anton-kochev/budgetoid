using Npgsql;

namespace IntegrationTests;

/// <summary>
/// Pins the shape of the <b>applied</b> schema. Every other schema test in this suite reads
/// <c>db.Model</c>, which is built from the configuration classes; the migration is a separate
/// artifact, and today nothing fails when the two disagree. These tests read the PostgreSQL
/// catalog of a migrated container instead, so they assert form rather than behaviour and do not
/// replace the behavioural tests that cover the same rules.
/// </summary>
/// <remarks>
/// PostgreSQL renders every line: <c>pg_get_constraintdef</c> and <c>pg_get_indexdef</c> are the
/// canonical renderers, and they carry expression columns, <c>INCLUDE</c> lists, partial
/// predicates, <c>MATCH</c> mode, <c>ON UPDATE</c> and opclasses that a hand-assembled column list
/// would silently drop. Comparison is deliberately order-insensitive — <c>IsEquivalentTo</c>
/// defaults to <c>CollectionOrdering.Any</c> — because a constraint set is a set and catalog order
/// is not policy; the <c>order by</c> in each query is there only so a failure dump reads well.
/// </remarks>
public sealed class SchemaConstraintSnapshotTests
{
    [Test]
    public async Task Schema_PinsEveryForeignKeyAndItsDeleteRule()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();

        // Act
        string[] foreignKeys = await ReadLinesAsync(
            connection,
            """
            select rel.relname || '.' || con.conname || ': ' || pg_get_constraintdef(con.oid)
            from pg_constraint con
            join pg_class rel on rel.oid = con.conrelid
            join pg_namespace ns on ns.oid = rel.relnamespace
            where con.contype = 'f' and ns.nspname = 'public'
            order by rel.relname, con.conname
            """);

        // Assert — the transactions -> budgets Restrict row is the rule, not an oversight, and must
        // not be normalized to Cascade: accounts, category groups, categories and payees all cascade
        // from the same budget_id, while recorded money movement is the one thing a budget must not
        // lose. Shortening any composite column list is the other way this set decays — a
        // single-column foreign key lets the database accept a transaction pointing at another
        // budget's row, and no query filter can enforce tenancy on a write. Two other tests catch
        // that collapse from their own side: Model_RequiresTransactionReferencesToStayInTheSameBudget
        // reads the configured principal key, and TransactionRepositoryTests'
        // Database_RejectsATransactionReferencingAnAccountInAnotherBudget inserts a cross-budget row
        // and expects the violation. The one place it does not show up is the unique-index snapshot
        // below: each AK_*_id_budget_id alternate key is declared explicitly in its configuration
        // (AccountConfiguration.cs:15), so it outlives the foreign key that referenced it and that
        // snapshot stays byte-identical.
        // One blunt edge: NO ACTION renders as the absence of an ON DELETE clause, so a
        // Restrict -> NO ACTION change still moves the line, it just reads as a deletion rather
        // than a substitution.
        string[] expected =
        [
            "accounts.FK_accounts_budgets_budget_id: FOREIGN KEY (budget_id) REFERENCES budgets(id) ON DELETE CASCADE",
            "accounts.FK_accounts_currencies_currency_code: FOREIGN KEY (currency_code) REFERENCES currencies(code) ON DELETE RESTRICT",
            "budgets.FK_budgets_currencies_base_currency_code: FOREIGN KEY (base_currency_code) REFERENCES currencies(code) ON DELETE RESTRICT",
            "budgets.FK_budgets_users_user_id: FOREIGN KEY (user_id) REFERENCES users(id) ON DELETE CASCADE",
            "categories.FK_categories_budgets_budget_id: FOREIGN KEY (budget_id) REFERENCES budgets(id) ON DELETE CASCADE",
            "categories.FK_categories_category_groups_category_group_id_budget_id: FOREIGN KEY (category_group_id, budget_id) REFERENCES category_groups(id, budget_id) ON DELETE RESTRICT",
            "category_groups.FK_category_groups_budgets_budget_id: FOREIGN KEY (budget_id) REFERENCES budgets(id) ON DELETE CASCADE",
            // Cascade, and deliberately not Restrict: a credential is how the account is reached,
            // not something the account owes anyone, so it must never be able to hold an erasure up.
            "credentials.FK_credentials_users_user_id: FOREIGN KEY (user_id) REFERENCES users(id) ON DELETE CASCADE",
            "payees.FK_payees_budgets_budget_id: FOREIGN KEY (budget_id) REFERENCES budgets(id) ON DELETE CASCADE",
            // Composite over three columns, and the composite is the rule: sessions carries user_id,
            // credential_id and credential_type, and all three must agree with the credential row.
            // Shortened to credential_id alone, the database would accept a session whose credential
            // belongs to somebody else and user_isolation would show it to the wrong person, because
            // the policy decides on user_id and never looks at the credential. Dropping
            // credential_type is the subtler collapse: that column is the only thing on the row
            // CK_sessions_kind_matches_credential can read, so without this reference it is a copy
            // free to disagree with its source, and a session could claim a passkey opened it while
            // naming a federated credential.
            // Cascade for the reason the credentials -> users row above records: a session must never
            // be able to hold an account erasure up.
            "sessions.FK_sessions_credentials_credential_id_user_id_credential_type: FOREIGN KEY (credential_id, user_id, credential_type) REFERENCES credentials(id, user_id, type) ON DELETE CASCADE",
            "transactions.FK_transactions_accounts_account_id_budget_id: FOREIGN KEY (account_id, budget_id) REFERENCES accounts(id, budget_id) ON DELETE RESTRICT",
            "transactions.FK_transactions_budgets_budget_id: FOREIGN KEY (budget_id) REFERENCES budgets(id) ON DELETE RESTRICT",
            "transactions.FK_transactions_categories_category_id_budget_id: FOREIGN KEY (category_id, budget_id) REFERENCES categories(id, budget_id) ON DELETE RESTRICT",
            "transactions.FK_transactions_payees_payee_id_budget_id: FOREIGN KEY (payee_id, budget_id) REFERENCES payees(id, budget_id) ON DELETE RESTRICT",
        ];
        await Assert.That(foreignKeys).IsEquivalentTo(expected);
    }

    [Test]
    public async Task Schema_PinsEveryUniqueIndex()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();

        // Act
        string[] uniqueIndexes = await ReadLinesAsync(
            connection,
            """
            select pg_get_indexdef(idx.indexrelid)
            from pg_index idx
            join pg_class cls on cls.oid = idx.indexrelid
            join pg_class tbl on tbl.oid = idx.indrelid
            join pg_namespace ns on ns.oid = tbl.relnamespace
            where idx.indisunique and ns.nspname = 'public'
            order by cls.relname
            """);

        // Assert — the four AK_*_id_budget_id rows are not indexes anyone queries through: they
        // exist solely as the principal keys the composite foreign keys target, so dropping one
        // breaks tenancy enforcement rather than merely costing a lookup.
        // PK___EFMigrationsHistory stays in the set rather than being filtered out by table name:
        // it is evidence the migration actually ran, and a name filter would silently hide any
        // other EF-managed object that appears later.
        // Non-unique indexes are out of scope here — those are performance choices, and
        // Model_IndexesTransactionReferencesWithTheBudget already pins the ones that matter.
        string[] expected =
        [
            """CREATE UNIQUE INDEX "AK_accounts_id_budget_id" ON public.accounts USING btree (id, budget_id)""",
            """CREATE UNIQUE INDEX "AK_categories_id_budget_id" ON public.categories USING btree (id, budget_id)""",
            """CREATE UNIQUE INDEX "AK_category_groups_id_budget_id" ON public.category_groups USING btree (id, budget_id)""",
            // The alternate key the sessions composite foreign key targets, and the same reasoning as
            // the four AK_*_id_budget_id rows: nobody queries through it, so dropping it reads as
            // tidying while it is what makes a session and its credential unable to disagree about
            // whose they are — or, since type joined the key, about what opened the session.
            """CREATE UNIQUE INDEX "AK_credentials_id_user_id_type" ON public.credentials USING btree (id, user_id, type)""",
            """CREATE UNIQUE INDEX "AK_payees_id_budget_id" ON public.payees USING btree (id, budget_id)""",
            """CREATE UNIQUE INDEX "IX_accounts_budget_id_name" ON public.accounts USING btree (budget_id, name)""",
            // The trailing clause is the rule, not rendering noise. Without it PostgreSQL counts
            // every NULL as distinct, so both provisioning racers insert an unnamed (user_id, NULL)
            // budget — the default budget is exactly the one with no name — and a user silently ends
            // up owning two; with it, one racer has to lose on 23505. And unlike case_insensitive,
            // which belongs to the column and needs the collation snapshot below to catch it,
            // pg_get_indexdef does render this clause: drop it and this line moves, so this snapshot
            // catches it alone.
            """CREATE UNIQUE INDEX "IX_budgets_user_id_name" ON public.budgets USING btree (user_id, name) NULLS NOT DISTINCT""",
            """CREATE UNIQUE INDEX "IX_categories_budget_id_name" ON public.categories USING btree (budget_id, name)""",
            """CREATE UNIQUE INDEX "IX_category_groups_budget_id_name" ON public.category_groups USING btree (budget_id, name)""",
            // One account per provider identity. The WHERE is rendered here, so this line alone
            // catches its removal — and removing it would make the index cover passkey rows too,
            // where every row carries (NULL, NULL) and the second one would be refused. Only
            // unique indexes are in scope for this query, so credentials' non-unique
            // IX_credentials_user_id is deliberately absent rather than missing.
            """CREATE UNIQUE INDEX "IX_credentials_provider_subject" ON public.credentials USING btree (provider, subject) WHERE ((type)::text = 'federated'::text)""",
            // A different rule from the line above, and the two are easy to mistake for one: that one
            // says one account per provider identity, this one says one federated credential per
            // account. Its WHERE is what keeps passkey rows out — an account may hold several of
            // those, which AppRoleGrantsTests inserts and relies on.
            """CREATE UNIQUE INDEX "IX_credentials_user_id_federated" ON public.credentials USING btree (user_id) WHERE ((type)::text = 'federated'::text)""",
            """CREATE UNIQUE INDEX "IX_payees_budget_id_name" ON public.payees USING btree (budget_id, name)""",
            // One email, one account. The index is only half the rule: users.email carries
            // case_insensitive, which the collation snapshot below pins, and pg_get_indexdef does
            // not render it here.
            """CREATE UNIQUE INDEX "IX_users_email" ON public.users USING btree (email)""",
            """CREATE UNIQUE INDEX "PK___EFMigrationsHistory" ON public."__EFMigrationsHistory" USING btree ("MigrationId")""",
            """CREATE UNIQUE INDEX "PK_accounts" ON public.accounts USING btree (id)""",
            """CREATE UNIQUE INDEX "PK_budgets" ON public.budgets USING btree (id)""",
            """CREATE UNIQUE INDEX "PK_categories" ON public.categories USING btree (id)""",
            """CREATE UNIQUE INDEX "PK_category_groups" ON public.category_groups USING btree (id)""",
            """CREATE UNIQUE INDEX "PK_credentials" ON public.credentials USING btree (id)""",
            """CREATE UNIQUE INDEX "PK_currencies" ON public.currencies USING btree (code)""",
            """CREATE UNIQUE INDEX "PK_payees" ON public.payees USING btree (id)""",
            """CREATE UNIQUE INDEX "PK_sessions" ON public.sessions USING btree (id)""",
            """CREATE UNIQUE INDEX "PK_transactions" ON public.transactions USING btree (id)""",
            """CREATE UNIQUE INDEX "PK_users" ON public.users USING btree (id)""",
        ];
        await Assert.That(uniqueIndexes).IsEquivalentTo(expected);
    }

    [Test]
    public async Task Schema_PinsEveryCheckConstraint()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();

        // Act
        string[] checkConstraints = await ReadLinesAsync(
            connection,
            """
            select con.conname || ': ' || rel.relname || ' ' || pg_get_constraintdef(con.oid)
            from pg_constraint con
            join pg_class rel on rel.oid = con.conrelid
            join pg_namespace ns on ns.oid = rel.relnamespace
            where con.contype = 'c' and ns.nspname = 'public'
            order by con.conname
            """);

        // Assert — explicit CHECKs only, and a major-version bump does not change that: PostgreSQL
        // 17 does not represent NOT NULL in pg_constraint at all, PostgreSQL 18 catalogs it as
        // contype = 'n', and this filter excludes it either way. "position" comes back quoted
        // because it is a COL_NAME_KEYWORD, which quote_identifier always quotes.
        // These are rendered expressions, not the ones the configuration writes: pg_get_constraintdef
        // normalizes, so an IN list comes back as = ANY (ARRAY[...]), BETWEEN as two ANDed
        // comparisons, and a numeric literal compared against numeric carries its cast. Editing a
        // line here to look like the configuration is how this test starts failing for no reason.
        string[] expected =
        [
            """CK_accounts_opening_balance: accounts CHECK ((abs(opening_balance) <= (1000000000)::numeric))""",
            """CK_accounts_type: accounts CHECK (((type)::text = ANY ((ARRAY['Checking'::character varying, 'Savings'::character varying, 'Cash'::character varying, 'CreditCard'::character varying])::text[])))""",
            """CK_categories_position: categories CHECK (("position" >= 0))""",
            """CK_category_groups_position: category_groups CHECK (("position" >= 0))""",
            // Bounds the issuer vocabulary the way CK_credentials_type below bounds the type
            // vocabulary, and without it 'Google' and 'google' are two accounts for one person.
            // PostgreSQL renders a single-element IN as '=' rather than as = ANY (ARRAY[...]), so
            // this deliberately does not read like the line beneath it — same idiom in the
            // configuration, different rendering in the catalog. "Fixing" it to look like a list is
            // how this test starts failing for no reason.
            """CK_credentials_provider: credentials CHECK (((provider IS NULL) OR ((provider)::text = 'google'::text)))""",
            """CK_credentials_type: credentials CHECK (((type)::text = ANY ((ARRAY['passkey'::character varying, 'federated'::character varying])::text[])))""",
            // The shape check is the reason `type` can be a varchar with a CHECK instead of a native
            // enum: it is what makes "a credential is exactly one type" a rule the database holds
            // rather than a convention the application is trusted to keep. The length test on the
            // federated arm is not redundant with the null test beside it: length(null) is null and a
            // check evaluating to null is satisfied, so neither test covers the other.
            """CK_credentials_type_shape: credentials CHECK (((((type)::text = 'federated'::text) AND (provider IS NOT NULL) AND (subject IS NOT NULL) AND (length((subject)::text) > 0)) OR (((type)::text = 'passkey'::text) AND (provider IS NULL) AND (subject IS NULL))))""",
            """CK_currencies_code: currencies CHECK (((code)::text ~ '^[A-Z]{3}$'::text))""",
            """CK_currencies_minor_unit: currencies CHECK (((minor_unit >= 0) AND (minor_unit <= 4)))""",
            // The kind vocabulary, and the lowercase spelling is the whole of it: the converter stores
            // these two strings, so a HasConversion<string>() writing PascalCase members would be
            // refused here rather than stored.
            """CK_sessions_kind: sessions CHECK (((kind)::text = ANY ((ARRAY['full'::character varying, 'locked'::character varying])::text[])))""",
            // The rule that a full session is opened by a passkey and by nothing else, held where it
            // rejects rather than where it is merely performed: Session.Establish derives kind from
            // the credential, but GRANT INSERT on this table covers the whole column list, so without
            // this line a federated credential paired with kind = 'full' is a row the application
            // role can write. An equality rather than an implication, so it refuses both directions
            // — a passkey opening a locked session moves this line too.
            """CK_sessions_kind_matches_credential: sessions CHECK ((((kind)::text = 'full'::text) = ((credential_type)::text = 'passkey'::text)))""",
            // Separate from the vocabulary check rather than ANDed with it, so a row that breaches one
            // reports exactly the name that describes what is wrong with it.
            """CK_sessions_lifetime: sessions CHECK ((expires_at_utc > created_at_utc))""",
            """CK_transactions_amount: transactions CHECK ((abs(amount) <= (1000000000)::numeric))""",
        ];
        await Assert.That(checkConstraints).IsEquivalentTo(expected);
    }

    [Test]
    public async Task Schema_PinsNotNullOnTenancyColumns()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();

        // Act — relkind = 'r' is not optional: pg_attribute carries rows for indexes too, and
        // without it index names leak into the output.
        string[] notNullColumns = await ReadLinesAsync(
            connection,
            """
            select rel.relname || '.' || att.attname
            from pg_attribute att
            join pg_class rel on rel.oid = att.attrelid
            join pg_namespace ns on ns.oid = rel.relnamespace
            where ns.nspname = 'public' and rel.relkind = 'r'
              and att.attnum > 0 and not att.attisdropped and att.attnotnull
              and att.attname in ('budget_id','account_id','payee_id','category_id','category_group_id')
            order by 1
            """);

        // Assert — transactions.budget_id being NOT NULL is what makes ON DELETE SET NULL
        // unreachable and what makes MATCH SIMPLE skip the foreign-key check for an absent payee.
        // It is also the silent trap Model_KeepsOptionalTransactionReferencesOptional names: calling
        // IsRequired(false) on a composite foreign key nullifies all of its columns including the
        // tenancy one, with no compiler error and no migration warning. That test pins it in the
        // model; this one pins it in the applied schema.
        // The absences are as load-bearing as the entries. transactions.payee_id and
        // transactions.category_id are deliberately nullable, and that asymmetry sitting in one
        // visible list is worth the test on its own.
        string[] expected =
        [
            "accounts.budget_id",
            "categories.budget_id",
            "categories.category_group_id",
            "category_groups.budget_id",
            "payees.budget_id",
            "transactions.account_id",
            "transactions.budget_id",
        ];
        await Assert.That(notNullColumns).IsEquivalentTo(expected);
    }

    [Test]
    public async Task Schema_PinsCaseInsensitiveNameColumns()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();

        // Act — a column declared without COLLATE carries the default collation, so excluding
        // 'default' leaves exactly the columns that were given one on purpose.
        string[] collatedColumns = await ReadLinesAsync(
            connection,
            """
            select rel.relname || '.' || att.attname || ' COLLATE ' || coll.collname
            from pg_attribute att
            join pg_class rel on rel.oid = att.attrelid
            join pg_namespace ns on ns.oid = rel.relnamespace
            join pg_collation coll on coll.oid = att.attcollation
            where ns.nspname = 'public' and rel.relkind = 'r'
              and att.attnum > 0 and not att.attisdropped and coll.collname <> 'default'
            order by 1
            """);

        // Assert — pg_get_indexdef does not render this collation, because it belongs to the column
        // rather than to the index. IX_payees_budget_id_name enforces case-insensitive uniqueness
        // only by virtue of payees.name carrying case_insensitive: drop it from the configuration
        // and every line of the unique-index snapshot stays byte-identical while the rule quietly
        // flips to case-sensitive. PayeeIntegrationTests covers one of these five columns
        // behaviourally. Asserting the whole set rather than five columns individually also catches
        // a collation added where it was not intended.
        // users.email is the one non-name column in the set, and the one whose collation carries a
        // uniqueness rule rather than a lookup convenience: drop it and Sam@x.com and sam@x.com
        // become two accounts for one mailbox.
        string[] expected =
        [
            "accounts.name COLLATE case_insensitive",
            "budgets.name COLLATE case_insensitive",
            "categories.name COLLATE case_insensitive",
            "category_groups.name COLLATE case_insensitive",
            "payees.name COLLATE case_insensitive",
            "users.email COLLATE case_insensitive",
        ];
        await Assert.That(collatedColumns).IsEquivalentTo(expected);
    }

    /// <summary>
    /// Runs a query that projects exactly one text column and returns its rows in query order.
    /// </summary>
    private static async Task<string[]> ReadLinesAsync(NpgsqlConnection connection, string sql)
    {
        await using NpgsqlCommand command = new(sql, connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        List<string> lines = [];

        while (await reader.ReadAsync())
        {
            // Pattern-matched rather than cast-and-null-forgive: anything other than text means the
            // query changed shape, and that should fail loudly here instead of at the assertion.
            lines.Add(reader.GetValue(0) switch
            {
                string line => line,
                var unexpected => throw new InvalidOperationException(
                    $"Expected a single text column, got '{unexpected.GetType().Name}'."),
            });
        }

        return [.. lines];
    }

    private static async Task<RepositoryTestHost> StartHostAsync()
    {
        RepositoryTestHost host = new();
        await host.StartAsync();
        return host;
    }
}
