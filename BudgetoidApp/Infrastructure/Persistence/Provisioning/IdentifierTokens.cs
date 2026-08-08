namespace Infrastructure.Persistence.Provisioning;

/// <summary>
/// The one way this codebase splits a database identifier into words, and the one way it asks
/// whether a pattern's words appear inside it.
/// </summary>
/// <remarks>
/// <para>
/// Three readers ask it: <see cref="ProhibitedColumnVocabulary" />, which says what a name tells us
/// the schema is keeping <i>about a person</i>; <c>ErasureRemnantVocabulary</c>, which says what a
/// name tells us survived an erasure; and <c>ErasureIrreversibilityTests</c>, which asks the same
/// of route patterns rather than of column names. <b>Their lists stay apart and only the reading of a
/// name is shared</b>, because the lists are different rules — different categories, different
/// remedies, different owning documents — while "what are this name's words, and does this phrase
/// appear among them" is one question with one right answer.
/// </para>
/// <para>
/// Two of the three also share <see cref="PluralOf" />, which the third does not ask for. It is a
/// separate, opt-in member rather than a stemming step inside the matching for exactly the reason
/// above: a reader that never asks for it sees the tokens and the verdicts it saw before.
/// </para>
/// <para>
/// One spelling rather than a copy each, because the readers run over the same identifiers in
/// neighbouring checks. Two copies drift silently in exactly the way that matters here: a case
/// boundary fixed in one of them makes <c>isDeleted</c> start matching in one scanner and not in the
/// other, and the pair of verdicts a reviewer is handed then has no explanation. Two written-down
/// copies of one rule have no adjudicator when they disagree.
/// </para>
/// <para>
/// It sits here because its production-side reader does. It could not follow the erasure-remnant
/// vocabulary into <c>TestSupport</c>: that project references Infrastructure, so a reference back
/// would be a cycle.
/// </para>
/// </remarks>
public static class IdentifierTokens
{
    /// <summary>
    /// Splits an identifier into lower-cased word tokens, on separators and on case boundaries.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The case boundaries are what let this read a quoted identifier EF may have mapped verbatim —
    /// <c>userAgent</c> and <c>IPAddress</c> both reach the same tokens their snake-cased spellings
    /// would, as does <c>isDeleted</c>. Two boundaries are needed for that: lower-to-upper splits
    /// <c>userAgent</c>, and an upper run followed by a lower letter splits <c>IPAddress</c> into
    /// <c>IP</c> and <c>Address</c>. Only the first would leave <c>IPAddress</c> as one token and
    /// miss it.
    /// </para>
    /// <para>
    /// Digits stay attached to the token they sit in, so <c>ga4_client_id</c> tokenizes as
    /// <c>ga4</c> rather than as <c>ga</c> and <c>4</c>. That is a miss for the <c>ga_client_id</c>
    /// phrase and it is the accepted direction: separating them would make <c>id</c>-adjacent
    /// patterns start matching numbered columns, which is the false-positive failure this whole
    /// design is arranged to avoid.
    /// </para>
    /// </remarks>
    /// <param name="identifier">A column or relation name, in any casing and from any source.</param>
    /// <returns>The identifier's words, lower-cased, in the order they appear.</returns>
    public static string[] Tokenize(string identifier)
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

    /// <summary>
    /// Whether <paramref name="pattern" /> appears as a contiguous run inside
    /// <paramref name="tokens" />.
    /// </summary>
    /// <remarks>
    /// Contiguous rather than merely present, because a phrase pattern is a claim about a name and
    /// not about a bag of words. <c>user_id</c> beside an <c>agent_code</c> column is two ordinary
    /// names; <c>user_agent</c> is one forbidden one. A <c>soft</c> currency rounding column beside a
    /// <c>delete_reason</c> is two ordinary names; <c>soft_delete</c> is one refused name. In both
    /// cases only adjacency tells them apart, which is why both vocabularies ask the question this
    /// way.
    /// </remarks>
    /// <param name="tokens">An identifier's tokens, as returned by <see cref="Tokenize" />.</param>
    /// <param name="pattern">A pattern's tokens, tokenized the same way.</param>
    /// <returns>Whether the pattern's tokens appear consecutively and in order.</returns>
    public static bool ContainsRun(string[] tokens, string[] pattern)
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

    /// <summary>
    /// The plural of a pattern, formed on its last token, for a vocabulary that wants a relation name
    /// to reach the rule its column-shaped spelling reaches.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Opt-in, and it changes no matcher behaviour.</b> <see cref="Tokenize" /> and
    /// <see cref="ContainsRun" /> are untouched, so a reader that never calls this sees exactly the
    /// tokens and exactly the verdicts it saw before. That separation is the point: the remarks above
    /// argue that reading a name is one question with one right answer for all three readers, and a
    /// stemming step folded into the answer would change what every one of them means without anybody
    /// touching a list. A vocabulary opts in by compiling each pattern twice, in the form it is
    /// written and in the form returned here; <see cref="ProhibitedColumnVocabulary" /> and
    /// <c>ErasureRemnantVocabulary</c> both do, and the route-pattern reader does not. It is spelled
    /// once here rather than once in each of those two, because a pluralisation rule written down
    /// twice is two rules with no adjudicator when they disagree.
    /// </para>
    /// <para>
    /// The <b>last</b> token, because an English composite pluralises its head noun and the head noun
    /// sits last: <c>event_log</c> becomes <c>event_logs</c> and never <c>events_log</c>. That token
    /// ends where the pattern ends, so its ending is read off the whole string and the pattern never
    /// has to be split apart and rejoined.
    /// </para>
    /// <para>
    /// <c>+es</c> after a sibilant — <c>s</c>, <c>x</c>, <c>z</c>, <c>ch</c> or <c>sh</c> — and
    /// <c>+s</c> otherwise. The sibilant branch is not tidiness: under a bare <c>+s</c>,
    /// <c>mac_address</c> expands to <c>mac_addresss</c> and a table named <c>mac_addresses</c> walks
    /// straight past the rule written for it, which is a gap rather than a curiosity.
    /// </para>
    /// <para>
    /// <b>The endings English inflects some other way are not handled.</b> A <c>y</c> becomes
    /// <c>ys</c> rather than <c>ies</c>, and no irregular is attempted. Nothing on either list needs
    /// them today: the only <c>y</c> ending across the two is the mass noun <c>telemetry</c>, which
    /// does not arrive in the plural, and no other pattern inflects irregularly. Where the ending is
    /// wrong the result is a non-word — <c>telemetrys</c>, <c>seens</c>, <c>deleteds</c>, and
    /// <c>analyticses</c> for a noun that was already plural — which matches nothing rather than
    /// something wrong, so the expansion stays in the direction that only ever adds refusals. A
    /// pattern that genuinely needs an irregular plural should be written down as its own rule
    /// carrying its own argument, which is cheaper to read than an inflection engine sitting between
    /// a list and its verdict.
    /// </para>
    /// </remarks>
    /// <param name="pattern">A pattern as its vocabulary writes it down.</param>
    /// <returns>The same pattern with its last token pluralised.</returns>
    public static string PluralOf(string pattern)
    {
        // Case-insensitive, so a pattern written in another casing pluralises the same way, and
        // ordinal rather than cultural, which is the comparison every other member here makes.
        bool endsInSibilant = pattern.EndsWith("s", StringComparison.OrdinalIgnoreCase)
            || pattern.EndsWith("x", StringComparison.OrdinalIgnoreCase)
            || pattern.EndsWith("z", StringComparison.OrdinalIgnoreCase)
            || pattern.EndsWith("ch", StringComparison.OrdinalIgnoreCase)
            || pattern.EndsWith("sh", StringComparison.OrdinalIgnoreCase);

        return endsInSibilant ? pattern + "es" : pattern + "s";
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
}
