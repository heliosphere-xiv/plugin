namespace Heliosphere.Model.Penumbra;

[Serializable]
internal class DefaultMod {
    public string Name { get; set; } = string.Empty;
    public int Priority { get; set; } = 0;
    public Dictionary<string, string> Files { get; set; } = new();
    public Dictionary<string, string> FileSwaps { get; set; } = new();
    public List<object> Manipulations { get; set; } = new();
}
