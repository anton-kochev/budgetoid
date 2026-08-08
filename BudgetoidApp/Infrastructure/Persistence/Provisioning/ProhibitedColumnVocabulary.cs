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
/// that can be satisfied by putting the column on another. The same list is read against relation
/// names for the same reason one level up: a table named for what it holds is the identical
/// refusal, and it is the level a behavioural feature is actually modelled at — <c>user_analytics</c>
/// and <c>device_fingerprints</c> arrive as tables far more often than as columns. Both of that pair
/// are caught, and the second only because <see cref="CompiledRules" /> matches every pattern in the
/// plural as well: <c>fingerprints</c> is a different token from <c>fingerprint</c> and reaches
/// nothing on its own. Back that expansion out and this sentence stops being true.
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
/// saved <i>view</i> is a plausible product concept that this rule has no business refusing. The
/// bare token <c>event</c> is deliberately <b>not</b> refused for the same reason: a transactional
/// outbox is the expected pattern for cross-aggregate work here and it names its columns
/// <c>event_type</c>, <c>event_id</c> and <c>event_data</c>, so the behavioural patterns are
/// phrases naming the analytics idea rather than the token every outbox row carries.
/// </para>
/// <para>
/// <b>The first matching rule wins, and the order of <see cref="Rules" /> is what decides it.</b> No
/// pattern shadows another — none of the token runs <see cref="CompiledRules" /> executes is a
/// contiguous run inside any other, checked pairwise across all 66 of them — but that only rules out
/// one pattern swallowing another wholesale. A single name can still carry two patterns side by side,
/// and then the order is the whole answer: <c>analytics_event_log</c> reaches <c>analytics</c> and
/// <c>event_log</c>, so reordering the list would turn an
/// <see cref="ProhibitedColumnCategory.AnalyticsIdentifier" /> verdict into a
/// <see cref="ProhibitedColumnCategory.BehaviouralEvent" /> one.
/// <c>user_event_log</c> is the sharper case, reaching <c>user_event</c> and <c>event_log</c> inside
/// one category, where what the order decides is which <i>reason</i> the reviewer is handed rather
/// than which bucket — which is why <see cref="Classify" /> answers with the whole rule. A future
/// pattern that overlaps an existing one has to say in its reason which category it means to win.
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
            "utm",
            ProhibitedColumnCategory.AnalyticsIdentifier,
            "prefixes the campaign-attribution parameters a link carries — source, medium, "
            + "campaign. Storing one against a row records how a person was steered here, which "
            + "is marketing telemetry the product has no campaign to attribute to"),
        new(
            "referrer",
            ProhibitedColumnCategory.AnalyticsIdentifier,
            "records the page a person came from, which is a browsing history entry contributed "
            + "by whoever they were visiting before. Nothing in a budget depends on it"),
        new(
            "referer",
            ProhibitedColumnCategory.AnalyticsIdentifier,
            "is the same value under the misspelling the HTTP header itself carries, listed "
            + "separately because the header's spelling is the one that reaches a schema by copy "
            + "and paste"),
        new(
            "anonymous_id",
            ProhibitedColumnCategory.AnalyticsIdentifier,
            "is the handle a measurement SDK mints before it knows who somebody is, kept so "
            + "their activity can be stitched to an account later. \"Anonymous\" names the moment "
            + "it was issued, not what it does"),
        new(
            "distinct_id",
            ProhibitedColumnCategory.AnalyticsIdentifier,
            "is the same stitching handle under the name two widely-used product-analytics "
            + "vendors give it. A phrase, because \"distinct\" and \"id\" are both ordinary "
            + "words"),
        new(
            "mixpanel",
            ProhibitedColumnCategory.AnalyticsIdentifier,
            "names a measurement vendor in a column, which means a row is being kept in that "
            + "vendor's shape. The vendor is beside the point; a column named after one is a "
            + "column whose reader is not this product"),
        new(
            "amplitude",
            ProhibitedColumnCategory.AnalyticsIdentifier,
            "is refused for the reason above it. Listed by name because a vendor identifier "
            + "arrives copied from a quickstart, where nobody is deciding anything"),
        new(
            "posthog",
            ProhibitedColumnCategory.AnalyticsIdentifier,
            "is refused for the reason above it, and included because a self-hosted measurement "
            + "product feels like an exception to a privacy rule and is not one — the data is the "
            + "same data"),
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
            "cookie_id",
            ProhibitedColumnCategory.DeviceFingerprint,
            "is the identifier a tracking cookie carries so one browser can be recognised across "
            + "sites. A phrase rather than the bare token \"cookie\": a first-party session's "
            + "opaque value is plausibly stored under a name containing it, and that value is a "
            + "credential to be revoked rather than a handle to be joined on"),
        new(
            "mac_address",
            ProhibitedColumnCategory.DeviceFingerprint,
            "is the hardware address of a network interface, which does not change when somebody "
            + "clears their data, signs out, or buys a new browser. It identifies a machine more "
            + "durably than anything else on this list"),
        new(
            "event_name",
            ProhibitedColumnCategory.BehaviouralEvent,
            "is the analytics spelling of \"what did this person just do\" — the label an event "
            + "stream is grouped by. A transactional outbox spells its discriminator "
            + "event_type, so refusing this one costs the outbox nothing"),
        new(
            "event_count",
            ProhibitedColumnCategory.BehaviouralEvent,
            "counts how often somebody did a thing, which is a usage metric wearing a column "
            + "name. The product measures money, not people"),
        new(
            "user_event",
            ProhibitedColumnCategory.BehaviouralEvent,
            "binds a record of an action directly to the person who took it, which is the join "
            + "a behavioural log exists to make. An outbox row belongs to an aggregate, not to "
            + "a user"),
        new(
            "event_log",
            ProhibitedColumnCategory.BehaviouralEvent,
            "is an accumulated history of actions rather than a single pending message. The "
            + "distinction from an outbox is durability of intent: an outbox row is consumed and "
            + "deleted, a log is kept because somebody wants to look back at it"),
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
        new(
            "last_login",
            ProhibitedColumnCategory.BehaviouralEvent,
            "accumulates a sign-in history one overwrite at a time. A session record may say when "
            + "it was created and when it was last used, because both are read to revoke it; a "
            + "column on the person is read by nobody and outlives every session"),
        new(
            "login_count",
            ProhibitedColumnCategory.BehaviouralEvent,
            "counts sign-ins, which measures engagement and answers no question the product asks"),
        new(
            "sign_in_count",
            ProhibitedColumnCategory.BehaviouralEvent,
            "is the same count under the other spelling. Listed separately because the two "
            + "spellings are chosen by habit and neither is more likely than the other"),
        new(
            "session_count",
            ProhibitedColumnCategory.BehaviouralEvent,
            "counts visits. Deliberately a phrase and not the bare token \"session\": a "
            + "first-party revocable session is a security record this product intends to keep, "
            + "and a rule that cannot tell it from a measurement session would refuse the "
            + "feature"),
        new(
            "impression",
            ProhibitedColumnCategory.BehaviouralEvent,
            "counts times something was shown to somebody. The product shows a person their own "
            + "money and has nothing to sell against the count"),
        new(
            "click_count",
            ProhibitedColumnCategory.BehaviouralEvent,
            "counts interactions, which is behaviour at the finest grain anybody bothers to "
            + "record. A phrase rather than the bare token, so that a domain concept that happens "
            + "to be called a click is not caught by a rule aimed at a metric"),
    ];

    /// <summary>
    /// The patterns above, pre-split into the tokens a name's tokens are matched against — each of
    /// them twice, in the form it is written and in the plural.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Built from <see cref="Rules" /> rather than written twice, so the list a reviewer reads and the
    /// list <see cref="Classify" /> executes cannot come apart.
    /// </para>
    /// <para>
    /// <b>The executed list is twice the length of the written one, and this is where the two
    /// differ.</b> Every rule contributes two entries pointing at the same rule object: its pattern as
    /// written, and the same pattern through <see cref="IdentifierTokens.PluralOf" />.
    /// <see cref="IdentifierTokens.ContainsRun" /> compares whole tokens ordinally and does not stem,
    /// so <c>fingerprints</c> reaches no rule <c>fingerprint</c> reaches — and the plural is the shape
    /// a tracking <i>relation</i> arrives in, every table this schema maps being named in the plural.
    /// <c>device_fingerprints</c>, <c>page_views</c>, <c>impressions</c>, <c>event_logs</c>,
    /// <c>user_agents</c>, <c>cookie_ids</c> and <c>mac_addresses</c> are all names this list was read
    /// as covering and did not. Both entries carry the rule itself rather than a copy of its category,
    /// so a plural red hands the reader the singular's argument and the two forms cannot disagree with
    /// each other.
    /// </para>
    /// <para>
    /// The expansion runs in the same safe direction the written list does: it only ever adds
    /// refusals. A plural nobody wanted leaves a name refused that a reviewer can argue about against
    /// the singular's own reason, while a missing plural leaves a tracking table allowed by a list that
    /// reports covering it. It costs nothing on the names that must stay legal either — no expanded
    /// form reaches an outbox column, a first-party security record's columns, or any table or column
    /// the shipped model maps, each of which has a test standing on it.
    /// </para>
    /// <para>
    /// Order survives the expansion: a rule's two forms sit together and before the next rule's, so a
    /// verdict is decided by a rule's position in <see cref="Rules" /> and never by which of its two
    /// spellings a name happened to carry.
    /// </para>
    /// <para>
    /// At this size a plain array beats a frozen collection: the work is a short scan of short token
    /// runs, and a hash-based structure could not answer a contiguous-run question anyway.
    /// </para>
    /// </remarks>
    private static readonly (string[] Tokens, ProhibitedColumnRule Rule)[] CompiledRules =
        Rules
            .SelectMany(rule => new[]
            {
                (Tokens: IdentifierTokens.Tokenize(rule.Pattern), Rule: rule),
                (Tokens: IdentifierTokens.Tokenize(IdentifierTokens.PluralOf(rule.Pattern)), Rule: rule),
            })
            .ToArray();

    /// <summary>
    /// Says which rule a column or relation name trips, or <see langword="null" /> when the name is
    /// one the product is happy to carry.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The rule rather than its category</b>, because the category alone is a bucket name and
    /// <see cref="ProhibitedColumnRule.Reason" /> is the member that makes this list arguable. Handing
    /// the whole rule back lets a caller print the argument for the refusal on the same line as the
    /// name it refused, instead of leaving the reason in a file the reader has to know to open — and it
    /// is the only way a caller can see <i>which</i> rule won when a name reaches two inside one
    /// category. A caller wanting only the bucket reads <see cref="ProhibitedColumnRule.Category" />
    /// off the answer.
    /// </para>
    /// <para>
    /// Null on a null or blank input rather than an exception. This is a classifier reading names
    /// out of a catalog or a model, not a validator of its caller's arguments, and a scan that threw
    /// partway through would report fewer offenders than exist — the fail-open direction.
    /// </para>
    /// <para>
    /// <b>The parameter is nullable because its callers' inputs are.</b>
    /// <c>IEntityType.GetTableName()</c> and <c>IProperty.GetColumnName()</c> both answer
    /// <see langword="null" /> for an entity or a property the model maps to no table or column, so a
    /// non-null parameter would leave every model-reading caller either reaching for <c>!</c> —
    /// asserting something the model does not promise — or dropping the name before the classifier
    /// ever sees it. Either way the blank guard below could not fire, which would make the fail-open
    /// argument for it a claim about unreachable code. Taking the null the callers actually hold is
    /// what leaves that argument true.
    /// </para>
    /// <para>
    /// Comparison is ordinal on invariantly lower-cased tokens. PostgreSQL folds unquoted
    /// identifiers to lower case, but a quoted one keeps its case and a caller may pass anything, so
    /// the folding happens here too. Culture-sensitive comparison is avoided outright: the Turkish
    /// dotless <c>i</c> alone would make <c>ip</c> and <c>idfa</c> match or miss depending on the
    /// machine the build ran on.
    /// </para>
    /// </remarks>
    /// <param name="identifier">
    /// A column or relation name, in any casing and from any source, or <see langword="null" /> when
    /// the model or catalog the caller read it from had none.
    /// </param>
    /// <returns>
    /// The rule the name trips — its pattern, the category it names and the argument for refusing it —
    /// or <see langword="null" /> when the name trips none.
    /// </returns>
    public static ProhibitedColumnRule? Classify(string? identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            return null;
        }

        string[] tokens = IdentifierTokens.Tokenize(identifier);

        foreach ((string[] patternTokens, ProhibitedColumnRule rule) in CompiledRules)
        {
            if (IdentifierTokens.ContainsRun(tokens, patternTokens))
            {
                return rule;
            }
        }

        return null;
    }
}
