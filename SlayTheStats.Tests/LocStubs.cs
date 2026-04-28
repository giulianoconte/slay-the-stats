// Minimal stubs for game-side types referenced by SlayTheStats source files
// compiled into the test project (EncounterStats.cs and L.cs). The real
// implementations live in the game's sts2.dll + the mod's MainFile.cs, neither
// of which the test project references — pulling them in would also pull in
// Godot and reproduce the SDK problem the test project's per-file Compile
// Include approach was designed to avoid.
//
// LocManager.Instance returns null here, so every `LocManager.Instance?.…`
// call site (L.T, L.CharacterName, EncounterStats.FormatName, etc.) short-
// circuits to its built-in fallback path. That's exactly the behaviour the
// FormatName / category-formatting tests assert against — they expect the
// title-cased fallback output, so tests exercise the same code paths the
// production runtime uses when LocManager isn't yet available.

namespace MegaCrit.Sts2.Core.Localization
{
    public sealed class LocManager
    {
        public static LocManager? Instance => null;
        public LocTable GetTable(string name) => new();
    }

    public sealed class LocTable
    {
        public bool HasEntry(string key) => false;
        public string GetRawText(string key) => key;
    }

    public sealed class LocException : System.Exception
    {
        public LocException(string message) : base(message) { }
    }
}

namespace SlayTheStats
{
    // L.cs surfaces missing-key warnings via `MainFile.Logger.Warn(...)`. The
    // production MainFile pulls in Godot + BaseLib + the game DLL. This stub
    // provides just enough surface for the warning calls to compile and become
    // silent no-ops in tests.
    internal static class MainFile
    {
        internal static class Logger
        {
            internal static void Info(string msg) { }
            internal static void Warn(string msg) { }
        }
    }
}
