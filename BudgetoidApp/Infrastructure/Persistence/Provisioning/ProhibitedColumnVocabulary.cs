namespace Infrastructure.Persistence.Provisioning;

/// <summary>
/// The kind of thing a prohibited column name says the schema is keeping about a person.
/// </summary>
/// <remarks>
/// The category is not decoration on a yes/no answer. A reviewer facing a red check needs to know
/// <i>which</i> refusal was tripped, because the four are refused for different reasons and the
/// remedy differs: an analytics identifier is usually a habit imported from another codebase, while
/// a behavioural event column is normally a feature somebody deliberately designed and did not
/// realise the product had already ruled out.
/// </remarks>
public enum ProhibitedColumnCategory
{
    /// <summary>A handle that lets one person's activity be joined up across sessions or products.</summary>
    AnalyticsIdentifier,

    /// <summary>An identifier issued for, or usable by, an advertising network.</summary>
    AdvertisingIdentifier,

    /// <summary>Something about the machine or the connection, which identifies a person by proxy.</summary>
    DeviceFingerprint,

    /// <summary>A record of what somebody did, when, or how often, rather than what they own.</summary>
    BehaviouralEvent,
}

/// <summary>One forbidden column-name pattern, the category it names, and the argument for it.</summary>
/// <remarks>
/// <para>
/// <paramref name="Reason" /> is the member that keeps this list arguable rather than merely
/// obeyed. Every entry here will one day produce a red on a column somebody had a reason to add,
/// and at that moment the only thing that can be weighed against their reason is the one written
/// down when the pattern was added. A reason reading "forbidden" restates the verdict and gives the
/// next reader nothing to disagree with, which is how a rule outlives the argument for it.
/// </para>
/// </remarks>
/// <param name="Pattern">
/// One token, or several joined by <c>_</c>, matched against a column name's tokens as described on
/// <see cref="ProhibitedColumnVocabulary" />. Not a substring and not a regular expression.
/// </param>
/// <param name="Category">Which of the four refusals this pattern is an instance of.</param>
/// <param name="Reason">
/// What the pattern names and why the product refuses to carry it — prose a reviewer can argue
/// with, in the sentence the refusal will be read next to.
/// </param>
public sealed record ProhibitedColumnRule(
    string Pattern,
    ProhibitedColumnCategory Category,
    string Reason);

/// <summary>
/// The single spelling of the column names the product refuses to carry: an analytics identifier, an
/// advertising identifier, a device fingerprint, or a record of behaviour.
/// </summary>
/// <remarks>
/// <para>
/// The rule is schema-wide rather than pointed at <c>users</c>. A tracking column is no better on
/// <c>transactions</c> than it is on the account row, and a rule written about one table is a rule
/// that can be satisfied by putting the column on another.
/// </para>
/// <para>
/// <b>The mechanism is token matching, not substring matching.</b> A column name is split into
/// tokens on <c>_</c> and on case boundaries, and a pattern matches when its own tokens appear as a
/// <i>contiguous run</i> of the column's tokens. A bare substring deny-list is the obvious design
/// and it is wrong in both directions at once: <c>id</c> would swallow <c>user_id</c>,
/// <c>budget_id</c> and every foreign key in the schema, while <c>name</c> — the token that must
/// stay legal — is a substring of <c>event_name</c>, which must not. Tokens separate those cases
/// cleanly: <c>event_name</c> carries the token <c>event</c> and <c>name</c> does not.
/// </para>
/// <para>
/// The trade-off is that a token match is blind to a name that spells the same idea without a
/// boundary — <c>ipaddress</c> as one word matches nothing here, and neither does a column named in
/// a language this list is not written in. That is accepted: the failure mode of the alternative is
/// a false positive on a legitimate column, which trains a reviewer to widen the exemption rather
/// than to look, and this check is only worth having while its reds are believed.
/// </para>
/// <para>
/// Patterns are single tokens wherever a single token is provably narrow enough, and multi-token
/// phrases where it is not. <c>fingerprint</c> and <c>telemetry</c> can stand alone because no
/// legitimate column in this domain is called either. <c>ga_client_id</c> is a phrase because
/// <c>client</c> and <c>id</c> are both ordinary words, and <c>page_view</c> is a phrase because a
/// saved <i>view</i> is a plausible product concept that this rule has no business refusing.
/// </para>
/// <para>
/// The first matching rule wins, but no name can reach a second one: the patterns are chosen to be
/// disjoint, so the ordering of <see cref="Rules" /> carries no meaning and reordering the list
/// cannot change a verdict. A future pattern that overlaps an existing one has to say in its reason
/// which category it means to win, because at that point the order stops being incidental.
/// </para>
/// <para>
/// This lives in the production assembly rather than in a test for the same reason
/// <see cref="RowLevelSecurityCoverage.Exemptions" /> does: a build gate will have to read this
/// exact list, and it cannot reference a test assembly. Two written-down copies of one rule have no
/// adjudicator when they disagree.
/// </para>
/// <para>
/// Unlike <see cref="RowLevelSecurityCoverage" />, it is deliberately <b>not</b> wired into the
/// deploy-time verifier in <c>Tools/DbProvision</c>, and the asymmetry is about how each rule can be
/// broken. Row-level security fails <i>open</i> and can drift from outside the repository — a policy
/// dropped by hand in a session nobody reviewed leaves no trace in the source — so it has to be
/// re-checked against the live database on every deploy. A column can only arrive through a
/// migration, and CI reads every migration before it merges. A deploy-time scan would therefore
/// catch nothing the build has not already caught, at the cost of one more way for a deploy to fail.
/// </para>
/// <para>
/// The rule sits at the build rather than in the database because the database cannot hold it.
/// PostgreSQL will not refuse a column for what its <i>name</i> connotes; reaching that would need
/// an event trigger, which is procedural logic pushed down purely to satisfy "lowest layer" —
/// exactly what ADR 0002 rules out. The build is genuinely the lowest capable layer here, and this
/// is the case that rule was written to allow rather than an exception to it.
/// </para>
/// </remarks>
public static class ProhibitedColumnVocabulary
{
    /// <summary>
    /// Every forbidden pattern, with the category it names and the argument for refusing it.
    /// </summary>
    /// <remarks>
    /// Written down rather than discovered, and that direction is safe here in a way it would not be
    /// for row-level security coverage. This list decides what is <i>refused</i>, so a pattern
    /// nobody added leaves a column allowed — the same shape the schema already had — whereas an
    /// unlisted table in a coverage check leaves a table unpoliced while reporting green. The list
    /// only ever adds refusals, so it can be short without being dishonest.
    /// </remarks>
    public static IReadOnlyList<ProhibitedColumnRule> Rules { get; } =
    [
        new(
            "analytics",
            ProhibitedColumnCategory.AnalyticsIdentifier,
            "names a column holding an analytics handle. The product measures nothing about the "
            + "person using it, so there is no id for a measurement to be keyed on"),
        new(
            "telemetry",
            ProhibitedColumnCategory.AnalyticsIdentifier,
            "names a column holding a telemetry handle. Operational telemetry is about the service "
            + "and belongs in logs and traces that name no person, never in a column beside a row "
            + "that does"),
        new(
            "tracking",
            ProhibitedColumnCategory.AnalyticsIdentifier,
            "names a column whose purpose is to follow one person between visits — which is the "
            + "capability the product refuses to have, independent of whether anything reads it "
            + "today"),
        new(
            "ga_client_id",
            ProhibitedColumnCategory.AnalyticsIdentifier,
            "is the client identifier a general-purpose analytics tag mints, stored against a row "
            + "so a person's activity can be joined to a profile held somewhere else. A phrase "
            + "rather than a token, because 'client' and 'id' are both ordinary words here"),
        new(
            "advertising",
            ProhibitedColumnCategory.AdvertisingIdentifier,
            "names an identifier issued so that a person can be matched to an advertising audience. "
            + "The product sells nothing about the people who use it, so it has nothing to match"),
        new(
            "idfa",
            ProhibitedColumnCategory.AdvertisingIdentifier,
            "is the mobile advertising identifier under the name it usually arrives with — copied "
            + "in from a vendor SDK rather than designed, which is exactly why the name is worth "
            + "recognising on sight"),
        new(
            "gaid",
            ProhibitedColumnCategory.AdvertisingIdentifier,
            "is the other platform's advertising identifier, refused for the reason its counterpart "
            + "is: a budgeting record has no business carrying a handle an ad network can resolve"),
        new(
            "fingerprint",
            ProhibitedColumnCategory.DeviceFingerprint,
            "names a value derived from a person's machine so that the machine can be recognised "
            + "again. It identifies a person more durably than an account does, and it does so "
            + "without ever asking them"),
        new(
            "device_id",
            ProhibitedColumnCategory.DeviceFingerprint,
            "pins one person's row to one piece of hardware, which makes the row a device history. "
            + "A phrase rather than the token 'device', so that naming an authenticator a person "
            + "chose is still possible"),
        new(
            "user_agent",
            ProhibitedColumnCategory.DeviceFingerprint,
            "is the browser's self-description, which is specific enough to single a person out and "
            + "is retained for no purpose the product has. Read it in a request if a bug needs it; "
            + "do not keep it"),
        new(
            "ip",
            ProhibitedColumnCategory.DeviceFingerprint,
            "names a network address, which locates a person and identifies their connection. A "
            + "stored address turns a budget row into a place-and-time record of where somebody "
            + "was"),
        new(
            "event",
            ProhibitedColumnCategory.BehaviouralEvent,
            "names a record of something a person did rather than something they own. The schema "
            + "holds the money the person is managing; what they clicked to manage it is not the "
            + "product's to keep"),
        new(
            "seen",
            ProhibitedColumnCategory.BehaviouralEvent,
            "names a column recording when a person was last observed, which is a usage log written "
            + "one row at a time. Nothing the product does needs to know whether somebody has been "
            + "away"),
        new(
            "page_view",
            ProhibitedColumnCategory.BehaviouralEvent,
            "names a count or record of screens a person looked at — the smallest complete unit of "
            + "behavioural surveillance. A phrase rather than the token 'view', which a saved "
            + "report could legitimately be called"),
    ];

    /// <summary>The patterns above, pre-split into the tokens a column's tokens are matched against.</summary>
    /// <remarks>
    /// Built once from <see cref="Rules" /> rather than written twice, so the list a reviewer reads
    /// and the list <see cref="Classify" /> executes cannot come apart. At this size a plain array
    /// beats a frozen collection: the work is a short scan of short token runs, and a hash-based
    /// structure could not answer a contiguous-run question anyway.
    /// </remarks>
    private static readonly (string[] Tokens, ProhibitedColumnCategory Category)[] CompiledRules =
        Rules.Select(rule => (Tokenize(rule.Pattern), rule.Category)).ToArray();

    /// <summary>
    /// Says which refusal a column name is an instance of, or <see langword="null" /> when the name
    /// is one the product is happy to carry.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Null on a null or blank input rather than an exception. This is a classifier reading names
    /// out of a catalog or a model, not a validator of its caller's arguments, and a scan that threw
    /// partway through would report fewer offenders than exist — the fail-open direction.
    /// </para>
    /// <para>
    /// Comparison is ordinal on invariantly lower-cased tokens. PostgreSQL folds unquoted
    /// identifiers to lower case, but a quoted one keeps its case and a caller may pass anything, so
    /// the folding happens here too. Culture-sensitive comparison is avoided outright: the Turkish
    /// dotless <c>i</c> alone would make <c>ip</c> and <c>idfa</c> match or miss depending on the
    /// machine the build ran on.
    /// </para>
    /// </remarks>
    /// <param name="columnName">A column name, in any casing and from any source.</param>
    /// <returns>The category the name falls into, or <see langword="null" /> when it falls into none.</returns>
    public static ProhibitedColumnCategory? Classify(string columnName)
    {
        if (string.IsNullOrWhiteSpace(columnName))
        {
            return null;
        }

        string[] tokens = Tokenize(columnName);

        foreach ((string[] patternTokens, ProhibitedColumnCategory category) in CompiledRules)
        {
            if (ContainsRun(tokens, patternTokens))
            {
                return category;
            }
        }

        return null;
    }

    /// <summary>
    /// Splits an identifier into lower-cased word tokens, on separators and on case boundaries.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The case boundaries are what let this read a quoted identifier EF may have mapped verbatim —
    /// <c>userAgent</c> and <c>IPAddress</c> both reach the same tokens their snake-cased spellings
    /// would. Two boundaries are needed for that: lower-to-upper splits <c>userAgent</c>, and an
    /// upper run followed by a lower letter splits <c>IPAddress</c> into <c>IP</c> and
    /// <c>Address</c>. Only the first would leave <c>IPAddress</c> as one token and miss it.
    /// </para>
    /// <para>
    /// Digits stay attached to the token they sit in, so <c>ga4_client_id</c> tokenizes as
    /// <c>ga4</c> rather than as <c>ga</c> and <c>4</c>. That is a miss for the <c>ga_client_id</c>
    /// phrase and it is the accepted direction: separating them would make <c>id</c>-adjacent
    /// patterns start matching numbered columns, which is the false-positive failure this whole
    /// design is arranged to avoid.
    /// </para>
    /// </remarks>
    private static string[] Tokenize(string identifier)
    {
        List<string> tokens = [];
        int start = -1;

        for (int index = 0; index <= identifier.Length; index++)
        {
            bool isWordCharacter = index < identifier.Length
                && char.IsLetterOrDigit(identifier[index]);

            if (!isWordCharacter)
            {
                AddToken(tokens, identifier, start, index);
                start = -1;
                continue;
            }

            if (start >= 0 && IsCaseBoundary(identifier, index))
            {
                AddToken(tokens, identifier, start, index);
                start = index;
                continue;
            }

            if (start < 0)
            {
                start = index;
            }
        }

        return [.. tokens];
    }

    /// <summary>Appends <c>[start, end)</c> as a lower-cased token when it is a real span.</summary>
    private static void AddToken(List<string> tokens, string identifier, int start, int end)
    {
        if (start >= 0 && end > start)
        {
            tokens.Add(identifier[start..end].ToLowerInvariant());
        }
    }

    /// <summary>
    /// Whether a new word starts at <paramref name="index" /> because of a change of case.
    /// </summary>
    private static bool IsCaseBoundary(string identifier, int index)
    {
        if (!char.IsUpper(identifier[index]))
        {
            return false;
        }

        // userAgent: an upper letter directly after a lower one or a digit starts a word.
        if (!char.IsUpper(identifier[index - 1]))
        {
            return true;
        }

        // IPAddress: the last upper letter of a run starts a word when a lower one follows it.
        return index + 1 < identifier.Length && char.IsLower(identifier[index + 1]);
    }

    /// <summary>
    /// Whether <paramref name="pattern" /> appears as a contiguous run inside
    /// <paramref name="tokens" />.
    /// </summary>
    /// <remarks>
    /// Contiguous rather than merely present, because a phrase pattern is a claim about a name and
    /// not about a bag of words. <c>user_id</c> beside an <c>agent_code</c> column is two ordinary
    /// names; <c>user_agent</c> is one forbidden one, and only adjacency tells them apart.
    /// </remarks>
    private static bool ContainsRun(string[] tokens, string[] pattern)
    {
        for (int offset = 0; offset + pattern.Length <= tokens.Length; offset++)
        {
            bool matched = true;

            for (int index = 0; index < pattern.Length; index++)
            {
                if (!string.Equals(tokens[offset + index], pattern[index], StringComparison.Ordinal))
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
            {
                return true;
            }
        }

        return false;
    }
}
