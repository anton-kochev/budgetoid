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

    /// <summary>
    /// The one entity whose name is still text this server can read, and whose uniqueness rule is
    /// therefore still enforced over the name column itself under a case-folding collation.
    /// </summary>
    /// <remarks>
    /// <b><see cref="Account" />, <see cref="Payee" /> and now <see cref="CategoryGroup" /> have all
    /// left this set, and none of them lost the rule</b> — see
    /// <see cref="Model_ScopesNameUniquenessToTheBudgetOverTheBlindIndex" />, which is where the same
    /// rule lives for all three. The absences are worth reading rather than filling back in: an
    /// argument naming any of them here would look for an index over <c>BudgetId, Name</c> that no
    /// longer exists and for a collation <c>bytea</c> cannot carry, so restoring one fails loudly
    /// rather than quietly. What that leaves is the honest shape of the schema mid-migration — one name
    /// column still in the clear, three sealed — and this set shrinking as the rest are sealed is the
    /// schema doing the right thing. It is now a single-argument case and stays data-driven for that
    /// reason: <see cref="Category" /> is the next column to move, and the day it does this member goes
    /// with it rather than being quietly rewritten around one hard-coded type.
    /// </remarks>
    [Test]
    [Arguments(typeof(Category))]
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

    /// <summary>
    /// The three entities whose name is a sealed envelope, and whose uniqueness rule therefore lives on
    /// the blind index beside it.
    /// </summary>
    /// <remarks>
    /// <b>Data-driven over all three rather than one case each, because the rule is one rule.</b> A
    /// second hand-written case would be the place a later reader relaxes one table's assertion without
    /// noticing the others still make it. What the three do NOT share is what a lost rule costs: on
    /// accounts a duplicate name is a nuisance, while on payees this index IS the deduplication of
    /// counterparties — the client resolves a name against the list it decrypted and mints a new payee
    /// when it finds no match — so a payee index that enforced nothing would hand one budget two rows
    /// for one counterparty with nothing on this side able to see it. Category groups read as accounts
    /// do: two groups under one name are a confusion a person can see and correct, and nothing in the
    /// product looks a group up by name, so the index's whole job is to refuse the second row.
    /// </remarks>
    [Test]
    [Arguments(typeof(Account))]
    [Arguments(typeof(Payee))]
    [Arguments(typeof(CategoryGroup))]
    public async Task Model_ScopesNameUniquenessToTheBudgetOverTheBlindIndex(Type entityClrType)
    {
        // Arrange
        await using BudgetoidDbContext db = CreateDbContext();

        // Act
        IEntityType entity = db.Model.FindEntityType(entityClrType)!;
        IIndex budgetNameKeyIndex = entity
            .GetIndexes()
            .Single(index => index.Properties.Select(property => property.Name)
                .SequenceEqual(new[] { "BudgetId", "NameKey" }));
        bool anyIndexOverTheEnvelope = entity
            .GetIndexes()
            .Any(index => index.Properties.Any(property => property.Name == "Name"));

        // Collation from the design-time model for the reason the sibling case reads it there: the
        // runtime read-optimized model does not carry one.
        IDesignTimeModel designTimeModel = db.GetService<IDesignTimeModel>();
        IEntityType designTimeEntity = designTimeModel.Model.FindEntityType(entityClrType)!;
        IProperty designTimeName = designTimeEntity.FindProperty("Name")!;
        IProperty designTimeNameKey = designTimeEntity.FindProperty("NameKey")!;

        // Assert — THE SAME RULE, one name per budget, over a different column. The envelope cannot
        // carry it: every seal draws a fresh nonce, so two rows holding one name hold different bytes
        // and a unique index over `name` would refuse nothing while still existing, being reported by
        // pg_get_indexdef, and passing any test that only checked it was unique. The blind index is
        // deterministic under the account's index key, which is what makes equality of names come back
        // as equality of digests.
        await Assert.That(budgetNameKeyIndex.IsUnique).IsTrue();

        // No index mentions the envelope at all, and this is the assertion that separates "the rule
        // moved" from "a second index was added beside the old one". A surviving unique index over
        // (BudgetId, Name) would be worse than useless: it would enforce nothing, cost a write on
        // every rename, and read to the next person as the rule's home.
        await Assert.That(anyIndexOverTheEnvelope)
            .IsFalse()
            .Because("uniqueness over the sealed envelope enforces nothing — every seal draws a fresh "
                     + "nonce — so an index there is a rule that looks present and is not");

        // Neither column carries a collation, and both halves of that are forced rather than chosen.
        // bytea is not a collatable type, so case folding could not live here even if somebody wanted
        // it to; it moved into the normalisation the client applies before it computes the HMAC, which
        // this server cannot check and no constraint here can be written to. A collation reappearing
        // on either property means the column went back to text.
        await Assert.That(designTimeName.GetCollation()).IsNull();
        await Assert.That(designTimeNameKey.GetCollation()).IsNull();
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
            // AN EQUALITY AND NOT A BAND, which is the difference between this column and the two
            // narrative bands in this list rather than a stricter mood. HMAC-SHA-256 emits exactly 32
            // bytes and nothing truncates in between, so there is no range of legal widths to allow
            // for and a bound written as a ceiling would admit a short digest in silence. The width is
            // the whole of the defence: the digest is the client's, taken under an index key that
            // lives in a browser, so this server cannot recompute it, cannot check it against the name
            // beside it, and cannot tell a correct 32 bytes from a fabricated 32 bytes. A wrong one is
            // stable, never collides, keys perfectly and matches nothing for the life of the account.
            // Rendered from IndexedName.BlindIndexLength, so the constant owns the number and moving
            // it moves this literal.
            //
            // No version arm, and there is nothing to write one from: a blind index is a keyed digest
            // and not an envelope — no version byte, no nonce, no tag, nothing to open.
            "CK_accounts_name_key_length: accounts length(name_key) = 32",
            // The account name's band, the same shape and the same two constants as
            // CK_budgets_name_length below — see that entry for why a band rather than a width, why
            // both bounds are inclusive, and why the numbers are rendered rather than typed.
            //
            // One thing this line does that the budgets one cannot: the floor is what refuses an empty
            // name at the only level left that can refuse one, because accounts.name is NOT NULL where
            // budgets.name is not. It is still a floor on ENVELOPE bytes and says nothing about the
            // text underneath — an envelope over an empty string satisfies it exactly — so it is not
            // the blank-name rule the entity gave up and must not be described as having restored it.
            "CK_accounts_name_length: accounts length(name) between 29 and 1024",
            // substring rather than get_byte, for the reason CK_budgets_name_version states below and
            // with one addition this table makes concrete. accounts sorts CK_accounts_name_key_length,
            // then CK_accounts_name_length, then CK_accounts_name_version, so a zero-length name here
            // happens to answer 23514 rather than a fatal 2202E — held by nothing but the word
            // "length" sorting before "version", which is not a decision anybody took. substring
            // carries no such dependency: it answers a zero-length bytea for a zero-length input, the
            // check is false rather than fatal, and the violation is 23514 under every ordering.
            "CK_accounts_name_version: accounts substring(name from 1 for 1) = '\\x01'::bytea",
            "CK_accounts_opening_balance: accounts abs(opening_balance) <= 1000000000",
            "CK_accounts_type: accounts type in ('Checking', 'Savings', 'Cash', 'CreditCard')",
            // A band and not a width, unlike the wrapped-key pair at the bottom of this list. AES-GCM
            // ciphertext is exactly the length of its plaintext, and a budget's name is as long as
            // whatever somebody typed, so only the two ends are decidable: the floor is the shortest
            // the framing can be — CiphertextEnvelope.MinimumLength, a version, a nonce and a tag over
            // an empty plaintext — and the ceiling is NarrativeFieldLimits.NameBytes. Both bounds are
            // inclusive because both name a length that is legal. The configuration renders both from
            // those constants rather than typing 29 and 1024, so this line is the pin on the RENDERING
            // and the Domain still owns the numbers; a constant that moves moves this literal too, and
            // that is the wanted failure rather than a nuisance.
            //
            // Nothing here says "or null", and the omission is the rule rather than a gap. A CHECK is
            // satisfied by NULL — length(null) is null, and a null predicate is not a violation — so
            // the nameless budget, which is the default budget and the common row, passes both of
            // these with no arm written for it.
            "CK_budgets_name_length: budgets length(name) between 29 and 1024",
            // substring, and deliberately NOT the get_byte idiom the wrapped-key version checks below
            // use. get_byte reads better and it RAISES 2202E on a zero-length bytea instead of
            // answering false — no constraint name, no failing row, and nothing a repository filtering
            // PostgresException on SqlState 23514 can ever see. The length check next door does not
            // save it: which of two CHECKs on one column fires first is decided by the constraint
            // NAME, alphabetically, so today's safety is the word "length" sorting before "version"
            // and nothing else. substring is total over every length, answers false rather than
            // raising, and still leaves NULL satisfying the predicate.
            //
            // Two tables now make that accident concrete rather than one: accounts and payees both
            // sort key_length, then length, then version. Neither ordering was chosen — the names are
            // forced by the column names — which is the whole reason the predicate has to be the thing
            // that cannot raise.
            //
            // That difference is invisible to THIS pin, which compares configured text and cannot see
            // which constraint fires or what SQLSTATE a bad row produces. The pin's job here is that
            // somebody rewriting the version check back into get_byte has to move this literal and
            // read this paragraph on the way past. The wrapped-key pair below still carries get_byte
            // and is correct today only by that same alphabetical accident; it is recorded in the
            // hardening backlog and belongs to its own change, not to a drive-by edit here.
            //
            // The escaped backslash is one character in the configured SQL: '\x01' is PostgreSQL's
            // hex-format bytea literal, and the version digit is rendered from CiphertextEnvelope.Version
            // two hex digits wide for the reason the band above is rendered from its own constants.
            "CK_budgets_name_version: budgets substring(name from 1 for 1) = '\\x01'::bytea",
            "CK_categories_position: categories position >= 0",
            // THE FIRST SEALED DESCRIPTION COLUMN IN THE PRODUCT, and the first table whose CHECK
            // ordering crosses two columns. Same band shape as every name above, a DIFFERENT ceiling —
            // NarrativeFieldLimits.DescriptionBytes, not NameBytes — because those are two caps over
            // field CLASSES rather than two guesses at one number. A reviewer pasting NameBytes onto
            // this line refuses values the column is meant to accept, and the failure lands on a client
            // that sent a perfectly legal note.
            //
            // NOTHING SAYS "OR NULL" AND THAT IS THE RULE, not a gap: a CHECK is satisfied by NULL, so
            // a group filing no note passes both description arms vacuously. Measured on
            // postgres:17.10 over exactly these six constraints — NULL accepted, 29 bytes accepted,
            // 2560 accepted, 2561 refused under this name, and clearing a description back to NULL by
            // UPDATE succeeds.
            "CK_category_groups_description_length: category_groups length(description) between 29 "
            + "and 2560",
            // substring, AND THE REASON A READER WILL GET BACKWARDS. The instinct is that a nullable
            // column escapes get_byte's zero-length trap. It does not. Measured on postgres:17.10:
            // get_byte(NULL::bytea, 0) answers NULL and does not raise, so a get_byte-spelled version
            // check here is green on every row holding a NULL and every row holding a valid envelope,
            // and bites only on the PRESENT, ZERO-LENGTH value — which is exactly what a client sending
            // an empty bytea produces and the one value this check exists for. get_byte(''::bytea, 0)
            // still raises 2202E from inside a CHECK on a nullable column.
            //
            // So nullability makes the wrong spelling QUIETER rather than safer, and the single case
            // that catches it is the one a reviewer is most likely to call redundant with the name's.
            "CK_category_groups_description_version: category_groups substring(description from 1 for "
            + "1) = '\\x01'::bytea",
            // The blind index's width, an equality for the reason CK_accounts_name_key_length and
            // CK_payees_name_key_length are equalities, rendered from the same
            // IndexedName.BlindIndexLength. No version arm: a blind index is a keyed digest, not an
            // envelope.
            "CK_category_groups_name_key_length: category_groups length(name_key) = 32",
            "CK_category_groups_name_length: category_groups length(name) between 29 and 1024",
            // A THIRD TABLE MAKES THE ALPHABETICAL ACCIDENT CONCRETE, and this one is the first where
            // it crosses columns. Measured on postgres:17.10 over exactly these six: the sort is
            // description_length, description_version, name_key_length, name_length, name_version,
            // position — so a row violating a NAME rule and a DESCRIPTION rule is reported under the
            // DESCRIPTION, and a row violating a description rule and the position rule is reported
            // under the description too. Nothing in the configuration depends on that, because every
            // narrative predicate here is spelled with substring and none of them can raise. What it
            // forbids is a case asserting a constraint NAME for a row carrying more than one violation:
            // a zero-length-name case must leave the description NULL, or it reports the description's.
            "CK_category_groups_name_version: category_groups substring(name from 1 for 1) = "
            + "'\\x01'::bytea",
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
            // The payee blind index's width, an equality for exactly the reason
            // CK_accounts_name_key_length is one and rendered from the same
            // IndexedName.BlindIndexLength. What differs is what a wrong value costs on this table:
            // accounts uniqueness is a convenience, while a payee's blind index IS the deduplication
            // of counterparties — the client resolves a name against the list it decrypted and mints a
            // new payee when it finds no match, so an index that is the right width and the wrong
            // value produces a second row for one counterparty with nothing on this side able to see
            // it. No version arm here either: a blind index is a keyed digest, not an envelope.
            "CK_payees_name_key_length: payees length(name_key) = 32",
            // The payee name's band, same shape and same two constants as its accounts twin, and NOT
            // NULL like it, so the floor refuses an empty value at the only level left that can. It is
            // a floor on ENVELOPE bytes: an envelope over an empty string satisfies it exactly. Do not
            // describe it as restoring the blank-name rule or the 200-character ceiling that
            // Payee.ValidateOrThrow gave up — both were SURRENDERED to the client, and nothing here
            // can count characters through ciphertext.
            "CK_payees_name_length: payees length(name) between 29 and 1024",
            // substring rather than get_byte, for the reason CK_budgets_name_version states below and
            // the one CK_accounts_name_version makes concrete: this table sorts
            // CK_payees_name_key_length, then CK_payees_name_length, then CK_payees_name_version, so a
            // zero-length name happens to answer 23514 from a length check — held by nothing but
            // "length" sorting before "version". substring is what makes that ordering cosmetic rather
            // than load-bearing, because no predicate here can raise under any ordering.
            "CK_payees_name_version: payees substring(name from 1 for 1) = '\\x01'::bytea",
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
            // Four pools now, not three. 'account_registration' is a pool of its own rather than a
            // qualifier on 'registration', because the two are minted for callers in different states
            // — one is already signed in and adding a device, the other holds a provider token and no
            // account at all — and a later commit derives the new account's id from a nonce in this
            // pool. Sharing the pool would let an add-a-device nonce name a brand-new account, which
            // is the cross-ceremony replay the vocabulary exists to refuse. Enumerated, so a fifth
            // pool is a schema change somebody has to make here rather than a spelling that quietly
            // becomes storable.
            "CK_webauthn_challenges_ceremony: webauthn_challenges ceremony in ('registration', "
            + "'authentication', 'reauthentication', 'account_registration')",
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
            // Equality rather than a range, for the reason CK_recovery_code_hashes_verifier_hash_length
            // gives: the value is a SHA-256 computed server-side, so it is 32 bytes or it is not a
            // digest this table can have produced.
            //
            // What this bound cannot see is worth writing down beside it, because a reader will assume
            // it covers more than it does. It watches the DIGEST, which is 32 bytes whatever went into
            // it — a short token hashes to a perfectly well-formed row nothing downstream could tell
            // from a real one. The token's own width is SessionToken.TokenLength and only
            // SessionToken.For refuses it, from both sides. So this constraint is not the guard against
            // a weak handle; it is the guard against a column holding something that is not a digest at
            // all.
            "CK_session_tokens_token_hash_length: session_tokens length(token_hash) = 32",
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
        // This literal moved again when wrapped_account_keys' key moved from credential_id to
        // factor_id: a set of recovery codes is ten separate secrets under one credentials row and the
        // client derives a key-encryption key from each CODE, so keying the table on the credential
        // stored one pair of envelopes and refused the other nine. The primary key is now factor_id,
        // credential_id is an ordinary non-unique column, and the separate unique index over factor_id
        // is gone because the key carries that uniqueness alone. The baseline was regenerated under the
        // open window (CON-002 — the production database holds no data), so the moved key, the dropped
        // index and the table's grants and user_isolation policy land in one initial migration rather
        // than in a chain nothing will ever replay step by step. The move is deliberate and it carries
        // the same obligation every earlier one did: whoever regenerates the baseline resets
        // production's __EFMigrationsHistory in the same deploy (DEPLOYMENT.md, Step 3), or that deploy
        // fails on the first CREATE TABLE against a database that already holds the schema.
        //
        // And it moved again for session_tokens: the table a presented handle is looked up on has to
        // exist before any route can issue one, and it arrives with its own grants and its exemption
        // from row-level security rather than with a policy. Regenerated under the same open window
        // (CON-002 — the production database holds no data) so that this story's schema lands as one
        // initial migration rather than as a chain nothing will ever replay step by step, and it
        // carries the same obligation every earlier move did: whoever regenerates the baseline resets
        // production's __EFMigrationsHistory in the same deploy (DEPLOYMENT.md, Step 3), or that
        // deploy fails on the first CREATE TABLE against a database that already holds the schema.
        // Editing this literal by hand is the checkpoint; deriving it from the migrations directory
        // would waive it, which is what the first paragraph above is about.
        //
        // And it moved again for budgets.name becoming bytea. The column stops being text the client
        // hands over in the clear and starts being a sealed narrative field — an AEAD envelope the
        // browser produces under a content key the server never sees — so the change is a column TYPE
        // change rather than an added column, which is the one shape an additive migration cannot
        // express without a data step. There is no data step to write: the production database holds
        // no rows (CON-002), which is why the rebaseline window is open and why this lands as one
        // initial migration rather than as an alter that would have to invent ciphertext for names
        // that were never sealed. Three things arrive with the type: the case_insensitive collation
        // leaves the column, because bytea is not a collatable type and that is a forced consequence
        // rather than a decision anybody took; a length band rendered from
        // CiphertextEnvelope.MinimumLength and NarrativeFieldLimits.NameBytes; and a version check
        // spelled with substring rather than get_byte, for the reason BudgetConfiguration states
        // inline — get_byte raises 2202E on a zero-length bytea instead of answering false, and which
        // of a column's checks fires first is decided by the constraint NAME, so an idiom that
        // depends on "length" sorting before "version" is depending on an accident. The obligation is
        // the one every earlier move carried: whoever regenerates the baseline resets production's
        // __EFMigrationsHistory in the same deploy (DEPLOYMENT.md, Step 3), or that deploy fails on
        // the first CREATE TABLE against a database that already holds the schema.
        // And it moved again for accounts.name becoming bytea and for the name_key column arriving
        // beside it. The same type change budgets.name took a commit earlier, with one addition that
        // budgets does not have and will not get from this story: a blind index. accounts.name is a
        // sealed narrative field, so uniqueness over it would enforce nothing — every seal draws a
        // fresh nonce, and two rows holding one name hold different bytes — and accounts is the table
        // where "one name per budget" is a rule the product keeps. So the rule moved columns rather
        // than being dropped: IX_accounts_budget_id_name became IX_accounts_budget_id_name_key over
        // (budget_id, name_key), and the case_insensitive collation left the column, because bytea is
        // not collatable and case folding is now part of the normalisation the client applies before
        // it computes the HMAC. Three checks arrive with the pair — an equality on name_key's width,
        // a length band on name rendered from CiphertextEnvelope.MinimumLength and
        // NarrativeFieldLimits.NameBytes, and a version check spelled with substring for the reason
        // BudgetConfiguration already states inline. A column TYPE change and a NOT NULL column added
        // to a table are both shapes an additive migration cannot express without a data step, and
        // there is no data step to write: the production database holds no rows (CON-002), which is
        // why the rebaseline window is open and why this lands as one initial migration rather than
        // as an alter inventing ciphertext and digests for names that were never sealed. The
        // obligation is the one every earlier move carried: whoever regenerates the baseline resets
        // production's __EFMigrationsHistory in the same deploy (DEPLOYMENT.md, Step 3), or that
        // deploy fails on the first CREATE TABLE against a database that already holds the schema.
        // And it moved again for payees.name becoming bytea and payees.name_key arriving beside it —
        // the accounts move, verbatim, on the table where it matters most. accounts uniqueness is a
        // convenience; the payee index IS the deduplication of counterparties, because the client
        // resolves a name against the list it decrypted and mints a new payee when it finds no match.
        // So IX_payees_budget_id_name became IX_payees_budget_id_name_key over (budget_id, name_key),
        // the case_insensitive collation left the column by force, and the same three checks arrived.
        // WHAT THIS ONE ALSO TOOK AWAY, unlike its two predecessors: the server's ability to look a
        // payee up by name at all, which is what deleted find-or-create and made POST /api/payees a
        // route. A column type change and a NOT NULL column added to a populated table are shapes an
        // additive migration cannot express without a data step, and there is none to write for the
        // reason above. Same obligation, unchanged: whoever regenerates the baseline resets
        // production's __EFMigrationsHistory in the same deploy (DEPLOYMENT.md, Step 3).
        // And it moved a fourth time for category_groups, which is the first move to carry a column
        // the three before it did not have: category_groups.name becomes bytea with
        // category_groups.name_key beside it — the accounts and payees move verbatim, index repointed
        // to (budget_id, name_key), collation gone by force, the same three checks — AND
        // category_groups.description becomes the product's first sealed free-text column. That one is
        // NULLABLE, capped at NarrativeFieldLimits.DescriptionBytes rather than NameBytes, and carries
        // NO blind index, because a description is never looked up and a deterministic digest over
        // free text is a fingerprint nothing on the other side asked for. So this baseline declares
        // FIVE new checks on one table rather than three, and the two description ones are the first
        // in the schema that a NULL satisfies vacuously. WHAT THIS ONE ALSO TOOK AWAY: the server can
        // no longer refuse a blank or over-long group name OR description — it holds envelopes it
        // cannot count characters in — and, unique to the nullable column, a write path that decodes a
        // description and forgets to assign it writes a legal NULL row where the NOT NULL name would
        // have answered 23502. Same obligation, unchanged: whoever regenerates the baseline resets
        // production's __EFMigrationsHistory in the same deploy (DEPLOYMENT.md, Step 3).
        const string frozenBaselineId = "20260902093424_InitialCreate";
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

        // The column is bytea and carries NEITHER a max length NOR a collation, and both absences are
        // asserted rather than dropped. This test used to pin 200 and case_insensitive; the name is a
        // sealed narrative field now, so the length that bounds it is a CHECK over the envelope
        // (CK_budgets_name_length, pinned in Model_BoundsEveryValueRangeTheDatabaseCanCheck) and not a
        // varchar width, and a collation is not something bytea can carry at all. Asserting null on
        // both is the difference between "the pin was updated" and "the pin was deleted": a MaxLength
        // reappearing means somebody put a width back on a column whose contents are ciphertext, where
        // a width bounds the ENVELOPE and silently truncates the name it seals, and a collation
        // reappearing means the column went back to text.
        //
        // Read off the design-time model, for the reason stated above where that model is fetched: the
        // runtime read-optimized model drops annotation-backed values, so a null read there would be
        // indistinguishable from the null this asserts.
        await Assert.That(designTimeNameProperty.GetMaxLength()).IsNull();
        await Assert.That(designTimeNameProperty.GetCollation()).IsNull();
        await Assert.That(designTimeNameProperty.GetColumnType()).IsEqualTo("bytea");
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
