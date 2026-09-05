using Domain.Common;

namespace UnitTests;

/// <summary>
/// One <see cref="ConflictKind" /> member and the token a client will read for it, written out by hand.
/// </summary>
/// <remarks>
/// <para>
/// <b>The member is referenced and the token is transcribed, and the asymmetry is the whole design.</b>
/// Referencing the member makes a deleted one a compile error and makes a pin naming no member
/// impossible to write — which is why this census has no bucket for that, unlike its sibling over
/// repository names, where the subject is discovered as text and a stale entry is a real hazard.
/// Transcribing the token is what gives the census a second, independent side: a rename of the wire
/// contract has to be made here as well as in <see cref="ConflictKindSpelling" /> before anything goes
/// green again.
/// </para>
/// <para>
/// <b>A pure member rename is deliberately NOT red.</b> An IDE rename carries the reference here with
/// it, the token is untouched, and nothing about the response changed — which is exactly the
/// independence <see cref="ConflictKindSpelling" /> exists to buy. What is red is the tidy-up that
/// follows one: a member renamed and its token "corrected" to match reissues the contract under a new
/// word, and this file is the only thing in the solution that notices.
/// </para>
/// </remarks>
/// <param name="Kind">The member, referenced so the compiler holds this half.</param>
/// <param name="Token">
/// The token that member is written down as, transcribed rather than read from the type under test.
/// </param>
public sealed record ConflictKindPin(ConflictKind Kind, string Token);

/// <summary>
/// Every declared <see cref="ConflictKind" /> member sorted by what the pins and the spelling table say
/// about it, plus the tokens that stand for more than one member.
/// </summary>
/// <param name="Unpinned">
/// Members no pin names. This is the bucket a new kind lands in, and it is red rather than derived:
/// there is nothing to compute for a member nobody has chosen a word for, and deriving one from the
/// member name is the precise thing this table exists instead of.
/// </param>
/// <param name="Unspelled">
/// Members <see cref="ConflictKindSpelling.Of" /> refuses. Reported rather than thrown so that a member
/// added with neither a token nor a pin is named twice — once here and once above — instead of aborting
/// the census at whichever member the loop reached first.
/// </param>
/// <param name="PinnedTwice">
/// Members more than one pin names. Two pins are two answers to "what does a client read", and the one
/// nobody looks at is the one that rots — the same reason its sibling census refuses a repository
/// claimed by two dispositions.
/// </param>
/// <param name="Disagreeing">
/// Members whose pin and spelling are different words, rendered as both words so a failure says what
/// the contract moved from and to rather than that it moved.
/// </param>
/// <param name="SharedTokens">
/// Tokens standing for more than one member. Nothing else in the solution catches this: a copy-paste in
/// the spelling table compiles, answers 409 with a real word, and quietly makes two remedies
/// indistinguishable again — which is the state the whole member was added to leave behind.
/// </param>
/// <param name="Agreed">
/// Members with exactly one pin that the spelling table agrees with. Carried so a caller can prove the
/// census read real members rather than passing over an empty sequence.
/// </param>
public sealed record ConflictKindCensus(
    IReadOnlyList<string> Unpinned,
    IReadOnlyList<string> Unspelled,
    IReadOnlyList<string> PinnedTwice,
    IReadOnlyList<string> Disagreeing,
    IReadOnlyList<string> SharedTokens,
    IReadOnlyList<string> Agreed);

/// <summary>
/// Pins the token every <see cref="ConflictKind" /> member is written down as, and refuses a member
/// nobody has chosen one for.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this covers that no integration case can.</b> Three of the eight tokens are asserted on the
/// wire — <c>duplicate_name</c> and <c>duplicate_identifier</c> in <c>PayeeIntegrationTests</c>,
/// <c>last_passkey</c> in <c>CredentialRevocationTests</c> — and the other five were held by review
/// alone, which is to say a one-character typo in the spelling table shipped a contract no client could
/// branch on and nothing went red. Covering the remaining five the same way is the wrong shape twice
/// over: each costs a container, and the argument those cases carry — that a test asserting a wire
/// contract must transcribe the word rather than read it from the code under test — does not scale to
/// eight. <b>This file is the one place the words are allowed to be transcribed, because it is the
/// place that owns them.</b>
/// </para>
/// <para>
/// <b>It is a census and not eight asserts, for the reason
/// <c>RepositoryAttributionCensusTests</c> is one.</b> Eight asserts pass forever after a ninth member
/// arrives: the subject would be written down, so the member nobody added to the list is the one day
/// the file matters and the one day it says nothing. Here the subject is <i>discovered</i> —
/// <c>Enum.GetValues</c> off the live type — and only the disposition is written down, so a ninth
/// member is claimed by nobody and stays red until a person chooses a word for it beside the seven it
/// has to be unlike. Do not "simplify" this into a switch over the members or a comparison against
/// <see cref="ConflictKindSpelling" />'s own output; the first is a written-down subject wearing a
/// loop, and the second asserts only that the code agrees with itself.
/// </para>
/// <para>
/// <b>The two sides are independent and neither is computed from the other.</b> The spelling side is
/// whatever <see cref="ConflictKindSpelling.Of" /> answers, reached through the real method so a
/// missing <c>switch</c> arm is observed rather than reasoned about. The pin side is text a person
/// typed. A test that derived one from the other — <c>kind.ToString()</c>, a snake-case helper, a
/// naming policy — would be green on the day the member name and the wire word part company, which is
/// the failure <c>Domain.Users.CredentialTypeSpelling</c> and this type were both written to prevent.
/// </para>
/// <para>
/// <b>The synthetic cases below are permanent negative controls, not scaffolding.</b> They feed the
/// census vocabularies this solution does not have, so the live assertion cannot pass by having found
/// nothing to report — measured: a census returning empty buckets for every input satisfies the live
/// case and fails all five of them.
/// </para>
/// </remarks>
public sealed class ConflictKindSpellingTests
{
    /// <summary>
    /// The eight tokens, transcribed. A ninth member of <see cref="ConflictKind" /> is red until it has
    /// a line here, and the line is where somebody chooses a word unlike the eight above it.
    /// </summary>
    private static readonly ConflictKindPin[] Pinned =
    [
        new(ConflictKind.DuplicateIdentifier, "duplicate_identifier"),
        new(ConflictKind.DuplicateName, "duplicate_name"),
        new(ConflictKind.SubjectAlreadyRegistered, "subject_already_registered"),
        new(ConflictKind.EmailAlreadyLinked, "email_already_linked"),
        new(ConflictKind.AuthenticatorAlreadyRegistered, "authenticator_already_registered"),
        new(ConflictKind.FactorAlreadyRegistered, "factor_already_registered"),
        new(ConflictKind.LastPasskey, "last_passkey"),
        new(ConflictKind.RecoveryCodesReplaced, "recovery_codes_replaced"),
    ];

    [Test]
    public async Task EveryConflictKind_IsSpelledAsThePinSaysItIs()
    {
        // Arrange — the subject is read off the live type and never written down here.
        ConflictKind[] declared = Enum.GetValues<ConflictKind>();

        // Act
        ConflictKindCensus census = ConflictKindVocabulary.Take(
            declared, Pinned, ConflictKindSpelling.Of);

        // Assert — joined rather than counted, so a failure names the member and both words rather
        // than reporting that some number of them are wrong.
        await Assert.That(string.Join(", ", census.Unpinned)).IsEqualTo(string.Empty);
        await Assert.That(string.Join(", ", census.Unspelled)).IsEqualTo(string.Empty);
        await Assert.That(string.Join(", ", census.PinnedTwice)).IsEqualTo(string.Empty);
        await Assert.That(string.Join(", ", census.Disagreeing)).IsEqualTo(string.Empty);
        await Assert.That(string.Join(", ", census.SharedTokens)).IsEqualTo(string.Empty);

        // Non-vacuity, and the half that makes the five lines above mean anything: every declared
        // member reached the comparison. Without it a census that silently dropped members — or an
        // Enum.GetValues that came back empty — leaves five empty buckets and a green bar.
        await Assert.That(string.Join(", ", census.Agreed))
            .IsEqualTo(string.Join(", ", declared.Select(kind => kind.ToString())));
    }

    [Test]
    public async Task Census_ReportsAMemberNobodyPinned()
    {
        // Arrange — the case this census exists for: a ninth kind lands in the enum, somebody spells
        // it in the table, and nobody decides here what a client is going to read. Synthetic, so the
        // proof survives the day the real vocabulary is complete.
        ConflictKind[] declared = [ConflictKind.LastPasskey, ConflictKind.DuplicateName];
        ConflictKindPin[] pins = [new(ConflictKind.LastPasskey, "last_passkey")];

        // Act — a distinct token per member, so what is reported is the missing pin and not a
        // collision this arrangement introduced.
        ConflictKindCensus census = ConflictKindVocabulary.Take(
            declared, pins, kind => kind == ConflictKind.LastPasskey ? "last_passkey" : "spelled_anyway");

        // Assert — named, not counted. Its neighbour stays in Agreed, which is what says the census
        // reported the one member nobody decided about rather than giving up on the whole vocabulary.
        await Assert.That(census.Unpinned).IsEquivalentTo(new[] { nameof(ConflictKind.DuplicateName) });
        await Assert.That(census.Agreed).IsEquivalentTo(new[] { nameof(ConflictKind.LastPasskey) });
    }

    [Test]
    public async Task Census_ReportsAMemberTheSpellingTableHasNoTokenFor()
    {
        // Arrange — the other half of a member added in a hurry: pinned here, and missing an arm in
        // ConflictKindSpelling.Of, which answers by throwing.
        ConflictKind[] declared = [ConflictKind.LastPasskey];
        ConflictKindPin[] pins = [new(ConflictKind.LastPasskey, "last_passkey")];

        // Act — the refusal is the real one: the exception type Of documents for an undeclared member.
        ConflictKindCensus census = ConflictKindVocabulary.Take(
            declared,
            pins,
            kind => throw new ArgumentOutOfRangeException(nameof(kind), kind, "No spelling."));

        // Assert — reported rather than propagated, so the census still runs to the end and a second
        // unspelled member is named in the same failure.
        await Assert.That(census.Unspelled).IsEquivalentTo(new[] { nameof(ConflictKind.LastPasskey) });
        await Assert.That(string.Join(", ", census.Agreed)).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task Census_ReportsAPinDisagreeingWithTheSpelling()
    {
        // Arrange — the tidy-up this file is really for: the token in the spelling table is edited,
        // the wire contract changes, and every caller in the solution still compiles.
        ConflictKind[] declared = [ConflictKind.LastPasskey];
        ConflictKindPin[] pins = [new(ConflictKind.LastPasskey, "last_passkey")];

        // Act
        ConflictKindCensus census = ConflictKindVocabulary.Take(declared, pins, _ => "only_passkey");

        // Assert — both words in the message, because "LastPasskey disagrees" sends the reader back to
        // two files to find out which way.
        await Assert.That(census.Disagreeing)
            .IsEquivalentTo(new[] { "LastPasskey: pinned 'last_passkey', spelled 'only_passkey'" });
        await Assert.That(string.Join(", ", census.Agreed)).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task Census_ReportsOneTokenStandingForTwoMembers()
    {
        // Arrange — a copy-paste in the spelling table. Both members are pinned, both agree with what
        // they are spelled as, and the vocabulary has silently stopped discriminating: two opposite
        // remedies arrive at a client as one word, which is the state the member was added to end.
        ConflictKind[] declared = [ConflictKind.DuplicateIdentifier, ConflictKind.DuplicateName];
        ConflictKindPin[] pins =
        [
            new(ConflictKind.DuplicateIdentifier, "duplicate_identifier"),
            new(ConflictKind.DuplicateName, "duplicate_identifier"),
        ];

        // Act
        ConflictKindCensus census = ConflictKindVocabulary.Take(declared, pins, _ => "duplicate_identifier");

        // Assert — the token is named with the members sharing it, and every other bucket is silent,
        // which is what makes this the only line that can catch it.
        await Assert.That(census.SharedTokens)
            .IsEquivalentTo(new[] { "duplicate_identifier: DuplicateIdentifier, DuplicateName" });
        await Assert.That(string.Join(", ", census.Disagreeing)).IsEqualTo(string.Empty);
        await Assert.That(string.Join(", ", census.Unpinned)).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task Census_ReportsAMemberPinnedTwice()
    {
        // Arrange — the well-meaning edit: a new line added without the old one being taken out, so
        // two entries answer "what does a client read" and the reader below picks whichever is first.
        ConflictKind[] declared = [ConflictKind.LastPasskey];
        ConflictKindPin[] pins =
        [
            new(ConflictKind.LastPasskey, "last_passkey"),
            new(ConflictKind.LastPasskey, "only_passkey"),
        ];

        // Act
        ConflictKindCensus census = ConflictKindVocabulary.Take(declared, pins, _ => "last_passkey");

        // Assert — reported as ambiguous rather than resolved. A census that took the first entry
        // would be green here on a table half of which is dead text.
        await Assert.That(census.PinnedTwice).IsEquivalentTo(new[] { nameof(ConflictKind.LastPasskey) });
        await Assert.That(string.Join(", ", census.Agreed)).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task Census_AcceptsAVocabularyWhereEveryMemberIsPinnedOnceAndAgrees()
    {
        // Arrange — without this, a census that reported every member in every bucket would satisfy
        // all five cases above while making the live assertion fire on a vocabulary nobody broke.
        ConflictKind[] declared = [ConflictKind.DuplicateIdentifier, ConflictKind.DuplicateName];
        ConflictKindPin[] pins =
        [
            new(ConflictKind.DuplicateIdentifier, "duplicate_identifier"),
            new(ConflictKind.DuplicateName, "duplicate_name"),
        ];

        // Act
        ConflictKindCensus census = ConflictKindVocabulary.Take(
            declared, pins, kind => kind == ConflictKind.DuplicateName ? "duplicate_name" : "duplicate_identifier");

        // Assert
        await Assert.That(string.Join(", ", census.Unpinned)).IsEqualTo(string.Empty);
        await Assert.That(string.Join(", ", census.Unspelled)).IsEqualTo(string.Empty);
        await Assert.That(string.Join(", ", census.PinnedTwice)).IsEqualTo(string.Empty);
        await Assert.That(string.Join(", ", census.Disagreeing)).IsEqualTo(string.Empty);
        await Assert.That(string.Join(", ", census.SharedTokens)).IsEqualTo(string.Empty);
        await Assert.That(census.Agreed).IsEquivalentTo(new[]
        {
            nameof(ConflictKind.DuplicateIdentifier), nameof(ConflictKind.DuplicateName),
        });
    }

    /// <summary>
    /// <c>default(ConflictKind)</c> has no token, and asking for one is loud.
    /// </summary>
    /// <remarks>
    /// The enum numbers from one so that <c>default</c> names no remedy, and this is the assertion that
    /// the numbering is load-bearing rather than decorative: renumber <see cref="ConflictKind" /> from
    /// zero and a conflict raised with a kind nobody chose would answer 409 wearing whichever member
    /// happened to sit at zero — a well-formed response sending a client down the wrong remedy, which
    /// nothing on this side would ever see. The 500 that this throw produces instead is the honest
    /// status for a programming error at a throw site.
    /// </remarks>
    [Test]
    public async Task Spelling_OfTheDefaultMember_IsRefused()
    {
        // Arrange
        ConflictKind unchosen = default;

        // Act, Assert — exactly, and not a base type: the refusal a caller of Of has to be able to
        // tell from any other ArgumentException the Domain raises.
        await Assert.That(() => ConflictKindSpelling.Of(unchosen))
            .ThrowsExactly<ArgumentOutOfRangeException>();
    }

    /// <summary>
    /// Sorts the declared members by what the pins and the spelling table say about each.
    /// </summary>
    /// <remarks>
    /// Takes the spelling resolver as an argument rather than calling
    /// <see cref="ConflictKindSpelling.Of" /> itself, which is what lets the synthetic cases above feed
    /// it vocabularies this solution does not have. The live case passes the real method, so nothing
    /// about the shipped answer is reproduced here.
    /// </remarks>
    private static class ConflictKindVocabulary
    {
        internal static ConflictKindCensus Take(
            IEnumerable<ConflictKind> declared,
            IEnumerable<ConflictKindPin> pins,
            Func<ConflictKind, string> spellingOf)
        {
            ArgumentNullException.ThrowIfNull(declared);
            ArgumentNullException.ThrowIfNull(pins);
            ArgumentNullException.ThrowIfNull(spellingOf);

            ILookup<ConflictKind, ConflictKindPin> byKind = pins.ToLookup(pin => pin.Kind);

            List<string> unpinned = [];
            List<string> unspelled = [];
            List<string> pinnedTwice = [];
            List<string> disagreeing = [];
            List<string> agreed = [];

            // Ordinal, and never a case-insensitive or culture-aware comparison: these are wire tokens,
            // so two spellings differing only by case are two contracts and a client branching on one
            // has no branch for the other.
            Dictionary<string, List<string>> membersByToken = new(StringComparer.Ordinal);

            foreach (ConflictKind kind in declared)
            {
                string name = kind.ToString();
                ConflictKindPin[] claims = [.. byKind[kind]];

                // Resolved before the pin is judged, so a member that is both unpinned and unspelled is
                // reported under both — the two are separate omissions with separate fixes.
                string? spelling = null;
                try
                {
                    spelling = spellingOf(kind);
                }
                catch (ArgumentOutOfRangeException)
                {
                    // The one exception Of documents for an undeclared member. Narrow on purpose:
                    // anything else out of the spelling table is a defect this census must not hide.
                    unspelled.Add(name);
                }

                if (spelling is not null)
                {
                    if (!membersByToken.TryGetValue(spelling, out List<string>? sharing))
                    {
                        sharing = [];
                        membersByToken[spelling] = sharing;
                    }

                    sharing.Add(name);
                }

                switch (claims.Length)
                {
                    case 0:
                        unpinned.Add(name);
                        break;
                    case 1 when spelling is not null && !string.Equals(claims[0].Token, spelling, StringComparison.Ordinal):
                        disagreeing.Add($"{name}: pinned '{claims[0].Token}', spelled '{spelling}'");
                        break;
                    case 1 when spelling is not null:
                        agreed.Add(name);
                        break;
                    case 1:
                        // Pinned, and reported above as unspelled. Deliberately not counted as agreed:
                        // there is no second word for the pin to have agreed with.
                        break;
                    default:
                        pinnedTwice.Add(name);
                        break;
                }
            }

            List<string> sharedTokens =
            [
                .. membersByToken
                    .Where(entry => entry.Value.Count > 1)
                    .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                    .Select(entry => $"{entry.Key}: {string.Join(", ", entry.Value)}"),
            ];

            return new ConflictKindCensus(
                unpinned, unspelled, pinnedTwice, disagreeing, sharedTokens, agreed);
        }
    }
}
