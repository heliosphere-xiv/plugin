namespace Heliosphere.Exceptions;

// internal class ModPathNetworkedException(string path, Windows.Storage.StorageProvider provider) : Exception {
internal class ModPathNetworkedException(string path, string provider) : Exception {
    public override string Message => $"Path at {path} is networked using {provider}. Refusing to install.";
}
