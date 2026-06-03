// Benchmark: time RunParser.ProcessNewRuns over a real data folder, producing a
// slay-the-stats.json — the same full pipeline the mod runs at launch (history
// scan → per-run parse → aggregate → serialize/save). Establishes a baseline to
// compare against future logic swaps (e.g. the #6 run-id refactor): run it now,
// run it again after the change, diff the numbers.
//
//   dotnet run -c Release --project SlayTheStats.Bench -- [dataDir] [iterations]
//
// dataDir defaults to the live appdata mount; it is the parent of "steam/"
// (GetHistoryDirectories appends "steam"). Each iteration parses every run from
// a fresh, empty db, so it measures a full cold rebuild, not incremental adds.

using System.Diagnostics;
using SlayTheStats;

string dataDir   = args.Length > 0 ? args[0] : "/media/sf_sts2-appdata";
int    iterations = args.Length > 1 && int.TryParse(args[1], out var n) ? Math.Max(1, n) : 5;

if (!Directory.Exists(Path.Combine(dataDir, "steam")))
{
    Console.Error.WriteLine($"No 'steam' dir under '{dataDir}'. Pass the data folder (parent of steam/) as arg 1.");
    return 1;
}

SlayTheStatsConfig.DataDirectory = dataDir;
string outPath = Path.Combine(Path.GetTempPath(), "slay-the-stats-bench.json");

int runCount = 0, warnCount = 0;
long jsonBytes = 0;
var timesMs = new List<double>();
// Histogram of warning messages (normalized: drop the run-id so like failures group).
var warnHist = new Dictionary<string, int>();
static string NormalizeWarn(string m)
{
    // "Failed to parse run <id>: <reason>" / "Run <id>: <reason>" → strip the id.
    if (m.StartsWith("Failed to parse run "))
    {
        int colon = m.IndexOf(':');
        return colon > 0 ? "Failed to parse run: " + m[(colon + 1)..].Trim() : m;
    }
    if (m.StartsWith("Run "))
    {
        int colon = m.IndexOf(':');
        return colon > 0 ? "Run:" + m[(colon + 1)..] : m;
    }
    return m;
}

Console.WriteLine($"Data dir : {dataDir}");
Console.WriteLine($"Schema   : v{StatsDb.CurrentSchemaVersion}  (iterations: {iterations})");
Console.WriteLine();

for (int i = 0; i < iterations; i++)
{
    var db = new StatsDb { SchemaVersion = StatsDb.CurrentSchemaVersion };
    if (File.Exists(outPath)) File.Delete(outPath);
    int warns = 0;
    bool firstIter = i == 0;
    bool isWarmup = firstIter && iterations > 1;

    // Start each iteration from a clean heap so a prior iteration's garbage
    // doesn't trigger a collection mid-measurement (this workload allocates
    // heavily via JsonNode). Reduces run-to-run variance.
    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();

    var sw = Stopwatch.StartNew();
    RunParser.ProcessNewRuns(db, outPath, log: null, warn: m =>
    {
        warns++;
        if (firstIter) warnHist[NormalizeWarn(m)] = warnHist.GetValueOrDefault(NormalizeWarn(m)) + 1;
    });
    sw.Stop();

    runCount  = db.ProcessedRuns.Count;
    warnCount = warns;
    jsonBytes = File.Exists(outPath) ? new FileInfo(outPath).Length : 0;
    // Discard iteration 0 as warmup (JIT, cold file cache) — it's an outlier that
    // skews the baseline we want to compare logic changes against.
    string tag = isWarmup ? "  (warmup, discarded)" : "";
    if (!isWarmup) timesMs.Add(sw.Elapsed.TotalMilliseconds);
    Console.WriteLine($"  iter {i + 1,2}: {sw.Elapsed.TotalMilliseconds,8:F1} ms{tag}");
}

timesMs.Sort();
double min    = timesMs[0];
double median = timesMs[timesMs.Count / 2];
double mean   = timesMs.Average();

Console.WriteLine();
Console.WriteLine($"Runs processed : {runCount}");
Console.WriteLine($"Parse warnings : {warnCount}");
Console.WriteLine($"Output JSON    : {jsonBytes / 1024.0:F1} KB  ({outPath})");
Console.WriteLine($"Time  MIN      : {min:F1} ms   <- baseline comparator (least noise)");
Console.WriteLine($"      median   : {median:F1} ms");
Console.WriteLine($"      mean/max : {mean:F1} / {timesMs[^1]:F1} ms");
Console.WriteLine($"Per run (min)  : {min / Math.Max(1, runCount):F2} ms/run");

if (warnHist.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine("Warning breakdown (first iteration):");
    foreach (var (msg, count) in warnHist.OrderByDescending(kv => kv.Value).Take(10))
        Console.WriteLine($"  {count,4}x  {msg}");
}
return 0;
