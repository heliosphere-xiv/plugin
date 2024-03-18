using Heliosphere.Util;
using ImGuiNET;

namespace Heliosphere.Ui.Dialogs;

internal abstract class Dialog(string title, ImGuiWindowFlags windowFlags = ImGuiWindowFlags.None) : IDrawable {
    internal string Title { get; } = title;
    private ImGuiWindowFlags WindowFlags { get; } = windowFlags;
    private bool _visible = true;

    public virtual void Dispose() {
    }

    public DrawStatus Draw() {
        if (!this._visible) {
            return DrawStatus.Finished;
        }

        using var end = new OnDispose(ImGui.End);
        if (!ImGui.Begin(this.Title, ref this._visible, this.WindowFlags)) {
            return DrawStatus.Continue;
        }

        return this.InnerDraw();
    }

    protected abstract DrawStatus InnerDraw();
}
