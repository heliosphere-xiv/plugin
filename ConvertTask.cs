using System.Security;
using System.Text;
using Blake3;
using Dalamud.Interface.ImGuiNotification;
using gfoidl.Base64;
using Heliosphere.Model;
using Heliosphere.Model.Penumbra;
using Heliosphere.Util;
using Newtonsoft.Json;
using StrawberryShake;
using Windows.Win32;

namespace Heliosphere;

internal class ConvertTask {
    private HeliosphereMeta Package { get; }
    private IActiveNotification Notification { get; }

    internal ConvertTask(HeliosphereMeta pkg, IActiveNotification notification) {
        this.Package = pkg;
        this.Notification = notification;
    }

    internal async Task Run() {
        if (!Plugin.Instance.DownloadCodes.TryGetCode(this.Package.Id, out var code)) {
            code = null;
        }

        this.Notification.AddOrUpdate(Plugin.Instance.NotificationManager, (notif, _) => {
            notif.Content = "Downloading package information";
        });

        var resp = await Plugin.GraphQl.ConvertTask.ExecuteAsync(
            this.Package.VersionId,
            this.Package.SelectedOptions,
            code,
            this.Package.FullInstall,
            Model.Generated.DownloadKind.Update
        );
        resp.EnsureNoErrors();

        var neededFiles = resp.Data?.GetVersion?.NeededFiles.Files.Files;
        if (neededFiles == null) {
            throw new Exception("TODO");
        }

        if (!Plugin.Instance.Penumbra.TryGetModDirectory(out var modDirectory)) {
            throw new Exception("TODO");
        }

        var dirName = this.Package.ModDirectoryName();
        var penumbraModPath = Path.Join(modDirectory, dirName);
        var filesPath = Path.Join(penumbraModPath, "files");

        this.Notification.AddOrUpdate(Plugin.Instance.NotificationManager, (notif, _) => {
            notif.Content = "Checking existing files";
            notif.Progress = 0;
        });

        // gather a list of all files in the files directory
        var installedFiles = Directory.EnumerateFileSystemEntries(filesPath, "*", SearchOption.AllDirectories)
            .Where(path => (File.GetAttributes(path) & FileAttributes.Directory) == 0)
            .Select(Path.GetFileName)
            .Where(path => path != null)
            .Cast<string>()
            .ToHashSet();

        // create a mapping of each file and its hash (hash => path)
        var finished = 0;
        var total = installedFiles.Count;
        var hashPaths = new Dictionary<string, string>();
        using var blake3 = new Blake3HashAlgorithm();
        foreach (var path in installedFiles) {
            blake3.Initialize();
            await using var file = FileHelper.OpenSharedReadIfExists(Path.Join(filesPath, path));
            if (file == null) {
                continue;
            }

            var expected = PathHelper.GetBaseName(path);
            if (hashPaths.ContainsKey(expected)) {
                continue;
            }

            var rawHash = await blake3.ComputeHashAsync(file);
            var hash = Base64.Url.Encode(rawHash);
            hashPaths[hash] = path;

            finished += 1;
            this.Notification.AddOrUpdate(Plugin.Instance.NotificationManager, (notif, _) => {
                notif.Progress = (float) finished / total;
            });
        }

        this.Notification.AddOrUpdate(Plugin.Instance.NotificationManager, (notif, _) => {
            notif.Content = "Converting file layout";
            notif.Progress = 0;
        });

        finished = 0;
        total = neededFiles.Count;

        // loop through all needed files and find their corresponding hash,
        // then link each output path to it
        foreach (var (hash, files) in neededFiles) {
            if (!hashPaths.TryGetValue(hash, out var existingPath)) {
                Plugin.Log.Warning($"missing a file for {hash}");
                continue;
            }

            existingPath = Path.Join(filesPath, existingPath);
            var outputPaths = DownloadTask.GetOutputPaths(files);
            foreach (var shortOutputPath in outputPaths) {
                var outputPath = Path.Join(filesPath, shortOutputPath);
                if (PathHelper.MakeRelativeSub(filesPath, Path.GetFullPath(outputPath)) == null) {
                    throw new SecurityException("path from mod was attempting to leave the files directory");
                }

                if (File.Exists(outputPath)) {
                    continue;
                }

                var parent = PathHelper.GetParent(outputPath);
                Directory.CreateDirectory(parent);

                if (!PInvoke.CreateHardLink(@$"\\?\{outputPath}", @$"\\?\{existingPath}")) {
                    throw new IOException($"failed to create hard link: {existingPath} -> {outputPath}");
                }
            }

            finished += 1;
            this.Notification.AddOrUpdate(Plugin.Instance.NotificationManager, (notif, _) => {
                notif.Progress = (float) finished / total;
            });
        }

        this.Notification.AddOrUpdate(Plugin.Instance.NotificationManager, (notif, _) => {
            notif.Content = "Removing old files";
            notif.Progress = 0;
        });

        finished = 0;
        total = installedFiles.Count;

        // delete all previously-existing files
        foreach (var path in installedFiles) {
            FileHelper.Delete(Path.Join(filesPath, path));

            finished += 1;
            this.Notification.AddOrUpdate(Plugin.Instance.NotificationManager, (notif, _) => {
                notif.Progress = (float) total / finished;
            });
        }

        this.Notification.AddOrUpdate(Plugin.Instance.NotificationManager, (notif, _) => {
            notif.Content = "Updating package metadata";
            notif.Progress = 0;
        });

        // map group => option => (game path => archive path)
        var pathsMap = new Dictionary<string, Dictionary<string, Dictionary<string, string>>>();
        foreach (var (_, files) in neededFiles) {
            foreach (var file in files) {
                var group = file[0] ?? DownloadTask.DefaultFolder;
                var option = file[1] ?? DownloadTask.DefaultFolder;
                var gamePath = file[2]!;
                var outputPath = file[3];

                if (!pathsMap.TryGetValue(group, out var groupMap)) {
                    groupMap = [];
                    pathsMap[group] = groupMap;
                }

                if (!groupMap.TryGetValue(option, out var pathsList)) {
                    pathsList = [];
                    groupMap[option] = pathsList;
                }

                var pathOnDisk = outputPath == null
                    ? Path.Join(
                        DownloadTask.MakeFileNameSafe(group),
                        DownloadTask.MakeFileNameSafe(option),
                        DownloadTask.MakePathPartsSafe(gamePath)
                    )
                    : DownloadTask.MakePathPartsSafe(outputPath);

                pathsList[gamePath] = pathOnDisk;
            }
        }

        var defaultModPath = Path.Join(penumbraModPath, "default_mod.json");
        if (FileHelper.OpenSharedReadIfExists(defaultModPath) is { } defaultModFile) {
            var defaultModJson = await new StreamReader(defaultModFile).ReadToEndAsync();
            var defaultMod = JsonConvert.DeserializeObject<DefaultMod>(defaultModJson);
            if (defaultMod != null) {
                if (pathsMap.TryGetValue(DownloadTask.DefaultFolder, out var groupPaths2)) {
                    if (groupPaths2.TryGetValue(DownloadTask.DefaultFolder, out var pathList2)) {
                        UpdatePaths(defaultMod.Files, pathList2);
                    }
                }

                defaultModJson = JsonConvert.SerializeObject(defaultMod, Formatting.Indented);
                await File.WriteAllTextAsync(defaultModPath, defaultModJson);
            }
        }

        // update groups
        var groupPaths = Directory.EnumerateFiles(penumbraModPath)
            .Select(Path.GetFileName)
            .Where(path => path != null)
            .Cast<string>()
            .Where(path => path.StartsWith("group_") && path.EndsWith(".json"));
        foreach (var groupPath in groupPaths) {
            var groupJson = await File.ReadAllTextAsync(Path.Join(penumbraModPath, groupPath));
            ModGroup? group;
            try {
                group = JsonConvert.DeserializeObject<StandardModGroup>(groupJson);
            } catch {
                group = JsonConvert.DeserializeObject<ImcModGroup>(groupJson);
            }

            if (group == null) {
                continue;
            }

            if (group is not StandardModGroup standard) {
                continue;
            }

            if (!pathsMap.TryGetValue(group.Name, out var groupMap)) {
                continue;
            }

            foreach (var option in standard.Options) {
                if (!groupMap.TryGetValue(option.Name, out var pathsList)) {
                    continue;
                }

                UpdatePaths(option.Files, pathsList);
            }

            groupJson = JsonConvert.SerializeObject(group, Formatting.Indented);
            await File.WriteAllTextAsync(groupPath, groupJson);
        }

        void UpdatePaths(Dictionary<string, string> files, Dictionary<string, string> pathsList) {
            var gamePaths = files.Keys.ToList();
            foreach (var gamePath in gamePaths) {
                if (!pathsList.TryGetValue(gamePath, out var archivePath)) {
                    continue;
                }

                files[gamePath] = archivePath;
            }
        }

        // update package
        this.Package.FileStorageMethod = FileStorageMethod.Original;
        var json = JsonConvert.SerializeObject(this.Package, Formatting.Indented);
        var metaPath = Path.Join(penumbraModPath, "heliosphere.json");
        await using var metaFile = FileHelper.Create(metaPath);
        await metaFile.WriteAsync(Encoding.UTF8.GetBytes(json));

        this.Notification.AddOrUpdate(Plugin.Instance.NotificationManager, (notif, _) => {
            notif.Type = NotificationType.Success;
            notif.Content = "Successfully converted file layout";
            notif.InitialDuration = TimeSpan.FromSeconds(3);
        });

        // tell penumbra to reload it
        if (!Plugin.Instance.Penumbra.ReloadMod(penumbraModPath)) {
            Plugin.Log.Warning($"could not reload mod at {penumbraModPath}");
        }
    }
}
