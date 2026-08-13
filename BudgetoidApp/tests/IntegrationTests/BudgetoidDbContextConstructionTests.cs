using Domain.Accounts;
using Domain.Budgets;
using Domain.Categories;
using Domain.CategoryGroups;
using Domain.Currencies;
using Domain.Payees;
using Domain.Transactions;
using Domain.Users;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace IntegrationTests;

public sealed class BudgetoidDbContextConstructionTests
{
    [Test]
    public async Task Model_CanBeBuiltWithoutAResolvedBudget()
    {
        await using BudgetoidDbContext db = CreateDbContext();

        await Assert.That(db.Model.FindEntityType(typeof(Transaction))).IsNotNull();
        await Assert.That(db.Model.FindEntityType(typeof(User))).IsNotNull();
    }

    [Test]
    [Arguments(typeof(Account))]
    [Arguments(typeof(CategoryGroup))]
    [Arguments(typeof(Category))]
    [Arguments(typeof(Payee))]
    [Arguments(typeof(Transaction))]
    public async Task Model_ScopesOwnedEntitiesToTheirBudget(Type entityClrType)
    {
        // Arrange
        await using BudgetoidDbContext db = CreateDbContext();

        // Act
        IEntityType entity = db.Model.FindEntityType(entityClrType)!;
        IForeignKey budgetForeignKey = entity
            .GetForeignKeys()
            .Single(foreignKey => foreignKey.PrincipalEntityType.ClrType == typeof(Budget)
                                  && foreignKey.Properties.Count == 1);
        IProperty? survivingUserId = entity.FindProperty(RemovedOwnerPropertyName);

        // Assert
        await Assert.That(budgetForeignKey.Properties.Single().Name).IsEqualTo("BudgetId");
        await Assert.That(budgetForeignKey.IsRequired).IsTrue();

        // The owner link is dropped, not duplicated: a surviving UserId would be a second source of
        // truth for tenancy that no query reads, and the one state where a cross-tenant bug can hide.
        await Assert.That(survivingUserId).IsNull();
        await Assert.That(entity.GetForeignKeys()
                .Any(foreignKey => foreignKey.PrincipalEntityType.ClrType == typeof(User)))
            .IsFalse();
    }

    [Test]
    [Arguments(typeof(Account), DeleteBehavior.Cascade)]
    [Arguments(typeof(CategoryGroup), DeleteBehavior.Cascade)]
    [Arguments(typeof(Category), DeleteBehavior.Cascade)]
    [Arguments(typeof(Payee), DeleteBehavior.Cascade)]
    // Transaction is the one asymmetric row, and it is deliberate: a budget that holds recorded
    // money movement must refuse deletion outright, while a budget with only structure and no
    // movement was created by mistake and takes its structure with it. Do not "normalize" this row
    // to Cascade — doing so silently turns a refused delete into an erased ledger.
    [Arguments(typeof(Transaction), DeleteBehavior.Restrict)]
    public async Task Model_RemovesOwnedRowsWithTheirBudgetExceptTransactions(
        Type entityClrType,
        DeleteBehavior expectedDeleteBehavior)
    {
        // Arrange
        await using BudgetoidDbContext db = CreateDbContext();

        // Act
        IEntityType entity = db.Model.FindEntityType(entityClrType)!;
        IForeignKey budgetForeignKey = entity
            .GetForeignKeys()
            .Single(foreignKey => foreignKey.PrincipalEntityType.ClrType == typeof(Budget)
                                  && foreignKey.Properties.Count == 1);

        // Assert — the table above IS the policy; each row states what deleting a budget does to
        // that kind of owned row.
        await Assert.That(budgetForeignKey.DeleteBehavior).IsEqualTo(expectedDeleteBehavior);
    }

    [Test]
    public async Task Model_RequiresCategoryToReferenceACategoryGroupInTheSameBudget()
    {
        // Arrange
        await using BudgetoidDbContext db = CreateDbContext();

        // Act
        IEntityType categoryEntity = db.Model.FindEntityType(typeof(Category))!;
        IForeignKey categoryGroupForeignKey = categoryEntity
            .GetForeignKeys()
            .Single(foreignKey => foreignKey.PrincipalEntityType.ClrType == typeof(CategoryGroup));

        // Assert
        await Assert.That(categoryGroupForeignKey.Properties.Select(property => property.Name).ToArray())
            .IsEquivalentTo(new[] { nameof(Category.CategoryGroupId), "BudgetId" });
        await Assert.That(categoryGroupForeignKey.PrincipalKey.Properties
                .Select(property => property.Name).ToArray())
            .IsEquivalentTo(new[] { nameof(CategoryGroup.Id), "BudgetId" });
        await Assert.That(categoryGroupForeignKey.IsRequired).IsTrue();
        await Assert.That(categoryGroupForeignKey.DeleteBehavior).IsEqualTo(DeleteBehavior.Restrict);
    }

    [Test]
    [Arguments(typeof(Account), "AccountId")]
    [Arguments(typeof(Category), "CategoryId")]
    [Arguments(typeof(Payee), "PayeeId")]
    public async Task Model_RequiresTransactionReferencesToStayInTheSameBudget(
        Type principalClrType,
        string referencePropertyName)
    {
        // Arrange
        await using BudgetoidDbContext db = CreateDbContext();

        // Act
        IEntityType transactionEntity = db.Model.FindEntityType(typeof(Transaction))!;
        IForeignKey referenceForeignKey = transactionEntity
            .GetForeignKeys()
            .Single(foreignKey => foreignKey.PrincipalEntityType.ClrType == principalClrType);

        // Assert — a single-column reference lets the database hold a transaction that points at a
        // row in another budget; only the composite key ties the reference to the ambient tenant.
        await Assert.That(referenceForeignKey.Properties.Select(property => property.Name).ToArray())
            .IsEquivalentTo(new[] { referencePropertyName, "BudgetId" });
        await Assert.That(referenceForeignKey.PrincipalKey.Properties
                .Select(property => property.Name).ToArray())
            .IsEquivalentTo(new[] { "Id", "BudgetId" });
        // SET NULL is unreachable for a composite key whose BudgetId column is NOT NULL, so every
        // one of these references deletes under Restrict.
        await Assert.That(referenceForeignKey.DeleteBehavior).IsEqualTo(DeleteBehavior.Restrict);
    }

    [Test]
    public async Task Model_KeepsOptionalTransactionReferencesOptional()
    {
        // Arrange
        await using BudgetoidDbContext db = CreateDbContext();

        // Act
        IEntityType transactionEntity = db.Model.FindEntityType(typeof(Transaction))!;
        IProperty budgetIdProperty = transactionEntity.FindProperty("BudgetId")!;
        IProperty accountIdProperty = transactionEntity.FindProperty(nameof(Transaction.AccountId))!;
        IProperty payeeIdProperty = transactionEntity.FindProperty(nameof(Transaction.PayeeId))!;
        IProperty categoryIdProperty = transactionEntity.FindProperty(nameof(Transaction.CategoryId))!;
        IForeignKey budgetForeignKey = transactionEntity
            .GetForeignKeys()
            .Single(foreignKey => foreignKey.PrincipalEntityType.ClrType == typeof(Budget));
        IForeignKey accountForeignKey = transactionEntity
            .GetForeignKeys()
            .Single(foreignKey => foreignKey.PrincipalEntityType.ClrType == typeof(Account));
        IForeignKey payeeForeignKey = transactionEntity
            .GetForeignKeys()
            .Single(foreignKey => foreignKey.PrincipalEntityType.ClrType == typeof(Payee));
        IForeignKey categoryForeignKey = transactionEntity
            .GetForeignKeys()
            .Single(foreignKey => foreignKey.PrincipalEntityType.ClrType == typeof(Category));

        // Assert — this pins an absence of change, and the trap it guards is silent: calling
        // .IsRequired(false) on a composite foreign key makes *all* of its properties nullable,
        // including the BudgetId tenancy column, with no compiler error and no migration warning.
        // Optionality belongs to payee_id and category_id alone.
        await Assert.That(payeeIdProperty.IsNullable).IsTrue();
        await Assert.That(categoryIdProperty.IsNullable).IsTrue();
        await Assert.That(budgetIdProperty.IsNullable).IsFalse();
        await Assert.That(accountIdProperty.IsNullable).IsFalse();
        await Assert.That(payeeForeignKey.IsRequired).IsFalse();
        await Assert.That(categoryForeignKey.IsRequired).IsFalse();
        await Assert.That(budgetForeignKey.IsRequired).IsTrue();
        await Assert.That(accountForeignKey.IsRequired).IsTrue();
    }

    [Test]
    public async Task Model_IndexesTransactionReferencesWithTheBudget()
    {
        // Arrange
        await using BudgetoidDbContext db = CreateDbContext();
        string[] referencePropertyNames =
        [
            nameof(Transaction.AccountId),
            nameof(Transaction.CategoryId),
            nameof(Transaction.PayeeId),
        ];

        // Act
        IEntityType transactionEntity = db.Model.FindEntityType(typeof(Transaction))!;
        string[][] indexes = transactionEntity
            .GetIndexes()
            .Select(index => index.Properties.Select(property => property.Name).ToArray())
            .ToArray();

        // Assert — EF's foreign-key index convention covers the composite pairs, so the three
        // single-column indexes are dead weight the composite ones already serve as a prefix.
        foreach (string referencePropertyName in referencePropertyNames)
        {
            await Assert.That(indexes.Any(index =>
                    index.SequenceEqual([referencePropertyName, "BudgetId"])))
                .IsTrue();
            await Assert.That(indexes.Any(index => index.SequenceEqual([referencePropertyName])))
                .IsFalse();
        }

        // The list query's covering index is untouched by the re-keying.
        await Assert.That(indexes.Any(index => index.SequenceEqual(
                ["BudgetId", nameof(Transaction.Date), nameof(Transaction.CreatedAtUtc)])))
            .IsTrue();
    }

    [Test]
    [Arguments(typeof(Account))]
    [Arguments(typeof(CategoryGroup))]
    [Arguments(typeof(Category))]
    [Arguments(typeof(Payee))]
    public async Task Model_ScopesNameUniquenessToTheBudget(Type entityClrType)
    {
        // Arrange
        await using BudgetoidDbContext db = CreateDbContext();

        // Act
        IEntityType entity = db.Model.FindEntityType(entityClrType)!;
        IIndex budgetNameIndex = entity
            .GetIndexes()
            .Single(index => index.Properties.Select(property => property.Name)
                .SequenceEqual(new[] { "BudgetId", "Name" }));
        // Collation is not carried by the runtime read-optimized model, only by the design-time one.
        IProperty designTimeNameProperty = db
            .GetService<IDesignTimeModel>()
            .Model
            .FindEntityType(entityClrType)!
            .FindProperty("Name")!;

        // Assert
        await Assert.That(budgetNameIndex.IsUnique).IsTrue();
        await Assert.That(designTimeNameProperty.GetCollation()).IsEqualTo("case_insensitive");
    }

    [Test]
    public async Task Model_OrdersCategoryGroupsPerBudgetAndCategoriesPerGroup()
    {
        // Arrange
        await using BudgetoidDbContext db = CreateDbContext();

        // Act
        IEntityType categoryGroupEntity = db.Model.FindEntityType(typeof(CategoryGroup))!;
        IEntityType categoryEntity = db.Model.FindEntityType(typeof(Category))!;

        bool groupsOrderPerBudget = categoryGroupEntity.GetIndexes().Any(index =>
            index.Properties.Select(property => property.Name)
                .SequenceEqual(new[] { "BudgetId", nameof(CategoryGroup.Position) }));
        // Categories stay group-scoped: groups are budget-scoped, so per-budget ordering holds
        // transitively and this index deliberately does not mention the budget.
        bool categoriesOrderPerGroup = categoryEntity.GetIndexes().Any(index =>
            index.Properties.Select(property => property.Name)
                .SequenceEqual(new[] { nameof(Category.CategoryGroupId), nameof(Category.Position) }));

        // Assert
        await Assert.That(groupsOrderPerBudget).IsTrue();
        await Assert.That(categoriesOrderPerGroup).IsTrue();
    }

    [Test]
    public async Task Model_BoundsEveryValueRangeTheDatabaseCanCheck()
    {
        // Arrange
        await using BudgetoidDbContext db = CreateDbContext();

        // Act — the design-time model for the same reason the collation assertions use it: the
        // runtime read-optimized model drops everything only migrations consume, and a check
        // constraint is exactly that.
        string[] checkConstraints = db
            .GetService<IDesignTimeModel>()
            .Model
            .GetEntityTypes()
            .SelectMany(entity => entity.GetCheckConstraints())
            .Select(constraint =>
                $"{constraint.Name}: {constraint.EntityType.GetTableName()} {constraint.Sql}")
            .ToArray();

        // Assert — one set equality rather than seven existence checks, because the set also fails
        // on a constraint nobody meant to add. Each row is a range the domain already refuses and
        // that a raw INSERT walks straight past today. The magnitude bound repeats the domain
        // literal (Account.cs, Transaction.cs: Math.Abs(x) > 1_000_000_000m), so 1000000000 itself
        // stays legal on both sides and only 1000000000.01 is out.
        string[] expected =
        [
            "CK_accounts_opening_balance: accounts abs(opening_balance) <= 1000000000",
            "CK_accounts_type: accounts type in ('Checking', 'Savings', 'Cash', 'CreditCard')",
            "CK_categories_position: categories position >= 0",
            "CK_category_groups_position: category_groups position >= 0",
            // The issuer vocabulary, bounded the way the type vocabulary below it is: neither the
            // column nor UserRepository's lookup folds case, so without this 'Google' and 'google'
            // are two accounts for one person.
            "CK_credentials_provider: credentials provider is null or provider in ('google')",
            // Three spellings, not two, since a set of recovery codes became a credential in its own
            // right. Widening a bounded vocabulary is the edit this pin exists to make somebody argue
            // for, so the argument is here: the set is one credential row the whole issued set hangs
            // off, which is what makes revoking the set one delete and redeeming one code a delete of
            // a child row. The alternative — a fourth table with no credentials row — would have put
            // recovery material outside the one cascade an erasure runs through.
            "CK_credentials_type: credentials type in ('passkey', 'federated', 'recovery_codes')",
            // The shape check is what makes "a credential is exactly one type" a database rule: a
            // federated row with no issuer, or a passkey row carrying one, is rejected rather than
            // stored. The length test sits alongside the null test rather than replacing it —
            // length(null) is null and a check evaluating to null is satisfied.
            //
            // The recovery_codes arm's predicate is identical to the passkey arm's, and that is
            // recorded rather than tidied away: this constraint no longer discriminates between those
            // two types, because both are self-contained credentials with no issuer and no provider
            // subject. What still tells them apart is the line above bounding the vocabulary, and each
            // child table's composite foreign key comparing its own credential_type copy against this
            // column — so a recovery-code row cannot hang off a passkey credential or the reverse.
            // Collapsing the two arms into one would say the same thing in less space and lose the
            // record of which types the schema has considered.
            "CK_credentials_type_shape: credentials (type = 'federated' and provider is not null and "
            + "subject is not null and length(subject) > 0) or (type = 'passkey' and provider is null "
            + "and subject is null) or (type = 'recovery_codes' and provider is null and subject is "
            + "null)",
            // The two COSE algorithms the verifier accepts. Anything else is a key no verification
            // path can read back, so the row would be a credential that authenticates nobody.
            "CK_passkey_public_keys_cose_algorithm: passkey_public_keys cose_algorithm in (-7, -257)",
            // Each table carries its own copy of the credential's type, so each owes its own pin: the
            // copy is what the composite foreign key ties back to the credential, and a copy free to
            // say 'federated' would put key material on a row nothing verifies.
            "CK_passkey_public_keys_credential_type: passkey_public_keys credential_type = 'passkey'",
            "CK_passkey_public_keys_public_key_length: passkey_public_keys length(public_key_cose) between 1 and 1024",
            "CK_passkey_public_keys_webauthn_credential_id_length: passkey_public_keys length(webauthn_credential_id) between 16 and 1023",
            "CK_passkey_signature_counters_credential_type: passkey_signature_counters credential_type = 'passkey'",
            // The unsigned 32-bit range a WebAuthn signature counter is defined over, written as two
            // comparisons rather than between because the upper bound exceeds int.
            "CK_passkey_signature_counters_value: passkey_signature_counters signature_counter >= 0 "
            + "and signature_counter <= 4294967295",
            // The third table carrying a copy of its credential's type, and it owes its own pin for
            // the reason the two above do: the copy is what the composite foreign key ties back to
            // the credential, and a copy free to say 'passkey' would be a recovery code hanging off
            // an authenticator. The single value rather than a vocabulary is the stronger claim —
            // this table exists only for recovery codes, so any other spelling is a row that should
            // not exist rather than a row meaning something else.
            "CK_recovery_code_hashes_credential_type: recovery_code_hashes credential_type = "
            + "'recovery_codes'",
            // Equality, not a range, and the difference is the claim. The value is a SHA-256 computed
            // server-side, so it is 32 bytes or it is not a hash this table can have produced; a range
            // would read as "some hashes are longer than others", which is a statement about input
            // nobody makes here.
            "CK_recovery_code_hashes_verifier_hash_length: recovery_code_hashes "
            + "length(verifier_hash) = 32",
            // A nonce issued for one ceremony and spent on another is cross-ceremony replay, which
            // this vocabulary refuses at the column rather than in whichever handler reads it. Three
            // pools, not two: 'reauthentication' is minted only from an authenticated endpoint and is
            // the only one that authorizes account erasure, so a nonce from either of the other two
            // reaching that path would make the whole gate a formality.
            "CK_webauthn_challenges_ceremony: webauthn_challenges ceremony in ('registration', "
            + "'authentication', 'reauthentication')",
            // Equality, not a minimum: a challenge of any other length is one the issuer never
            // emitted, and a lower bound would accept it.
            "CK_webauthn_challenges_length: webauthn_challenges length(challenge) = 32",
            // The same rule CK_sessions_lifetime states, owed here for the same reason: a row whose
            // expiry is at or before its creation was never live for an instant.
            "CK_webauthn_challenges_lifetime: webauthn_challenges expires_at_utc > created_at_utc",
            // The fourth table carrying a copy of its credential's type, and the only one whose copy may
            // say two things rather than one: account keys are wrapped under whichever factors have a
            // key-encryption key — a passkey through its PRF output, a set of recovery codes through the
            // code the holder still has. 'federated' is the spelling this arm exists to refuse; OAuth has
            // no PRF equivalent, so a row filed against the federated credential would be two envelopes
            // nothing in the world can open, presented as a way back into the account. Enumerated rather
            // than written as an exclusion of federated, so a fourth credential type is refused until
            // somebody decides here that it may hold the account's keys.
            "CK_wrapped_account_keys_credential_type: wrapped_account_keys credential_type in "
            + "('passkey', 'recovery_codes')",
            // Two bounds per envelope and both read off WrappedAccountKeys, which is the whole reason
            // they are not local literals: WrappedAccountKeys.For refuses an envelope that is not exactly
            // EnvelopeLength bytes carrying EnvelopeVersion, and these constraints refuse the identical
            // row arriving by any other path. A copy of 61 here would not be a second fact, it would be
            // the same fact able to disagree with itself.
            //
            // Equality rather than a range, and stated per column rather than once over both. AES-GCM
            // ciphertext is the length of its plaintext and the plaintext is a 32-byte key, so an
            // envelope has one legal size and both sides of the bound are refused; per column, so a
            // violation names which envelope was malformed — nothing else can tell the two apart, since
            // every check here reads the same on either.
            "CK_wrapped_account_keys_wrapped_content_key_length: wrapped_account_keys "
            + "length(wrapped_content_key) = 61",
            "CK_wrapped_account_keys_wrapped_content_key_version: wrapped_account_keys "
            + "get_byte(wrapped_content_key, 0) = 1",
            "CK_wrapped_account_keys_wrapped_index_key_length: wrapped_account_keys "
            + "length(wrapped_index_key) = 61",
            "CK_wrapped_account_keys_wrapped_index_key_version: wrapped_account_keys "
            + "get_byte(wrapped_index_key, 0) = 1",
            "CK_currencies_code: currencies code ~ '^[A-Z]{3}$'",
            "CK_currencies_minor_unit: currencies minor_unit between 0 and 4",
            // The kind vocabulary, bounded the way the credential vocabularies above are, and it is
            // the column that decides whether a session reaches budget content at all.
            "CK_sessions_kind: sessions kind in ('full', 'locked')",
            // The derivation Session.Establish performs, restated where it can be rejected rather
            // than merely performed: a rule deciding what a session may read is not one to leave to
            // a single factory while the column list stays reachable by any INSERT.
            //
            // Two spellings on the full side, not one, and the second is not a widening of the kind
            // CK_credentials_type's list is. A set of recovery codes is a key factor: the account's
            // content and index keys are wrapped under the set, so the code the holder typed unwraps
            // them, which is the property a passkey's authenticator has and a provider's claims do not.
            // Federated stays alone on the locked side because it is the only type that cannot hold
            // the account's keys.
            //
            // The full side is ENUMERATED and it stays enumerated. The inversion —
            //   (kind = 'locked') = (credential_type = 'federated')
            // says the same thing about every row this model can hold today and reads better as the
            // list grows, which is why somebody will propose it. It fails OPEN: a fourth credential
            // type is not federated, so it satisfies the right-hand side and gets a full session by
            // default with nobody having chosen that. This form fails closed, which is the same
            // decision Session.KindFor forces by writing out every arm rather than defaulting.
            //
            // This pin does turn red on that rewrite — the configured Sql string is what it compares,
            // so a changed predicate moves the line. It moves as a literal to update, though, and no
            // assertion here separates "the rule changed meaning" from "the wording changed": both
            // spellings are true of every row the model can hold until a fourth credential type
            // exists, so the catalog snapshot goes red the same way and neither failure argues
            // anything. This paragraph is what stands between the red line and the paste.
            "CK_sessions_kind_matches_credential: sessions (kind = 'full') = (credential_type in "
            + "('passkey', 'recovery_codes'))",
            // A session whose expiry is at or before its creation was never live for an instant. A
            // separate constraint from the one above rather than an AND of both, because one defect
            // must report exactly one name.
            "CK_sessions_lifetime: sessions expires_at_utc > created_at_utc",
            "CK_transactions_amount: transactions abs(amount) <= 1000000000",
        ];
        await Assert.That(checkConstraints).IsEquivalentTo(expected);
    }

    [Test]
    public async Task Migrations_KeepTheBaselineFrozen()
    {
        // Arrange — the id is spelled out here on purpose, and reading it off the migrations
        // directory instead would defeat the test: an id derived from the files agrees with whatever
        // file is present, including the replacement, so the assertion could never fail. Production's
        // __EFMigrationsHistory names this exact string, and the deploy pipeline applies migrations
        // unattended on every push to main, so a regenerated baseline arrives under a new id, the
        // next push finds nothing applied, and it re-creates every table against a populated
        // database. Having to edit this line is the checkpoint the retired manual deploy step was.
        //
        // This literal has now moved more than once, and every move is the checkpoint working rather
        // than being waived — a second edit is not a precedent that makes the third free, because what
        // the edit costs is unchanged: whoever makes it resets production's __EFMigrationsHistory in
        // the same deploy or the deploy fails on the first CREATE TABLE.
        // docs/engineering/migrations.md: the rebaseline window in the migrations-guard CI job
        // is open, so regenerating the baseline is *permitted* — the production database holds no data
        // and the schema changes still ahead are worth landing as one initial migration rather than a
        // chain nothing will ever replay step by step. Permitted is not free, and the window covers
        // that CI job and nothing else. This test still fails on a regenerated baseline by design: the
        // id is edited by a person, in the same commit, who has read that whoever regenerates also
        // resets production's __EFMigrationsHistory in the same deploy (DEPLOYMENT.md, Step 3) or the
        // deploy fails on the first CREATE TABLE.
        //
        // Two things the window does NOT change, spelled out because both look like the obvious
        // follow-up edit. The window stays open — closing it is a decision about whether the database
        // has started holding data anyone wants back, not a consequence of a rebaseline. And
        // FROZEN_FROM in that job does not move with this literal: it is a lower bound every present
        // file already sorts above, so advancing it while the window is open would be a second,
        // silent change to what the guard covers. It moves once, together with the window closing.
        //
        // This literal moved again when wrapped_account_keys joined the schema: the baseline was
        // regenerated under the open window (CON-002 — the production database holds no data), so the
        // new table, its grants and its user_isolation policy land in one initial migration rather than
        // in a chain nothing will ever replay step by step. The move is deliberate and it carries the
        // same obligation every earlier one did: whoever regenerates the baseline resets production's
        // __EFMigrationsHistory in the same deploy (DEPLOYMENT.md, Step 3), or that deploy fails on the
        // first CREATE TABLE against a database that already holds the schema.
        const string frozenBaselineId = "20260813112541_InitialCreate";
        await using BudgetoidDbContext db = CreateDbContext();

        // Act
        IReadOnlyList<string> migrations = db.Database.GetMigrations().ToList();
        bool inApplyOrder = migrations.SequenceEqual(
            migrations.OrderBy(migration => migration, StringComparer.Ordinal));

        // Assert — the count is deliberately not pinned. Schema changes are additive migrations from
        // here on (docs/engineering/migrations.md; ADR 0006), so a second and a tenth id are
        // both legal and only the first is frozen. That inversion is what makes this a stronger guard
        // than the count it replaces: counting one migration failed on a diffed baseline, which is
        // now the wanted thing, and stayed green through a regenerated one, which is the dangerous
        // thing — regeneration keeps the count at one and changes nothing but the id.
        // The ordering assertion is what turns "first" into "earliest", so no separate check that
        // nothing sorts ahead of the baseline is needed: GetMigrations returns ids in the order they
        // apply, which for timestamp-prefixed ids is ordinal sort order, so a back-dated migration
        // lands at index 0 and fails the id assertion rather than hiding behind it. The claim is
        // asserted rather than assumed because everything below rests on it.
        await Assert.That(migrations).IsNotEmpty();
        await Assert.That(inApplyOrder)
            .IsTrue()
            .Because("GetMigrations stopped returning ids in apply order, so the first element is "
                     + "no longer necessarily the baseline");
        await Assert.That(migrations[0])
            .IsEqualTo(frozenBaselineId)
            .Because("the baseline is frozen; add a migration instead of regenerating InitialCreate");
    }

    [Test]
    public async Task Migrations_MatchTheModel()
    {
        // Arrange
        await using BudgetoidDbContext db = CreateDbContext();

        // Act — synchronous by necessity: EF Core 10 ships no async overload of this. No live
        // server is contacted either, because the check diffs the migrations assembly snapshot
        // against the design-time model and never opens the connection it builds.
        bool pending = db.Database.HasPendingModelChanges();

        // Assert — every other test in this class reads db.Model, which is the configuration code,
        // and those stay green when the migration disagrees with it. Drift is not silent elsewhere,
        // though: MigrateAsync refuses to migrate a drifted model, so every container-backed test in
        // the suite fails on PendingModelChangesWarning. This test earns its place by being the
        // cheap, legible version of that — no Docker, no container wait, and a message naming
        // regeneration as the fix rather than dozens of identical warnings on unrelated tests.
        await Assert.That(pending)
            .IsFalse()
            .Because("the model drifted from the migration; regenerate InitialCreate");
    }

    [Test]
    public async Task Model_ScopesBudgetToItsOwningUser()
    {
        // Arrange
        await using BudgetoidDbContext db = CreateDbContext();

        // Act
        IEntityType budgetEntity = db.Model.FindEntityType(typeof(Budget))!;
        IForeignKey userForeignKey = budgetEntity
            .GetForeignKeys()
            .Single(foreignKey => foreignKey.PrincipalEntityType.ClrType == typeof(User));

        // Assert
        await Assert.That(userForeignKey.Properties.Single().Name).IsEqualTo(nameof(Budget.UserId));
        await Assert.That(userForeignKey.IsRequired).IsTrue();
        await Assert.That(userForeignKey.DeleteBehavior).IsEqualTo(DeleteBehavior.Cascade);
    }

    [Test]
    public async Task Model_ScopesBudgetNameUniquenessToTheOwner()
    {
        // Arrange
        await using BudgetoidDbContext db = CreateDbContext();

        // Act
        IEntityType budgetEntity = db.Model.FindEntityType(typeof(Budget))!;
        IIndex ownerNameIndex = budgetEntity
            .GetIndexes()
            .Single(index => index.Properties.Select(property => property.Name)
                .SequenceEqual(new[] { nameof(Budget.UserId), nameof(Budget.Name) }));
        IProperty nameProperty = budgetEntity.FindProperty(nameof(Budget.Name))!;
        // The rule, stated once: the runtime read-optimized model drops everything only migrations
        // consume, so an annotation-backed getter read off db.Model answers null instead of the
        // configured value — and null quietly satisfies neither IsTrue nor IsFalse. Collation and
        // NULLS NOT DISTINCT are both in that group, so both come from the design-time model.
        IEntityType designTimeBudgetEntity = db
            .GetService<IDesignTimeModel>()
            .Model
            .FindEntityType(typeof(Budget))!;
        IProperty designTimeNameProperty = designTimeBudgetEntity.FindProperty(nameof(Budget.Name))!;
        IIndex designTimeOwnerNameIndex = designTimeBudgetEntity
            .GetIndexes()
            .Single(index => index.Properties.Select(property => property.Name)
                .SequenceEqual(new[] { nameof(Budget.UserId), nameof(Budget.Name) }));

        // The schema is multi-budget-ready from day one: "exactly one budget per user" is a
        // release-scope property (no code path creates a second one), never a schema invariant, so a
        // unique constraint over the owner alone would have to be dropped by the multi-budget release.
        bool constrainsOwnerAlone =
            budgetEntity.GetIndexes().Any(index =>
                index.IsUnique
                && index.Properties.Count == 1
                && index.Properties[0].Name == nameof(Budget.UserId))
            || budgetEntity.GetKeys().Any(key =>
                key.Properties.Count == 1
                && key.Properties[0].Name == nameof(Budget.UserId));

        // Assert — the name is nullable because the budget created at provisioning has no name, and
        // the index must be NULLS NOT DISTINCT because of it. PostgreSQL's default treats every NULL
        // as distinct, so without the opt-out two concurrent provisioning requests would each insert
        // a (user_id, NULL) row and the user would end up owning two budgets. Making two unnamed
        // budgets collide is what replaces the shared literal name the racers used to collide on —
        // and it states the real invariant: at most one unnamed budget per user, any number of named
        // ones.
        // GetAreNullsDistinct is worded the way PostgreSQL words the option, not the way the rule
        // reads: it answers false exactly when the index is NULLS NOT DISTINCT. IsFalse below is
        // therefore the assertion that pins the opt-out, and flipping it to IsTrue would assert the
        // default this test exists to refuse.
        await Assert.That(ownerNameIndex.IsUnique).IsTrue();
        await Assert.That(designTimeOwnerNameIndex.GetAreNullsDistinct()).IsFalse();
        await Assert.That(nameProperty.IsNullable).IsTrue();
        await Assert.That(nameProperty.GetMaxLength()).IsEqualTo(200);
        await Assert.That(designTimeNameProperty.GetCollation()).IsEqualTo("case_insensitive");
        await Assert.That(constrainsOwnerAlone).IsFalse();
    }

    [Test]
    public async Task Model_AllowsABudgetWithoutABaseCurrency()
    {
        // Arrange
        await using BudgetoidDbContext db = CreateDbContext();

        // Act
        IEntityType budgetEntity = db.Model.FindEntityType(typeof(Budget))!;
        IProperty baseCurrencyProperty = budgetEntity.FindProperty(nameof(Budget.BaseCurrencyCode))!;
        IForeignKey currencyForeignKey = budgetEntity
            .GetForeignKeys()
            .Single(foreignKey => foreignKey.PrincipalEntityType.ClrType == typeof(Currency));

        // Assert
        await Assert.That(baseCurrencyProperty.IsNullable).IsTrue();
        await Assert.That(baseCurrencyProperty.GetMaxLength()).IsEqualTo(3);
        await Assert.That(currencyForeignKey.Properties.Single().Name).IsEqualTo(nameof(Budget.BaseCurrencyCode));
        await Assert.That(currencyForeignKey.IsRequired).IsFalse();
        await Assert.That(currencyForeignKey.DeleteBehavior).IsEqualTo(DeleteBehavior.Restrict);
    }

    [Test]
    public async Task Model_ContainsIsoCurrencySeedDataForFreshBaseline()
    {
        // Arrange
        await using BudgetoidDbContext db = CreateDbContext();

        // Act
        IReadOnlyList<IDictionary<string, object?>> seeds = db
            .GetService<IDesignTimeModel>()
            .Model
            .FindEntityType(typeof(Currency))!
            .GetSeedData()
            .ToList();

        // Assert
        await Assert.That(seeds.Count).IsEqualTo(13);
        await Assert.That(seeds.Any(seed => (string)seed[nameof(Currency.Code)]! == "USD")).IsTrue();
    }

    /// <summary>
    /// The property name the re-scope removes. Spelled as a string on purpose: <c>nameof</c> would
    /// stop compiling once the property is gone, and the point of the assertion is that it is gone.
    /// </summary>
    private const string RemovedOwnerPropertyName = "UserId";

    private static BudgetoidDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<BudgetoidDbContext>()
            .UseNpgsql("Host=localhost;Port=5432;Database=budgetoid;Username=postgres;Password=postgres")
            .Options;

        // The IBudgetContext parameter stays optional so the model can be built without a resolved
        // tenant — design-time tooling, seeding, and these tests all rely on that.
        return new BudgetoidDbContext(options);
    }
}
