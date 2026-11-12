// SPIKE (phase 2): does a windowless exe read a hook's JSON on stdin? Replaced by the real entry point in phase 3.
using System.Runtime.InteropServices;

static partial class Program
{
    [LibraryImport("kernel32.dll")] private static partial IntPtr GetConsoleWindow();

    static int Main(string[] args)
    {
        if (args is ["hook-spike", var dir])
        {
            var raw = Console.In.ReadToEnd();
            Directory.CreateDirectory(dir);
            var report = $"{{\"bytes\":{raw.Length},\"console_window\":{(GetConsoleWindow() != IntPtr.Zero ? "true" : "false")},"
                       + $"\"stdin_redirected\":{(Console.IsInputRedirected ? "true" : "false")},\"pid\":{Environment.ProcessId},"
                       + $"\"input\":{System.Text.Json.JsonSerializer.Serialize(raw, SpikeJson.Default.String)}}}";
            File.WriteAllText(Path.Combine(dir, $"spike-{Environment.ProcessId}.json"), report);
        }
        return 0;
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(string))]
internal partial class SpikeJson : System.Text.Json.Serialization.JsonSerializerContext;
