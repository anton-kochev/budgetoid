using Application.Passkeys;
using Domain.Payees;

namespace Application.Payees;

/// <summary>
/// One payee as every payee-reading route hands it back.
/// </summary>
/// <param name="Id">
/// The row's identifier — also the associated data <paramref name="Name"/> was sealed against, which is
/// why it is on a read at all rather than only on the write that chose it.
/// </param>
/// <param name="Name">The <b>sealed</b> name as unpadded base64url; see the remarks.</param>
/// <remarks>
/// <para>
/// <b><see cref="Name"/> is still a <see cref="string"/> and no longer holds a name</b> — the treatment
/// <c>AccountDto.Name</c> and <c>ExportedBudget.Name</c> already carry. The column is an AEAD envelope
/// this server cannot open, so what ships is that envelope in the one alphabet every binary member of
/// this API crosses JSON in: unpadded base64url, which the client's strict decoder already reads. Not
/// <c>System.Text.Json</c>'s own <see cref="byte"/><c>[]</c> handling, which emits padded standard
/// base64 — two spellings that disagree the first time somebody decodes one with the other.
/// </para>
/// <para>
/// <b>The blind index is on no read, and its absence is a decision rather than an omission.</b> The
/// requirement says the index is stored, not returned: a client recomputes it from the name it just
/// decrypted, under a key only it holds, and needs it solely to write. A member nobody reads is a
/// standing surface with no reason — and on this table it would be the worst one to offer, because a
/// payee list is the set of counterparties one person deals with and its index column is a
/// deterministic, per-budget fingerprint of every one of those names. Handing it back would let anybody
/// who saw two responses tell which names they had in common, with no key anywhere in the exchange.
/// </para>
/// </remarks>
public sealed record PayeeDto(Guid Id, string Name)
{
    public static PayeeDto FromPayee(Payee payee) => new(
        payee.Id,
        PasskeyEncoding.Encode(payee.Name.Envelope.Span));
}

public sealed record PayeeListResponse(IReadOnlyList<PayeeDto> Items);
