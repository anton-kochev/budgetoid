using Budgetoid.Domain.Entities;
using MediatR;
using Microsoft.Azure.Cosmos;

namespace Budgetoid.Application.Payees.Queries.GetPayees;

public record GetPayeesQuery(Guid UserId) : IRequest<IEnumerable<PayeeDto>>;

public sealed class GetPayeesHandler : IRequestHandler<GetPayeesQuery, IEnumerable<PayeeDto>>
{
    private readonly Container _container;

    public GetPayeesHandler(CosmosClient cosmosClient)
    {
        _container = cosmosClient.GetContainer("Budgetoid", "Payees");
    }

    public async Task<IEnumerable<PayeeDto>> Handle(GetPayeesQuery request, CancellationToken cancellationToken)
    {
        IEnumerable<PayeeDto> payees =
            (await _container.GetItemQueryIterator<Payee>().ReadNextAsync(cancellationToken))
            .Where(p => p.UserId == request.UserId.ToString())
            .Select(p => new PayeeDto(p.Name, p.GeoLocation));

        return payees;
    }
}
