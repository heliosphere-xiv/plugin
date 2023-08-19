using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dalamud.Game;
using Dalamud.Game.ClientState;
using Dalamud.Game.Command;
using Dalamud.Game.Gui;
using Dalamud.Interface.Internal.Notifications;
using Dalamud.IoC;
using Dalamud.Logging;
using Dalamud.Plugin;
using Heliosphere.Exceptions;
using Heliosphere.Model.Generated;
using Heliosphere.Ui;
using Heliosphere.Util;
using ImGuiScene;
using Microsoft.Extensions.DependencyInjection;
using Sentry;
using Sentry.Extensibility;
using StrawberryShake.Serialization;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace Heliosphere;

public class Plugin : IDalamudPlugin {
    public string Name => "Heliosphere";

    internal static HttpClient Client { get; } = new();
    internal static Plugin Instance { get; private set; }
    internal static IHeliosphereClient GraphQl { get; private set; }
    internal static SemaphoreSlim DownloadSemaphore { get; } = new(Environment.ProcessorCount, Environment.ProcessorCount);
    internal static SemaphoreSlim ImageLoadSemaphore { get; } = new(1, 1);

    internal static GameFont GameFont { get; private set; }
    internal static DalamudPluginInterface PluginInterface { get; private set; }

    [PluginService]
    internal DalamudPluginInterface Interface { get; init; }

    [PluginService]
    internal ChatGui ChatGui { get; init; }

    [PluginService]
    internal ClientState ClientState { get; init; }

    [PluginService]
    internal CommandManager CommandManager { get; init; }

    [PluginService]
    internal Framework Framework { get; init; }

    internal Configuration Config { get; }
    internal DownloadCodes DownloadCodes { get; }
    internal PenumbraIpc Penumbra { get; }
    internal PackageState State { get; }
    internal Guard<List<DownloadTask>> Downloads { get; } = new(new List<DownloadTask>());
    internal PluginUi PluginUi { get; }
    internal Server Server { get; }
    internal LinkPayloads LinkPayloads { get; }
    private CommandHandler CommandHandler { get; }
    private IDisposable Sentry { get; }

    internal bool IntegrityFailed { get; private set; }
    internal Guard<Dictionary<string, TextureWrap>> CoverImages { get; } = new(new Dictionary<string, TextureWrap>());

    public Plugin() {
        var checkTask = Task.Run(async () => {
            var showWarning = await DependencyHelper.CheckDependencies(this);
            if (showWarning) {
                this.IntegrityFailed = true;

                if (this.PluginUi != null) {
                    this.PluginUi.ShowAvWarning = true;
                }
            }

            return showWarning;
        });

        Instance = this;
        GameFont = new GameFont(this);
        PluginInterface = this.Interface!;

        this.Sentry = SentrySdk.Init(o => {
            o.Dsn = "https://540decab4a5941f1ba826cd50b4b6efd@sentry.heliosphere.app/4";
            o.EnableTracing = true;
            o.TracesSampleRate = 0.15f;

            var version = this.GetType().Assembly.GetName().Version?.ToString(3);
            if (version != null) {
                o.Release = $"plugin@{version}";
            }

            #if DEBUG
            o.Environment = "development";
            #else
            o.Environment = "production";
            #endif

            o.IsGlobalModeEnabled = true;

            // black hole all events from an invalid install
            o.SetBeforeSend(e => this.IntegrityFailed ? null : e);

            o.AddExceptionFilter(new ExceptionFilter());
        });

        var startWithAvWarning = false;
        try {
            DependencyLoader.Load();
        } catch (Exception ex) {
            PluginLog.Error(ex, "Failed to initialise native libraries (probably AV)");
            startWithAvWarning = true;
            this.IntegrityFailed = true;
        }

        var collection = new ServiceCollection();
        collection
            .AddSerializer<FileListSerializer>()
            .AddSerializer<OptionsSerializer>()
            .AddSerializer<InstallerImageListSerializer>()
            .AddSerializer<BatchListSerializer>()
            .AddSerializer<FileSwapsSerializer>()
            .AddSerializer<GraphqlJsonSerializer>()
            .AddHeliosphereClient()
            .ConfigureHttpClient(client => {
                client.BaseAddress = new Uri($"{DownloadTask.ApiBase}/api/graphql");

                var version = this.GetType().Assembly.GetName().Version?.ToString(3) ?? "???";
                client.DefaultRequestHeaders.UserAgent.Clear();
                client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Heliosphere", version));
            });
        var services = collection.BuildServiceProvider();
        GraphQl = services.GetRequiredService<IHeliosphereClient>();

        this.Config = this.Interface!.GetPluginConfig() as Configuration ?? new Configuration();
        if (this.Config.Version == 1) {
            // save the config with their new generated user id
            this.Config.Version = 2;

            this.SaveConfig();
        }

        SentrySdk.ConfigureScope(scope => {
            scope.User = new User {
                Id = this.Config.UserId.ToString("N"),
            };
        });

        SentrySdk.StartSession();

        var codesPath = Path.Join(
            this.Interface.GetPluginConfigDirectory(),
            "download-codes.json"
        );
        this.DownloadCodes = DownloadCodes.Load(codesPath) ?? DownloadCodes.Create(codesPath);
        this.Penumbra = new PenumbraIpc(this);
        this.State = new PackageState(this);
        this.PluginUi = new PluginUi(this);
        this.Server = new Server(this);
        this.LinkPayloads = new LinkPayloads(this);
        this.CommandHandler = new CommandHandler(this);

        if (startWithAvWarning) {
            this.PluginUi.ShowAvWarning = true;
        }

        if (checkTask.Status == TaskStatus.RanToCompletion && !this.PluginUi.ShowAvWarning) {
            this.PluginUi.ShowAvWarning = checkTask.Result;
        }

        Task.Run(async () => await this.State.UpdatePackages());
    }

    public void Dispose() {
        this.CommandHandler.Dispose();
        this.LinkPayloads.Dispose();
        this.Server.Dispose();
        this.PluginUi.Dispose();
        SentrySdk.EndSession();
        this.Sentry.Dispose();
        this.Penumbra.Dispose();
        this.DownloadCodes.Dispose();
        GameFont.Dispose();
        ImageLoadSemaphore.Dispose();
        DownloadSemaphore.Dispose();

        foreach (var wrap in this.CoverImages.Deconstruct().Values) {
            wrap.Dispose();
        }
    }

    internal void SaveConfig() {
        this.Interface.SavePluginConfig(this.Config);
    }

    internal async Task AddDownloadAsync(DownloadTask task, CancellationToken token = default) {
        var wasAdded = false;
        using (var guard = await this.Downloads.WaitAsync(token)) {
            if (guard.Data.All(download => download.Version != task.Version)) {
                guard.Data.Add(task);
                wasAdded = true;
            }
        }

        if (!wasAdded) {
            this.Interface.UiBuilder.AddNotification(
                $"Already downloading that mod!",
                this.Name,
                NotificationType.Error
            );

            return;
        }

        // if we await this, we'll be waiting for the whole download to finish
        #pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
        task.Start();
        #pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
    }

    private class ExceptionFilter : IExceptionFilter {
        private static readonly HashSet<int> IgnoredHResults = new() {
            // ERROR_HANDLE_DISK_FULL
            unchecked((int) 0x80070027),
            // ERROR_DISK_FULL
            unchecked((int) 0x80070070),

            // ERROR_ACCESS_DENIED
            unchecked((int) 0x80070005),

            // E_OUTOFMEMORY
            unchecked((int) 0x8007000e),
            // ERROR_NOT_ENOUGH_MEMORY
            unchecked((int) 0x80070008),
            // ERROR_INSUFFICIENT_VIRTUAL_ADDR_RESOURCES
            unchecked((int) 0x800701d9),

            // SEC_E_UNSUPPORTED_FUNCTION
            // this is for the tls errors on wine
            unchecked((int) 0x80090302),
        };

        #pragma warning disable SYSLIB1045
        private static readonly List<Regex> IgnoredMessages = new() {
            new Regex(@"^No such host is known\.", RegexOptions.Compiled),
        };
        #pragma warning restore SYSLIB1045

        public bool Filter(Exception ex) {
            // make sure exception stacktrace contains our namespace
            if (ex.StackTrace == null || !ex.StackTrace.Contains("Heliosphere.")) {
                return true;
            }

            switch (ex) {
                // ignore the dalamud imgui image loading exceptions
                // they're useless ("Load failed.")
                case InvalidOperationException { Message: "Load failed." } when ex.StackTrace.Contains("Dalamud.Interface.UiBuilder.") && ex.StackTrace.Contains("LoadImage"):
                // ignore cancelled tasks
                case TaskCanceledException:
                case OperationCanceledException:
                // ignore download errors, usually not actionable
                case HttpRequestException:
                // ignore already-existing mod errors
                case ModAlreadyExistsException:
                // ignore packages/versions/variants deleted on server
                case BaseMissingThingException:
                    return true;
            }

            // ignore specific io errors
            if (ex.GetHResults().Any(hResult => IgnoredHResults.Contains(hResult))) {
                return true;
            }

            return ex.AsEnumerable().Any(inner => IgnoredMessages.Any(regex => regex.IsMatch(inner.Message)));
        }
    }
}

[Serializable]
public class FileList {
    public Dictionary<string, List<List<string?>>> Files { get; init; }
}

public class FileListSerializer : ScalarSerializer<JsonElement, FileList> {
    public FileListSerializer(string typeName = "FileList") : base(typeName) {
    }

    public override FileList Parse(JsonElement serializedValue) {
        return new FileList {
            Files = serializedValue.Deserialize<Dictionary<string, List<List<string?>>>>()!,
        };
    }

    protected override JsonElement Format(FileList runtimeValue) {
        return JsonSerializer.SerializeToElement(runtimeValue.Files);
    }
}

public class OptionsSerializer : ScalarSerializer<JsonElement, Dictionary<string, List<string>>> {
    public OptionsSerializer(string typeName = "Options") : base(typeName) {
    }

    public override Dictionary<string, List<string>> Parse(JsonElement serializedValue) {
        return serializedValue.Deserialize<Dictionary<string, List<string>>>()!;
    }

    protected override JsonElement Format(Dictionary<string, List<string>> runtimeValue) {
        return JsonSerializer.SerializeToElement(runtimeValue);
    }
}

[Serializable]
public class InstallerImageList {
    public Dictionary<string, HashSet<string>> Images { get; init; }
}

public class InstallerImageListSerializer : ScalarSerializer<JsonElement, InstallerImageList> {
    public InstallerImageListSerializer(string typeName = "InstallerImageList") : base(typeName) {
    }

    public override InstallerImageList Parse(JsonElement serializedValue) {
        return new InstallerImageList {
            Images = serializedValue.Deserialize<Dictionary<string, HashSet<string>>>()!,
        };
    }

    protected override JsonElement Format(InstallerImageList runtimeValue) {
        return JsonSerializer.SerializeToElement(runtimeValue.Images);
    }
}

[Serializable]
public class BatchList {
    public Dictionary<string, Dictionary<string, BatchedFile>> Files { get; init; }
}

[SuppressMessage("ReSharper", "InconsistentNaming")]
[Serializable]
public class BatchedFile {
    public ulong offset { get; init; }
    public ulong size_compressed { get; init; }
    public ulong size_uncompressed { get; init; }

    public ulong Offset => this.offset;
    public ulong SizeCompressed => this.size_compressed;
    public ulong SizeUncompressed => this.size_uncompressed;
}

public class BatchListSerializer : ScalarSerializer<JsonElement, BatchList> {
    public BatchListSerializer(string typeName = "BatchList") : base(typeName) {
    }

    public override BatchList Parse(JsonElement serializedValue) {
        return new BatchList {
            Files = serializedValue.Deserialize<Dictionary<string, Dictionary<string, BatchedFile>>>()!,
        };
    }

    protected override JsonElement Format(BatchList runtimeValue) {
        return JsonSerializer.SerializeToElement(runtimeValue.Files);
    }
}

[Serializable]
public class FileSwaps {
    public Dictionary<string, string> Swaps { get; init; }
}

public class FileSwapsSerializer : ScalarSerializer<JsonElement, FileSwaps> {
    public FileSwapsSerializer(string typeName = "FileSwaps") : base(typeName) {
    }

    public override FileSwaps Parse(JsonElement serializedValue) {
        return new FileSwaps {
            Swaps = serializedValue.Deserialize<Dictionary<string, string>>()!,
        };
    }

    protected override JsonElement Format(FileSwaps runtimeValue) {
        return JsonSerializer.SerializeToElement(runtimeValue.Swaps);
    }
}

public class GraphqlJsonSerializer : ScalarSerializer<JsonElement, JsonElement> {
    public GraphqlJsonSerializer(string typeName = "JSON") : base(typeName) {
    }

    public override JsonElement Parse(JsonElement serializedValue) {
        return serializedValue;
    }

    protected override JsonElement Format(JsonElement runtimeValue) {
        return runtimeValue;
    }
}
