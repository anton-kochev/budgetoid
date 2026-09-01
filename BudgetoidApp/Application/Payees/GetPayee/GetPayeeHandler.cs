using Application.Abstractions;

namespace Application.Payees.GetPayee;

public sealed class GetPayeeHandler(IPayeeReadService readService)
    : IQueryHandler<GetPayeeQuery, PayeeDto?>
{
    public async Task<PayeeDto?> HandleAsync(
        GetPayeeQuery query,
        CancellationToken cancellationToken = default)
    {
        // The read runs through the budget query filter, so a payee belonging to another budget is
        // indistinguishable from one that never existed - both come back null and become a 404.
        return await readService.GetByIdAsync(query.Id, cancellationToken);
    }
}
