using KvaliTool.Core.Configuration;

namespace KvaliTool.Tests;

[TestClass]
public sealed class SettingsStoreTests
{
    [TestMethod]
    public void Save_then_load_gives_back_every_setting()
    {
        using var temp = new TempFolder();
        var path = temp.Combine("KvaliTool", "settings.json");
        var settings = new ScanSettings
        {
            TempMinAgeHours = 48,
            DownloadsMinAgeDays = 120,
            LargeFileThresholdBytes = 5 * ScanSettings.OneGiB,
            DuplicateMinBytes = 4096,
            LogMinAgeDays = 10,
            QuarantineRetentionDays = 14,
            FindDuplicates = true,
            AnalyzeComponentStore = false,
            Exclusions = [@"D:\Projects\*\build", "*.psd", "%USERPROFILE%\\keep"],
            UserFileScanRoots = [@"D:\Data"],
            DuplicateIgnoredFolderNames = ["node_modules", "vendor"],
        };

        SettingsStore.Save(settings, path);
        var loaded = SettingsStore.Load(path);

        Assert.AreEqual(48, loaded.TempMinAgeHours);
        Assert.AreEqual(120, loaded.DownloadsMinAgeDays);
        Assert.AreEqual(5 * ScanSettings.OneGiB, loaded.LargeFileThresholdBytes);
        Assert.AreEqual(4096, loaded.DuplicateMinBytes);
        Assert.AreEqual(10, loaded.LogMinAgeDays);
        Assert.AreEqual(14, loaded.QuarantineRetentionDays);
        Assert.IsTrue(loaded.FindDuplicates);
        Assert.IsFalse(loaded.AnalyzeComponentStore);
        CollectionAssert.AreEqual(settings.Exclusions, loaded.Exclusions);
        CollectionAssert.AreEqual(settings.UserFileScanRoots, loaded.UserFileScanRoots);
        CollectionAssert.AreEqual(settings.DuplicateIgnoredFolderNames, loaded.DuplicateIgnoredFolderNames);
    }

    [TestMethod]
    public void Find_duplicates_is_off_by_default_and_survives_a_round_trip_when_off()
    {
        using var temp = new TempFolder();
        var path = temp.Combine("settings.json");
        Assert.IsFalse(new ScanSettings().FindDuplicates);

        SettingsStore.Save(new ScanSettings(), path);

        Assert.IsFalse(SettingsStore.Load(path).FindDuplicates);
    }

    [TestMethod]
    public void Saving_replaces_the_previous_file_and_leaves_no_temp_file()
    {
        using var temp = new TempFolder();
        var path = temp.Combine("settings.json");
        SettingsStore.Save(new ScanSettings { Exclusions = ["first"] }, path);

        SettingsStore.Save(new ScanSettings { Exclusions = ["second"] }, path);

        CollectionAssert.AreEqual(new[] { "second" }, SettingsStore.Load(path).Exclusions);
        CollectionAssert.AreEqual(new[] { path }, Directory.GetFiles(temp.Path), "Only settings.json exists; no .tmp file is left behind.");
    }

    [TestMethod]
    public void Invalid_settings_throw_and_write_nothing()
    {
        using var temp = new TempFolder();
        var path = temp.Combine("settings.json");

        Assert.ThrowsExactly<SettingsException>(() => SettingsStore.Save(new ScanSettings { TempMinAgeHours = 0 }, path));

        Assert.IsFalse(File.Exists(path));
        Assert.AreEqual(0, Directory.GetFileSystemEntries(temp.Path).Length, "Not even a temp file was created.");
    }

    [TestMethod]
    public void Invalid_settings_do_not_touch_an_existing_good_file()
    {
        using var temp = new TempFolder();
        var path = temp.Combine("settings.json");
        SettingsStore.Save(new ScanSettings { Exclusions = ["important"] }, path);
        var before = File.ReadAllBytes(path);

        Assert.ThrowsExactly<SettingsException>(() => SettingsStore.Save(new ScanSettings { LargeFileThresholdBytes = 1 }, path));

        CollectionAssert.AreEqual(before, File.ReadAllBytes(path));
    }

    [TestMethod]
    public void A_save_that_fails_midway_leaves_the_old_settings_intact()
    {
        using var temp = new TempFolder();
        var path = temp.Combine("settings.json");
        SettingsStore.Save(new ScanSettings { Exclusions = ["keep me"] }, path);

        // Another program has the file open and won't let it be replaced, so the final step of the save fails.
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            TempFolder.AssertFileError(() => SettingsStore.Save(new ScanSettings { Exclusions = ["lose me"] }, path));
        }

        CollectionAssert.AreEqual(new[] { "keep me" }, SettingsStore.Load(path).Exclusions);
        CollectionAssert.AreEqual(new[] { path }, Directory.GetFiles(temp.Path));
    }

    [TestMethod]
    public void A_broken_settings_file_is_an_error_not_silently_replaced_by_defaults()
    {
        using var temp = new TempFolder();
        var path = temp.Write("settings.json", "{ not json"u8.ToArray());

        Assert.ThrowsExactly<SettingsException>(() => SettingsStore.Load(path));
    }
}
