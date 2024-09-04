namespace Heliosphere.Util;

internal static class UintHelper {
    internal static uint OverflowSubtractValue(uint amount) {
        return amount == 0
            ? 0
            : uint.MaxValue - (amount - 1);
    }
}
