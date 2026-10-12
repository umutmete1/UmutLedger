using UmutLedger.Domain.Enums;

namespace UmutLedger.Infrastructure.Persistence.Configurations;

internal static class CurrencyCheck
{
    public static string Sql(string column)
    {
        var supportedCurrencies = string.Join(", ", Enum.GetNames<Currency>().Select(name => $"'{name}'"));
        return $"{column} IN ({supportedCurrencies})";
    }
}
