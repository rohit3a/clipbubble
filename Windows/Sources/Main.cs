using System;
using System.Linq;
using System.Threading;

namespace ClipBubble
{
    static class Program
    {
        /// Names for this data folder's lock and signals, so a test or portable copy never talks to the real one.
        public static string Named(string what) => @"Local\studio.krnl.clipbubble." + what + "." + Store.Sha(Paths.Dir.ToLowerInvariant()).Substring(0, 12);

        /// A second launch asks the running copy to open its panel instead of starting another bubble.
        public static readonly string OpenEvent = Named("open");

        [STAThread]
        static int Main(string[] args)
        {
            if (args.Contains("--peer")) return Relay.Run();
            if (args.Contains("--mcp"))
            {
                Mcp.Run();
                return 0;
            }
            if (args.Contains("--after")) Updater.AfterUpdate(args);
            using var single = new Mutex(true, Named("app"), out var first);
            if (!first)
            {
                AllowSetForegroundWindow(-1);   // ASFW_ANY: we were just launched, so we may hand focus to the running copy
                if (EventWaitHandle.TryOpenExisting(OpenEvent, out var open)) open.Set();
                return 0;
            }
            // Made before the app starts, so a second launch during startup still opens the panel once it's up.
            using var openSignal = new EventWaitHandle(false, EventResetMode.AutoReset, OpenEvent);
            new App().Run();
            return 0;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int pid);
    }
}

// Lets records compile on .NET Framework.
namespace System.Runtime.CompilerServices
{
    static class IsExternalInit { }
}
