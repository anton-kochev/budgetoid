using Domain.Common;
using Domain.Currencies;

namespace UnitTests;

public sealed class CurrencyTests
{
    [Test]
    public async Task Create_WithValidInput_NormalizesAndStoresFields()
    {
        Currency currency = Currency.Create(" usd ", " US Dollar ", " $ ", 2);

        await Assert.That(currency.Code).IsEqualTo("USD");
        await Assert.That(currency.Name).IsEqualTo("US Dollar");
        await Assert.That(currency.Symbol).IsEqualTo("$");
        await Assert.That(currency.MinorUnit).IsEqualTo(2);
    }

    [Arguments("")]
    [Arguments("US")]
    [Arguments("US1")]
    [Arguments("USDE")]
    [Test]
    public async Task Create_WithInvalidCode_ThrowsValidationException(string code)
    {
        ValidationException exception = ThrowsValidationException(() =>
            Currency.Create(code, "US Dollar", "$", 2));

        await Assert.That(exception.Errors.ContainsKey("Code")).IsTrue();
    }

    [Test]
    public async Task Create_WithBlankName_ThrowsValidationException()
    {
        ValidationException exception = ThrowsValidationException(() =>
            Currency.Create("USD", "   ", "$", 2));

        await Assert.That(exception.Errors.ContainsKey("Name")).IsTrue();
    }

    [Test]
    public async Task Create_WithNameLongerThan100Characters_ThrowsValidationException()
    {
        ValidationException exception = ThrowsValidationException(() =>
            Currency.Create("USD", new string('x', 101), "$", 2));

        await Assert.That(exception.Errors.ContainsKey("Name")).IsTrue();
    }

    [Test]
    public async Task Create_WithBlankSymbol_ThrowsValidationException()
    {
        ValidationException exception = ThrowsValidationException(() =>
            Currency.Create("USD", "US Dollar", "   ", 2));

        await Assert.That(exception.Errors.ContainsKey("Symbol")).IsTrue();
    }

    [Test]
    public async Task Create_WithSymbolLongerThan8Characters_ThrowsValidationException()
    {
        ValidationException exception = ThrowsValidationException(() =>
            Currency.Create("USD", "US Dollar", new string('$', 9), 2));

        await Assert.That(exception.Errors.ContainsKey("Symbol")).IsTrue();
    }

    [Arguments(-1)]
    [Arguments(5)]
    [Test]
    public async Task Create_WithMinorUnitOutsideSupportedRange_ThrowsValidationException(int minorUnit)
    {
        ValidationException exception = ThrowsValidationException(() =>
            Currency.Create("USD", "US Dollar", "$", minorUnit));

        await Assert.That(exception.Errors.ContainsKey("MinorUnit")).IsTrue();
    }

    private static ValidationException ThrowsValidationException(Action action)
    {
        try
        {
            action();
        }
        catch (ValidationException exception)
        {
            return exception;
        }

        throw new InvalidOperationException("Expected ValidationException.");
    }
}
