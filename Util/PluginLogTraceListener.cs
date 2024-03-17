using System.Diagnostics;
using System.Text;

namespace Heliosphere.Util;

internal class PluginLogTraceListener : TraceListener {
    public override void Write(string? message) {
        this.WriteLine(message);
    }

    public override void WriteLine(string? message) {
        if (message == null) {
            return;
        }

        if (this.NeedIndent) {
            var sb = new StringBuilder();

            var spaces = this.IndentLevel * this.IndentSize;
            for (var i = 0; i < spaces; i++) {
                sb.Append(' ');
            }

            sb.Append(message);

            Plugin.Log.Verbose(sb.ToString());
        } else {
            Plugin.Log.Verbose(message);
        }
    }
}
