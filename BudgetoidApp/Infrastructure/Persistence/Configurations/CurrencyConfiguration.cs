using Domain.Currencies;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Persistence.Configurations;

public sealed class CurrencyConfiguration : IEntityTypeConfiguration<Currency>
{
    public void Configure(EntityTypeBuilder<Currency> builder)
    {
        builder.ToTable("currencies");
        builder.HasKey(currency => currency.Code);

        builder.Property(currency => currency.Code).HasColumnName("code").HasMaxLength(3).IsRequired();
        builder.Property(currency => currency.Name).HasColumnName("name").HasMaxLength(100).IsRequired();
        builder.Property(currency => currency.Symbol).HasColumnName("symbol").HasMaxLength(8).IsRequired();
        builder.Property(currency => currency.MinorUnit).HasColumnName("minor_unit").IsRequired();
    }
}
