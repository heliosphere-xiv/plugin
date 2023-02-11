using System.Net;
using System.Reflection;
using System.Text;
using Dalamud.Interface.Internal.Notifications;
using Dalamud.Logging;
using Heliosphere.Ui;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace Heliosphere;

internal class Server : IDisposable {
    private Plugin Plugin { get; }
    private HttpListener Listener { get; }

    internal bool Listening => this.Listener.IsListening;

    internal Server(Plugin plugin) {
        this.Plugin = plugin;

        this.Listener = new HttpListener {
            Prefixes = { "http://localhost:27389/" },
        };

        try {
            this.StartServer();
        } catch (HttpListenerException ex) {
            PluginLog.LogError(ex, "Could not start HTTP server");
        }
    }

    internal void StartServer() {
        if (this.Listener.IsListening) {
            return;
        }

        this.Listener.Start();

        new Thread(() => {
            while (this.Listener.IsListening) {
                try {
                    this.HandleConnection();
                } catch (InvalidOperationException) {
                    break;
                } catch (Exception ex) {
                    PluginLog.LogError(ex, "Error handling request");
                }
            }
        }).Start();
    }

    private void HandleConnection() {
        var ctx = this.Listener.GetContext();
        var req = ctx.Request;
        var resp = ctx.Response;
        var url = req.Url?.AbsolutePath ?? "/";
        var method = req.HttpMethod.ToLowerInvariant();

        int statusCode;
        object? response = null;

        switch (url) {
            case "/install" when method == "post": {
                using var reader = new StreamReader(req.InputStream);
                var json = reader.ReadToEnd();
                var info = JsonConvert.DeserializeObject<InstallRequest>(json);

                Task.Run(async () => {
                    try {
                        this.Plugin.Interface.UiBuilder.AddNotification(
                            "Opening mod installer, please wait...",
                            this.Plugin.Name,
                            NotificationType.Info
                        );
                        var window = await PromptWindow.Open(this.Plugin, info.PackageId, (int) info.VersionId);
                        await this.Plugin.PluginUi.AddToDrawAsync(window);
                    } catch (Exception ex) {
                        PluginLog.LogError(ex, "Error opening prompt window");
                        this.Plugin.Interface.UiBuilder.AddNotification(
                            "Error opening installer prompt.",
                            this.Plugin.Name,
                            NotificationType.Error
                        );
                    }
                });

                statusCode = 204;
                break;
            }
            case "/version" when method == "get": {
                var version = Assembly.GetAssembly(typeof(Plugin))?
                    .GetName()
                    .Version?
                    .ToString(3);

                statusCode = 200;
                response = new {
                    Version = version,
                };
                break;
            }
            default: {
                statusCode = 404;
                response = new {
                    Error = "not found",
                };

                break;
            }
        }

        resp.StatusCode = statusCode;

        if (response != null) {
            var json = JsonConvert.SerializeObject(response, Formatting.None, new JsonSerializerSettings {
                ContractResolver = new DefaultContractResolver {
                    NamingStrategy = new SnakeCaseNamingStrategy(),
                },
            });
            resp.AddHeader("content-type", "application/json");
            resp.OutputStream.Write(Encoding.UTF8.GetBytes(json));
        }

        resp.Close();
    }

    public void Dispose() {
        ((IDisposable) this.Listener).Dispose();
    }
}

[Serializable]
[JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
internal class InstallRequest {
    public Guid PackageId { get; set; }
    public uint VersionId { get; set; }

    // values to display in a temp window while grabbing metadata?
    // public string PackageName { get; set; }
    // public string VariantName { get; set; }
    // public string Version { get; set; }
    // public string AuthorName { get; set; }
}
