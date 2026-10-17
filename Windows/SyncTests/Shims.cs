using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace ClipBubble.SyncTests
{
    static class Native
    {
        /// The app binds Windows' own SQLite; elsewhere the system one has the same API.
        [ModuleInitializer]
        internal static void Init() => NativeLibrary.SetDllImportResolver(typeof(Native).Assembly, (name, _, _) =>
            name == "winsqlite3.dll" && !OperatingSystem.IsWindows()
                ? NativeLibrary.Load(OperatingSystem.IsMacOS() ? "libsqlite3.dylib" : "libsqlite3.so.0")
                : IntPtr.Zero);
    }
}

namespace System.Web.Script.Serialization
{
    /// The bits of .NET Framework's serializer the store uses, on System.Text.Json: JSON values come back as
    /// bool, string, double, object[] and dictionaries, like the original.
    sealed class JavaScriptSerializer
    {
        public int MaxJsonLength { get; set; }
        public int RecursionLimit { get; set; }

        public string Serialize(object? o) => JsonSerializer.Serialize(o);

        public T Deserialize<T>(string json)
        {
            if (typeof(T) == typeof(Dictionary<string, object?>))
                return (T)(object)JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!.ToDictionary(p => p.Key, p => Plain(p.Value));
            return JsonSerializer.Deserialize<T>(json)!;
        }

        static object? Plain(JsonElement e) => e.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => e.GetString(),
            JsonValueKind.Number => e.GetDouble(),
            JsonValueKind.Array => e.EnumerateArray().Select(Plain).ToArray(),
            JsonValueKind.Object => e.EnumerateObject().ToDictionary(p => p.Name, p => Plain(p.Value)),
            _ => null,
        };
    }
}
