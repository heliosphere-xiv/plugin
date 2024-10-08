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

    internal static async Task<bool> CheckDependencies(Plugin plugin, CancellationToken token = default) {
        var dllPath = plugin.Interface.AssemblyLocation.Directory?.FullName;
        if (dllPath == null) {
            Plugin.Log.Warning("no parent directory for assembly");
            return false;
        }

        var infoFilePath = Path.Join(dllPath, $"{InternalName}.deps.json");
        var infoFileJson = await FileHelper.ReadAllTextAsync(infoFilePath, token);
        var info = JsonConvert.DeserializeObject<DependencyInfo>(infoFileJson)!;

        var dlls = Directory.EnumerateFiles(dllPath)
            .Select(Path.GetFileName)
            .ToList();

        var showWarning = false;
        foreach (var (name, dep) in NeededDependencies(info)) {
            if (dlls.Contains(dep)) {
                continue;
            }

            Plugin.Log.Warning($"Missing dependency {name} with file name {dep}");
            showWarning = true;
        }

        return showWarning;
    }
}

#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.

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
