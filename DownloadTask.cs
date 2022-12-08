using System.Text;
using Dalamud.Interface.Internal.Notifications;
using Dalamud.Logging;
using gfoidl.Base64;
using Heliosphere.Model;
using Heliosphere.Model.Generated;
using Heliosphere.Model.Penumbra;
using Heliosphere.Util;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using SHA3.Net;
using StrawberryShake;
using ZstdSharp;

namespace Heliosphere;

internal class DownloadTask : IDisposable {
    #if DEBUG
    internal const string ApiBase = "http://192.168.174.222:42011";
    #else
    internal const string ApiBase = "https://heliosphere.app/api";
    #endif

    private Plugin Plugin { get; }
    private string ModDirectory { get; }
    private int Version { get; }
    private Dictionary<string, List<string>> Options { get; }
    private bool Full { get; }
    private bool IncludeTags { get; }
    private string? PenumbraModPath { get; set; }
    internal string? PackageName { get; private set; }

    internal CancellationTokenSource CancellationToken { get; } = new();
    internal State State { get; private set; } = State.NotStarted;
    internal uint StateData { get; private set; }
    internal uint StateDataMax { get; private set; }
    internal Exception? Error { get; private set; }

    private bool _disposed;
    private string? _oldModName;

    internal DownloadTask(Plugin plugin, string modDirectory, int version, bool includeTags) {
        this.Plugin = plugin;
        this.ModDirectory = modDirectory;
        this.Version = version;
        this.Options = new Dictionary<string, List<string>>();
        this.Full = true;
        this.IncludeTags = includeTags;
    }

    internal DownloadTask(Plugin plugin, string modDirectory, int version, Dictionary<string, List<string>> options, bool includeTags) {
        this.Plugin = plugin;
        this.ModDirectory = modDirectory;
        this.Version = version;
        this.Options = options;
        this.IncludeTags = includeTags;
    }

    ~DownloadTask() {
        this.Dispose();
    }

    public void Dispose() {
        if (this._disposed) {
            return;
        }

        this._disposed = true;
        GC.SuppressFinalize(this);
        this.CancellationToken.Dispose();
    }

    internal Task Start() {
        return Task.Run(this.Run, this.CancellationToken.Token);
    }

    private async Task Run() {
        try {
            var info = await this.GetPackageInfo();
            if (this.Full) {
                foreach (var group in info.Groups) {
                    this.Options[group.Name] = new List<string>();

                    foreach (var option in group.Options) {
                        this.Options[group.Name].Add(option.Name);
                    }
                }
            }

            this.PackageName = info.Package.Name;
            await this.DownloadFiles(info);
            await this.ConstructModPack(info);
            this.AddMod();
            this.State = State.Finished;
            this.Plugin.Interface.UiBuilder.AddNotification(
                $"{this.PackageName} installed in Penumbra.",
                this.Plugin.Name,
                NotificationType.Success
            );

            // refresh the manager package list after install finishes
            await this.Plugin.State.UpdatePackages();
        } catch (Exception ex) {
            this.State = State.Errored;
            this.Error = ex;
            PluginLog.LogError(ex, $"Error downloading version {this.Version}");
            this.Plugin.Interface.UiBuilder.AddNotification(
                $"Failed to install {this.PackageName ?? "mod"}.",
                this.Plugin.Name,
                NotificationType.Error
            );
        }
    }

    private void SetStateData(uint current, uint max) {
        this.StateData = current;
        this.StateDataMax = max;
    }

    internal static async Task<HttpResponseMessage> GetImage(Guid id, int imageId, CancellationToken token = default) {
        var resp = await Plugin.Client.GetAsync($"{ApiBase}/web/package/{id:N}/image/{imageId}", HttpCompletionOption.ResponseHeadersRead, token);
        resp.EnsureSuccessStatusCode();
        return resp;
    }

    private async Task<IDownloadTask_GetVersion> GetPackageInfo() {
        this.State = State.DownloadingPackageInfo;
        this.SetStateData(0, 1);

        var resp = await Plugin.GraphQl.DownloadTask.ExecuteAsync(this.Version, this.Options, this.Full);
        resp.EnsureNoErrors();
        this.StateData += 1;
        return resp.Data!.GetVersion!;
    }

    private async Task DownloadFiles(IDownloadTask_GetVersion info) {
        this.State = State.DownloadingFiles;
        this.SetStateData(0, (uint) info.NeededFiles.Files.Files.Count);

        var directories = Directory.EnumerateDirectories(this.ModDirectory)
            .Select(Path.GetFileName)
            .Where(path => !string.IsNullOrEmpty(path))
            .Where(path => path!.StartsWith("hs-") && path.EndsWith($"-{info.Package.Id:N}"))
            .ToArray();

        var invalidChars = Path.GetInvalidFileNameChars();
        var slug = info.Package.Name.Select(c => invalidChars.Contains(c) ? '-' : c)
            .Aggregate(new StringBuilder(), (sb, c) => sb.Append(c))
            .ToString();
        this.PenumbraModPath = Path.Join(this.ModDirectory, $"hs-{slug}-{info.Version}-{info.Package.Id:N}");
        if (directories.Length == 1) {
            var oldName = Path.Join(this.ModDirectory, directories[0]!);
            if (oldName != this.PenumbraModPath) {
                this._oldModName = directories[0];
                Directory.Move(oldName, this.PenumbraModPath);
            }
        } else if (directories.Length > 1) {
            PluginLog.Warning($"multiple heliosphere mod directories found for {info.Package.Name} - not attempting a rename");
        }

        var filesPath = Path.Join(this.PenumbraModPath, "files");
        Directory.CreateDirectory(filesPath);

        // remove any old files no longer being used
        var neededHashes = info.NeededFiles.Files.Files.Keys.ToHashSet();
        var presentHashes = Directory.EnumerateFiles(filesPath)
            .Select(Path.GetFileName)
            .Where(path => !string.IsNullOrEmpty(path))
            .Cast<string>()
            .GroupBy(path => Path.ChangeExtension(path, null))
            .ToDictionary(group => group.Key, group => group.ToHashSet());
        var present = presentHashes.Keys.ToHashSet();
        present.ExceptWith(neededHashes);
        foreach (var extra in present) {
            foreach (var file in presentHashes[extra]) {
                var extraPath = Path.Join(filesPath, file);
                PluginLog.Log($"removing extra file {extraPath}");
                File.Delete(extraPath);
            }
        }

        using var semaphore = new SemaphoreSlim(Environment.ProcessorCount, Environment.ProcessorCount);
        var tasks = info.NeededFiles.Files.Files
            .Select(pair => Task.Run(async () => {
                // semaphore isn't disposed until after Task.WhenAll, so this is
                // fine

                var (hash, files) = pair;
                var extensions = files
                    .Select(file => Path.GetExtension(file[2]!))
                    .ToHashSet();

                if (extensions.Count == 0) {
                    // how does this happen?
                    PluginLog.LogWarning($"{hash} has no extension");
                    extensions.Add("unk");
                }

                // ReSharper disable once AccessToDisposedClosure
                await semaphore.WaitAsync();
                try {
                    await this.DownloadFile(new Uri(info.NeededFiles.BaseUri), filesPath, extensions.ToArray(), hash);
                } finally {
                    // ReSharper disable once AccessToDisposedClosure
                    semaphore.Release();
                }
            }));
        await Task.WhenAll(tasks);
    }

    private async Task DownloadFile(Uri baseUri, string filesPath, string[] extensions, string hash) {
        var path = Path.ChangeExtension(Path.Join(filesPath, hash), extensions[0]);

        if (File.Exists(path)) {
            // make sure checksum matches
            using var sha3 = Sha3.Sha3256();
            sha3.Initialize();
            await using var file = File.OpenRead(path);
            var computed = await sha3.ComputeHashAsync(file, this.CancellationToken.Token);
            if (Base64.Url.Encode(computed) == hash) {
                this.StateData += 1;
                return;
            }
        }

        for (var i = 0; i < 3; i++) {
            try {
                var uri = new Uri(baseUri, hash).ToString();
                var resp = await Plugin.Client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, this.CancellationToken.Token);
                resp.EnsureSuccessStatusCode();

                await using var file = File.Create(path);
                var stream = await resp.Content.ReadAsStreamAsync(this.CancellationToken.Token);
                await new DecompressionStream(stream).CopyToAsync(file, this.CancellationToken.Token);
                break;
            } catch (Exception ex) {
                PluginLog.LogError(ex, $"Error downloading {baseUri}/{hash}");

                if (i == 2) {
                    // failed three times, so rethrow
                    throw;
                }
            }
        }

        foreach (var ext in extensions[1..]) {
            File.Copy(path, Path.ChangeExtension(path, ext));
        }

        this.StateData += 1;
    }

    private async Task ConstructModPack(IDownloadTask_GetVersion info) {
        this.State = State.ConstructingModPack;
        this.SetStateData(0, 4);
        await this.ConstructMeta(info);
        await this.ConstructHeliosphereMeta(info);
        await this.ConstructDefaultMod(info);
        await this.ConstructGroups(info);
    }

    private async Task ConstructMeta(IDownloadTask_GetVersion info) {
        var meta = new ModMeta {
            Name = $"{this.Plugin.Config.TitlePrefix}{info.Package.Name}",
            Author = info.Package.User.Username,
            Description = info.Package.Description,
            Version = info.Version,
            Website = $"https://heliosphere.app/mod/{info.Package.Id.ToCrockford()}",
            ModTags = this.IncludeTags
                ? info.Package.Tags.Select(tag => tag.Slug).ToArray()
                : Array.Empty<string>(),
        };
        var json = JsonConvert.SerializeObject(meta, Formatting.Indented);

        var path = Path.Join(this.PenumbraModPath, "meta.json");
        await using var file = File.Create(path);
        await file.WriteAsync(Encoding.UTF8.GetBytes(json), this.CancellationToken.Token);
        this.State += 1;
    }

    private async Task ConstructHeliosphereMeta(IDownloadTask_GetVersion info) {
        var selectedAll = true;
        foreach (var group in info.Groups) {
            if (!this.Options.TryGetValue(group.Name, out var selected)) {
                selectedAll = false;
                break;
            }

            if (group.Options.All(option => selected.Contains(option.Name))) {
                continue;
            }

            selectedAll = false;
            break;
        }

        var meta = new HeliosphereMeta {
            Id = info.Package.Id,
            Name = info.Package.Name,
            Tagline = info.Package.Tagline,
            Description = info.Package.Description,
            Author = info.Package.User.Username,
            AuthorUuid = info.Package.User.Id,
            Version = info.Version,
            VersionId = this.Version,
            FullInstall = selectedAll,
            IncludeTags = this.IncludeTags,
            SelectedOptions = this.Options,
        };

        var metaJson = JsonConvert.SerializeObject(meta, Formatting.Indented);
        var path = Path.Join(this.PenumbraModPath, "heliosphere.json");
        await using var file = File.Create(path);
        await file.WriteAsync(Encoding.UTF8.GetBytes(metaJson), this.CancellationToken.Token);

        // save cover image
        if (info.Package.Images.Count > 0) {
            var coverImage = info.Package.Images[0];
            var coverPath = Path.Join(this.PenumbraModPath, "cover.jpg");

            try {
                var image = await GetImage(info.Package.Id, coverImage.Id, this.CancellationToken.Token);
                await using var cover = File.Create(coverPath);
                await image.Content.CopyToAsync(cover, this.CancellationToken.Token);
            } catch (Exception ex) {
                PluginLog.LogError(ex, "Could not download cover image");
            }
        }

        this.State += 1;
    }

    private async Task ConstructDefaultMod(IDownloadTask_GetVersion info) {
        var defaultMod = new DefaultMod {
            Manipulations = info.NeededFiles.Manipulations
                .FirstOrDefault(group => group.Name == null)
                ?.Options
                .FirstOrDefault(opt => opt.Name == null)
                ?.Manipulations
                .Select(manip => JToken.Parse(manip.GetRawText()))
                .ToList() ?? new List<JToken>(),
        };
        foreach (var (hash, files) in info.NeededFiles.Files.Files) {
            foreach (var file in files) {
                if (file[0] != null || file[1] != null) {
                    continue;
                }

                var replacedPath = Path.Join("files", hash);
                replacedPath = Path.ChangeExtension(replacedPath, Path.GetExtension(file[2]!));
                defaultMod.Files[file[2]!] = replacedPath;
            }
        }

        var json = JsonConvert.SerializeObject(defaultMod, Formatting.Indented);

        var path = Path.Join(this.PenumbraModPath, "default_mod.json");
        await using var output = File.Create(path);
        await output.WriteAsync(Encoding.UTF8.GetBytes(json), this.CancellationToken.Token);
        this.StateData += 1;
    }

    private async Task ConstructGroups(IDownloadTask_GetVersion info) {
        var modGroups = new Dictionary<string, ModGroup>(info.Groups.Count);
        foreach (var group in info.Groups) {
            var modGroup = new ModGroup(group.Name, "", group.SelectionType.ToString());
            var groupManips = info.NeededFiles.Manipulations.FirstOrDefault(manips => manips.Name == group.Name);

            foreach (var option in group.Options) {
                var manipulations = groupManips?.Options
                    .FirstOrDefault(opt => opt.Name == option.Name)
                    ?.Manipulations
                    .Select(manip => JToken.Parse(manip.GetRawText()))
                    .ToList();

                modGroup.Options.Add(new DefaultMod {
                    Name = option.Name,
                    Manipulations = manipulations?.ToList() ?? new List<JToken>(),
                });
            }

            modGroups[group.Name] = modGroup;
        }

        foreach (var (hash, files) in info.NeededFiles.Files.Files) {
            foreach (var file in files) {
                if (file[0] == null || file[1] == null) {
                    continue;
                }

                var groupName = file[0]!;
                var optionName = file[1]!;
                var gamePath = file[2]!;

                var modGroup = modGroups[groupName];

                var option = modGroup.Options.FirstOrDefault(opt => opt.Name == optionName);
                // this shouldn't be possible?
                if (option == null) {
                    var opt = new DefaultMod {
                        Name = optionName,
                    };

                    modGroup.Options.Add(opt);
                    option = opt;
                }

                var replacedPath = Path.Join("files", hash);
                replacedPath = Path.ChangeExtension(replacedPath, Path.GetExtension(file[2]!));
                option.Files[gamePath] = replacedPath;
            }
        }

        foreach (var group in modGroups.Values) {
            if (this.Options.TryGetValue(group.Name, out var selected)) {
                group.Options.RemoveAll(opt => !selected.Contains(opt.Name));
            } else {
                group.Options.Clear();
            }
        }

        var invalidChars = Path.GetInvalidFileNameChars();
        var list = modGroups.Values
            .OrderBy(group => info.Groups.FindIndex(g => g.Name == group.Name))
            .ToList();
        for (var i = 0; i < list.Count; i++) {
            var slug = list[i].Name.ToLowerInvariant()
                .Select(c => invalidChars.Contains(c) ? '-' : c)
                .Aggregate(new StringBuilder(), (sb, c) => sb.Append(c))
                .ToString();
            var json = JsonConvert.SerializeObject(list[i], Formatting.Indented);
            var path = Path.Join(this.PenumbraModPath, $"group_{i + 1:000}_{slug}.json");
            await using var file = File.Create(path);
            await file.WriteAsync(Encoding.UTF8.GetBytes(json), this.CancellationToken.Token);
            this.StateData += 1;
        }
    }

    private void AddMod() {
        this.State = State.AddingMod;
        this.SetStateData(0, 1);

        this.Plugin.Framework.RunOnFrameworkThread(() => {
            if (this._oldModName != null) {
                this.Plugin.Penumbra.DeleteMod(this._oldModName);
            }

            var modPath = Path.GetFileName(this.PenumbraModPath!);
            if (this.Plugin.Penumbra.AddMod(modPath)) {
                // reload just in case
                this.Plugin.Penumbra.ReloadMod(modPath);

                // put mod in folder
                if (!string.IsNullOrWhiteSpace(this.Plugin.Config.PenumbraFolder)) {
                    var modName = $"{this.Plugin.Config.TitlePrefix}{this.PackageName}";
                    this.Plugin.Penumbra.SetModPath(modPath, $"{this.Plugin.Config.PenumbraFolder}/{modName}");
                }

                this.StateData += 1;
            } else {
                throw new Exception("could not add mod to Penumbra");
            }
        });
    }

    [Serializable]
    [JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
    private struct DownloadOptions {
        public Dictionary<string, List<string>> Options;

        internal DownloadOptions(Dictionary<string, List<string>> options) {
            this.Options = options;
        }
    }

    [Serializable]
    [JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
    private struct FullDownloadOptions {
        public bool Full;
    }
}

internal enum State {
    NotStarted,
    DownloadingPackageInfo,
    DownloadingFiles,
    ConstructingModPack,
    AddingMod,
    Finished,
    Errored,
}

internal static class StateExt {
    internal static string Name(this State state) {
        return state switch {
            State.NotStarted => "Not started",
            State.DownloadingPackageInfo => "Downloading package info",
            State.DownloadingFiles => "Downloading files",
            State.ConstructingModPack => "Constructing mod pack",
            State.AddingMod => "Adding mod",
            State.Finished => "Finished",
            State.Errored => "Errored",
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
        };
    }
}
