using System;

namespace Budgetoid;

public static class DateTimeExtensions
{
    public static DateOnly ToDateOnly(this DateTime date) => new(date.Year, date.Month, date.Day);
    public static DateTime ToDateTime(this DateOnly date) => new(date.Year, date.Month, date.Day);
}
