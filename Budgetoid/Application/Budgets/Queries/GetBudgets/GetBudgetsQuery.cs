using Budgetoid.Domain.Entities;
using MediatR;
using Microsoft.Azure.Cosmos;

namespace Budgetoid.Application.Budgets.Queries.GetBudgets;

public record GetBudgetsQuery(Guid UserId) : IRequest<IEnumerable<BudgetBriefDto>>;

public sealed class GetBudgetsHandler : IRequestHandler<GetBudgetsQuery, IEnumerable<BudgetBriefDto>>
{
    private readonly Container _container;

    public GetBudgetsHandler(CosmosClient cosmosClient)
    {
        _container = cosmosClient.GetContainer("Budgetoid", "Budgets");
    }

    public async Task<IEnumerable<BudgetBriefDto>> Handle(GetBudgetsQuery request, CancellationToken cancellationToken)
    {
        IEnumerable<BudgetBriefDto> result =
            (await _container.GetItemQueryIterator<Budget>().ReadNextAsync(cancellationToken))
            .Where(b => b.UserId == request.UserId.ToString())
            .Select(b => new BudgetBriefDto(Guid.Parse(b.Id), b.Name));

        return result;
    }
}
