namespace WorldEcon.Tui.Resources;

/// <summary>
/// Builds a human-readable display name for a representative merchant. Merchants have no stored name
/// of their own, so we derive one from their seat settlement plus a stable ordinal: the first merchant
/// seated at a settlement is "&lt;Seat&gt; Caravaneer", the second "&lt;Seat&gt; Caravaneer II", and so on.
/// <para>
/// This is presentation-only and deterministic (callers assign the ordinal by ordering a seat's
/// merchants by id). Race/culture-keyed personal-name pools are a future enhancement; until then this
/// keeps merchant names distinct from the bare city name.
/// </para>
/// </summary>
internal static class MerchantNaming
{
    private const string RoleNoun = "Caravaneer";

    /// <summary>
    /// <paramref name="ordinal"/> is the 0-based position of this merchant among its seat siblings
    /// (ordered by id). 0 → no numeral; 1 → "II"; 2 → "III"; …
    /// </summary>
    public static string DisplayName(string seatName, int ordinal)
        => ordinal <= 0
            ? $"{seatName} {RoleNoun}"
            : $"{seatName} {RoleNoun} {ToRoman(ordinal + 1)}";

    private static string ToRoman(int n)
    {
        if (n <= 0) return n.ToString();   // defensive: roman numerals have no zero/negative form
        var sb = new System.Text.StringBuilder();
        foreach (var (value, symbol) in RomanTable)
            while (n >= value)
            {
                sb.Append(symbol);
                n -= value;
            }
        return sb.ToString();
    }

    private static readonly (int Value, string Symbol)[] RomanTable =
    [
        (1000, "M"), (900, "CM"), (500, "D"), (400, "CD"),
        (100, "C"), (90, "XC"), (50, "L"), (40, "XL"),
        (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I"),
    ];
}
