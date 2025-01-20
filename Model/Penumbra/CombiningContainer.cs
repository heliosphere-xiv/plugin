using Newtonsoft.Json.Linq;

namespace Heliosphere.Model.Penumbra;

[Serializable]
internal class CombiningContainer : IContainer {
    public string? Name { get; set; }
    public Dictionary<string, string> Files { get; set; } = new();
    public Dictionary<string, string> FileSwaps { get; set; } = new();
    public List<JToken> Manipulations { get; set; } = [];

    public void AddFile(string gamePath, string redirectPath) {
        this.Files[gamePath] = redirectPath;
    }
}
