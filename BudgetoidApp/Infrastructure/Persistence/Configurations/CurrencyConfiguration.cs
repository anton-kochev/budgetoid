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

        builder.HasData(
            Currency.Create("AUD", "Australian Dollar", "A$", 2),
            Currency.Create("BHD", "Bahraini Dinar", "BD", 3),
            Currency.Create("CAD", "Canadian Dollar", "C$", 2),
            Currency.Create("CHF", "Swiss Franc", "Fr", 2),
            Currency.Create("EUR", "Euro", "€", 2),
            Currency.Create("GBP", "Pound Sterling", "£", 2),
            Currency.Create("JPY", "Japanese Yen", "¥", 0),
            Currency.Create("KWD", "Kuwaiti Dinar", "KD", 3),
            Currency.Create("NOK", "Norwegian Krone", "kr", 2),
            Currency.Create("PLN", "Polish Zloty", "zł", 2),
            Currency.Create("SEK", "Swedish Krona", "kr", 2),
            Currency.Create("UAH", "Ukrainian Hryvnia", "₴", 2),
            Currency.Create("USD", "US Dollar", "$", 2));
    }
}
