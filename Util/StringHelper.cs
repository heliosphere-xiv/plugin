using System.Globalization;

namespace Heliosphere.Util;

internal static class StringHelper {
    internal static bool ContainsIgnoreCase(this string haystack, string needle) {
        return CultureInfo.InvariantCulture.CompareInfo.IndexOf(haystack, needle, CompareOptions.IgnoreCase) >= 0;
    }
}
