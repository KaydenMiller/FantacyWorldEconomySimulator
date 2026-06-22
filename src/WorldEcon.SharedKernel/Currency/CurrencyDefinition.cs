namespace WorldEcon.SharedKernel.Currency;

/// <summary>
/// A denomination within a currency system (e.g. "Gold" / "g" / 100 copper).
/// <paramref name="Units"/> is the value of this denomination in base units (the denomination
/// with Units == 1 is the base).
/// </summary>
public sealed record Denomination(string Name, string Symbol, long Units);

/// <summary>
/// Data-driven currency configuration. Denominations must be ordered ascending by Units,
/// with exactly one denomination having Units == 1 (the base unit).
/// <para>
/// Currency is display-only: <see cref="Money"/> always stores base units (copper by default).
/// Denominations are used only for formatting — they have zero impact on simulation logic.
/// </para>
/// </summary>
public sealed record CurrencyDefinition(IReadOnlyList<Denomination> Denominations)
{
    /// <summary>
    /// Default D&amp;D-style 4-tier currency: copper (1), silver (10), gold (100), platinum (1000).
    /// </summary>
    public static CurrencyDefinition Default { get; } = new(
    [
        new Denomination("Copper",   "c",    1),
        new Denomination("Silver",   "s",   10),
        new Denomination("Gold",     "g",  100),
        new Denomination("Platinum", "p", 1000),
    ]);

    /// <summary>
    /// Formats <paramref name="money"/> using this currency's denominations.
    /// Decomposes highest-denomination-first, omitting zero parts.
    /// Always shows at least the base-unit part when the value is zero.
    /// Negative values are prefixed with "-"; the parts themselves are unsigned.
    /// Examples (Default): 321 → "3g 2s 1c"; 300 → "3g"; 0 → "0c"; -5 → "-5c".
    /// </summary>
    public string Format(Money money)
    {
        bool negative = money.Units < 0;
        long remaining = negative ? -money.Units : money.Units;

        // Work high-to-low through denominations (they are ordered ascending, so reverse).
        var parts = new List<string>();
        for (int i = Denominations.Count - 1; i >= 0; i--)
        {
            var denom = Denominations[i];
            long amount = remaining / denom.Units;
            remaining %= denom.Units;

            if (amount > 0)
                parts.Add($"{amount}{denom.Symbol}");
        }

        // Zero value: show "0<baseSymbol>"
        if (parts.Count == 0)
        {
            var baseSymbol = Denominations[0].Symbol;
            return $"0{baseSymbol}";
        }

        var formatted = string.Join(" ", parts);
        return negative ? $"-{formatted}" : formatted;
    }
}
