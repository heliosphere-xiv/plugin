using Dalamud.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace Heliosphere.Util;

internal static class DependencyHelper {
    private static string InternalName => typeof(Plugin).Assembly.GetName().Name ?? "heliosphere-plugin";

    private static IEnumerable<(string, string)> NeededDependencies(DependencyInfo info) {
        var assemblyName = InternalName;

        var dict = info.Targets.First().Value;
        foreach (var (key, target) in dict) {
            if (key.StartsWith($"{assemblyName}/") || target.Runtime == null || target.Runtime.Count == 0) {
                continue;
            }

            foreach (var path in target.Runtime.Keys) {
                yield return (key, Path.GetFileName(path));
            }
        }
    }

    internal static async Task<bool> CheckDependencies(Plugin plugin) {
        var dllPath = Path.GetDirectoryName(plugin.GetType().Assembly.Location)!;
        var infoFilePath = Path.Join(dllPath, $"{InternalName}.deps.json");
        var infoFileJson = await File.ReadAllTextAsync(infoFilePath);
        var info = JsonConvert.DeserializeObject<DependencyInfo>(infoFileJson)!;

        var dlls = Directory.EnumerateFiles(dllPath)
            .Select(Path.GetFileName)
            .ToList();

        var showWarning = false;
        foreach (var (name, dep) in NeededDependencies(info)) {
            if (dlls.Contains(dep)) {
                continue;
            }

            PluginLog.Warning($"Missing dependency {name} with file name {dep}");
            showWarning = true;
        }

        return showWarning;
    }
}

[Serializable]
[JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
internal class DependencyInfo {
    public Dictionary<string, Dictionary<string, Target>> Targets { get; set; }
    public Dictionary<string, Library> Libraries { get; set; }
}

[Serializable]
[JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
internal class Target {
    public Dictionary<string, TargetRuntime>? Runtime { get; set; }
    public Dictionary<string, string>? Dependencies { get; set; }
}

[Serializable]
[JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
internal class TargetRuntime {
    public string AssemblyVersion { get; set; }
    public string FileVersion { get; set; }
}

[Serializable]
[JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
internal class Library {
    public string Type { get; set; }
    public bool Serviceable { get; set; }
    public string Sha512 { get; set; }
}
