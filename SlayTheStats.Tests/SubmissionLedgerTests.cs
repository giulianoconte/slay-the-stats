using System;
using System.IO;
using SlayTheStats.Community;
using Xunit;

namespace SlayTheStats.Tests;

/// <summary>Round-trip + skip-set behaviour of the #36 submission ledger.</summary>
public class SubmissionLedgerTests
{
    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"sts-ledger-{Guid.NewGuid():N}.json");

    [Fact]
    public void Roundtrips_EntriesThroughSaveAndLoad()
    {
        var path = TempPath();
        try
        {
            var ledger = new SubmissionLedger();
            ledger.Record("run-A", new SubmissionEntry
            {
                RunHash = "hashA",
                SubmittedUtc = new DateTimeOffset(2026, 6, 17, 1, 2, 3, TimeSpan.Zero),
                Duplicate = false,
                OwnerTagged = true,
                Verified = true,
            });
            ledger.Save(path);

            var loaded = SubmissionLedger.Load(path);

            Assert.True(loaded.Contains("run-A"));
            var e = loaded.Submitted["run-A"];
            Assert.Equal("hashA", e.RunHash);
            Assert.True(e.OwnerTagged);
            Assert.True(e.Verified);
            Assert.False(e.Duplicate);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Contains_IsFalse_ForUnknownRun()
    {
        var ledger = new SubmissionLedger();
        Assert.False(ledger.Contains("never-seen"));
    }

    [Fact]
    public void Load_MissingFile_ReturnsEmptyLedger()
    {
        var loaded = SubmissionLedger.Load(TempPath());
        Assert.Empty(loaded.Submitted);
    }

    [Fact]
    public void Load_CorruptFile_ReturnsEmptyLedger_DoesNotThrow()
    {
        var path = TempPath();
        try
        {
            File.WriteAllText(path, "{ this is not json ");
            string? warned = null;
            var loaded = SubmissionLedger.Load(path, m => warned = m);
            Assert.Empty(loaded.Submitted);
            Assert.NotNull(warned);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Load_SchemaMismatch_DiscardsFile()
    {
        var path = TempPath();
        try
        {
            File.WriteAllText(path, """{"schema_version":999,"submitted":{"r":{"run_hash":"h"}}}""");
            var loaded = SubmissionLedger.Load(path);
            Assert.Empty(loaded.Submitted); // discarded — re-derived by idempotent re-submit
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Record_OverwritesExistingEntry()
    {
        var ledger = new SubmissionLedger();
        ledger.Record("r", new SubmissionEntry { RunHash = "h1", Verified = false });
        ledger.Record("r", new SubmissionEntry { RunHash = "h1", Verified = true });
        Assert.True(ledger.Submitted["r"].Verified);
        Assert.Single(ledger.Submitted);
    }

    [Fact]
    public void HashlessEntry_StillCountsAsHandled()
    {
        // Abandoned runs are ledgered with no hash purely as a skip marker.
        var ledger = new SubmissionLedger();
        ledger.Record("abandoned", new SubmissionEntry { SubmittedUtc = DateTimeOffset.UtcNow });
        Assert.True(ledger.Contains("abandoned"));
        Assert.Null(ledger.Submitted["abandoned"].RunHash);
    }
}
