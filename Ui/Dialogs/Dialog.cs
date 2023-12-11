using Heliosphere.Util;
using ImGuiNET;

namespace Heliosphere.Ui.Dialogs;

internal abstract class Dialog : IDrawable {
    internal string Title { get; }
    private ImGuiWindowFlags WindowFlags { get; }
    private bool _visible = true;

    protected Dialog(string title, ImGuiWindowFlags windowFlags = ImGuiWindowFlags.None) {
        this.Title = title;
        this.WindowFlags = windowFlags;
    }

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
