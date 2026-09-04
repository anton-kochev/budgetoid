using Domain.Currencies;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Persistence.Configurations;

public sealed class CurrencyConfiguration : IEntityTypeConfiguration<Currency>
{
    public void Configure(EntityTypeBuilder<Currency> builder)
    {
        builder.ToTable("currencies", table =>
        {
            // minor_unit is an input to the rounding rule, so a bad value produces wrongly rounded
            // money rather than nothing at all. The bound is not an arbitrary sanity range: it equals
            // the scale of the money columns, accounts.opening_balance and transactions.amount, so
            // whoever changes that scale trips over this constraint and learns the two move together.
            table.HasCheckConstraint("CK_currencies_minor_unit", "minor_unit between 0 and 4");

            // varchar(3) already bounds the length; this adds the ISO 4217 shape. The realistic
            // failure it prevents is a row that never went through Currency.Create - inserted by hand
            // or by migrationBuilder.InsertData - as 'usd'. ICurrencyReadService.GetByCodeAsync
            // upper-cases its input before matching, so such a row would sit in the table permanently
            // unfindable: a silently broken currency instead of a loud error. The regex also makes
            // this column permanently incompatible with a nondeterministic collation, which fails
            // regex matching with 0A000 - measured on postgres:17.10 and 18.3 alike, because 18
            // relaxed that restriction for LIKE and for substring search but not for regular
            // expressions. code carries the default collation today, unlike users.email, the only
            // case_insensitive column left in the schema now that the four name columns are bytea and
            // bytea is not collatable, so there is no interaction yet.
            table.HasCheckConstraint("CK_currencies_code", "code ~ '^[A-Z]{3}$'");
        });
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
