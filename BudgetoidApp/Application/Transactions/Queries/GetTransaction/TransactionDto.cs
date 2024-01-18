using AutoMapper;
using Domain.Common;
using Domain.Entities;

namespace Application.Transactions.Queries.GetTransaction;

public record TransactionDto
{
    public Guid AccountId { get; init; } = Guid.Empty;
    public decimal Amount { get; init; }
    public Guid CategoryId { get; init; } = Guid.Empty;
    public string Comment { get; init; } = string.Empty;
    public DateOnly Date { get; init; }
    public Guid Id { get; init; } = Guid.Empty;
    public string Payee { get; init; } = string.Empty;
    public string[] Tags { get; init; } = Array.Empty<string>();
}

internal sealed class TransactionProfile : Profile
{
    public TransactionProfile()
    {
        CreateMap<Transaction, TransactionDto>()
            .ForMember(dest => dest.Id, opt => opt.MapFrom(src => Guid.Parse(src.Id)))
            .ForMember(dest => dest.Date, opt => opt.MapFrom(src => src.Date.ToDateOnly()));
    }
}
