using Infrastructure.Persistence.Provisioning;

namespace TestSupport;

/// <summary>
/// The way in which a column, relation or member name says the server is holding a value that would
/// unlock an account.
/// </summary>
/// <remarks>
/// The four are FR-063's own four nouns, in the requirement's order — "no unwrapped key,
/// key-encryption key, PRF output or recovery code reaches the server". Derived from the clause
/// rather than invented, so a reviewer facing a red is handed the exact sentence they tripped instead
/// of a bucket name somebody chose. The category is not decoration on a yes/no answer either: the
/// remedy differs per noun. An unwrapped key is a value that should have been sealed before it left
/// the browser, and is fixed by wrapping it; a PRF output is a value that should never have been read
/// out of the ceremony at all, and is fixed by deriving in the browser and sending nothing.
/// </remarks>
public enum UnwrappedKeyMaterialCategory
{
    /// <summary>The account's content key or index key, out of its envelope.</summary>
    UnwrappedKey,

    /// <summary>The key a wrapped envelope opens under, derived from a recovery factor.</summary>
    KeyEncryptionKey,

    /// <summary>The authenticator's PRF evaluation — the passkey branch's input keying material.</summary>
    PrfOutput,

    /// <summary>A recovery code itself, as opposed to a hash of a verifier derived from one.</summary>
    RecoveryCode,
}

/// <summary>Which side of a pattern's tokens a permitted qualifier has to sit on.</summary>
/// <remarks>
/// Two positions rather than one "adjacent", because the two shipped exemptions sit on opposite
/// sides and neither argument transfers to the other. <c>wrapped_content_key</c> is sealed by the
/// word in <i>front</i> of it; <c>recovery_code_hashes</c> is made harmless by the word
/// <i>after</i> it. A single symmetric mechanism would silently permit <c>content_key_wrapped</c>,
/// which reads far more like a boolean flag beside the key than like a sealed value, and
/// <c>hash_recovery_code</c>, which reads like a code somebody is about to hash.
/// </remarks>
public enum QualifierPosition
{
    /// <summary>The qualifier's tokens end immediately before the pattern's first token.</summary>
    Preceding,

    /// <summary>The qualifier's tokens begin immediately after the pattern's last token.</summary>
    Following,
}

/// <summary>
/// A token run that, sitting immediately beside a pattern, means the name is not the thing the
/// pattern refuses.
/// </summary>
/// <remarks>
/// <para>
/// <b>A qualifier is not a second pattern and is not a negative lookahead over the whole name.</b>
/// It is matched exactly the way a pattern is — whole tokens, ordinal, no stemming, through
/// <see cref="IdentifierTokens" /> — and it only ever answers about <i>one</i> occurrence of the
/// pattern. A name carrying the pattern twice, once qualified and once not, is refused on the
/// unqualified one. That is the fail-closed direction and it is the whole reason the check is a loop
/// over offsets rather than a call to <see cref="IdentifierTokens.ContainsRun" /> and a second call
/// asking whether the qualifier appears anywhere.
/// </para>
/// <para>
/// Adjacency is the rule, and it is the same claim <see cref="IdentifierTokens.ContainsRun" /> makes
/// about a phrase pattern: a qualifier is a word about the token beside it, not a word in the bag.
/// <c>wrapped_at_content_key</c> carries <c>wrapped</c> and is still refused, because whatever that
/// column is, the <c>wrapped</c> is describing an <c>at</c>.
/// </para>
/// </remarks>
/// <param name="Tokens">
/// One token, or several joined by <c>_</c>, tokenized the way a pattern is.
/// </param>
/// <param name="Position">Which side of the pattern's run the qualifier has to sit on.</param>
public sealed record IdentifierQualifier(string Tokens, QualifierPosition Position);

/// <summary>
/// One key-material naming pattern, the category it names, the argument for refusing it, and the
/// qualifiers that make it legal.
/// </summary>
/// <remarks>
/// <para>
/// <paramref name="Reason" /> is the member that keeps this list arguable rather than merely obeyed,
/// exactly as on <see cref="ErasureRemnantRule" /> and <see cref="ProhibitedColumnRule" />. Every
/// entry here will one day red a name somebody had a reason to add, and the only thing that can be
/// weighed against their reason at that moment is the one written down when the pattern was added. A
/// reason reading "secret" restates the verdict and gives the next reader nothing to disagree with.
/// It is a whole rule rather than a category that
/// <see cref="UnwrappedKeyMaterialVocabulary.Classify" /> hands back, so the reason can reach the
/// sentence a red is read in instead of waiting in a file the reader has to know to open.
/// </para>
/// <para>
/// <b><see cref="PermittedQualifiers" /> is an <c>init</c> property rather than a fourth positional
/// parameter</b>, because the overwhelming majority of these rules have none and a trailing
/// <c>[]</c> on eleven of fourteen entries is a line every reader has to check and nobody can learn
/// anything from. Where a rule does carry one, the object initializer puts the exemption on its own
/// line right under the reason that has to account for it.
/// </para>
/// </remarks>
/// <param name="Pattern">
/// One token, or several joined by <c>_</c>, matched against an identifier's tokens as described on
/// <see cref="UnwrappedKeyMaterialVocabulary" />. Not a substring and not a regular expression.
/// </param>
/// <param name="Category">Which of the four secrets this pattern is an instance of.</param>
/// <param name="Reason">
/// What the pattern names and why a server holding it could open an account — prose a reviewer can
/// argue with, in the sentence the refusal will be read next to.
/// </param>
public sealed record UnwrappedKeyMaterialRule(
    string Pattern,
    UnwrappedKeyMaterialCategory Category,
    string Reason)
{
    /// <summary>
    /// The qualifiers beside which this pattern is not the secret it otherwise names. Empty for
    /// every rule that has no legal spelling at all.
    /// </summary>
    public IReadOnlyList<IdentifierQualifier> PermittedQualifiers { get; init; } = [];
}

/// <summary>
/// The single spelling of the column, relation and member names that would say the server is holding
/// a value by which a wrapped account key could be unwrapped.
/// </summary>
/// <remarks>
/// <para>
/// <b>This census is a deliberate strengthening of an Inspection requirement, and it is not the whole
/// of it.</b> FR-063 and FR-064 are verified by Inspection in the specification: a person reads the
/// request surface and the schema and states that neither carries key material. Turning that into an
/// executable scan buys the part a person cannot repeat on every commit — a new column or a new
/// request member reaching a reviewer's eye — and buys nothing about the part that matters most,
/// which is what a value <i>is</i> rather than what it is called. A column named <c>blob</c> holding
/// a PRF output passes every line of this file. Read the two named gaps at the bottom of these
/// remarks before treating a green run as the requirement being met.
/// </para>
/// <para>
/// This is a <b>sibling</b> of <see cref="ProhibitedColumnVocabulary" /> and
/// <see cref="ErasureRemnantVocabulary" />, and deliberately not a fifth category on either. Those
/// two ask "what does the schema keep <i>about a person</i>" and "would this row outlive its own
/// erasure"; this one asks a third question — "could this name hold a secret that would unlock the
/// account". The three verdicts are read by different people for different reasons and the remedies
/// do not overlap: a tracking column is <i>deleted</i> because the product measures nothing, a
/// <c>deleted_at</c> is <i>replaced by an actual delete</i>, and a <c>content_key</c> is
/// <i>wrapped in the browser before it is ever sent</i> — the value is legitimate, the place is not.
/// The owning document differs too, <c>docs/business-logic/account-keys.md</c> rather than
/// <c>users-and-ownership.md</c> or <c>erasure.md</c>. Folded together, the three would produce a
/// classifier that cannot tell a reviewer which of three arguments they are having.
/// </para>
/// <para>
/// The mechanism is <see cref="IdentifierTokens" />, the one both siblings read: an identifier is
/// split into tokens on <c>_</c> and on case boundaries, and a pattern matches when its own tokens
/// appear as a <i>contiguous run</i> of the identifier's tokens. Shared as a single spelling because
/// all three vocabularies are read over the same identifiers by neighbouring checks, and a name that
/// tokenized one way for one and another way for another would make the set of verdicts a reviewer
/// is handed unexplainable. It is read against relation and member names as well as columns, because
/// this refusal arrives at all three grains — <c>account_keys</c> would be a table,
/// <c>wrappedContentKey</c> is a request member, <c>content_key</c> would be a column.
/// </para>
/// <para>
/// <b>The hard part of this list is that <c>wrapped_content_key</c> and <c>wrapped_index_key</c> are
/// columns in the shipped schema and must pass, while <c>content_key</c> and <c>index_key</c> must
/// not.</b> A pattern alone cannot express that: <see cref="IdentifierTokens.ContainsRun" /> asks
/// only whether a run occurs, and the run <c>content / key</c> occurs in both. Nor can a narrower
/// pattern, because there is no token that <c>content_key</c> carries and <c>wrapped_content_key</c>
/// does not — the offending name is a <i>prefix-free</i> subset of the legal one. So a pattern and a
/// permitted qualifier are <b>two mechanisms</b> here rather than one: the pattern says which run
/// names a secret, and <see cref="IdentifierQualifier" /> says which single adjacent word makes that
/// run not one. A vocabulary that could not tell the two apart would refuse the schema this story
/// just shipped, and one that permitted both would be worthless.
/// </para>
/// <para>
/// <b>The qualifier is per-rule and never global, and that is where the argument lives.</b>
/// <c>wrapped</c> excuses <c>content_key</c>, <c>index_key</c> and <c>account_key</c> because those
/// three are exactly the values this design <i>does</i> store, and stores only in a 61-byte envelope
/// the server holds no key for. It excuses nothing else: <c>wrapped_key_encryption_key</c>,
/// <c>wrapped_prf_output</c> and <c>wrapped_recovery_code</c> stay refused, because there is no
/// design in which any of those exists sealed — a wrapped key-encryption key implies a second
/// key-encryption key above it, and nobody has proposed one. Symmetrically, <c>hash</c> excuses
/// <c>recovery_code</c> and nothing else: a hash of a wrapped key is not a thing anybody stores, so
/// permitting <c>content_key_hash</c> would buy a name for nothing at the cost of a spelling a
/// reviewer would wave through.
/// </para>
/// <para>
/// <b>Both the pattern and its qualifiers are compiled in the plural as well.</b>
/// <see cref="IdentifierTokens.ContainsRun" /> compares whole tokens and does not stem, so
/// <c>account_keys</c> reaches no rule <c>account_key</c> reaches, and <c>hashes</c> excuses nothing
/// <c>hash</c> excuses. Both halves are load-bearing on one name that exists today:
/// <c>wrapped_account_keys</c> needs the pattern's plural to be reached at all and the qualifier's
/// singular to be let go, while <c>recovery_code_hashes</c> needs the pattern's singular and the
/// qualifier's plural. Back either expansion out and one of the two shipped tables reds.
/// </para>
/// <para>
/// <b>The omissions are arguments, not gaps, and every one of them is a name in this schema
/// today.</b> <c>verifier_hash</c> is SHA-256 of a value the client derived from a code; two
/// one-way steps and a different HKDF <c>info</c> stand between it and the key-encryption key, which
/// is the whole reason the column is storable. <c>signature_counter</c> is a monotonic integer a
/// passkey reports to catch cloning, and derives nothing. <c>webauthn_credential_id</c> is the
/// authenticator's opaque handle for a credential — it <i>selects</i> a key and is not one.
/// <c>public_key_cose</c> and any <c>cose_key</c> beside it are <i>public</i> keys, published by
/// design, which verify a signature and decrypt nothing; a rule on the bare token <c>key</c> would
/// take both, and would take <c>key_encryption_key</c>'s own reason with it. <c>challenge</c> is a
/// 32-byte nonce the server minted for one ceremony and expires — the client signs over it, and
/// nothing is ever derived from it. And <c>wrapped_content_key</c> and <c>wrapped_index_key</c> are
/// the envelopes themselves, which are the one of the four key-shaped things the design says may
/// cross the wire.
/// </para>
/// <para>
/// <b>Three bare tokens are deliberately left legal, each because a real name needs it.</b>
/// <c>code</c> is <c>currencies.code</c> — a three-letter ISO currency, the primary key of a
/// reference table, and the token every currency column in the schema carries. <c>key</c> is
/// argued above. <c>prf</c> is a member of the registration request: <c>clientExtensionResults.prf.enabled</c>
/// is a claim a browser makes about a device, carries no bytes, and is what the registration gate
/// reads. Refusing any of the three would red correct code, which is the shape of red that teaches a
/// reviewer to stop believing the check.
/// </para>
/// <para>
/// <b>Two gaps are named rather than papered over, and they are why the requirement stays an
/// Inspection.</b> First, WebAuthn's own spelling for a PRF evaluation is
/// <c>clientExtensionResults.prf.results.first</c> — so a request member that carried the real output
/// under the specification's word for it would be called <c>results</c> or <c>first</c>, and neither
/// token can be refused by any list that also has to let <c>clientExtensionResults</c> through. The
/// <c>prf_result</c> rule below reaches the top-level spelling and not the nested one. Second, this
/// classifier reads <b>names</b> and never values: a <c>bytea</c> column called <c>payload</c>
/// holding a key-encryption key passes every rule here. That second gap is the one
/// <c>KeyMaterialSecrecyTests.Schema_ClassifiesEveryBinaryColumn</c> exists to close from the other
/// side, by requiring every binary column in the schema to carry a written argument for why holding
/// it unwraps nothing. The two checks are complementary and neither is sufficient.
/// </para>
/// <para>
/// The first matching rule wins, and the ordering of <see cref="Rules" /> carries no meaning. No
/// pattern's tokens are a contiguous run inside another's — checked pairwise by
/// <c>UnwrappedKeyMaterialVocabularyTests.Vocabulary_HasNoPatternThatShadowsAnother</c> — so no rule
/// can shadow another and reordering cannot change any verdict. A future pattern that overlaps an
/// existing one has to say in its reason which category it means to win, because at that point the
/// order stops being incidental.
/// </para>
/// <para>
/// <b>This is a test-only deny-list and it lives in <c>TestSupport</c></b>, beside
/// <see cref="ErasureRemnantVocabulary" /> and for the reason that one gives: shared test-only code
/// with more than one reader lives there, and both test projects already reference it. Nothing that
/// ships reads it, and deliberately not the deploy-time verifier in <c>Tools/DbProvision</c> — a
/// column or a member can only arrive through a commit, and CI runs this list on every pull request,
/// so a deploy-time scan would catch nothing the build has not already caught at the cost of one
/// more way for a deploy to fail. Row-level security is verified there instead because it fails open
/// and can drift from outside the repository; a name cannot.
/// </para>
/// </remarks>
public static class UnwrappedKeyMaterialVocabulary
{
    /// <summary>
    /// Every key-material naming pattern, with the category it names, the argument for refusing it,
    /// and the qualifier that makes it legal where one exists.
    /// </summary>
    /// <remarks>
    /// Written down rather than discovered, which is safe in this direction for the reason both
    /// siblings give: the list decides what is <i>refused</i>, so a pattern nobody added leaves a
    /// name allowed — the same shape the schema already had — whereas an unlisted table in a
    /// coverage check leaves a table unpoliced while reporting green. The list only ever adds
    /// refusals, so it can be short without being dishonest. <b>A qualifier runs the other way</b>
    /// and is the one thing here that can make the list weaker, which is why each of the two is
    /// argued in the reason of the rule that carries it rather than in a table of exemptions
    /// somewhere else.
    /// </remarks>
    public static IReadOnlyList<UnwrappedKeyMaterialRule> Rules { get; } =
    [
        new(
            "content_key",
            UnwrappedKeyMaterialCategory.UnwrappedKey,
            "names the account's content key by itself — the 32 bytes every narrative field will be "
            + "encrypted under, drawn in the browser and never transmitted. Legal only behind the "
            + "qualifier 'wrapped', which is what turns the value into a 61-byte envelope the server "
            + "holds no key for; content_key with nothing in front of it is the key in the clear")
        {
            PermittedQualifiers = [Preceding("wrapped")],
        },
        new(
            "index_key",
            UnwrappedKeyMaterialCategory.UnwrappedKey,
            "names the account's index key by itself — the 32 bytes a blind index over a payee name "
            + "is computed under. The bare name is refused for two reasons rather than one: a second "
            + "index key is a correctness failure as well as a secrecy one, because two keys produce "
            + "two index values for one name and the uniqueness constraint stops colliding")
        {
            PermittedQualifiers = [Preceding("wrapped")],
        },
        new(
            "account_key",
            UnwrappedKeyMaterialCategory.UnwrappedKey,
            "is the pair's collective name, and the one the shipped table already carries in its "
            + "sealed form. Refused bare because a column called account_key holds whichever of the "
            + "two keys the author stopped distinguishing between, which is also the moment the "
            + "content/index binding in the associated data stops being checkable")
        {
            PermittedQualifiers = [Preceding("wrapped")],
        },
        new(
            "unwrapped_key",
            UnwrappedKeyMaterialCategory.UnwrappedKey,
            "is the requirement's own noun. Nothing in this design needs a name saying a value is "
            + "out of its envelope, so a column or member spelled this way is either the key in the "
            + "clear or a place somebody meant to put one; no qualifier makes it legal, and none is "
            + "offered"),
        new(
            "plaintext_key",
            UnwrappedKeyMaterialCategory.UnwrappedKey,
            "names a key in the clear outright, and arrives from a debugging session that was never "
            + "taken back out — the value somebody stored beside the sealed one so a comparison "
            + "could be repeated. It reads like scaffolding, which is exactly why it survives review"),
        new(
            "key_encryption_key",
            UnwrappedKeyMaterialCategory.KeyEncryptionKey,
            "is the key every wrapped envelope opens under. It is derived in the browser from a "
            + "recovery factor and imported non-extractable, so no correct path can produce bytes "
            + "for a column to hold; one that exists hands the operator every account it names, "
            + "along with every backup of it"),
        new(
            "kek",
            UnwrappedKeyMaterialCategory.KeyEncryptionKey,
            "is the abbreviation the same value arrives under when somebody is naming a column "
            + "rather than writing prose. Listed separately because the matcher does not expand "
            + "abbreviations: kek_bytes and kek_id carry no 'encryption' token and would walk "
            + "straight past the rule above it"),
        new(
            "wrapping_key",
            UnwrappedKeyMaterialCategory.KeyEncryptionKey,
            "names the key by what it does rather than by what it is, which is the spelling a "
            + "reviewer skims past. Wrapping and unwrapping are one key here — AES-GCM is symmetric "
            + "— so a wrapping key on the server is an unwrapping key on the server, whatever the "
            + "name suggests about direction"),
        new(
            "master_key",
            UnwrappedKeyMaterialCategory.KeyEncryptionKey,
            "is the key-encryption key under the word a key-hierarchy diagram gives the root. This "
            + "hierarchy does have a root and it lives in the browser behind a recovery factor; a "
            + "server-side column of that name is the root having been moved, which is the one move "
            + "that makes every other control here decorative"),
        new(
            "prf_output",
            UnwrappedKeyMaterialCategory.PrfOutput,
            "is the authenticator's PRF evaluation — the input keying material the passkey branch "
            + "derives its key-encryption key from. Holding it is strictly worse than holding that "
            + "key, because every branch ever derived from it follows, including ones this product "
            + "has not defined yet"),
        new(
            "prf_secret",
            UnwrappedKeyMaterialCategory.PrfOutput,
            "is the same value under the word somebody reaches for when they know it must not be "
            + "stored and are storing it anyway. A phrase rather than the bare token 'secret', "
            + "which is an ordinary word a configuration or a client registration may legitimately "
            + "want"),
        new(
            "prf_result",
            UnwrappedKeyMaterialCategory.PrfOutput,
            "is WebAuthn's own name for the object carrying the evaluation, from "
            + "clientExtensionResults.prf.results. This API deliberately models only prf.enabled, so "
            + "a member spelled prfResult or prfResults at the request surface is the output itself "
            + "arriving under the specification's word for it. The nested spelling — a member simply "
            + "called results, under prf — is a gap no name list can close"),
        new(
            "recovery_code",
            UnwrappedKeyMaterialCategory.RecoveryCode,
            "names the code the client mints and never transmits, and from which the next epic "
            + "derives a key-encryption key. Legal only in front of 'hash': recovery_code_hashes is "
            + "the shipped table, and SHA-256 of a verifier derived from a code is two one-way steps "
            + "and a different HKDF info away from the key. Any other adjacent word is refused with "
            + "it, count or label included, deliberately — on this one word a reviewer should be "
            + "made to look")
        {
            PermittedQualifiers = [Following("hash")],
        },
        new(
            "backup_code",
            UnwrappedKeyMaterialCategory.RecoveryCode,
            "is the same secret under the name most other products give it, which makes it the "
            + "spelling that arrives by habit from another codebase rather than by a decision "
            + "anybody made here. Nothing in this product is called a backup code, so the name can "
            + "only be the recovery code wearing somebody else's word"),
    ];

    /// <summary>
    /// The rules above, pre-split into the tokens an identifier's tokens are matched against — each
    /// pattern twice, in the form it is written and in the plural, and each carrying its qualifiers
    /// split by side and expanded the same way.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Built from <see cref="Rules" /> rather than written twice, so the list a reviewer reads and
    /// the list <see cref="Classify" /> executes cannot come apart. The pluralisation is
    /// <see cref="IdentifierTokens.PluralOf" />'s, the one place this codebase spells that rule:
    /// <c>+es</c> after a sibilant and <c>+s</c> otherwise. On this list the sibilant branch reaches
    /// exactly one thing, and it is not a pattern but the qualifier <c>hash</c>, whose expansion
    /// <c>hashes</c> is what the shipped <c>recovery_code_hashes</c> relation needs. Where an ending
    /// is wrong the result is a non-word — <c>keks</c>, <c>wrappeds</c> — which matches nothing
    /// rather than something wrong.
    /// </para>
    /// <para>
    /// <b>The qualifiers are split by side here rather than filtered inside the matcher</b>, because
    /// the matcher runs once per candidate offset and the split is a property of the rule. Both
    /// sides are arrays of token runs, which is also why an absent side is an empty array rather
    /// than a null: the neutralisation loop over it then simply does not run, and there is no branch
    /// anybody could get backwards.
    /// </para>
    /// <para>
    /// At this size a plain array beats a frozen collection: the work is a short scan of short token
    /// runs, and a hash-based structure could not answer a contiguous-run question anyway.
    /// </para>
    /// </remarks>
    private static readonly CompiledRule[] CompiledRules =
        Rules
            .SelectMany(rule => new[]
                {
                    IdentifierTokens.Tokenize(rule.Pattern),
                    IdentifierTokens.Tokenize(IdentifierTokens.PluralOf(rule.Pattern)),
                }
                .Select(tokens => new CompiledRule(
                    tokens,
                    QualifierForms(rule, QualifierPosition.Preceding),
                    QualifierForms(rule, QualifierPosition.Following),
                    rule)))
            .ToArray();

    /// <summary>
    /// Says which rule a column, relation or member name trips, or <see langword="null" /> when the
    /// name could hold no secret this vocabulary knows how to refuse.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The rule rather than its category</b>, because the category alone is a bucket name and
    /// <see cref="UnwrappedKeyMaterialRule.Reason" /> is the member that makes this list arguable.
    /// Handing the whole rule back lets a caller print the argument for the refusal in the same line
    /// as the offender it refused. A caller wanting only the bucket reads
    /// <see cref="UnwrappedKeyMaterialRule.Category" /> off the answer.
    /// </para>
    /// <para>
    /// Null on a null or blank input rather than an exception, for the reason both siblings give:
    /// this is a classifier reading names out of a catalog, a model or a reflected type, not a
    /// validator of its caller's arguments, and a scan that threw partway through would report
    /// <i>fewer</i> offenders than exist — the fail-open direction.
    /// </para>
    /// <para>
    /// The parameter is nullable because its callers' inputs are. <c>IEntityType.GetTableName()</c>
    /// and <c>IProperty.GetColumnName()</c> both answer <see langword="null" /> for something the
    /// model maps nowhere, so a non-null parameter would leave a model-reading caller either
    /// reaching for <c>!</c> or dropping the name before the classifier ever saw it.
    /// </para>
    /// <para>
    /// Comparison is ordinal on invariantly lower-cased tokens, which is
    /// <see cref="IdentifierTokens" />'s doing rather than this file's. PostgreSQL folds unquoted
    /// identifiers to lower case, but a quoted one keeps its case and a reflected member name is
    /// PascalCase, so the folding has to happen here too. Culture-sensitive comparison is avoided
    /// outright: the Turkish dotless <c>i</c> alone would make <c>index_key</c> and
    /// <c>plaintext_key</c> match or miss depending on the machine the build ran on.
    /// </para>
    /// </remarks>
    /// <param name="identifier">
    /// A column, relation or member name, in any casing and from any source, or
    /// <see langword="null" /> when the model, catalog or type the caller read it from had none.
    /// </param>
    /// <returns>
    /// The rule the name trips — its pattern, the secret it names and the argument for refusing it —
    /// or <see langword="null" /> when the name trips none.
    /// </returns>
    public static UnwrappedKeyMaterialRule? Classify(string? identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            return null;
        }

        string[] tokens = IdentifierTokens.Tokenize(identifier);

        foreach (CompiledRule compiled in CompiledRules)
        {
            if (HasUnqualifiedOccurrence(tokens, compiled))
            {
                return compiled.Rule;
            }
        }

        return null;
    }

    /// <summary>A qualifier that has to end immediately before the pattern's first token.</summary>
    /// <remarks>
    /// A factory method rather than a shared <c>static readonly</c> field, so nothing about
    /// <see cref="Rules" /> depends on the order two static initializers happen to run in. A field
    /// declared below the list it is used in is null when the list is built, and the symptom is a
    /// rule that silently permits nothing.
    /// </remarks>
    private static IdentifierQualifier Preceding(string tokens) =>
        new(tokens, QualifierPosition.Preceding);

    /// <summary>A qualifier that has to begin immediately after the pattern's last token.</summary>
    private static IdentifierQualifier Following(string tokens) =>
        new(tokens, QualifierPosition.Following);

    /// <summary>
    /// One side's qualifiers for a rule, tokenized and expanded into the plural the same way its
    /// pattern is.
    /// </summary>
    private static string[][] QualifierForms(
        UnwrappedKeyMaterialRule rule,
        QualifierPosition position) =>
        rule.PermittedQualifiers
            .Where(qualifier => qualifier.Position == position)
            .SelectMany(qualifier => new[]
            {
                IdentifierTokens.Tokenize(qualifier.Tokens),
                IdentifierTokens.Tokenize(IdentifierTokens.PluralOf(qualifier.Tokens)),
            })
            .ToArray();

    /// <summary>
    /// Whether the pattern occurs in <paramref name="tokens" /> at least once with no permitted
    /// qualifier beside it.
    /// </summary>
    /// <remarks>
    /// Every occurrence is examined rather than the first, and the rule fires on the first
    /// <i>unqualified</i> one. The alternative — asking whether the run occurs and then whether a
    /// qualifier occurs anywhere — would let <c>wrapped_content_key_content_key</c> pass on the
    /// strength of a <c>wrapped</c> that qualifies the other half of the name. That is a contrived
    /// name and the cost of being right about it is one loop.
    /// </remarks>
    private static bool HasUnqualifiedOccurrence(string[] tokens, CompiledRule compiled)
    {
        string[] pattern = compiled.Tokens;

        for (int offset = 0; offset + pattern.Length <= tokens.Length; offset++)
        {
            if (!MatchesAt(tokens, pattern, offset))
            {
                continue;
            }

            if (!IsQualified(tokens, compiled, offset, pattern.Length))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether a permitted qualifier sits immediately beside the occurrence at
    /// <paramref name="offset" />, on the side that rule permits.
    /// </summary>
    private static bool IsQualified(
        string[] tokens,
        CompiledRule compiled,
        int offset,
        int length)
    {
        foreach (string[] qualifier in compiled.PermittedPreceding)
        {
            if (MatchesAt(tokens, qualifier, offset - qualifier.Length))
            {
                return true;
            }
        }

        foreach (string[] qualifier in compiled.PermittedFollowing)
        {
            if (MatchesAt(tokens, qualifier, offset + length))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether <paramref name="run" /> sits in <paramref name="tokens" /> starting exactly at
    /// <paramref name="offset" />.
    /// </summary>
    /// <remarks>
    /// Delegates the comparison to <see cref="IdentifierTokens.ContainsRun" /> over a window of the
    /// run's own length, where "contains a run" and "equals" are the same question. The point is
    /// that this file spells no token comparison of its own: the ordinal, whole-token, no-stemming
    /// rule stays in the one place all three vocabularies read it from, and a change there reaches
    /// this matcher too. An empty run answers false rather than true, so a rule that somehow lost
    /// its tokens permits nothing rather than matching everywhere.
    /// </remarks>
    private static bool MatchesAt(string[] tokens, string[] run, int offset)
    {
        if (run.Length == 0 || offset < 0 || offset + run.Length > tokens.Length)
        {
            return false;
        }

        return IdentifierTokens.ContainsRun(tokens[offset..(offset + run.Length)], run);
    }

    /// <summary>
    /// One executable form of a rule: its tokens, its permitted qualifiers split by side, and the
    /// rule itself.
    /// </summary>
    /// <remarks>
    /// The rule travels rather than a copy of its category, so a plural match hands back the same
    /// argument the singular does and the two forms cannot disagree with each other.
    /// </remarks>
    /// <param name="Tokens">The pattern's tokens, in this form — as written, or in the plural.</param>
    /// <param name="PermittedPreceding">Qualifier runs that may end immediately before an occurrence.</param>
    /// <param name="PermittedFollowing">Qualifier runs that may begin immediately after an occurrence.</param>
    /// <param name="Rule">The rule this form answers with.</param>
    private sealed record CompiledRule(
        string[] Tokens,
        string[][] PermittedPreceding,
        string[][] PermittedFollowing,
        UnwrappedKeyMaterialRule Rule);
}
