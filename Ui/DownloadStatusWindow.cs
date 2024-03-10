using System.Diagnostics;
using System.Numerics;
using Dalamud.Interface.Utility;
using Heliosphere.Util;
using ImGuiNET;

namespace Heliosphere.Ui;

internal class DownloadStatusWindow : IDisposable {
    private Plugin Plugin { get; }
    internal bool Preview;
    private readonly Stopwatch _previewTimer = new();

    internal DownloadStatusWindow(Plugin plugin) {
        this.Plugin = plugin;

        this.Plugin.Interface.UiBuilder.Draw += this.Draw;
    }

    public void Dispose() {
        this.Plugin.Interface.UiBuilder.Draw -= this.Draw;
    }

    private void Draw() {
        using var guard = this.Plugin.Downloads.Wait(0);
        if (!this.Preview) {
            if (guard == null) {
                return;
            }

            if (guard.Data.All(task => task.State.IsDone())) {
                return;
            }
        }

        if (this.Preview && !this._previewTimer.IsRunning) {
            this._previewTimer.Restart();
        } else if (!this.Preview && this._previewTimer.IsRunning) {
            this._previewTimer.Stop();
        }

        var flags = ImGuiWindowFlags.NoBringToFrontOnFocus
                    | ImGuiWindowFlags.NoFocusOnAppearing
                    | ImGuiWindowFlags.NoTitleBar;
        if (!this.Preview) {
            flags |= ImGuiWindowFlags.NoBackground
                     | ImGuiWindowFlags.NoResize
                     | ImGuiWindowFlags.NoMove;
        }

        ImGui.SetNextWindowSize(new Vector2(500, 160), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin($"{Plugin.Name} downloads", flags)) {
            ImGui.End();
            return;
        }

        if (this.Preview) {
            this.DrawPreviewDownloads();
        } else if (guard != null) {
            this.DrawRealDownloads(guard);
        }

        var size = ImGui.GetWindowSize();
        ImGui.SetWindowSize(size with {
            Y = ImGui.GetCursorPosY(),
        });

        ImGui.End();
    }

    private void DrawRealDownloads(Guard<List<DownloadTask>>.Handle guard) {
        var toRemove = -1;

        for (var i = 0; i < guard.Data.Count; i++) {
            var task = guard.Data[i];
            if (task.State.IsDone()) {
                continue;
            }

            var bps = task.BytesPerSecond;
            var speed = string.Empty;
            if (task.State == State.DownloadingFiles) {
                speed = bps switch {
                    >= 1_073_741_824 => $" ({bps / 1_073_741_824:N2} GiB/s)",
                    >= 1_048_576 => $" ({bps / 1_048_576:N2} MiB/s)",
                    >= 1_024 => $" ({bps / 1_024:N2} KiB/s)",
                    _ => $" ({bps:N2} B/s)",
                };
            }

            var packageName = task switch {
                { PackageName: not null, VariantName: null } => $"{task.PackageName} - ",
                { PackageName: not null, VariantName: not null } => $"{task.PackageName} ({task.VariantName}) - ",
                _ => string.Empty,
            };
            ImGui.ProgressBar(
                (float) task.StateData / task.StateDataMax,
                new Vector2(ImGui.GetContentRegionAvail().X, 25 * ImGuiHelpers.GlobalScale),
                $"{packageName}{task.State.Name()}: {task.StateData:N0} / {task.StateDataMax:N0}{speed}"
            );

            ImGuiHelper.Tooltip("Hold Ctrl and click to cancel.");

            if (!ImGui.GetIO().KeyCtrl || !ImGui.IsItemClicked()) {
                continue;
            }

            toRemove = i;
        }

        if (toRemove <= -1) {
            return;
        }

        guard.Data[toRemove].CancellationToken.Cancel();
        guard.Data.RemoveAt(toRemove);
    }

    private void DrawPreviewDownloads() {
        var progress = this._previewTimer.ElapsedMilliseconds / 200;
        if (progress >= 100) {
            progress = 0;
            this._previewTimer.Restart();
        }

        for (var i = 0; i < 5; i++) {
            var max = (i + 1) * 20;
            var prog = Math.Min(progress, max);
            var ratio = (float) prog / max;
            ImGui.ProgressBar(
                ratio,
                new Vector2(ImGui.GetContentRegionAvail().X, 25 * ImGuiHelpers.GlobalScale),
                $"Example mod {i + 1} - {State.DownloadingFiles.Name()}: {prog:N0} / {max:N0}"
            );
        }
    }
}
