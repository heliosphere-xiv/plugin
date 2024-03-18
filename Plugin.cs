using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Interface.Internal;
using Dalamud.Interface.Internal.Notifications;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Heliosphere.Exceptions;
using Heliosphere.Model.Generated;
using Heliosphere.Ui;
using Heliosphere.Ui.Dialogs;
using Heliosphere.Util;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Polly;
using Sentry.Extensibility;
using StrawberryShake.Serialization;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace Heliosphere;

#pragma warning disable EXTEXP0001

public class Plugin : IDalamudPlugin {
    internal static string Name = "Heliosphere";
    internal static string InternalName = "heliosphere-plugin";
    internal static string Version => typeof(Plugin).Assembly.GetName().Version?.ToString(3) ?? "???";
    private static readonly ProductInfoHeaderValue UserAgent = new(InternalName, Version);

    internal static HttpClient Client { get; }

    internal static Plugin Instance { get; private set; }
    internal static IHeliosphereClient GraphQl { get; private set; }
    internal static SemaphoreSlim DownloadSemaphore { get; } = new(Environment.ProcessorCount, Environment.ProcessorCount);
    internal static SemaphoreSlim ImageLoadSemaphore { get; } = new(1, 1);

    internal static GameFont GameFont { get; private set; }
    internal static DalamudPluginInterface PluginInterface { get; private set; }

    [PluginService]
    internal static IPluginLog Log { get; private set; }

    [PluginService]
    internal DalamudPluginInterface Interface { get; init; }

    [PluginService]
    internal IChatGui ChatGui { get; init; }

    [PluginService]
    internal IClientState ClientState { get; init; }

    [PluginService]
    internal ICommandManager CommandManager { get; init; }

    [PluginService]
    internal ICondition Condition { get; init; }

    [PluginService]
    internal IDutyState DutyState { get; init; }

    [PluginService]
    internal IFramework Framework { get; init; }

    [PluginService]
    internal IPartyList PartyList { get; init; }

    internal Configuration Config { get; }
    internal DownloadCodes DownloadCodes { get; }
    internal PenumbraIpc Penumbra { get; }
    internal PackageState State { get; }
    internal Guard<List<DownloadTask>> Downloads { get; } = new([]);
    internal PluginUi PluginUi { get; }
    internal Server Server { get; }
    internal LinkPayloads LinkPayloads { get; }
    private CommandHandler CommandHandler { get; }
    private IDisposable Sentry { get; }
    private Stopwatch LimitTimer { get; } = Stopwatch.StartNew();

    internal bool IntegrityFailed { get; private set; }
    internal Guard<Dictionary<string, IDalamudTextureWrap>> CoverImages { get; } = new(new Dictionary<string, IDalamudTextureWrap>());
    internal bool TracingEnabled { get; set; }

    static Plugin() {
        var retryPipeline = new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRetry(new HttpRetryStrategyOptions {
                BackoffType = DelayBackoffType.Exponential,
                MaxRetryAttempts = 3,
            })
            .Build();

        var socketHandler = new SocketsHttpHandler {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        var resilienceHandler = new ResilienceHandler(retryPipeline) {
            InnerHandler = socketHandler,
        };

        Client = new HttpClient(resilienceHandler) {
            DefaultRequestHeaders = {
                UserAgent = { UserAgent },
            },
            Timeout = TimeSpan.FromSeconds(15),
        };
    }

    public Plugin() {
        var checkTask = Task.Run(async () => {
            var showWarning = await DependencyHelper.CheckDependencies(this);
            if (showWarning) {
                this.IntegrityFailed = true;
                this.PluginUi?.OpenAntiVirusWarning();
            }

            return showWarning;
        });

        Instance = this;
        GameFont = new GameFont(this);
        PluginInterface = this.Interface!;

        this.Sentry = SentrySdk.Init(o => {
            o.Dsn = "https://f0b33e3640b17f36b2a22099a1249efe@sentry.heliosphere.app/4";
            o.TracesSampler = _ => this.TracingEnabled
                ? 1.0
                : 0.0;

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

            // include a user-agent header
            o.ConfigureClient = client => {
                client.DefaultRequestHeaders.UserAgent.Add(UserAgent);
            };
        });

        var startWithAvWarning = false;
        try {
            DependencyLoader.Load();
        } catch (Exception ex) {
            Log.Error(ex, "Failed to initialise native libraries (probably AV)");
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
                client.BaseAddress = new Uri($"{DownloadTask.ApiBase}/graphql");

                client.DefaultRequestHeaders.UserAgent.Clear();
                client.DefaultRequestHeaders.UserAgent.Add(UserAgent);
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
            scope.User = new SentryUser {
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

        this.Framework!.Update += this.CalculateSpeedLimit;

        if (startWithAvWarning || checkTask is { Status: TaskStatus.RanToCompletion, Result: true }) {
            this.PluginUi.OpenAntiVirusWarning();
        }

        var assemblyName = typeof(Plugin).Assembly.GetName();
        if (this.Interface.InstalledPlugins.FirstOrDefault(plugin => plugin.InternalName == assemblyName.Name) is { } installed) {
            var actual = assemblyName.Version;
            if (actual != null && installed.Version != actual) {
                this.PluginUi.AddIfNotPresent(new VersionMismatchDialog(installed.Version, actual));
            }
        }

        Task.Run(async () => await this.State.UpdatePackages());
    }

    public void Dispose() {
        this.Framework.Update -= this.CalculateSpeedLimit;
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
        GloballyThrottledStream.Shutdown();

        foreach (var wrap in this.CoverImages.Deconstruct().Values) {
            wrap.Dispose();
        }
    }

    internal void SaveConfig() {
        this.Interface.SavePluginConfig(this.Config);
    }

    private void CalculateSpeedLimit(IFramework framework) {
        // don't need to check this every frame, let's be real
        if (this.LimitTimer.Elapsed < TimeSpan.FromSeconds(1)) {
            return;
        }

        this.LimitTimer.Restart();

        var limit = this.CalculateLimit();
        GloballyThrottledStream.MaxBytesPerSecond = limit switch {
            Configuration.SpeedLimit.On => this.Config.MaxKibsPerSecond * 1_024,
            Configuration.SpeedLimit.Alternate => this.Config.AltMaxKibsPerSecond * 1_024,
            Configuration.SpeedLimit.Default => this.Config.LimitNormal switch {
                Configuration.SpeedLimit.On => this.Config.MaxKibsPerSecond * 1_024,
                Configuration.SpeedLimit.Default => this.Config.MaxKibsPerSecond * 1_024,
                Configuration.SpeedLimit.Alternate => this.Config.AltMaxKibsPerSecond * 1_024,
                _ => 0,
            },
            _ => 0,
        };
    }

    private Configuration.SpeedLimit CalculateLimit() {
        if (this.Condition[ConditionFlag.InCombat]) {
            return this.Config.LimitCombat;
        }

        if (this.DutyState.IsDutyStarted) {
            return this.Config.LimitInstance;
        }

        // if (this.Condition.Any(
        //         ConditionFlag.BoundByDuty,
        //         ConditionFlag.BoundByDuty56,
        //         ConditionFlag.BoundByDuty95,
        //         ConditionFlag.BoundToDuty97
        //     )) {
        //     return this.Config.LimitInstance;
        // }

        if (this.PartyList.Length > 0) {
            return this.Config.LimitParty;
        }

        return this.Config.LimitNormal;
    }

    /// <summary>
    /// Attempt to add a download to the download queue. This can fail (and
    /// therefore return null) if a download for the same version is already in
    /// the queue.
    /// </summary>
    /// <param name="task">download to add</param>
    /// <param name="token">cancellation token</param>
    /// <returns>the running download task or null</returns>
    internal async Task<Task?> AddDownloadAsync(DownloadTask task, CancellationToken token = default) {
        bool wasAdded;
        using (var guard = await this.Downloads.WaitAsync(token)) {
            wasAdded = guard.Data
                .Where(download => !download.State.IsDone())
                .All(download => download.Version != task.Version);
            if (wasAdded) {
                guard.Data.Add(task);
            }
        }

        if (!wasAdded) {
            this.Interface.UiBuilder.AddNotification(
                "Already downloading that mod!",
                Name,
                NotificationType.Error
            );

            return null;
        }

        // if we await this, we'll be waiting for the whole download to finish
        return task.Start();
    }

    private class ExceptionFilter : IExceptionFilter {
        private static readonly HashSet<int> IgnoredHResults = [
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
        ];

        #pragma warning disable SYSLIB1045
        private static readonly List<Regex> IgnoredMessages = [
            new Regex(@"^No such host is known\.", RegexOptions.Compiled),
        ];
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

// ReSharper disable once ClassNeverInstantiated.Global
public class FileListSerializer(string typeName = "FileList") : ScalarSerializer<JsonElement, FileList>(typeName) {
    public override FileList Parse(JsonElement serializedValue) {
        return new FileList {
            Files = serializedValue.Deserialize<Dictionary<string, List<List<string?>>>>()!,
        };
    }

    protected override JsonElement Format(FileList runtimeValue) {
        return JsonSerializer.SerializeToElement(runtimeValue.Files);
    }
}

// ReSharper disable once ClassNeverInstantiated.Global
public class OptionsSerializer(string typeName = "Options") : ScalarSerializer<JsonElement, Dictionary<string, List<string>>>(typeName) {
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

// ReSharper disable once ClassNeverInstantiated.Global
public class InstallerImageListSerializer(string typeName = "InstallerImageList") : ScalarSerializer<JsonElement, InstallerImageList>(typeName) {
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

// ReSharper disable once ClassNeverInstantiated.Global
public class BatchListSerializer(string typeName = "BatchList") : ScalarSerializer<JsonElement, BatchList>(typeName) {
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

// ReSharper disable once ClassNeverInstantiated.Global
public class FileSwapsSerializer(string typeName = "FileSwaps") : ScalarSerializer<JsonElement, FileSwaps>(typeName) {
    public override FileSwaps Parse(JsonElement serializedValue) {
        return new FileSwaps {
            Swaps = serializedValue.Deserialize<Dictionary<string, string>>()!,
        };
    }

    protected override JsonElement Format(FileSwaps runtimeValue) {
        return JsonSerializer.SerializeToElement(runtimeValue.Swaps);
    }
}

// ReSharper disable once ClassNeverInstantiated.Global
public class GraphqlJsonSerializer(string typeName = "JSON") : ScalarSerializer<JsonElement, JsonElement>(typeName) {
    public override JsonElement Parse(JsonElement serializedValue) {
        return serializedValue;
    }

    protected override JsonElement Format(JsonElement runtimeValue) {
        return runtimeValue;
    }
}
