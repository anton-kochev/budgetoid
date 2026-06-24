using Application.Abstractions;
using Domain.Payees;

namespace Application.Payees.GetPayees;

public sealed class GetPayeesHandler(IPayeeRepository repository)
    : IQueryHandler<GetPayeesQuery, PayeeListResponse>
{
    public async Task<PayeeListResponse> HandleAsync(
        GetPayeesQuery query,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<Payee> payees = await repository.GetAllAsync(cancellationToken);

        return new PayeeListResponse([.. payees.Select(PayeeDto.FromPayee)]);
    }
}
