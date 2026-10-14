using KvaliTool.Core.Configuration;
using KvaliTool.Core.Models;
using KvaliTool.Core.Platform;
using KvaliTool.Core.Scanning;

namespace KvaliTool.Tests;

/// <summary>Runs the real scan engine on a fake Windows tree (a "sandbox") in a temp folder.</summary>
[TestClass]
public sealed class DuplicateScanTests
{
    private const int Size = 200 * 1024;

    /// <summary>A cache that records how it was used, so tests can see which files were read.</summary>
    private sealed class RecordingCache : IHashCache
    {
        private readonly Dictionary<string, (HashCacheKey Key, string Hash)> _entries = new(StringComparer.OrdinalIgnoreCase);

        public int Hits { get; private set; }

        public int Misses { get; private set; }

        public List<string> SetPaths { get; } = [];

        public int Saves { get; private set; }

        public bool TryGet(in HashCacheKey key, out string sha256Hex)
        {
            if (_entries.TryGetValue(key.Path, out var entry) && entry.Key == key)
            {
                Hits++;
                sha256Hex = entry.Hash;
                return true;
            }

            Misses++;
            sha256Hex = string.Empty;
            return false;
        }

        public void Set(in HashCacheKey key, string sha256Hex)
        {
            SetPaths.Add(key.Path);
            _entries[key.Path] = (key, sha256Hex);
        }

        public void Save() => Saves++;

        public void ResetCounters()
        {
            Hits = 0;
            Misses = 0;
            SetPaths.Clear();
        }
    }

    private static byte[] Content(byte fill, int size = Size, byte? lastByte = null)
    {
        var bytes = new byte[size];
        Array.Fill(bytes, fill);
        if (lastByte is { } last)
        {
            bytes[^1] = last;
        }

        return bytes;
    }

    private static ScanSettings Settings(bool findDuplicates = true) => new()
    {
        FindDuplicates = findDuplicates,
        DuplicateMinBytes = 1024,
    };

    private static async Task<(CategoryResult Result, ScanProgress? Last)> ScanAsync(string root, ScanSettings settings, IHashCache? cache, CancellationToken token = default)
    {
        ScanProgress? last = null;
        var progress = new Progress<ScanProgress>(p => last = p);
        var report = await ScanEngine.CreateDefault().ScanAsync(
            new ScanRequest
            {
                Settings = settings,
                Paths = new SandboxKnownPaths(root),
                Processes = new FixedProcessDetector([]),
                Categories = new HashSet<CategoryId> { CategoryId.DuplicateFiles },
                HashCache = cache,
            },
            progress,
            token);
        return (report.Categories.Single(), last);
    }

    private static string Docs(string root, string name) => Path.Combine(root, "Users", "Test", "Documents", name);

    private static void SetOldTime(string path, int daysAgo) => File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-daysAgo));

    private static List<string> Summary(CategoryResult result) =>
        [.. result.Items.Select(i => $"{i.Path}|{i.DuplicateKeeperPath}|{i.DuplicateGroupId}|{i.Group}|{i.SizeBytes}")];

    // ---------------------------------------------------------------- off by default

    [TestMethod]
    public async Task Duplicate_search_off_reports_skipped_with_a_note_and_examines_no_files()
    {
        using var temp = new TempFolder();
        var a = temp.Write(@"Users\Test\Documents\a.bin", Content(1));
        var b = temp.Write(@"Users\Test\Documents\b.bin", Content(1));
        SetOldTime(a, 10);
        SetOldTime(b, 5);
        var cache = new RecordingCache();

        var (result, last) = await ScanAsync(temp.Path, Settings(findDuplicates: false), cache);

        Assert.AreEqual(CategoryStatus.Skipped, result.Status);
        Assert.AreEqual(0, result.Items.Count);
        Assert.AreEqual(0, result.Skipped.Count);
        Assert.IsTrue(result.Notes.Any(n => n.Contains("Duplicate search is off") && n.Contains("Find duplicates (slow)")));
        Assert.AreEqual(0, last?.FilesExamined ?? 0, "No folder was walked.");
        Assert.AreEqual(0, cache.Hits + cache.Misses + cache.SetPaths.Count, "No file was opened for hashing.");
        Assert.AreEqual(0, cache.Saves, "The hash cache is not even saved when the search is off.");
    }

    [TestMethod]
    public void Find_duplicates_is_off_in_a_default_settings_object()
    {
        Assert.IsFalse(new ScanSettings().FindDuplicates);
    }

    // ---------------------------------------------------------------- correctness

    [TestMethod]
    public async Task Identical_files_are_duplicates_and_the_oldest_copy_is_kept()
    {
        using var temp = new TempFolder();
        var original = temp.Write(@"Users\Test\Documents\original.bin", Content(7));
        var copy = temp.Write(@"Users\Test\Desktop\copy.bin", Content(7));
        SetOldTime(original, 30);
        SetOldTime(copy, 5);

        var (result, _) = await ScanAsync(temp.Path, Settings(), cache: null);

        var item = result.Items.Single();
        Assert.AreEqual(CategoryStatus.Scanned, result.Status);
        Assert.IsTrue(string.Equals(copy, item.Path, StringComparison.OrdinalIgnoreCase), item.Path);
        Assert.IsTrue(string.Equals(original, item.DuplicateKeeperPath, StringComparison.OrdinalIgnoreCase), item.DuplicateKeeperPath);
    }

    [TestMethod]
    public async Task Files_with_the_same_first_64_KB_but_different_endings_are_not_duplicates()
    {
        using var temp = new TempFolder();
        SetOldTime(temp.Write(@"Users\Test\Documents\one.bin", Content(3, lastByte: 1)), 10);
        SetOldTime(temp.Write(@"Users\Test\Documents\two.bin", Content(3, lastByte: 2)), 5);

        var (result, _) = await ScanAsync(temp.Path, Settings(), cache: null);

        Assert.AreEqual(0, result.Items.Count, "Only a full SHA-256 match is a duplicate.");
    }

    [TestMethod]
    public async Task Hard_links_to_the_same_file_are_not_duplicates()
    {
        using var temp = new TempFolder();
        var first = temp.Write(@"Users\Test\Documents\hard-a.bin", Content(9));
        TempFolder.RunCmd($"mklink /H \"{Docs(temp.Path, "hard-b.bin")}\" \"{first}\"");
        var cache = new RecordingCache();

        var (result, _) = await ScanAsync(temp.Path, Settings(), cache);

        Assert.AreEqual(0, result.Items.Count);
        Assert.IsTrue(result.Skipped.Any(s => s.Reason == SkipReason.HardLinkOfSameFile));
    }

    [TestMethod]
    public async Task Hard_links_are_still_not_duplicates_when_their_hash_is_cached()
    {
        using var temp = new TempFolder();
        var first = temp.Write(@"Users\Test\Documents\hard-a.bin", Content(9));
        TempFolder.RunCmd($"mklink /H \"{Docs(temp.Path, "hard-b.bin")}\" \"{first}\"");
        var plain = temp.Write(@"Users\Test\Documents\plain.bin", Content(9));
        SetOldTime(plain, 1);
        var cache = new RecordingCache();

        var (firstRun, _) = await ScanAsync(temp.Path, Settings(), cache);
        cache.ResetCounters();
        var (secondRun, _) = await ScanAsync(temp.Path, Settings(), cache);

        CollectionAssert.AreEqual(Summary(firstRun), Summary(secondRun));
        Assert.IsFalse(secondRun.Items.Any(i => i.Path.EndsWith("hard-b.bin", StringComparison.OrdinalIgnoreCase)));
        Assert.AreEqual(0, cache.SetPaths.Count);
    }

    // ---------------------------------------------------------------- the hash cache

    private static void MakeSandboxWithOneDuplicatePair(TempFolder temp, out string original, out string copy)
    {
        original = temp.Write(@"Users\Test\Documents\original.bin", Content(7));
        copy = temp.Write(@"Users\Test\Desktop\copy.bin", Content(7));
        temp.Write(@"Users\Test\Documents\unrelated.bin", Content(1, size: Size + 5));
        SetOldTime(original, 30);
        SetOldTime(copy, 5);
    }

    [TestMethod]
    public async Task A_second_scan_with_the_cache_gives_identical_results_without_reading_the_files_again()
    {
        using var temp = new TempFolder();
        MakeSandboxWithOneDuplicatePair(temp, out _, out _);
        var cache = new RecordingCache();

        var (first, _) = await ScanAsync(temp.Path, Settings(), cache);
        Assert.AreEqual(1, first.Items.Count);
        Assert.AreEqual(2, cache.SetPaths.Count, "The first scan fully hashed both candidates and remembered them.");
        Assert.AreEqual(1, cache.Saves, "A completed scan saves the cache once.");
        cache.ResetCounters();

        var (second, _) = await ScanAsync(temp.Path, Settings(), cache);

        CollectionAssert.AreEqual(Summary(first), Summary(second));
        Assert.AreEqual(2, cache.Hits, "Both candidate files were answered from the cache.");
        Assert.AreEqual(0, cache.SetPaths.Count, "Nothing was hashed again.");
        Assert.AreEqual(2, cache.Saves);
    }

    [TestMethod]
    public async Task Results_are_the_same_with_no_cache_an_empty_cache_and_a_full_cache()
    {
        using var temp = new TempFolder();
        MakeSandboxWithOneDuplicatePair(temp, out _, out _);
        temp.Write(@"Users\Test\Documents\other-a.bin", Content(4, size: Size + 100));
        temp.Write(@"Users\Test\Documents\other-b.bin", Content(4, size: Size + 100));
        var cache = new RecordingCache();

        var (none, _) = await ScanAsync(temp.Path, Settings(), cache: null);
        var (empty, _) = await ScanAsync(temp.Path, Settings(), cache);
        var (full, _) = await ScanAsync(temp.Path, Settings(), cache);

        Assert.AreEqual(2, none.Items.Count);
        CollectionAssert.AreEqual(Summary(none), Summary(empty));
        CollectionAssert.AreEqual(Summary(none), Summary(full));
    }

    [TestMethod]
    public async Task A_new_file_next_to_cached_ones_is_still_compared_against_them()
    {
        using var temp = new TempFolder();
        MakeSandboxWithOneDuplicatePair(temp, out _, out var copy);
        var cache = new RecordingCache();
        await ScanAsync(temp.Path, Settings(), cache);
        cache.ResetCounters();
        var third = temp.Write(@"Users\Test\Downloads\third.bin", Content(7));
        SetOldTime(third, 1);

        var (result, _) = await ScanAsync(temp.Path, Settings(), cache);

        CollectionAssert.AreEquivalent(new[] { copy, third }, result.Items.Select(i => i.Path).ToArray());
        CollectionAssert.AreEqual(new[] { third }, cache.SetPaths.ToArray(), "Only the new file was read; the other two came from the cache.");
        Assert.AreEqual(2, cache.Hits);
    }

    [TestMethod]
    public async Task A_rewritten_file_with_its_modified_time_put_back_is_not_taken_from_the_cache()
    {
        using var temp = new TempFolder();
        MakeSandboxWithOneDuplicatePair(temp, out var original, out var copy);
        var cache = new RecordingCache();
        var (before, _) = await ScanAsync(temp.Path, Settings(), cache);
        Assert.AreEqual(copy, before.Items.Single().Path);
        cache.ResetCounters();

        // Change the copy's content (same size), then restore its old modified time, as some tools do.
        var restoredTime = File.GetLastWriteTimeUtc(copy);
        Thread.Sleep(100);
        File.WriteAllBytes(copy, Content(7, lastByte: 99));
        File.SetLastWriteTimeUtc(copy, restoredTime);
        Assert.AreEqual(restoredTime, File.GetLastWriteTimeUtc(copy), "The test really did put the modified time back.");

        var (after, _) = await ScanAsync(temp.Path, Settings(), cache);

        Assert.AreEqual(0, after.Items.Count, "The copy no longer has the same content, so it is not a duplicate any more.");
        CollectionAssert.Contains(cache.SetPaths, copy, "The changed file was hashed again (its change time differs).");
        Assert.AreEqual(1, cache.Hits, "The untouched original was still answered from the cache.");
        Assert.IsFalse(cache.SetPaths.Contains(original, StringComparer.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task A_resized_file_is_not_taken_from_the_cache()
    {
        using var temp = new TempFolder();
        MakeSandboxWithOneDuplicatePair(temp, out _, out var copy);
        var cache = new RecordingCache();
        await ScanAsync(temp.Path, Settings(), cache);
        cache.ResetCounters();

        File.WriteAllBytes(copy, Content(7, size: Size + 1));
        var (after, _) = await ScanAsync(temp.Path, Settings(), cache);

        Assert.AreEqual(0, after.Items.Count);
    }

    [TestMethod]
    public async Task A_cancelled_scan_does_not_save_the_cache()
    {
        using var temp = new TempFolder();
        MakeSandboxWithOneDuplicatePair(temp, out _, out _);
        var cache = new RecordingCache();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        try
        {
            await ScanAsync(temp.Path, Settings(), cache, cancelled.Token);
            Assert.Fail("A cancelled scan must throw.");
        }
        catch (OperationCanceledException)
        {
            // expected
        }

        Assert.AreEqual(0, cache.Saves);
    }

    [TestMethod]
    public async Task The_real_file_cache_round_trips_through_the_engine()
    {
        using var temp = new TempFolder();
        using var cacheFolder = new TempFolder();
        MakeSandboxWithOneDuplicatePair(temp, out _, out _);
        var cachePath = cacheFolder.Combine("hashcache.bin");

        var (first, _) = await ScanAsync(temp.Path, Settings(), FileHashCache.Load(cachePath));
        Assert.IsTrue(File.Exists(cachePath), "The completed scan saved the cache.");
        var reloaded = FileHashCache.Load(cachePath);
        Assert.AreEqual(2, reloaded.Count);
        var watched = new CountingCache(reloaded);
        var (second, _) = await ScanAsync(temp.Path, Settings(), watched);

        CollectionAssert.AreEqual(Summary(first), Summary(second));
        Assert.AreEqual(2, watched.Hits, "Both files came from the saved cache.");
        Assert.AreEqual(0, watched.Sets, "Nothing was hashed again.");
    }

    /// <summary>Lets a test watch a real <see cref="FileHashCache"/> being used.</summary>
    private sealed class CountingCache(FileHashCache real) : IHashCache
    {
        public int Hits { get; private set; }

        public int Sets { get; private set; }

        public bool TryGet(in HashCacheKey key, out string sha256Hex)
        {
            var hit = real.TryGet(key, out sha256Hex);
            if (hit)
            {
                Hits++;
            }

            return hit;
        }

        public void Set(in HashCacheKey key, string sha256Hex)
        {
            Sets++;
            real.Set(key, sha256Hex);
        }

        public void Save() => real.Save();
    }
}
