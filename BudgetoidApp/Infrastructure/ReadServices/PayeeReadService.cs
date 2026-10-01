using Application.Passkeys;
using Application.Payees;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.ReadServices;

// Neither query below builds the PayeeDto inside the Select. payees.name carries a value converter, so
// the provider translates the property itself and the NarrativeField exists only once the row has
// materialized; encoding it in the projection would be a call the translator has to make sense of. The
// shape is AccountReadService's, deliberately, because it is the same problem.
public sealed class PayeeReadService(BudgetoidDbContext dbContext) : IPayeeReadService
{
    public async Task<IReadOnlyList<PayeeDto>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        // ORDERED BY THE CREATION INSTANT AND THEN BY ID, AND NOT BY NAME. `OrderBy(payee => payee.Name)`
        // was here and cannot stay: over a bytea column it orders by whatever the first differing byte is,
        // which after the version byte is the nonce — freshly drawn on every seal. That is stable within
        // one read and reshuffled by every save, so the list would silently reorder itself when an
        // unrelated payee was renamed, and no ordering a person recognises would ever come back. A name
        // order can only be restored by the client, which is the only side that holds the text.
        //
        // The instant leads and the id breaks ties, the shape AccountReadService, ExportReadService and
        // CredentialReadService already use: UUID v7 sorts by creation time under PostgreSQL's uuid byte
        // order but not under .NET's Guid.CompareTo, so an id-first ordering is one an in-memory
        // implementation cannot reproduce. IBudgetRepository.FindFirstForUserAsync states the same rule
        // for the same reason.
        var rows = await dbContext.Payees
            .AsNoTracking()
            .OrderBy(payee => payee.CreatedAtUtc)
            .ThenBy(payee => payee.Id)
            .Select(payee => new { payee.Id, payee.Name })
            .ToListAsync(cancellationToken);

        return [.. rows.Select(row => new PayeeDto(row.Id, PasskeyEncoding.Encode(row.Name.Envelope.Span)))];
    }

    public async Task<PayeeDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var row = await dbContext.Payees
            .AsNoTracking()
            .Where(payee => payee.Id == id)
            .Select(payee => new { payee.Id, payee.Name })
            .FirstOrDefaultAsync(cancellationToken);

        // The envelope goes out as text in the one alphabet every binary member of this API crosses JSON
        // in. PasskeyEncoding despite its name, because it is that alphabet's only implementation here and
        // a second base64url encoder beside it is exactly the drift its neighbours argue against. Not
        // System.Text.Json's own byte[] handling, which emits padded standard base64 the client's strict
        // decoder refuses.
        return row is null
            ? null
            : new PayeeDto(row.Id, PasskeyEncoding.Encode(row.Name.Envelope.Span));
    }
}
