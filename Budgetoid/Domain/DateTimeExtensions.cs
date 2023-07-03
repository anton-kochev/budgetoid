namespace Domain;

public static class DateTimeExtensions
{
    public static DateOnly ToDateOnly(this DateTime date)
    {
        return new DateOnly(date.Year, date.Month, date.Day);
    }

    public static DateTime ToDateTime(this DateOnly date)
    {
        return new DateTime(date.Year, date.Month, date.Day);
    }
}
