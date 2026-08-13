using Infrastructure.Persistence.Provisioning;
using TestSupport;

namespace UnitTests;

/// <summary>
/// Covers the vocabulary that names the columns, relations and request members the server must never
/// hold: an unwrapped key, a key-encryption key, a PRF output, or a recovery code. FR-063 is the
/// clause — "no unwrapped key, key-encryption key, PRF output or recovery code reaches the server" —
/// and the four categories are its own four nouns in its own order, so a reviewer meeting a red is
/// handed the sentence they tripped rather than a bucket name somebody invented.
/// </summary>
/// <remarks>
/// <para>
/// <b>FR-063 and FR-064 are Inspection requirements and this file does not replace that.</b> The
/// census is a deliberate strengthening: it repeats on every commit the part of the inspection a
/// person cannot, which is noticing a <i>new</i> name. It says nothing about the part that matters
/// most — what a value is rather than what it is called — and the vocabulary's own remarks name the
/// two gaps that follow from reading names.
/// </para>
/// <para>
/// The tests here are a pair of opposing forces, and the second kind is what makes the first mean
/// anything. <see cref="Vocabulary_MatchesANameFromEachSecretCategory" /> shows the vocabulary can
/// still recognise something; on its own it is satisfied by a pattern far wider than anyone
/// intended. The three <c>DoesNotRefuse</c> tests close that gap by hand — a rule widened from
/// <c>content_key</c> to the bare token <c>key</c> passes the first test and detonates on
/// <c>public_key_cose</c>, and only they say so. What none of them can do is notice a name that
/// arrives <i>after</i> this file was written, which is why the schema-wide and request-surface
/// scans live next door in <c>IntegrationTests/KeyMaterialSecrecyTests</c> and read the same list.
/// </para>
/// <para>
/// <b>The permit/refuse pairs are the centre of this file.</b>
/// <see cref="Vocabulary_RefusesAnUnsealedKeyNameAndPermitsItsSealedSpelling" /> reads both
/// directions of the one distinction the vocabulary exists to make, and it is the test that fails
/// both ways: a rule that cannot tell <c>content_key</c> from <c>wrapped_content_key</c> refuses the
/// schema this story shipped, and one that permits both is worthless. Neither direction alone would
/// have caught the other.
/// </para>
/// <para>
/// This is a <b>sibling</b> of <see cref="ProhibitedColumnVocabularyTests" /> and
/// <see cref="ErasureRemnantVocabularyTests" />, and the shape is mirrored deliberately: the three
/// answer different questions over the same identifiers by the same mechanism, so a divergence
/// between the test files reads as an oversight unless it is argued. Two divergences are argued
/// where they occur — the reachability test is generative here rather than a hand-written row per
/// rule, because the qualifier mechanism gives a rule a new way to become unreachable that neither
/// sibling has; and the qualifier tests have no counterpart at all, for the same reason.
/// </para>
/// </remarks>
public sealed class UnwrappedKeyMaterialVocabularyTests
{
    /// <summary>
    /// Every way a name says the server is holding something that would unlock an account, in each
    /// of the four categories and in the spellings a column, a relation and a request member arrive
    /// in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The camel-cased rows are not decoration.</b> This vocabulary is the first of the three read
    /// over reflected <i>member</i> names as well as over catalog names, and a request member is
    /// PascalCase — <c>WrappedContentKey</c>, never <c>wrapped_content_key</c>. What makes the two
    /// reach the same rule is <see cref="IdentifierTokens.Tokenize" /> splitting on case boundaries
    /// as well as on underscores, and these rows are what holds that in place from this side.
    /// </para>
    /// <para>
    /// <b>The plural rows are load-bearing on a name that exists today.</b>
    /// <see cref="IdentifierTokens.ContainsRun" /> compares whole tokens and does not stem, so
    /// <c>account_keys</c> reaches no rule <c>account_key</c> reaches; the vocabulary compiles every
    /// pattern in the plural too, and <c>account_keys</c> is the row that pins it. Every table this
    /// schema maps is named in the plural, so the plural is the spelling a key-material <i>relation</i>
    /// would arrive in.
    /// </para>
    /// <para>
    /// <c>kek_bytes</c> earns its own row against the temptation to delete the <c>kek</c> rule as
    /// redundant beside <c>key_encryption_key</c>. It carries no <c>encryption</c> token at all, so
    /// the longer rule cannot see it, and an abbreviation is exactly what a column name reaches for.
    /// </para>
    /// <para>
    /// <c>unwrapped_content_key</c> is the sharpest row here, and it belongs in this test rather than
    /// among the permitted spellings: it carries the qualifier's token as a <i>substring</i> of its
    /// first token and must still be refused, because <c>unwrapped</c> and <c>wrapped</c> are
    /// different tokens under an ordinal whole-token comparison. A matcher that had drifted into
    /// substring or suffix matching to make <c>wrapped_content_key</c> pass would let this one
    /// through with it, and nothing else in the file would notice.
    /// </para>
    /// </remarks>
    [Test]
    [Arguments("content_key", UnwrappedKeyMaterialCategory.UnwrappedKey)]
    [Arguments("contentKey", UnwrappedKeyMaterialCategory.UnwrappedKey)]
    [Arguments("index_key", UnwrappedKeyMaterialCategory.UnwrappedKey)]
    [Arguments("indexKey", UnwrappedKeyMaterialCategory.UnwrappedKey)]
    [Arguments("account_key", UnwrappedKeyMaterialCategory.UnwrappedKey)]
    [Arguments("account_keys", UnwrappedKeyMaterialCategory.UnwrappedKey)]
    [Arguments("unwrapped_content_key", UnwrappedKeyMaterialCategory.UnwrappedKey)]
    [Arguments("unwrapped_key", UnwrappedKeyMaterialCategory.UnwrappedKey)]
    [Arguments("plaintext_key", UnwrappedKeyMaterialCategory.UnwrappedKey)]
    [Arguments("key_encryption_key", UnwrappedKeyMaterialCategory.KeyEncryptionKey)]
    [Arguments("keyEncryptionKey", UnwrappedKeyMaterialCategory.KeyEncryptionKey)]
    [Arguments("kek", UnwrappedKeyMaterialCategory.KeyEncryptionKey)]
    [Arguments("kek_bytes", UnwrappedKeyMaterialCategory.KeyEncryptionKey)]
    [Arguments("wrapping_key", UnwrappedKeyMaterialCategory.KeyEncryptionKey)]
    [Arguments("master_key", UnwrappedKeyMaterialCategory.KeyEncryptionKey)]
    [Arguments("prf_output", UnwrappedKeyMaterialCategory.PrfOutput)]
    [Arguments("prfOutput", UnwrappedKeyMaterialCategory.PrfOutput)]
    [Arguments("prf_secret", UnwrappedKeyMaterialCategory.PrfOutput)]
    [Arguments("prf_result", UnwrappedKeyMaterialCategory.PrfOutput)]
    [Arguments("prf_results", UnwrappedKeyMaterialCategory.PrfOutput)]
    [Arguments("recovery_code", UnwrappedKeyMaterialCategory.RecoveryCode)]
    [Arguments("recoveryCode", UnwrappedKeyMaterialCategory.RecoveryCode)]
    [Arguments("recovery_codes", UnwrappedKeyMaterialCategory.RecoveryCode)]
    [Arguments("backup_code", UnwrappedKeyMaterialCategory.RecoveryCode)]
    public async Task Vocabulary_MatchesANameFromEachSecretCategory(
        string identifier,
        UnwrappedKeyMaterialCategory expectedCategory)
    {
        // Arrange — the argument rows above are the subject; each is a name that would say the server
        // is holding a value by which a wrapped account key could be unwrapped.

        // Act — the rule the name trips, read for its category. The whole rule comes back so a caller
        // scanning a schema or a request surface can print the argument for the refusal; this test
        // only wants the bucket.
        UnwrappedKeyMaterialCategory? category =
            UnwrappedKeyMaterialVocabulary.Classify(identifier)?.Category;

        // Assert — the category rather than merely "not null". The four nouns are four different
        // secrets with four different remedies: an unwrapped key is a value that should have been
        // sealed before it left the browser, a PRF output is a value that should never have been read
        // out of the ceremony at all. A classifier that swept every name into one bucket would pass a
        // non-null check while telling a reviewer nothing about which of those they are looking at.
        await Assert.That(category).IsEqualTo(expectedCategory);
    }

    /// <summary>
    /// The sealed spelling of a key passes and the unsealed one does not — both directions of the one
    /// distinction this vocabulary exists to make.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two rows per pair, and the test is worthless without both.</b> A vocabulary that refuses
    /// <c>content_key</c> and <c>wrapped_content_key</c> alike reds the schema this story shipped,
    /// and the cheapest way out of that red is to delete the rule — so the permitting half is what
    /// keeps the refusing half alive. A vocabulary that permits both is a decoration, and the
    /// refusing half is what says so.
    /// </para>
    /// <para>
    /// <b>The distinction cannot be made by a pattern.</b> <see cref="IdentifierTokens.ContainsRun" />
    /// asks only whether a run occurs, and <c>content / key</c> occurs in both names; nor is there a
    /// narrower pattern available, because the refused name's tokens are a subset of the permitted
    /// one's. So the vocabulary carries two mechanisms — a pattern, and a permitted qualifier that
    /// has to sit immediately beside an occurrence — and these rows are the only place both are read
    /// together against the exact names the schema and the request surface use.
    /// </para>
    /// <para>
    /// <c>wrapped_account_keys</c> is here as the third pair because it is a <b>relation</b> in the
    /// live schema and it needs two expansions at once to pass: the pattern's plural to be reached at
    /// all, and the qualifier's singular to let it go. Nothing else in this file exercises that
    /// combination, and backing either expansion out reds the shipped table.
    /// </para>
    /// <para>
    /// <c>recovery_code_hashes</c> is the pair that runs the other way round — the qualifier
    /// <i>follows</i> the pattern, and it is the pattern's singular beside the qualifier's plural.
    /// Two positions exist because these two exemptions sit on opposite sides, and a single symmetric
    /// "adjacent" rule would have permitted <c>content_key_wrapped</c>, which reads like a boolean
    /// flag sitting next to a key rather than like a sealed value.
    /// </para>
    /// </remarks>
    [Test]
    [Arguments("content_key", "wrapped_content_key")]
    [Arguments("contentKey", "wrappedContentKey")]
    [Arguments("index_key", "wrapped_index_key")]
    [Arguments("indexKey", "wrappedIndexKey")]
    [Arguments("account_keys", "wrapped_account_keys")]
    [Arguments("recovery_code", "recovery_code_hash")]
    [Arguments("recovery_codes", "recovery_code_hashes")]
    public async Task Vocabulary_RefusesAnUnsealedKeyNameAndPermitsItsSealedSpelling(
        string refused,
        string permitted)
    {
        // Arrange — the argument rows above are the subject; each pair is one name the server must
        // never carry beside the qualified spelling of it the server does carry today.

        // Act
        UnwrappedKeyMaterialRule? refusedRule = UnwrappedKeyMaterialVocabulary.Classify(refused);
        UnwrappedKeyMaterialRule? permittedRule = UnwrappedKeyMaterialVocabulary.Classify(permitted);

        // Assert — the refusal first, so a vocabulary that stopped matching anything fails here
        // rather than passing the permitting line for the wrong reason. Then the permission: a rule
        // here is the deny-list having taken a column or a member that ships today, which reds the
        // build with no remedy except deleting the rule.
        await Assert.That(refusedRule).IsNotNull();
        await Assert.That(permittedRule).IsNull();
    }

    /// <summary>
    /// A qualifier only excuses the rule that carries it, and only from the side that rule permits.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The qualifier mechanism is the one thing in this vocabulary that can make the list
    /// <i>weaker</i>, so it needs a boundary as much as it needs a demonstration. Three boundaries
    /// are read here.
    /// </para>
    /// <para>
    /// <b>It does not travel between rules.</b> <c>wrapped</c> excuses the three key names because
    /// those are the values this design does store sealed; there is no design in which a
    /// key-encryption key, a PRF output or a recovery code exists wrapped, so
    /// <c>wrapped_key_encryption_key</c>, <c>wrapped_prf_output</c> and <c>wrapped_recovery_code</c>
    /// stay refused. A qualifier declared once for the whole vocabulary would have permitted all
    /// three, and the name that would then walk in is <c>wrapped_prf_output</c> — which sounds
    /// reassuring and is a PRF output on the server.
    /// </para>
    /// <para>
    /// <b>It does not travel between sides.</b> <c>content_key_wrapped</c> and
    /// <c>hash_recovery_code</c> each carry the right word on the wrong side and are refused, which
    /// is why <see cref="QualifierPosition" /> exists rather than a bare "adjacent".
    /// </para>
    /// <para>
    /// <b>It has to be adjacent.</b> <c>wrapped_at_content_key</c> carries <c>wrapped</c> and is
    /// still refused, because whatever that column is, the <c>wrapped</c> is describing an <c>at</c>.
    /// This is the same claim <see cref="IdentifierTokens.ContainsRun" /> makes about a phrase
    /// pattern, applied to the exemption.
    /// </para>
    /// </remarks>
    [Test]
    [Arguments("wrapped_key_encryption_key")]
    [Arguments("wrapped_prf_output")]
    [Arguments("wrapped_recovery_code")]
    [Arguments("wrapped_kek")]
    [Arguments("content_key_wrapped")]
    [Arguments("hash_recovery_code")]
    [Arguments("wrapped_at_content_key")]
    public async Task Vocabulary_DoesNotLetAQualifierExcuseARuleThatDoesNotCarryIt(string identifier)
    {
        // Arrange — the argument rows above are the subject; each carries a permitted word beside the
        // wrong rule, on the wrong side, or at the wrong distance.

        // Act
        UnwrappedKeyMaterialRule? rule = UnwrappedKeyMaterialVocabulary.Classify(identifier);

        // Assert — not null. A null here is the qualifier having widened into a general amnesty for
        // any name that mentions the word, which is the one change to this file that makes it look
        // stricter while making it weaker.
        await Assert.That(rule).IsNotNull();
    }

    /// <summary>
    /// Every rule can be reached, and each answers with itself.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Generative rather than a hand-written row per rule, which is where this file diverges from
    /// both siblings, and the divergence is the qualifier mechanism. A rule there can only become
    /// unreachable by being shadowed by another pattern; a rule <i>here</i> can also become
    /// unreachable by acquiring a qualifier so wide that nothing is left for it to refuse. A written
    /// list of names would grow a row per rule and would be edited in the same commit as the rule
    /// that broke it; asking every rule to answer for its own bare pattern cannot be.
    /// </para>
    /// <para>
    /// The bare pattern is the right probe because it is the name with no context at all — nothing
    /// precedes it and nothing follows it, so no qualifier can apply and the rule has to answer if it
    /// is reachable by anything. That it answers with <i>itself</i> rather than merely with something
    /// is what makes this a shadowing check as well as a reachability one: a pattern swallowed by an
    /// earlier rule would come back under that rule's name.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Vocabulary_AnswersEveryRuleWithItselfOnItsOwnPattern()
    {
        // Arrange
        IReadOnlyList<UnwrappedKeyMaterialRule> rules = UnwrappedKeyMaterialVocabulary.Rules;

        // Act — reported by pattern and by the rule that answered, so a failure names both sides of
        // the collision rather than saying that one exists.
        string[] unreachable = rules
            .Select(rule => (Rule: rule, Answer: UnwrappedKeyMaterialVocabulary.Classify(rule.Pattern)))
            .Where(match => !ReferenceEquals(match.Answer, match.Rule))
            .Select(match => $"{match.Rule.Pattern} is answered by {match.Answer?.Pattern ?? "nothing"}")
            .Order(StringComparer.Ordinal)
            .ToArray();

        // Assert — the non-empty check first: an empty vocabulary has no unreachable rule either, and
        // would pass the real assertion with nothing in it.
        await Assert.That(rules).IsNotEmpty();
        await Assert.That(unreachable).IsEmpty();
    }

    /// <summary>
    /// No pattern's tokens are a contiguous run inside another pattern's, so no rule can shadow a
    /// rule listed after it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The subject's remarks claim the ordering of its list carries no meaning; this is what makes
    /// that fail rather than persuade. The candidate that would break it is the bare token
    /// <c>key</c>, which would swallow <c>content_key</c>, <c>index_key</c>, <c>account_key</c>,
    /// <c>unwrapped_key</c>, <c>plaintext_key</c>, <c>key_encryption_key</c>, <c>wrapping_key</c>
    /// and <c>master_key</c> — collapsing eight arguments into one and, worse, taking the
    /// <c>wrapped</c> qualifier with them, since a shadowing rule answers with its own exemptions
    /// rather than the swallowed rule's. <c>wrapped_content_key</c> would then be refused by a rule
    /// that never heard of the word <c>wrapped</c>.
    /// </para>
    /// <para>
    /// It reads the written patterns rather than the compiled ones, as the prohibited-column sibling
    /// does and for its reason: the compiled list is private and holds each pattern twice, and the
    /// written list is the one a reviewer edits, so it is the one a red has a remedy on.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Vocabulary_HasNoPatternThatShadowsAnother()
    {
        // Arrange — every pattern tokenized the way Classify tokenizes it, so the comparison is the
        // one the matcher will actually make rather than a comparison of the written strings.
        (string Pattern, string[] Tokens)[] patterns = UnwrappedKeyMaterialVocabulary.Rules
            .Select(rule => (rule.Pattern, Tokens: IdentifierTokens.Tokenize(rule.Pattern)))
            .ToArray();

        // Act — every ordered pair, because shadowing has a direction: the shorter run swallows the
        // longer one and never the other way round.
        string[] shadowed = patterns
            .SelectMany(outer => patterns
                .Where(inner => !ReferenceEquals(outer.Tokens, inner.Tokens)
                                && IdentifierTokens.ContainsRun(inner.Tokens, outer.Tokens))
                .Select(inner => $"{outer.Pattern} shadows {inner.Pattern}"))
            .Order(StringComparer.Ordinal)
            .ToArray();

        // Assert — the pairs by name. A count would say a rule is unreachable; the pair says which
        // argument was swallowed by which, and that is the one the new pattern has to answer.
        await Assert.That(patterns).IsNotEmpty();
        await Assert.That(shadowed).IsEmpty();
    }

    /// <summary>
    /// Every rule carries an argument long enough to be one.
    /// </summary>
    /// <remarks>
    /// The floor is both siblings', at forty characters, and it is met rather than restated as a
    /// shared constant because neither sibling shares it — each test file spells the same number in
    /// its own <c>Where</c> clause. Copying that shape keeps the three files comparable; extracting
    /// the number into <c>TestSupport</c> would put a rule in one place that is currently three
    /// independent statements of the same standard, and the first vocabulary to want a different bar
    /// would have to unpick it. A reason short enough to fit in a few words is a restatement of the
    /// verdict — "secret", "not allowed" — and the threshold is what separates an argument a
    /// reviewer can weigh from a label they can only obey.
    /// </remarks>
    [Test]
    public async Task Vocabulary_StatesAReasonForEveryRule()
    {
        // Arrange
        IReadOnlyList<UnwrappedKeyMaterialRule> rules = UnwrappedKeyMaterialVocabulary.Rules;

        // Act — reported by pattern rather than counted, so a failure names the entry to argue about.
        string[] unexplained = rules
            .Where(rule => string.IsNullOrWhiteSpace(rule.Reason) || rule.Reason.Length < 40)
            .Select(rule => rule.Pattern)
            .ToArray();

        // Assert — the non-empty check first: an empty vocabulary has no unexplained entry either,
        // and would pass the real assertion with nothing in it. A rule whose reason is blank is one
        // nobody argued for, and the next person to hit a false positive has nothing to weigh.
        await Assert.That(rules).IsNotEmpty();
        await Assert.That(unexplained).IsEmpty();
    }

    /// <summary>
    /// Every permitted qualifier is one a rule can actually use.
    /// </summary>
    /// <remarks>
    /// A qualifier is the only thing on this list that removes a refusal, so a broken one fails
    /// silently in the dangerous direction — either it never applies, leaving a shipped column red
    /// with no explanation, or it applies far more widely than its rule's reason claims. Two shapes
    /// are refused here. An <b>empty or whitespace</b> qualifier tokenizes to nothing, and a
    /// zero-length run beside an occurrence would be trivially satisfiable at every offset, which
    /// would turn one exemption into a general amnesty for its rule. A qualifier that is <b>a run
    /// inside its own rule's pattern</b> is the subtler one: <c>key</c> beside <c>content_key</c>
    /// would be matched by the pattern's own tokens and permit the very name the rule is written for.
    /// </remarks>
    [Test]
    public async Task Vocabulary_HasNoQualifierThatCouldNotDoItsJob()
    {
        // Arrange — every qualifier with the rule that carries it, both tokenized the way the matcher
        // reads them.
        (string Pattern, string Qualifier, string[] PatternTokens, string[] QualifierTokens)[]
            qualifiers = UnwrappedKeyMaterialVocabulary.Rules
                .SelectMany(rule => rule.PermittedQualifiers.Select(qualifier => (
                    rule.Pattern,
                    Qualifier: qualifier.Tokens,
                    PatternTokens: IdentifierTokens.Tokenize(rule.Pattern),
                    QualifierTokens: IdentifierTokens.Tokenize(qualifier.Tokens))))
                .ToArray();

        // Act
        string[] broken = qualifiers
            .Where(entry => entry.QualifierTokens.Length == 0
                            || IdentifierTokens.ContainsRun(entry.PatternTokens, entry.QualifierTokens))
            .Select(entry => $"{entry.Pattern} permits '{entry.Qualifier}'")
            .Order(StringComparer.Ordinal)
            .ToArray();

        // Assert — the non-empty check first, and it says something real here rather than guarding a
        // vacuous pass: the whole design rests on there being at least one exemption, and a list that
        // had lost every qualifier would refuse wrapped_content_key while passing this test.
        await Assert.That(qualifiers).IsNotEmpty();
        await Assert.That(broken).IsEmpty();
    }

    /// <summary>
    /// A name the model, the catalog or a reflected type does not have is answered with nothing
    /// rather than an exception.
    /// </summary>
    /// <remarks>
    /// Fail-open is the right direction here and only here: this is a classifier reading names out of
    /// a catalog, a model or a type, not a validator of its caller's arguments, and a scan that threw
    /// partway through would report <i>fewer</i> offenders than exist. The parameter is nullable
    /// because its callers' inputs are — <c>IEntityType.GetTableName()</c> and
    /// <c>IProperty.GetColumnName()</c> both answer null for something mapped nowhere.
    /// </remarks>
    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    public async Task Vocabulary_AnswersNothingForANameThatIsNotThere(string? identifier)
    {
        // Arrange — the argument rows above are the subject; each is what a model read hands a caller
        // for something mapped to no table or column at all.

        // Act
        UnwrappedKeyMaterialRule? rule = UnwrappedKeyMaterialVocabulary.Classify(identifier);

        // Assert — null, and no exception. A throw here is a scan that stops at the first unmapped
        // entity and reports a clean schema for the rest of it.
        await Assert.That(rule).IsNull();
    }

    /// <summary>
    /// The binary columns this schema carries today are values that unwrap nothing, and every one of
    /// them stays permitted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each of these is a real column name in the live schema, and each is named here rather than
    /// left to the catalog scan because the catalog scan covers them silently, as entries in a list
    /// nobody reads. Naming them puts them where somebody widening a pattern will look.
    /// </para>
    /// <para>
    /// <b><c>verifier_hash</c></b> is SHA-256 of a verifier the client derived from a recovery code.
    /// Two one-way steps and a different HKDF <c>info</c> stand between it and the key-encryption key
    /// the same code produces, which is the entire reason the column is storable at all — and it is
    /// the name a widened <c>recovery_code</c> rule would take first.
    /// <b><c>recovery_code_hashes</c></b> is the relation it sits on, and the qualified spelling the
    /// <c>recovery_code</c> rule exists to let through.
    /// </para>
    /// <para>
    /// <b><c>public_key_cose</c> and <c>cose_key</c></b> are <i>public</i> keys, published by design.
    /// They verify a signature and decrypt nothing, and they are the pair a rule on the bare token
    /// <c>key</c> would take — which is the widening this vocabulary is most likely to be offered.
    /// <b><c>webauthn_credential_id</c></b> is the authenticator's opaque handle: it selects which
    /// public key to verify against and is not a key.
    /// <b><c>signature_counter</c></b> is a monotonic integer a passkey reports so a clone can be
    /// caught, and nothing is derived from it.
    /// </para>
    /// <para>
    /// <b><c>challenge</c></b> is a 32-byte nonce the server minted for one ceremony and expires. It
    /// is the one value here the server generated itself, which is what makes it look like a secret
    /// worth refusing; it is not, because the client signs over it and nothing is ever derived from
    /// it.
    /// </para>
    /// <para>
    /// <b><c>factor_id</c> and <c>credential_id</c></b> are identifiers. <c>factor_id</c> is the
    /// value the associated data binds a wrapped key to, so it is genuinely part of the cryptographic
    /// contract and still holds no secret — knowing which factor an envelope was sealed against
    /// brings nobody a step closer to opening it, which is why
    /// <c>docs/business-logic/account-keys.md</c> has the client mint it in the clear.
    /// </para>
    /// <para>
    /// <b><c>code</c>, <c>currency_code</c> and <c>base_currency_code</c></b> are why the bare token
    /// <c>code</c> is not a pattern. <c>currencies.code</c> is a three-letter ISO currency and the
    /// primary key of a reference table, and a rule on the token would red the oldest table in the
    /// schema in order to catch a name — <c>recovery_code</c> — that a phrase already catches.
    /// </para>
    /// </remarks>
    [Test]
    [Arguments("verifier_hash")]
    [Arguments("recovery_code_hashes")]
    [Arguments("signature_counter")]
    [Arguments("cose_key")]
    [Arguments("public_key_cose")]
    [Arguments("cose_algorithm")]
    [Arguments("webauthn_credential_id")]
    [Arguments("challenge")]
    [Arguments("factor_id")]
    [Arguments("credential_id")]
    [Arguments("code")]
    [Arguments("currency_code")]
    [Arguments("base_currency_code")]
    public async Task Vocabulary_DoesNotRefuseTheValuesTheServerLegitimatelyHolds(string identifier)
    {
        // Arrange — the argument rows above are the subject; each is a column or relation name the
        // live schema carries today, or the token that would take one with it.

        // Act
        UnwrappedKeyMaterialRule? rule = UnwrappedKeyMaterialVocabulary.Classify(identifier);

        // Assert — null. A rule here is a pattern that has widened past the secret it was written for
        // and into a value the server is supposed to hold, and the remedy is to narrow the pattern
        // rather than to drop the column.
        await Assert.That(rule).IsNull();
    }

    /// <summary>
    /// The members the two write paths legitimately carry stay permitted, including the one that
    /// mentions the PRF extension by name.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These are request member names rather than column names, and they are the reason the bare
    /// token <c>prf</c> is deliberately legal. <c>clientExtensionResults.prf.enabled</c> is a claim a
    /// browser makes about a device — the registration gate reads it and refuses an authenticator
    /// that reports no enabled result — and it carries no bytes at all. A rule on <c>prf</c> would
    /// red the gate that exists to make PRF mandatory, which is as backwards as a refusal gets.
    /// </para>
    /// <para>
    /// <c>wrappedContentKey</c> and <c>wrappedIndexKey</c> are here in their wire spelling as well as
    /// in the column spelling the pair test uses, because the two reach the same rule only through
    /// case-boundary tokenization and this is the only place the member form is read beside the
    /// members it travels with.
    /// </para>
    /// <para>
    /// <c>verifiers</c> is the sharpest of these. A verifier is derived from a recovery code and
    /// really does cross the wire, so it is the one member on either write path that a reader might
    /// expect this vocabulary to refuse. It is permitted because a verifier is not a code: the server
    /// stores a hash of it, the key-encryption key comes off a different HKDF branch, and refusing
    /// the name would refuse the only value a recovery-code set can be established with.
    /// </para>
    /// </remarks>
    [Test]
    [Arguments("prf")]
    [Arguments("enabled")]
    [Arguments("clientExtensionResults")]
    [Arguments("attestationObject")]
    [Arguments("clientDataJson")]
    [Arguments("authenticatorData")]
    [Arguments("signature")]
    [Arguments("userHandle")]
    [Arguments("factorId")]
    [Arguments("wrappedContentKey")]
    [Arguments("wrappedIndexKey")]
    [Arguments("verifier")]
    [Arguments("verifiers")]
    public async Task Vocabulary_DoesNotRefuseTheMembersTheWritePathsCarry(string identifier)
    {
        // Arrange — the argument rows above are the subject; each is a member name a registration, an
        // assertion or a recovery-code generation carries today.

        // Act
        UnwrappedKeyMaterialRule? rule = UnwrappedKeyMaterialVocabulary.Classify(identifier);

        // Assert — null. A rule here refuses a member the API already accepts, so the red arrives on
        // correct code and the cheapest way out of it is to delete the rule.
        await Assert.That(rule).IsNull();
    }

    /// <summary>
    /// The words a later epic will need for the encryption this key custody exists to serve stay
    /// permitted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// None of these names exist in the schema. They are what arrives when the narrative is actually
    /// encrypted and the blind index is actually built — a ciphertext column, a nonce, an
    /// authentication tag, an envelope version, a blind index value. This control is written
    /// <b>before</b> those columns exist, and that is the whole point of it: whoever builds them
    /// would otherwise meet this vocabulary as an unexplained red bar on their own branch, which is
    /// the worst possible moment to start arguing about what a deny-list should contain, because the
    /// cheapest way out of a red on a branch is to widen or delete the rule.
    /// </para>
    /// <para>
    /// The distinction the vocabulary has to keep is that a <i>ciphertext</i> and the public
    /// parameters that open it under a key nobody here holds are exactly what this design wants
    /// stored. Only the key is refused. A rule shaped as "anything to do with encryption" would take
    /// every one of these and would refuse the feature in order to protect it.
    /// </para>
    /// </remarks>
    [Test]
    [Arguments("ciphertext")]
    [Arguments("encrypted_description")]
    [Arguments("nonce")]
    [Arguments("auth_tag")]
    [Arguments("envelope_version")]
    [Arguments("blind_index")]
    [Arguments("payee_name_index")]
    [Arguments("key_version")]
    public async Task Vocabulary_DoesNotRefuseTheColumnsTheEncryptionEpicWillCarry(string identifier)
    {
        // Arrange — the argument rows above are the subject; each is a name the encryption this key
        // custody exists to serve would bring, if this product grew it tomorrow.

        // Act
        UnwrappedKeyMaterialRule? rule = UnwrappedKeyMaterialVocabulary.Classify(identifier);

        // Assert — null. A rule here is this vocabulary refusing the capability the requirement asks
        // for, and it has to be argued out here rather than on the branch that builds it.
        await Assert.That(rule).IsNull();
    }
}
