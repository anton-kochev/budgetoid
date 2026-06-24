using Domain.Payees;

namespace Application.Payees;

public sealed record PayeeDto(Guid Id, string Name)
{
    public static PayeeDto FromPayee(Payee payee) => new(payee.Id, payee.Name);
}

public sealed record PayeeListResponse(IReadOnlyList<PayeeDto> Items);
