using System;

namespace JunimoServer.Util;

/// <summary>
/// Strict enum parsing for operator-supplied setting values: member names only,
/// case-insensitive, surrounding whitespace ignored. Bare <c>Enum.TryParse</c> silently
/// accepts any numeric string ("1" maps to a member by ordinal, "7" becomes a live
/// undefined value) and comma lists that OR into a defined value ("CabinStack, None") —
/// config traps every caller's error text denies. The name round-trip rejects all of
/// those; <c>IsDefined</c> additionally guards out-of-range numerics, whose
/// <c>ToString()</c> is the numeric string itself and would pass the round-trip.
/// </summary>
public static class StrictEnumParser
{
    public static bool TryParse<TEnum>(string value, out TEnum result)
        where TEnum : struct, Enum
    {
        return Enum.TryParse(value, ignoreCase: true, out result)
            && Enum.IsDefined(result)
            && result.ToString().Equals(value.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
