using System.Security.Cryptography;
using System.Text;
using KvaliTool.Core.Configuration;

namespace KvaliTool.Tests;

[TestClass]
public sealed class FileHashCacheTests
{
    private sealed class FakeClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void AdvanceDays(int days) => _now = _now.AddDays(days);
    }

    private static readonly DateTime Written = new(2024, 5, 6, 7, 8, 9, DateTimeKind.Utc);
    private static readonly DateTime Changed = new(2024, 5, 6, 7, 8, 10, DateTimeKind.Utc);

    private static HashCacheKey Key(string path = @"C:\data\movie.mkv", long size = 123, string fileId = "0000ABCD-0000000000000042") =>
        new(path, size, Written, Changed, fileId);

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    [TestMethod]
    public void Load_of_a_missing_file_starts_empty_and_does_not_create_it()
    {
        using var temp = new TempFolder();
        var path = temp.Combine("hashcache.bin");

        var cache = FileHashCache.Load(path);

        Assert.AreEqual(0, cache.Count);
        Assert.AreEqual(path, cache.Path);
        cache.Save();
        Assert.IsFalse(File.Exists(path), "Nothing changed, so nothing is written.");
    }

    [TestMethod]
    public void Saved_entries_come_back_after_a_reload()
    {
        using var temp = new TempFolder();
        var path = temp.Combine("sub", "hashcache.bin");
        var cache = FileHashCache.Load(path);
        cache.Set(Key(@"C:\a.bin"), Hash("a"));
        cache.Set(Key(@"C:\ü ñ 日本\b.bin", size: 99), Hash("b"));
        cache.Save();

        var reloaded = FileHashCache.Load(path);

        Assert.AreEqual(2, reloaded.Count);
        Assert.IsTrue(reloaded.TryGet(Key(@"C:\a.bin"), out var a));
        Assert.AreEqual(Hash("a"), a, "Hashes come back as upper-case hex.");
        Assert.IsTrue(reloaded.TryGet(Key(@"C:\ü ñ 日本\b.bin", size: 99), out var b));
        Assert.AreEqual(Hash("b"), b);
        Assert.AreEqual(0, Directory.GetFiles(temp.Combine("sub"), "*.tmp").Length);
    }

    [TestMethod]
    [DataRow("size")]
    [DataRow("lastWrite")]
    [DataRow("changeTime")]
    [DataRow("fileId")]
    public void A_hash_is_never_returned_for_a_file_that_differs_in_size_modified_time_change_time_or_file_id(string field)
    {
        var cache = FileHashCache.Load(Path.Combine(Path.GetTempPath(), "KvaliToolTests-never-written", "hashcache.bin"));
        cache.Set(Key(), Hash("original"));
        var changed = field switch
        {
            "size" => Key() with { Size = 124 },
            "lastWrite" => Key() with { LastWriteUtc = Written.AddTicks(1) },
            "changeTime" => Key() with { ChangeTimeUtc = Changed.AddTicks(1) },
            _ => Key() with { FileId = "0000ABCD-0000000000000043" },
        };

        Assert.IsFalse(cache.TryGet(changed, out var hash), field);
        Assert.AreEqual(string.Empty, hash);
        Assert.IsTrue(cache.TryGet(Key(), out _), "The exact key still hits.");
    }

    [TestMethod]
    public void Paths_are_compared_ignoring_case_like_windows_does()
    {
        var cache = FileHashCache.Load(Path.Combine(Path.GetTempPath(), "KvaliToolTests-never-written", "hashcache.bin"));
        cache.Set(Key(@"C:\Data\Movie.MKV"), Hash("x"));

        Assert.IsTrue(cache.TryGet(Key(@"c:\data\movie.mkv"), out _));
    }

    [TestMethod]
    public void Set_replaces_the_entry_for_the_same_path()
    {
        var cache = FileHashCache.Load(Path.Combine(Path.GetTempPath(), "KvaliToolTests-never-written", "hashcache.bin"));
        cache.Set(Key(), Hash("old"));
        var newer = Key(size: 500);

        cache.Set(newer, Hash("new"));

        Assert.AreEqual(1, cache.Count);
        Assert.IsFalse(cache.TryGet(Key(), out _), "The old version of the file is no longer remembered.");
        Assert.IsTrue(cache.TryGet(newer, out var hash));
        Assert.AreEqual(Hash("new"), hash);
    }

    [TestMethod]
    public void Set_rejects_something_that_is_not_a_sha256()
    {
        var cache = FileHashCache.Load(Path.Combine(Path.GetTempPath(), "KvaliToolTests-never-written", "hashcache.bin"));

        Assert.ThrowsExactly<ArgumentException>(() => cache.Set(Key(), "ABCD"));
    }

    // ---------------------------------------------------------------- bad files start an empty cache, never throw

    private static FileHashCache SaveSampleAndGetBytes(TempFolder temp, out string path, out byte[] bytes)
    {
        path = temp.Combine("hashcache.bin");
        var cache = FileHashCache.Load(path);
        cache.Set(Key(@"C:\one.bin"), Hash("1"));
        cache.Set(Key(@"C:\two.bin"), Hash("2"));
        cache.Save();
        bytes = File.ReadAllBytes(path);
        return cache;
    }

    [TestMethod]
    public void A_corrupt_file_starts_an_empty_cache_and_the_next_save_replaces_it()
    {
        using var temp = new TempFolder();
        SaveSampleAndGetBytes(temp, out var path, out var bytes);
        bytes[bytes.Length / 2] ^= 0xFF;
        File.WriteAllBytes(path, bytes);

        var cache = FileHashCache.Load(path);

        Assert.AreEqual(0, cache.Count);
        Assert.IsFalse(cache.TryGet(Key(@"C:\one.bin"), out _));
        cache.Set(Key(@"C:\fresh.bin"), Hash("fresh"));
        cache.Save();
        Assert.AreEqual(1, FileHashCache.Load(path).Count);
    }

    [TestMethod]
    public void A_corrupt_file_is_replaced_on_save_even_if_nothing_else_changed()
    {
        using var temp = new TempFolder();
        var path = temp.Write("hashcache.bin", "garbage"u8.ToArray());

        FileHashCache.Load(path).Save();

        var after = File.ReadAllBytes(path);
        CollectionAssert.AreNotEqual("garbage"u8.ToArray(), after);
        Assert.AreEqual(0, FileHashCache.Load(path).Count);
    }

    [TestMethod]
    public void A_truncated_file_starts_empty()
    {
        using var temp = new TempFolder();
        SaveSampleAndGetBytes(temp, out var path, out var bytes);
        File.WriteAllBytes(path, bytes[..(bytes.Length - 10)]);

        Assert.AreEqual(0, FileHashCache.Load(path).Count);
    }

    [TestMethod]
    public void An_empty_file_and_random_bytes_start_empty()
    {
        using var temp = new TempFolder();
        var empty = temp.Write("empty.bin", []);
        var random = temp.Write("random.bin", RandomNumberGenerator.GetBytes(5000));

        Assert.AreEqual(0, FileHashCache.Load(empty).Count);
        Assert.AreEqual(0, FileHashCache.Load(random).Count);
    }

    [TestMethod]
    public void A_file_written_by_another_format_version_starts_empty()
    {
        using var temp = new TempFolder();
        SaveSampleAndGetBytes(temp, out var path, out var bytes);

        // Same file, but with the version number changed and the checksum made valid again: only the version is wrong.
        var body = bytes[..^SHA256.HashSizeInBytes];
        BitConverter.GetBytes(999).CopyTo(body, 4);
        File.WriteAllBytes(path, [.. body, .. SHA256.HashData(body)]);

        Assert.AreEqual(0, FileHashCache.Load(path).Count);
    }

    [TestMethod]
    public void A_folder_where_the_file_should_be_starts_empty_instead_of_throwing()
    {
        using var temp = new TempFolder();
        var path = temp.Combine("hashcache.bin");
        Directory.CreateDirectory(path);

        Assert.AreEqual(0, FileHashCache.Load(path).Count);
    }

    // ---------------------------------------------------------------- age and size limits

    [TestMethod]
    public void Entries_not_seen_for_the_maximum_age_are_dropped_on_save()
    {
        using var temp = new TempFolder();
        var path = temp.Combine("hashcache.bin");
        var clock = new FakeClock(new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero));
        var cache = FileHashCache.Load(path, clock);
        cache.Set(Key(@"C:\stale.bin"), Hash("stale"));
        cache.Set(Key(@"C:\recent.bin"), Hash("recent"));
        cache.Save();

        clock.AdvanceDays(FileHashCache.MaxEntryAgeDays - 1);
        cache = FileHashCache.Load(path, clock);
        cache.Set(Key(@"C:\recent.bin", size: 1), Hash("recent2"));
        cache.Save();
        Assert.AreEqual(2, FileHashCache.Load(path, clock).Count, "Just inside the limit: nothing is dropped yet.");

        clock.AdvanceDays(2);
        cache = FileHashCache.Load(path, clock);
        cache.Set(Key(@"C:\recent.bin", size: 1), Hash("recent2"));
        cache.Save();

        var reloaded = FileHashCache.Load(path, clock);
        Assert.AreEqual(1, reloaded.Count);
        Assert.IsFalse(reloaded.TryGet(Key(@"C:\stale.bin"), out _));
        Assert.IsTrue(reloaded.TryGet(Key(@"C:\recent.bin", size: 1), out _));
    }

    [TestMethod]
    public void A_cache_hit_counts_as_seeing_the_entry_so_it_is_kept()
    {
        using var temp = new TempFolder();
        var path = temp.Combine("hashcache.bin");
        var clock = new FakeClock(new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero));
        var cache = FileHashCache.Load(path, clock);
        cache.Set(Key(@"C:\used.bin"), Hash("used"));
        cache.Set(Key(@"C:\unused.bin"), Hash("unused"));
        cache.Save();

        // 80 days later a scan finds one file unchanged (a hit); only that entry is refreshed.
        clock.AdvanceDays(80);
        cache = FileHashCache.Load(path, clock);
        Assert.IsTrue(cache.TryGet(Key(@"C:\used.bin"), out _));
        cache.Save();

        // 100 days after the start: unused is 100 days old, used was seen 20 days ago.
        clock.AdvanceDays(20);
        cache = FileHashCache.Load(path, clock);
        cache.Set(Key(@"C:\other.bin"), Hash("other"));
        cache.Save();

        var reloaded = FileHashCache.Load(path, clock);
        Assert.IsTrue(reloaded.TryGet(Key(@"C:\used.bin"), out _), "A hit refreshed 'last seen'.");
        Assert.IsFalse(reloaded.TryGet(Key(@"C:\unused.bin"), out _), "Never seen again, so dropped.");
    }

    [TestMethod]
    public void A_miss_does_not_refresh_last_seen()
    {
        using var temp = new TempFolder();
        var path = temp.Combine("hashcache.bin");
        var clock = new FakeClock(new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero));
        var cache = FileHashCache.Load(path, clock);
        cache.Set(Key(@"C:\changed.bin"), Hash("old version"));
        cache.Save();

        clock.AdvanceDays(80);
        cache = FileHashCache.Load(path, clock);
        Assert.IsFalse(cache.TryGet(Key(@"C:\changed.bin", size: 5), out _));
        cache.Set(Key(@"C:\x.bin"), Hash("x"));
        cache.Save();

        clock.AdvanceDays(20);
        cache = FileHashCache.Load(path, clock);
        cache.Set(Key(@"C:\y.bin"), Hash("y"));
        cache.Save();

        Assert.IsFalse(FileHashCache.Load(path, clock).TryGet(Key(@"C:\changed.bin"), out _));
    }

    [TestMethod]
    public void The_file_never_holds_more_than_the_maximum_and_keeps_the_most_recently_seen()
    {
        using var temp = new TempFolder();
        var path = temp.Combine("hashcache.bin");
        var clock = new FakeClock(new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero));
        var cache = FileHashCache.Load(path, clock, maxEntries: 5);
        for (var i = 0; i < 4; i++)
        {
            cache.Set(Key($@"C:\old{i}.bin"), Hash("old" + i));
        }

        cache.Save();
        clock.AdvanceDays(3);
        cache = FileHashCache.Load(path, clock, maxEntries: 5);
        for (var i = 0; i < 4; i++)
        {
            cache.Set(Key($@"C:\new{i}.bin"), Hash("new" + i));
        }

        cache.Save();

        var reloaded = FileHashCache.Load(path, clock, maxEntries: 5);
        Assert.AreEqual(5, reloaded.Count);
        for (var i = 0; i < 4; i++)
        {
            Assert.IsTrue(reloaded.TryGet(Key($@"C:\new{i}.bin"), out _), $"new{i} is the most recent and must stay");
        }
    }

    [TestMethod]
    public void Saving_goes_through_the_atomic_writer_so_a_blocked_save_keeps_the_old_cache()
    {
        using var temp = new TempFolder();
        var path = temp.Combine("hashcache.bin");
        var cache = FileHashCache.Load(path);
        cache.Set(Key(@"C:\one.bin"), Hash("1"));
        cache.Save();
        cache.Set(Key(@"C:\two.bin"), Hash("2"));

        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            TempFolder.AssertFileError(cache.Save);
        }

        Assert.AreEqual(1, FileHashCache.Load(path).Count, "The old file is intact.");
        CollectionAssert.AreEqual(new[] { path }, Directory.GetFiles(temp.Path));

        cache.Save();
        Assert.AreEqual(2, FileHashCache.Load(path).Count, "A later save succeeds: the changes were not forgotten.");
    }
}
