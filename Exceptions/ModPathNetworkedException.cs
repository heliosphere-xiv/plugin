namespace Heliosphere.Exceptions;

internal class ModPathNetworkedException(string path, Windows.Storage.StorageProvider provider) : Exception {
    public override string Message => $"Path at {path} is networked using {provider.DisplayName}. Refusing to install.";
}
