using System.Text;

namespace Heliosphere.Util;

internal static class EnumHelper {
    internal static string? PrettyName<T>(T value, bool capitaliseAllWords = false)
        where T : struct, Enum {
        var pretty = new StringBuilder();
        var ugly = Enum.GetName(value);
        if (ugly == null) {
            return null;
        }

        foreach (var ch in ugly.ToCharArray()) {
            if (pretty.Length == 0) {
                pretty.Append(ch);
                continue;
            }

            if (char.IsAsciiLetterUpper(ch)) {
                pretty.Append(' ');
                pretty.Append(capitaliseAllWords ? ch : char.ToLowerInvariant(ch));
            } else {
                pretty.Append(ch);
            }
        }

        return pretty.ToString();
    }
}
