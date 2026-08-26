using System.Diagnostics.CodeAnalysis;

namespace Application.Security;

/// <summary>
/// The one parse-and-validate step for a client-minted identifier that crosses the wire as text, on
/// every write path that accepts one.
/// </summary>
/// <remarks>
/// <para>
/// <b>One definition rather than one per handler</b>, the shape
/// <see cref="Application.Passkeys.WrappedKeyEnvelope"/> already holds for the members beside it and for
/// the same reason: the paths that accept a client-minted identifier are not that many separate
/// decisions about what an identifier is. A factor identifier and a narrative row identifier are minted
/// by different screens and land in different columns, but each is a uuid a client chose and then sent
/// as text, and a rule that drifted on one of them would accept a spelling the others cannot reproduce.
/// Wherever such a value binds the associated data of an envelope, that drift seals ciphertext under a
/// name no later read can rebuild. What stays per caller is the sentence each refuses with — those are
/// worded for different callers and are keyed under different members.
/// </para>
/// <para>
/// <b>In <c>Application</c> and not in <c>Domain</c>, because a spelling is a wire concern.</b>
/// Application is the only ring that sees text at all: by the time a value reaches Domain it is a
/// <see cref="Guid"/> that something else already parsed, so a rule about how a uuid is written down
/// would have no subject there. This type is the edge that decides which of the many texts denoting one
/// <see cref="Guid"/> this API will accept, and it is the only place that question is asked.
/// </para>
/// <para>
/// A <c>Try</c> shape rather than a throw, as the types beside it argue: every call site already turns a
/// malformed member into a refusal of its own.
/// </para>
/// </remarks>
public static class CanonicalIdentifier
{
    /// <summary>
    /// Parses <paramref name="value"/> when it is the canonical spelling of a non-empty uuid, or returns
    /// false when it is absent, spelled any other way, or the all-zero uuid.
    /// </summary>
    /// <param name="value">The text a caller supplied.</param>
    /// <param name="id">The parsed identifier, when the value was accepted.</param>
    public static bool TryParse([NotNullWhen(true)] string? value, out Guid id)
    {
        id = Guid.Empty;

        if (!Guid.TryParseExact(value, "D", out Guid parsed))
        {
            return false;
        }

        // THE ROUND TRIP IS THE RULE, and it is not a redundant restatement of the parse above. "D" is a
        // format, not a spelling: TryParseExact also admits upper-case and mixed-case hex, and it trims
        // leading and trailing whitespace before it looks at the format at all. Each of those is a
        // different value on the wire and the same Guid in the row — and the row is what every later read
        // hands back, rendered lower case, unspaced, once.
        //
        // This value binds the associated data of every envelope sealed against it — a factor's two, a
        // narrative field's one — and associated data is rebuilt from where the ciphertext was found
        // rather than carried inside it. A client that binds the spelling it sent and is handed back
        // another rebuilds a binding that cannot reproduce the seal, so every envelope under it stops
        // opening, permanently, with nothing anywhere naming the cause. This repository's own client
        // escapes that in two different ways — the wrapped-key path folds to lower case before sealing,
        // the narrative path refuses a row id it would have had to fold — and the contract is
        // cross-client, with the mobile client's source not here to check.
        // Comparing the text back against what the parsed value renders as is the only formulation that
        // cannot drift from the guarantee it is making, because it IS the rendering. A length check
        // closes neither the case folding nor the trim; a regular expression can be written to close
        // both, but it is a second, hand-maintained copy of a format this code does not own, and the day
        // Guid renders differently the copy goes on being confident.
        if (!string.Equals(value, parsed.ToString("D"), StringComparison.Ordinal))
        {
            return false;
        }

        // Refused for its own reason and not as a spelling: the all-zero uuid is what an unset field
        // sends — a zero-filled struct, a stubbed client, a member nobody assigned — and it arrives
        // indistinguishable from a value somebody chose. Accepting it stores "nobody chose one" in a
        // column every later read, and every binding rebuilt from it, treats as a choice, and by then
        // there is nothing left to tell the two apart by. It is also the one value two clients reach
        // without coordinating, so where the identifier is a key unique across a whole table —
        // wrapped_account_keys.factor_id is one — that same client bug lands as a collision BETWEEN
        // accounts, which is the worst shape it takes and not the reason it is refused: an ordinary row
        // id on a budget-scoped table is turned away here just as flatly. WrappedAccountKeys.For and
        // the database each restate it.
        if (parsed == Guid.Empty)
        {
            return false;
        }

        id = parsed;

        return true;
    }
}
