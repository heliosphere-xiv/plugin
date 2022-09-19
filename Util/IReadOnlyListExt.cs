namespace Heliosphere.Util;

internal static class ReadOnlyListExt {
    internal static int FindIndex<T>(this IReadOnlyList<T> list, Predicate<T> predicate) {
        for (var i = 0; i < list.Count; i++) {
            if (predicate(list[i])) {
                return i;
            }
        }

        return -1;
    }
}
