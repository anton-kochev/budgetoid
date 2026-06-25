using Application.Abstractions;

namespace Application.Payees.GetPayees;

public sealed class GetPayeesHandler(IPayeeReadService readService)
    : IQueryHandler<GetPayeesQuery, PayeeListResponse>
{
    public async Task<PayeeListResponse> HandleAsync(
        GetPayeesQuery query,
        CancellationToken cancellationToken = default)
    {
        return new PayeeListResponse(await readService.GetAllAsync(cancellationToken));
    }
}
