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
            // The same three-column composite the sessions row below carries, and owed for the same
            // reason: the key and the counter each hold a copy of user_id and credential_type, and
            // only a reference over all three stops those copies from disagreeing with the credential
            // they describe. Shortened to credential_id, the database would accept key material filed
            // under somebody else's user_id — and user_isolation on the counter table decides on that
            // column and never looks at the credential.
            // Cascade, and deliberately not Restrict, for the reason the credentials row above gives:
            // neither a stored key nor a counter may hold an account erasure up.
            "passkey_public_keys.FK_passkey_public_keys_credentials: FOREIGN KEY (credential_id, user_id, credential_type) REFERENCES credentials(id, user_id, type) ON DELETE CASCADE",
            "passkey_signature_counters.FK_passkey_signature_counters_credentials: FOREIGN KEY (credential_id, user_id, credential_type) REFERENCES credentials(id, user_id, type) ON DELETE CASCADE",
            "payees.FK_payees_budgets_budget_id: FOREIGN KEY (budget_id) REFERENCES budgets(id) ON DELETE CASCADE",
            // The third composite over the same three credential columns, and on this table the first
            // of them carries more weight than anywhere else on the schema: a redemption request
            // arrives anonymous and adopts the user_id it finds on the row, so a row whose user_id
            // disagreed with its credential's would hand the redeemer somebody else's account — and
            // recovery_code_hashes is exempt from row-level security, so no policy is watching. A
            // shortened single-column reference is exactly that hole.
            // Cascade, and deliberately not Restrict, for the reason the credentials -> users row
            // above records: an unredeemed code must never hold up a credential's deletion, and
            // through it an account erasure.
            "recovery_code_hashes.FK_recovery_code_hashes_credentials: FOREIGN KEY (credential_id, user_id, credential_type) REFERENCES credentials(id, user_id, type) ON DELETE CASCADE",
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
            // The one composite on this schema that is not over the credential's three columns, and the
            // composite is why the table can be trusted at all. session_tokens is EXEMPT from row-level
            // security — a presented handle is looked up before the request has said who it is — so the
            // user_id on this row is the one the request then adopts, with no policy underneath
            // comparing it to anything. Shortened to session_id alone, a token naming another person's
            // session would be storable, and presenting it would sign the caller into that account.
            // Referencing sessions(id, user_id) through AK_sessions_id_user_id is what makes the two
            // columns agree by construction rather than by a rule somebody remembers.
            // Cascade for the reason the credentials -> users row above records, and for one of its
            // own: a token whose session is gone names nothing, so the row it would leave behind is a
            // handle resolving to a dangling id. Note what the cascade does NOT do — removing a token
            // row is not revocation, and a path that deleted the token instead of stamping
            // revoked_at_utc would sign the browser out while leaving nothing that says when access
            // ended.
            "session_tokens.FK_session_tokens_sessions: FOREIGN KEY (session_id, user_id) REFERENCES sessions(id, user_id) ON DELETE CASCADE",
            "transactions.FK_transactions_accounts_account_id_budget_id: FOREIGN KEY (account_id, budget_id) REFERENCES accounts(id, budget_id) ON DELETE RESTRICT",
            "transactions.FK_transactions_budgets_budget_id: FOREIGN KEY (budget_id) REFERENCES budgets(id) ON DELETE RESTRICT",
            "transactions.FK_transactions_categories_category_id_budget_id: FOREIGN KEY (category_id, budget_id) REFERENCES categories(id, budget_id) ON DELETE RESTRICT",
            "transactions.FK_transactions_payees_payee_id_budget_id: FOREIGN KEY (payee_id, budget_id) REFERENCES payees(id, budget_id) ON DELETE RESTRICT",
            // The fourth composite over the same three credential columns, and the owner half is the
            // half that decides who is handed the account's wrapped keys: user_isolation on this table
            // reads user_id and never looks at the credential, so shortened to credential_id alone the
            // database would accept one account's envelopes filed under another account's owner id.
            // The credential_type half is what keeps CK_wrapped_account_keys_credential_type honest —
            // that check can only read the copy on this row, so without this reference the copy is free
            // to say 'passkey' over a federated credential, and a federated credential has no PRF
            // equivalent: the row would be two envelopes nothing in the world can open, presented as a
            // way back into the account.
            // Cascade, and deliberately not Restrict, for the reason the credentials -> users row above
            // records and for one of its own: key bookkeeping must never hold up a person's erasure, and
            // a factor that no longer exists cannot derive the key-encryption key that opens these
            // envelopes, so the row it would leave behind is ciphertext nothing can ever read.
            "wrapped_account_keys.FK_wrapped_account_keys_credentials: FOREIGN KEY (credential_id, user_id, credential_type) REFERENCES credentials(id, user_id, type) ON DELETE CASCADE",
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
            // Redundant as a uniqueness claim — id is already the primary key of sessions, so
            // (id, user_id) cannot repeat — and that is not what it is for. It is the referencable
            // target the session_tokens composite foreign key needs: PostgreSQL accepts a foreign key
            // only against a unique constraint covering exactly the referenced columns. Dropping it
            // therefore reads as removing a duplicate index while it is the thing that stops a stored
            // handle naming a session belonging to somebody else — and session_tokens is exempt from
            // row-level security, so nothing underneath would notice.
            """CREATE UNIQUE INDEX "AK_sessions_id_user_id" ON public.sessions USING btree (id, user_id)""",
            // THE SAME RULE — one name per budget — OVER A DIFFERENT COLUMN, and the column is the
            // whole of what moved. `name` is a sealed narrative field now, and every seal draws a
            // fresh nonce, so two rows holding one name hold different bytes: a unique index left on
            // `name` would still exist, still be rendered here, still be unique, and refuse nothing.
            // name_key is deterministic under the account's index key, so equality of names comes back
            // as equality of digests and this index refuses the second one.
            //
            // The identifier follows EF's own convention rather than being pinned by hand, which is
            // why the tail is _name_key. AccountRepository matches PostgresException.ConstraintName
            // against AccountConfiguration.NameIndexName to decide whether a 23505 is the collision it
            // models, so the two spellings have to agree; a name only the schema knows about costs
            // that 400 outright.
            //
            // Two things this index cannot do that its predecessor could. It cannot fold case — that
            // moved into the normalisation the client applies before it computes the HMAC, which no
            // constraint on this side can be written to, and which is why accounts.name leaves the
            // collation snapshot below. And nobody with the database open can read which two accounts
            // collided: that is a question only a browser holding the account's keys can answer.
            """CREATE UNIQUE INDEX "IX_accounts_budget_id_name_key" ON public.accounts USING btree (budget_id, name_key)""",
            // The trailing clause is the rule, not rendering noise. Without it PostgreSQL counts
            // every NULL as distinct, so both provisioning racers insert an unnamed (user_id, NULL)
            // budget — the default budget is exactly the one with no name — and a user silently ends
            // up owning two; with it, one racer has to lose on 23505. And unlike a column collation,
            // which belongs to the column and needs the collation snapshot below to catch it,
            // pg_get_indexdef does render this clause: drop it and this line moves, so this snapshot
            // catches it alone.
            // This line is byte-identical across budgets.name becoming bytea, and that was checked
            // rather than assumed. Neither half moved: pg_get_indexdef prints an opclass only when it
            // is not the type's default, so text_ops giving way to bytea_ops renders as nothing, and
            // the case_insensitive collation was never rendered here either — it belonged to the
            // column, which is the sentence above. What that leaves is exactly the NULLS NOT DISTINCT
            // half, which is the half that was ever load-bearing here: two rows holding the same name
            // now hold different bytes, since every seal draws a fresh nonce, so this index stopped
            // refusing duplicate names and never stopped refusing a second unnamed budget.
            """CREATE UNIQUE INDEX "IX_budgets_user_id_name" ON public.budgets USING btree (user_id, name) NULLS NOT DISTINCT""",
            // categories re-keyed onto its blind index in this slice, the fourth table to make the move.
            // THE NAME OF THE INDEX CHANGED TOO - IX_categories_budget_id_name became
            // IX_categories_budget_id_name_key - and comparing the WHOLE indexdef rather than searching
            // it for a substring is what makes that visible: an index still declared over (budget_id,
            // name) while wearing the new NAME would satisfy any Contains("name_key") check, because the
            // index's own name contains the substring.
            """CREATE UNIQUE INDEX "IX_categories_budget_id_name_key" ON public.categories USING btree (budget_id, name_key)""",
            // The same rule — one group name per budget — over the blind index, on the accounts terms
            // the two lines above already argue and which are not restated a third time. The value
            // moved and the identifier did not: CategoryGroupConfiguration.NameIndexName still says
            // "Name" in C# because refusing a name this budget already holds is what the index is FOR,
            // while the tail here is _name_key because that is the column it is over. Pointing the
            // index back at `name` leaves a unique index that refuses nothing — every seal draws a
            // fresh nonce — while still existing, still being rendered here, and still passing any
            // check that only asked whether it was unique.
            """CREATE UNIQUE INDEX "IX_category_groups_budget_id_name_key" ON public.category_groups USING btree (budget_id, name_key)""",
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
            // The same rule shape as the line above over the other self-contained credential type: one
            // issued set of recovery codes per account. Nothing else on the row refuses a second — the
            // provider-identity index names federated rows only, and every recovery-codes row carries
            // (NULL, NULL) — and two sets are two remaining-counts with nothing saying which one binds.
            //
            // The WHERE is the load-bearing half of this line, and it is the half that reads like
            // rendering noise. Drop the filter and the index still enforces one set per account, so
            // UserRepositoryTests' Database_RejectsASecondRecoveryCodesCredentialForTheSameUser stays
            // green while an unfiltered unique index over user_id has quietly started refusing an
            // account a SECOND PASSKEY, which FR-043 allows and which AppRoleGrantsTests seeds.
            // Database_AcceptsTwoPasskeyCredentialsForTheSameUser is the only control in the suite that
            // would notice, and this snapshot is the only place the filter itself is visible —
            // pg_get_indexdef renders it, so removing it moves this line and nothing about the name
            // changes. A green one-set-per-account test is therefore not evidence the filter survived.
            """CREATE UNIQUE INDEX "IX_credentials_user_id_recovery_codes" ON public.credentials USING btree (user_id) WHERE ((type)::text = 'recovery_codes'::text)""",
            // One account per WebAuthn credential handle. Unlike the two credentials indexes above
            // this one carries no WHERE, and it must not grow one: the handle an assertion arrives
            // under is the whole of what the lookup has to go on, so a second row under the same
            // handle would make "whose key is this" ambiguous before anybody is authenticated.
            """CREATE UNIQUE INDEX "IX_passkey_public_keys_webauthn_credential_id" ON public.passkey_public_keys USING btree (webauthn_credential_id)""",
            // Over the BLIND INDEX and not over the name, the same move accounts made and for the same
            // reason: payees.name is an AEAD envelope drawn under a fresh nonce, so two rows a client
            // sealed from one word hold different bytes and an index over that column could not see a
            // duplicate at all. Uniqueness — one counterparty per budget, which on this table is the
            // whole of deduplication rather than a convenience — survived by moving to a column the
            // database cannot interpret and can still compare for equality. The case folding did not
            // survive here: it moved into the normalisation the client applies before it computes the
            // HMAC, and nothing on this side can check that it happened.
            """CREATE UNIQUE INDEX "IX_payees_budget_id_name_key" ON public.payees USING btree (budget_id, name_key)""",
            // One email, one account. The index is only half the rule: users.email carries
            // case_insensitive, which the collation snapshot below pins, and pg_get_indexdef does
            // not render it here.
            """CREATE UNIQUE INDEX "IX_users_email" ON public.users USING btree (email)""",
            // A challenge is spent by being looked up under its own bytes, so uniqueness here is the
            // rule that keeps a nonce from being redeemable twice through two rows.
            """CREATE UNIQUE INDEX "IX_webauthn_challenges_challenge" ON public.webauthn_challenges USING btree (challenge)""",
            """CREATE UNIQUE INDEX "PK___EFMigrationsHistory" ON public."__EFMigrationsHistory" USING btree ("MigrationId")""",
            """CREATE UNIQUE INDEX "PK_accounts" ON public.accounts USING btree (id)""",
            """CREATE UNIQUE INDEX "PK_budgets" ON public.budgets USING btree (id)""",
            """CREATE UNIQUE INDEX "PK_categories" ON public.categories USING btree (id)""",
            """CREATE UNIQUE INDEX "PK_category_groups" ON public.category_groups USING btree (id)""",
            """CREATE UNIQUE INDEX "PK_credentials" ON public.credentials USING btree (id)""",
            """CREATE UNIQUE INDEX "PK_currencies" ON public.currencies USING btree (code)""",
            // Both keyed on credential_id alone, which is what makes each table hold at most one row
            // per credential: a key that could be joined by a second row, or a counter that could,
            // would leave the ceremony with two answers and no rule saying which one binds.
            """CREATE UNIQUE INDEX "PK_passkey_public_keys" ON public.passkey_public_keys USING btree (credential_id)""",
            """CREATE UNIQUE INDEX "PK_passkey_signature_counters" ON public.passkey_signature_counters USING btree (credential_id)""",
            """CREATE UNIQUE INDEX "PK_payees" ON public.payees USING btree (id)""",
            // The one primary key in this set that is not a surrogate id, and the difference is the
            // rule. A redemption request arrives carrying a code and nothing else, so the SHA-256 of
            // the verifier is the only handle the lookup has; making it the key is also what makes two
            // codes hashing alike unstorable rather than a duplicate nothing would notice. A surrogate
            // id added beside it would demote this to an ordinary unique index and quietly permit the
            // second row, so this line moving is not a rename to wave through.
            """CREATE UNIQUE INDEX "PK_recovery_code_hashes" ON public.recovery_code_hashes USING btree (verifier_hash)""",
            """CREATE UNIQUE INDEX "PK_sessions" ON public.sessions USING btree (id)""",
            // The second primary key in this set that is not a surrogate id, and it is keyed on the
            // digest for the reason PK_recovery_code_hashes is: the request arrives carrying a token
            // and nothing else, so the hash is the only handle the lookup has. Being the key is also
            // what makes two sessions sharing a token unstorable rather than a duplicate nothing would
            // notice — and a duplicate here is two accounts reachable by one cookie, resolved by
            // whichever row the read happened to return. A surrogate id added beside it would demote
            // this to an ordinary unique index and quietly permit that second row.
            """CREATE UNIQUE INDEX "PK_session_tokens" ON public.session_tokens USING btree (token_hash)""",
            """CREATE UNIQUE INDEX "PK_transactions" ON public.transactions USING btree (id)""",
            """CREATE UNIQUE INDEX "PK_users" ON public.users USING btree (id)""",
            """CREATE UNIQUE INDEX "PK_webauthn_challenges" ON public.webauthn_challenges USING btree (id)""",
            // Keyed on factor_id, and NOT on credential_id the way the two passkey tables above are keyed
            // — the difference between this table and those is a set of recovery codes. A passkey is one
            // factor under one credential, so credential_id would have served; a set is TEN separate
            // secrets under one credentials row, and the client derives a key-encryption key from each
            // CODE. Ten codes are ten key-encryption keys and ten pairs of envelopes, so keying on the
            // credential stored one of them and left nine codes opening nothing — a person redeems any
            // one of them, is handed a session, and nine times out of ten still cannot unlock the
            // account. The factor is the identity of the row; credential_id is an ordinary, non-unique
            // column carrying no key of its own.
            //
            // Being the key is also the whole of this column's uniqueness, and there is deliberately no
            // second unique index over factor_id beside it. The value is minted by the CLIENT, which is
            // what makes uniqueness a rule here rather than a lookup that happens to hold: nothing else
            // stops two rows carrying the same one, and it is the associated data both of a row's
            // envelopes were sealed with, so a shared factor id would let one factor's keys be opened
            // against another's. A repository filters a 23505 on the constraint NAME to tell that
            // collision from every other unique violation the same statement can raise, and a second
            // constraint saying the same thing would be a second name the same duplicate could arrive
            // under. Unique across the whole table rather than per user on purpose: scoping it to an
            // owner would make the duplicate storable and leave the associated data ambiguous exactly
            // where it is trusted. This table's two non-unique indexes — over user_id, and over the
            // foreign key's three columns — are out of scope for this query rather than missing from
            // this list, and the second of them stopped being incidental with this line: the foreign
            // key's columns are no longer led by the primary key at all.
            """CREATE UNIQUE INDEX "PK_wrapped_account_keys" ON public.wrapped_account_keys USING btree (factor_id)""",
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
            // An EQUALITY, and this is the one narrative-adjacent bound in this file that is a width
            // rather than a band. HMAC-SHA-256 emits exactly 32 bytes and nothing truncates in
            // between, so there is no range of legal sizes and a ceiling would admit a short digest in
            // silence. It bounds what MAY BE STORED and restates nothing this server computed: the
            // digest is taken under an index key that lives in a browser, so this side cannot
            // recompute it, cannot check it against the name beside it, and cannot tell a correct 32
            // bytes from a fabricated 32 bytes. The width is the whole of the defence here.
            //
            // pg_get_constraintdef renders the integer bare rather than casting it, unlike the numeric
            // comparison on opening_balance two lines down: length() returns integer and 32 is already
            // one, so there is no cast to print. "Correcting" this line to carry one is how this test
            // starts failing for no reason.
            """CK_accounts_name_key_length: accounts CHECK ((length(name_key) = 32))""",
            // The account name's band, rendered as two ANDed comparisons the way pg_get_constraintdef
            // renders every BETWEEN. Same two constants and same argument as CK_budgets_name_length
            // below — 29 is CiphertextEnvelope.MinimumLength, 1024 is NarrativeFieldLimits.NameBytes,
            // both rendered from those constants in the configuration.
            //
            // Unlike its budgets twin this one meets no NULL: accounts.name is NOT NULL, so the "a
            // CHECK is satisfied by NULL" sentence that carries the nameless default budget has no
            // work to do here. What that buys is a floor that actually refuses something — the
            // shortest legal envelope — at the only level left that can refuse anything about a name.
            // It is still a floor on ENVELOPE bytes and says nothing about the text underneath.
            """CK_accounts_name_length: accounts CHECK (((length(name) >= 29) AND (length(name) <= 1024)))""",
            // The one envelope version this deployment implements, over the second narrative column to
            // arrive. substring rather than get_byte for the reason CK_budgets_name_version states
            // below at length, and on this table the accident that reason describes is visible: the
            // three CK_accounts_name* constraints sort key_length, length, version, so a zero-length
            // name answers 23514 only because "length" sorts before "version". substring does not
            // depend on that — it answers a zero-length bytea for a zero-length input, so the check is
            // false rather than fatal under every ordering.
            //
            // SUBSTRING comes back upper-cased and in the SQL-standard FROM/FOR spelling, because
            // PostgreSQL 14 and later print substring as SQL syntax rather than as a function call.
            // The configuration writes it lower-case with the same keywords.
            """CK_accounts_name_version: accounts CHECK ((SUBSTRING(name FROM 1 FOR 1) = '\x01'::bytea))""",
            """CK_accounts_opening_balance: accounts CHECK ((abs(opening_balance) <= (1000000000)::numeric))""",
            """CK_accounts_type: accounts CHECK (((type)::text = ANY ((ARRAY['Checking'::character varying, 'Savings'::character varying, 'Cash'::character varying, 'CreditCard'::character varying])::text[])))""",
            // The budget name's length band, rendered the way pg_get_constraintdef renders every
            // BETWEEN: two ANDed comparisons rather than the word the configuration writes. A band and
            // not a width, unlike the wrapped-key pair at the bottom of this list — AES-GCM ciphertext
            // is exactly as long as its plaintext, and a name is as long as whatever somebody typed,
            // so only the ends are decidable. 29 is CiphertextEnvelope.MinimumLength, the shortest the
            // framing can be over an empty plaintext; 1024 is NarrativeFieldLimits.NameBytes. Both are
            // rendered from those constants in the configuration, so a constant that moves moves this
            // line, which is the wanted failure.
            //
            // Neither this nor the version check below says "or null", and that is the rule rather
            // than an omission: a CHECK is satisfied by NULL, so the nameless budget — the default
            // budget, and the common row — passes both with no arm written for it.
            """CK_budgets_name_length: budgets CHECK (((length(name) >= 29) AND (length(name) <= 1024)))""",
            // The one envelope version this deployment implements, over the narrative column rather
            // than over a wrapped key — and spelled with substring rather than with the get_byte the
            // two wrapped-key version checks at the bottom of this list use. That is not a style
            // difference. get_byte RAISES 2202E on a zero-length bytea instead of answering false: no
            // constraint name, no failing row, and nothing a repository filtering PostgresException on
            // SqlState 23514 can see. The length check above DOES rescue it, and reading that rescue as
            // a guarantee is the trap: which of a column's CHECKs fires first is decided by the
            // constraint NAME and not by declaration order or by left-to-right evaluation inside an AND,
            // so "length" sorting before "version" is what keeps a zero-length name answering 23514 —
            // and that is not a decision anybody took. The wrapped-key pair at the bottom of this list
            // is shielded by the same accident; it is recorded in the hardening backlog and belongs to
            // its own change, so do not fix it by editing these lines.
            //
            // What this snapshot can and cannot see is the point of saying it here. It compares
            // RENDERED TEXT, so it moves when the spelling moves and it is blind to which constraint
            // fires and to what SQLSTATE a zero-length name produces. But so is every other test:
            // measured on postgres:17.10 over a table carrying a length band beside a get_byte-spelled
            // version check, the band answers 23514 first and no probe reaches the raise, so a narrative
            // column going back to get_byte reddens THIS LITERAL AND NOTHING ELSE IN THE REPOSITORY —
            // not because the snapshot is weak, but because the wrong predicate is unreachable through
            // the schema as declared. A literal moving is a paste unless somebody reads why, and this
            // paragraph is the whole of what stands between the red line and the paste.
            //
            // SUBSTRING comes back upper-cased and in the SQL-standard FROM/FOR spelling because
            // PostgreSQL 14 and later print substring as SQL syntax rather than as a function call.
            // The configuration writes it lower-case with the same keywords; "correcting" this line to
            // match the configuration is how this test starts failing for no reason.
            """CK_budgets_name_version: budgets CHECK ((SUBSTRING(name FROM 1 FOR 1) = '\x01'::bytea))""",
            // The five categories arms, in the alphabetical order PostgreSQL reports them and this
            // snapshot preserves. description_length sorts FIRST of the six on this table, which is the
            // fact that decides which constraint a row breaking several rules at once is reported
            // under - and it is a fact about the NAMES, not about declaration order.
            """CK_categories_description_length: categories CHECK (((length(description) >= 29) AND (length(description) <= 2560)))""",
            """CK_categories_description_version: categories CHECK ((SUBSTRING(description FROM 1 FOR 1) = '\x01'::bytea))""",
            """CK_categories_name_key_length: categories CHECK ((length(name_key) = 32))""",
            """CK_categories_name_length: categories CHECK (((length(name) >= 29) AND (length(name) <= 1024)))""",
            """CK_categories_name_version: categories CHECK ((SUBSTRING(name FROM 1 FOR 1) = '\x01'::bytea))""",
            """CK_categories_position: categories CHECK (("position" >= 0))""",
            // THE FIRST SEALED DESCRIPTION COLUMN IN THE PRODUCT. Same band shape as every narrative
            // length check above, a DIFFERENT ceiling — NarrativeFieldLimits.DescriptionBytes rather
            // than NameBytes — because those are two caps over field CLASSES and not two guesses at one
            // number. Rendered as two comparisons for the reason the accounts and payees bands are:
            // PostgreSQL expands BETWEEN.
            //
            // Nothing says "or null" and that is the rule rather than a gap: a CHECK is satisfied by
            // NULL, so a group filing no note passes both description arms vacuously. Measured on
            // postgres:17.10 over exactly these six constraints — NULL accepted, 29 accepted, 2560
            // accepted, 2561 refused under this name, present-and-zero-length refused under this name
            // on INSERT and on UPDATE alike, and clearing back to NULL by UPDATE accepted.
            """CK_category_groups_description_length: category_groups CHECK (((length(description) >= 29) AND (length(description) <= 2560)))""",
            // SUBSTRING again, AND THE NULLABLE COLUMN IS WHERE A READER GETS THIS BACKWARDS. The
            // instinct is that a nullable column escapes get_byte's zero-length trap. Measured on
            // postgres:17.10: get_byte(NULL::bytea, 0) answers NULL and does not raise, so a
            // get_byte-spelled version check here would be green on every row holding a NULL and every
            // row holding a valid envelope, and would bite only on the PRESENT, ZERO-LENGTH value —
            // which is exactly what a client sending an empty bytea produces and the one value this
            // check exists for. get_byte(''::bytea, 0) still raises 2202E from inside a CHECK on a
            // nullable column, with no constraint name and no failing row.
            //
            // Nullability therefore makes the wrong spelling QUIETER rather than safer — and it is
            // quieter still than that, which is the correction. NO CASE IN THIS REPOSITORY CATCHES IT:
            // description_length sorts ahead of description_version, so on the one value the wrong
            // spelling bites on, the length floor answers 23514 first and the version predicate is never
            // evaluated. Measured on postgres:17.10 over a table carrying all six shipped constraints
            // with both version checks spelled get_byte, nothing produced 2202E. This line is the pin,
            // and review is the rest of it.
            """CK_category_groups_description_version: category_groups CHECK ((SUBSTRING(description FROM 1 FOR 1) = '\x01'::bytea))""",
            // The blind index's width, an equality for the reason CK_accounts_name_key_length and
            // CK_payees_name_key_length are equalities. No version arm: a keyed digest has no framing.
            """CK_category_groups_name_key_length: category_groups CHECK ((length(name_key) = 32))""",
            """CK_category_groups_name_length: category_groups CHECK (((length(name) >= 29) AND (length(name) <= 1024)))""",
            // THE THIRD TABLE MAKING THE ALPHABETICAL ACCIDENT CONCRETE, and the first where it crosses
            // two columns. Measured on postgres:17.10 over exactly these six: the sort is
            // description_length, description_version, name_key_length, name_length, name_version,
            // position — so a row breaking a NAME rule and a DESCRIPTION rule is reported under the
            // description, and one breaking a description rule and the position rule is reported under
            // the description too. Nothing here depends on that, because every narrative predicate on
            // this table is spelled with SUBSTRING and none can raise. What it forbids is a case
            // asserting a constraint NAME for a row carrying more than one violation.
            """CK_category_groups_name_version: category_groups CHECK ((SUBSTRING(name FROM 1 FOR 1) = '\x01'::bytea))""",
            """CK_category_groups_position: category_groups CHECK (("position" >= 0))""",
            // Bounds the issuer vocabulary the way CK_credentials_type below bounds the type
            // vocabulary, and without it 'Google' and 'google' are two accounts for one person.
            // PostgreSQL renders a single-element IN as '=' rather than as = ANY (ARRAY[...]), so
            // this deliberately does not read like the line beneath it — same idiom in the
            // configuration, different rendering in the catalog. "Fixing" it to look like a list is
            // how this test starts failing for no reason.
            """CK_credentials_provider: credentials CHECK (((provider IS NULL) OR ((provider)::text = 'google'::text)))""",
            // Three spellings, not two, since a set of recovery codes became a credential in its own
            // right: one credentials row stands for the whole issued set, which is what puts recovery
            // material inside the cascade an erasure already runs through and makes revoking the set
            // one delete. Widening a bounded vocabulary is the edit this snapshot exists to make
            // somebody argue for, so the argument is written here rather than inferred from the fact
            // that the line moved.
            """CK_credentials_type: credentials CHECK (((type)::text = ANY ((ARRAY['passkey'::character varying, 'federated'::character varying, 'recovery_codes'::character varying])::text[])))""",
            // The shape check is the reason `type` can be a varchar with a CHECK instead of a native
            // enum: it is what makes "a credential is exactly one type" a rule the database holds
            // rather than a convention the application is trusted to keep. The length test on the
            // federated arm is not redundant with the null test beside it: length(null) is null and a
            // check evaluating to null is satisfied, so neither test covers the other.
            // The recovery_codes arm's predicate is identical to the passkey arm's, and that is
            // recorded rather than collapsed: this constraint no longer discriminates between those
            // two types, because both are self-contained credentials with no issuer and no provider
            // subject. What tells them apart is CK_credentials_type above bounding the vocabulary and
            // each child table's composite foreign key comparing its own credential_type copy against
            // this column. Folding the two arms into one would say the same thing in less space and
            // lose the record of which types the schema has considered.
            """CK_credentials_type_shape: credentials CHECK (((((type)::text = 'federated'::text) AND (provider IS NOT NULL) AND (subject IS NOT NULL) AND (length((subject)::text) > 0)) OR (((type)::text = 'passkey'::text) AND (provider IS NULL) AND (subject IS NULL)) OR (((type)::text = 'recovery_codes'::text) AND (provider IS NULL) AND (subject IS NULL))))""",
            """CK_currencies_code: currencies CHECK (((code)::text ~ '^[A-Z]{3}$'::text))""",
            """CK_currencies_minor_unit: currencies CHECK (((minor_unit >= 0) AND (minor_unit <= 4)))""",
            // The two COSE algorithms the verifier accepts, bounded here rather than trusted to the
            // writer. A row naming any other algorithm is one no verification path can read back, so
            // it would be a credential that authenticates nobody. Negative literals render with the
            // sign inside the quotes and an explicit ::integer cast — that is PostgreSQL's rendering
            // of the list, not a typo to tidy into (-7, -257).
            """CK_passkey_public_keys_cose_algorithm: passkey_public_keys CHECK ((cose_algorithm = ANY (ARRAY['-7'::integer, '-257'::integer])))""",
            // The copy of the credential's type that the composite foreign key ties back to its
            // source, pinned to the one value this table may hold. Without it the column could agree
            // with a federated credential and put key material on a row nothing verifies.
            """CK_passkey_public_keys_credential_type: passkey_public_keys CHECK (((credential_type)::text = 'passkey'::text))""",
            """CK_passkey_public_keys_public_key_length: passkey_public_keys CHECK (((length(public_key_cose) >= 1) AND (length(public_key_cose) <= 1024)))""",
            """CK_passkey_public_keys_webauthn_credential_id_length: passkey_public_keys CHECK (((length(webauthn_credential_id) >= 16) AND (length(webauthn_credential_id) <= 1023)))""",
            // The same type pin as on the key table, and it is owed separately: the two tables carry
            // their own copy of the column, so one constraint cannot cover both.
            """CK_passkey_signature_counters_credential_type: passkey_signature_counters CHECK (((credential_type)::text = 'passkey'::text))""",
            // The unsigned 32-bit range a WebAuthn signature counter is defined over. The upper bound
            // renders as a quoted ::bigint literal because the column is bigint and the value exceeds
            // integer — matching the lower bound's bare 0 would be the wrong rendering.
            """CK_passkey_signature_counters_value: passkey_signature_counters CHECK (((signature_counter >= 0) AND (signature_counter <= '4294967295'::bigint)))""",
            // The payee blind index's width, an EQUALITY for the reason CK_accounts_name_key_length
            // states: HMAC-SHA-256 emits exactly 32 bytes, nothing truncates in between, and a ceiling
            // would admit a short digest in silence. The width is the whole of the defence — the digest
            // is taken under an index key that lives in a browser, so this side cannot recompute it,
            // cannot check it against the name beside it, and cannot tell a correct 32 bytes from a
            // fabricated 32 bytes.
            """CK_payees_name_key_length: payees CHECK ((length(name_key) = 32))""",
            // The payee name's band, same two constants and same argument as its accounts twin: 29 is
            // CiphertextEnvelope.MinimumLength, 1024 is NarrativeFieldLimits.NameBytes, both rendered
            // from those constants in the configuration. Like accounts and unlike budgets it meets no
            // NULL, because payees.name is NOT NULL.
            //
            // It is a floor on ENVELOPE bytes and says nothing about the text underneath, which is
            // load-bearing on this table specifically: the 200-character ceiling and the "a name is not
            // just spaces" rule that used to live in Payee.ValidateOrThrow were SURRENDERED, not moved
            // here. This line cannot count characters and no line here can.
            """CK_payees_name_length: payees CHECK (((length(name) >= 29) AND (length(name) <= 1024)))""",
            // The one envelope version this deployment implements, over the third sealed column to
            // arrive — budgets.name, accounts.name, payees.name, in that order and no others.
            // substring rather than get_byte for the reason CK_budgets_name_version states at
            // length below: get_byte raises 2202E on a zero-length bytea instead of answering false,
            // which carries no constraint name and no failing row. The three CK_payees_name*
            // constraints sort key_length, length, version, so a zero-length name would answer 23514
            // from a length check under this ordering — but substring is what makes that irrelevant
            // rather than lucky, because no predicate here can raise under any ordering.
            """CK_payees_name_version: payees CHECK ((SUBSTRING(name FROM 1 FOR 1) = '\x01'::bytea))""",
            // The third copy of a credential's type pinned to the one value its table may hold, owed
            // separately for the reason the two above are owed separately: each table carries its own
            // column, so one constraint cannot cover the others. Here the pin is a single value rather
            // than a vocabulary, and that is the stronger claim — this table exists only for recovery
            // codes, so 'passkey' is not a value with a different meaning, it is a row that should not
            // exist.
            """CK_recovery_code_hashes_credential_type: recovery_code_hashes CHECK (((credential_type)::text = 'recovery_codes'::text))""",
            // Equality, not a range, and the same honesty CK_webauthn_challenges_length rests on: the
            // value is a SHA-256 computed server-side, so it is 32 bytes or it is not a hash this table
            // can have produced. A range would accept the bug rather than stop it, and the row it
            // accepted would be a code nothing can ever redeem.
            """CK_recovery_code_hashes_verifier_hash_length: recovery_code_hashes CHECK ((length(verifier_hash) = 32))""",
            // The kind vocabulary, and the lowercase spelling is the whole of it: the converter stores
            // these two strings, so a HasConversion<string>() writing PascalCase members would be
            // refused here rather than stored.
            """CK_sessions_kind: sessions CHECK (((kind)::text = ANY ((ARRAY['full'::character varying, 'locked'::character varying])::text[])))""",
            // The rule that a full session is opened by a passkey or by a set of recovery codes and by
            // nothing else, held where it rejects rather than where it is merely performed:
            // Session.Establish derives kind from the credential, but GRANT INSERT on this table covers
            // the whole column list, so without this line a federated credential paired with
            // kind = 'full' is a row the application role can write. An equality rather than an
            // implication, so it refuses both directions — a passkey opening a locked session moves
            // this line too.
            //
            // Two spellings on the full side, not one, because a set of recovery codes is a key factor:
            // the account's content and index keys are wrapped under the set, so the code the holder
            // typed unwraps them. Federated is the only type that cannot hold the account's keys, which
            // is why it is the only one left on the locked side.
            //
            // The full side is ENUMERATED and it stays enumerated. The prettier inversion —
            //   (kind = 'locked') = (credential_type = 'federated')
            // says the same thing about every row this schema can hold today, renders shorter, and is
            // what a later reader will propose against this line's growing IN list. It fails OPEN: a
            // fourth credential type is not federated, so it satisfies the right-hand side and is
            // granted a full session by default with nobody having decided that. This form fails
            // closed — an unenumerated type gets no full session until somebody adds it here.
            //
            // What this pin does and does not do about that, stated precisely, because the difference
            // is the whole reason the paragraph is this long. Rewriting the configuration to the
            // inversion DOES turn this test red: the rendered text changes, so the line moves. But it
            // moves the way a rendering change moves it, and no assertion here can tell "the rule now
            // means something different" from "PostgreSQL spells it differently" — the two are the
            // same event to a string comparison. So the failure arrives as a literal to update, the
            // update is one paste, and both forms are green on every row the schema can hold until a
            // fourth credential type exists. This paragraph and its twin on SessionConfiguration are
            // what a person hits between the red line and the paste; they are the enforcement, and the
            // string comparison is only what makes somebody read them.
            """CK_sessions_kind_matches_credential: sessions CHECK ((((kind)::text = 'full'::text) = ((credential_type)::text = ANY ((ARRAY['passkey'::character varying, 'recovery_codes'::character varying])::text[]))))""",
            // Separate from the vocabulary check rather than ANDed with it, so a row that breaches one
            // reports exactly the name that describes what is wrong with it.
            """CK_sessions_lifetime: sessions CHECK ((expires_at_utc > created_at_utc))""",
            // Equality rather than a range, the same honesty CK_recovery_code_hashes_verifier_hash_length
            // rests on: the value is a SHA-256 computed server-side, so it is 32 bytes or it is not a
            // digest this table can have produced.
            //
            // What it deliberately cannot see, because a reader will credit it with more: it watches the
            // DIGEST, which is 32 bytes whatever went into it, so a short token hashes to a perfectly
            // well-formed row the database has no way to tell from a real one. The token's own width is
            // SessionToken.TokenLength and SessionToken.For is the only place a bad one stops. This line
            // is not the guard against a weak handle; it is the guard against the column holding
            // something that is not a digest.
            """CK_session_tokens_token_hash_length: session_tokens CHECK ((length(token_hash) = 32))""",
            // transactions takes TWO arms and not five: it is the first sealed table in the product with
            // no name column, so there is no blind index and no name_key for a width check to be about.
            // "amount" sorts ahead of both, which is the ordering novelty TransactionConfiguration
            // records - a description probe written beside an out-of-range amount is reported under
            // CK_transactions_amount and passes for the wrong reason.
            """CK_transactions_amount: transactions CHECK ((abs(amount) <= (1000000000)::numeric))""",
            """CK_transactions_description_length: transactions CHECK (((length(description) >= 29) AND (length(description) <= 2560)))""",
            """CK_transactions_description_version: transactions CHECK ((SUBSTRING(description FROM 1 FOR 1) = '\x01'::bytea))""",
            // The four ceremonies a challenge can belong to. A nonce issued for one and spent on
            // another is the cross-ceremony replay this vocabulary refuses at the column.
            //
            // 'account_registration' is a fourth POOL, not a qualifier on 'registration', and the two
            // are not interchangeable in either direction: 'registration' is minted for somebody
            // already signed in who is adding a device to an account that exists, while this one is
            // minted for a caller holding a provider token and no account at all. A later commit
            // derives the new account's id from a nonce in this pool, so sharing 'registration' would
            // let an add-a-device nonce name a brand-new account. Snake case for the reason
            // credentials.type spells recovery_codes: it is a two-word member, and camel case would
            // produce a token that reads like a third thing.
            """CK_webauthn_challenges_ceremony: webauthn_challenges CHECK (((ceremony)::text = ANY ((ARRAY['registration'::character varying, 'authentication'::character varying, 'reauthentication'::character varying, 'account_registration'::character varying])::text[])))""",
            // Exactly 32 bytes, not a range: a challenge shorter than the issuer emits is one the
            // issuer never emitted, so equality is the honest rule and a minimum would accept it.
            """CK_webauthn_challenges_length: webauthn_challenges CHECK ((length(challenge) = 32))""",
            // The same shape as CK_sessions_lifetime above, owed for the same reason: a row whose
            // expiry is at or before its creation was never live for an instant.
            """CK_webauthn_challenges_lifetime: webauthn_challenges CHECK ((expires_at_utc > created_at_utc))""",
            // The fourth copy of a credential's type pinned to what its own table may hold, and the only
            // one of the four that is a vocabulary of TWO rather than a single value. That is the whole
            // difference between this table and the passkey tables above: those hold rows for exactly one
            // credential type, while account keys are wrapped under whichever factors have a
            // key-encryption key — a passkey through its PRF output, a set of recovery codes through the
            // code the holder still has. 'federated' is the spelling that must not appear: OAuth has no
            // PRF equivalent, so a row filed against the federated credential would be two envelopes
            // nothing in the world can open, presented as a way back into the account.
            """CK_wrapped_account_keys_credential_type: wrapped_account_keys CHECK (((credential_type)::text = ANY ((ARRAY['passkey'::character varying, 'recovery_codes'::character varying])::text[])))""",
            // Exactly 61 bytes, not a range, and stated once per column. AES-GCM ciphertext is the length
            // of its plaintext and the plaintext is a 32-byte key, so an envelope over a wrapped account
            // key has one legal size — 1 version + 12 nonce + 32 ciphertext + 16 tag — and both sides of
            // the bound are refused. Padding or truncating either one would store a well-formed row
            // holding an envelope whose tag cannot verify, and the account would look registered until
            // the day somebody needed the keys. Per column rather than once over both, so a violation
            // names which envelope was malformed; nothing else here can tell the two apart.
            """CK_wrapped_account_keys_wrapped_content_key_length: wrapped_account_keys CHECK ((length(wrapped_content_key) = 61))""",
            // The one envelope version this deployment implements (IFR-007): AES-256-GCM, 96-bit nonce,
            // 128-bit tag. Bounded here rather than left to the client because the successor does not
            // exist — a row carrying version 2 is a client claiming a contract nothing has implemented,
            // and storing it would file bytes no version of this system can interpret.
            """CK_wrapped_account_keys_wrapped_content_key_version: wrapped_account_keys CHECK ((get_byte(wrapped_content_key, 0) = 1))""",
            """CK_wrapped_account_keys_wrapped_index_key_length: wrapped_account_keys CHECK ((length(wrapped_index_key) = 61))""",
            """CK_wrapped_account_keys_wrapped_index_key_version: wrapped_account_keys CHECK ((get_byte(wrapped_index_key, 0) = 1))""",
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
        // rather than to the index: drop a collation from the configuration and every line of the
        // unique-index snapshot above stays byte-identical while the rule it describes quietly flips to
        // case-sensitive. That is this test's whole reason for existing, and it now has exactly one
        // subject: users.email, whose collation carries a uniqueness rule rather than a lookup
        // convenience.
        //
        // EVERY NAME COLUMN IN THE SCHEMA HAS NOW LEFT THIS SET - budgets.name, accounts.name,
        // payees.name, category_groups.name and, in this slice, categories.name - AND EACH ABSENCE IS AS
        // DELIBERATE AS THE ONE ENTRY LEFT. All five columns are bytea — sealed narrative
        // fields — and bytea
        // is not a collatable type, so the collation did not lose an argument, it lost the type that
        // could carry one. That is a forced consequence rather than a decision, and it is the preview
        // the budgets paragraph promised arriving on schedule: this set SHRINKS as narrative columns
        // become ciphertext, and shrinking is the schema doing the right thing.
        //
        // THE FIVE DEPARTURES ARE NOT ONE EVENT, and collapsing them is the mistake to refuse.
        // budgets.name left and the case-folding uniqueness rule left with it, because that column has
        // no blind index and the requirement excludes one. accounts.name left and the rule STAYED: it
        // moved to IX_accounts_budget_id_name_key over (budget_id, name_key), which the unique-index
        // snapshot above pins, and the case folding moved into the normalisation the client applies
        // before it computes the HMAC. payees.name left on the accounts terms — the rule moved to
        // IX_payees_budget_id_name_key — and it is the departure that costs the most, because on that
        // table the uniqueness IS the deduplication of counterparties rather than a convenience.
        // category_groups.name left on the ACCOUNTS terms and not the budgets ones — the rule moved to
        // IX_category_groups_budget_id_name_key — and the cost sits between the two neighbours: nothing
        // in the product looks a group up by name, so the index's whole job is to refuse the second
        // row, which is a confusion a person can see rather than a deduplication anything rests on.
        // categories.name left LAST and on the category_groups terms - the rule moved to
        // IX_categories_budget_id_name_key - and it is the departure that empties this set of name
        // columns entirely. Its own case-insensitive test went with it: CategoryIntegrationTests
        // deleted CategoryNames_AreCaseInsensitivelyUniqueAcrossGroups with no replacement, which is
        // the third such deletion in the product and is argued at the site.
        // This server cannot check that the folding happened and no constraint here can be written to
        // it, which is the honest cost of the move and is worth writing down rather than leaving a
        // reader to infer that nothing changed.
        //
        // The "drop it and a snapshot stays byte-identical" half of the argument is INTACT and now
        // covers users.email alone - categories was the other subject until this slice, and the sentence
        // shrank with the set rather than being left to describe a column that no longer carries one.
        // ONE ENTRY REMAINS, AND A ONE-ENTRY PIN IS EXACTLY WHAT A LATER READER DELETES AS A LEFTOVER.
        // It is not one. users.email is the last collated column in the schema and the only uniqueness
        // rule in the product still enforced by a collation: drop it and Sam@x.com and sam@x.com become
        // two accounts for one mailbox, with IX_users_email still present, still unique, still rendered
        // byte-identically by pg_get_indexdef, and enforcing something weaker than it says.
        //
        // The test's second job survived the shrinking intact and is now the larger one: asserting the
        // WHOLE SET catches a collation appearing where none was intended. There are five such
        // additions to catch - budgets.name, accounts.name, payees.name, category_groups.name and now
        // categories.name - and every one of them would mean that column had gone back to TEXT, because
        // bytea cannot carry a collation. So this is not a list of one thing; it is a list of one thing
        // plus five absences, each of which is an assertion that the sealing survived.
        string[] expected =
        [
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
