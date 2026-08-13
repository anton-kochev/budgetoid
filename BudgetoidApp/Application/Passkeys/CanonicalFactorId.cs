using System.Diagnostics.CodeAnalysis;

namespace Application.Passkeys;

/// <summary>
/// The one parse-and-validate step for the client-minted factor identifier, on every write path that
/// accepts one.
/// </summary>
/// <remarks>
/// <para>
/// <b>One definition rather than one per handler</b>, the shape <see cref="WrappedKeyEnvelope"/> already
/// holds for the members beside this one and for the same reason: passkey registration and recovery-code
/// generation are not two decisions about what a factor identifier is. They write the same column, and
/// the value binds the associated data of the envelopes they write with it, so a rule that drifted on
/// one path would seal an account's keys under a spelling the other path cannot reproduce. What stays
/// per handler is the sentence each refuses with — those are worded for different callers and are keyed
/// under different members.
/// </para>
/// <para>
/// A <c>Try</c> shape rather than a throw, again as that type argues: both call sites already turn a
/// malformed member into a refusal of their own.
/// </para>
/// </remarks>
public static class CanonicalFactorId
{
    /// <summary>
    /// Parses <paramref name="value"/> when it is the canonical spelling of a non-empty uuid, or returns
    /// false when it is absent, spelled any other way, or the all-zero uuid.
    /// </summary>
    /// <param name="value">The text a caller supplied.</param>
    /// <param name="factorId">The parsed identifier, when the value was accepted.</param>
    public static bool TryParse([NotNullWhen(true)] string? value, out Guid factorId)
    {
        factorId = Guid.Empty;

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
        // This value is the associated data both of the caller's envelopes were sealed with. A client
        // that binds to the spelling it sent and is handed back another rebuilds associated data that
        // cannot reproduce the seal, so BOTH envelopes stop opening, permanently, with nothing anywhere
        // naming the cause. This repository's own client is safe only because it lower-cases before
        // sealing; the contract is cross-client, and the mobile client's source is not here to check.
        // Comparing the text back against what the parsed value renders as is the only formulation that
        // cannot drift from the guarantee it is making, because it IS the rendering. A length check
        // closes neither the case folding nor the trim; a regular expression can be written to close
        // both, but it is a second, hand-maintained copy of a format this code does not own, and the day
        // Guid renders differently the copy goes on being confident.
        if (!string.Equals(value, parsed.ToString("D"), StringComparison.Ordinal))
        {
            return false;
        }

        // Refused for its own reason, not as a spelling: it is what an unset field sends, and it is the
        // one value two accounts reach independently — on a key unique across the whole table, that turns
        // a client bug into a cross-account collision. WrappedAccountKeys.For and the database each
        // restate it.
        if (parsed == Guid.Empty)
        {
            return false;
        }

        factorId = parsed;

        return true;
    }
}
