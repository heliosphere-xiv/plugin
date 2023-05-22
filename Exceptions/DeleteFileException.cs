namespace Heliosphere.Exceptions;

internal class DeleteFileException : Exception {
    private string Path { get; }
    public override string Message => $"Could not delete file at path {this.Path}";

    internal DeleteFileException(string path) {
        this.Path = path;
    }
}
