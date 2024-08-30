namespace Heliosphere.Exceptions;

internal class MultipleModDirectoriesException : Exception {
    internal string PackageName { get; }
    internal string VariantName { get; }
    internal string Version { get; }
    internal IReadOnlyList<string> Directories { get; }

    internal MultipleModDirectoriesException(
        string packageName,
        string variantName,
        string version,
        string[] directories
    ) {
        this.PackageName = packageName;
        this.VariantName = variantName;
        this.Version = version;
        this.Directories = directories;
    }
}
