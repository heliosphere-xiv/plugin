namespace Heliosphere.Util;

internal static class ICollectionExt {
    internal static void AddRange<T>(this ICollection<T> self, IEnumerable<T> other) {
        foreach (var toAdd in other) {
            self.Add(toAdd);
        }
    }
}
