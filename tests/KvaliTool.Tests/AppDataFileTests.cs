using System.Text;
using KvaliTool.Core.Configuration;

namespace KvaliTool.Tests;

[TestClass]
public sealed class AppDataFileTests
{
    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    [TestMethod]
    public void Writes_a_new_file_and_creates_the_parent_folder()
    {
        using var temp = new TempFolder();
        var path = temp.Combine("sub", "deeper", "settings.json");

        AppDataFile.WriteAtomic(path, Bytes("hello"));

        Assert.AreEqual("hello", File.ReadAllText(path));
        Assert.AreEqual(0, Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp").Length, "No temp file is left behind.");
    }

    [TestMethod]
    public void Replaces_an_existing_file_completely_and_leaves_no_temp_file()
    {
        using var temp = new TempFolder();
        var path = temp.Write("settings.json", Bytes("old contents that are longer than the new ones"));

        AppDataFile.WriteAtomic(path, Bytes("new"));

        Assert.AreEqual("new", File.ReadAllText(path), "The file holds exactly the new contents, not a mix.");
        Assert.AreEqual(0, Directory.GetFiles(temp.Path, "*.tmp").Length);
    }

    [TestMethod]
    public void Writes_an_empty_file()
    {
        using var temp = new TempFolder();
        var path = temp.Write("a.bin", Bytes("something"));

        AppDataFile.WriteAtomic(path, []);

        Assert.AreEqual(0, new FileInfo(path).Length);
    }

    [TestMethod]
    public void Refuses_a_target_that_is_a_folder_and_writes_nothing()
    {
        using var temp = new TempFolder();
        var folder = temp.Combine("settings.json");
        Directory.CreateDirectory(folder);

        Assert.ThrowsExactly<IOException>(() => AppDataFile.WriteAtomic(folder, Bytes("x")));

        Assert.IsTrue(Directory.Exists(folder));
        Assert.AreEqual(0, Directory.GetFileSystemEntries(temp.Path, "*.tmp").Length);
        Assert.AreEqual(0, Directory.GetFileSystemEntries(folder).Length);
    }

    [TestMethod]
    public void Refuses_a_junction_and_does_not_write_through_it()
    {
        using var temp = new TempFolder();
        var outside = temp.Combine("outside");
        Directory.CreateDirectory(outside);
        var link = temp.Combine("hashcache.bin");
        TempFolder.RunCmd($"mklink /J \"{link}\" \"{outside}\"");

        Assert.ThrowsExactly<IOException>(() => AppDataFile.WriteAtomic(link, Bytes("x")));

        Assert.AreEqual(0, Directory.GetFileSystemEntries(outside).Length, "Nothing was written into the folder the junction points to.");
    }

    [TestMethod]
    public void Refuses_a_file_symlink_and_leaves_the_file_it_points_to_unchanged()
    {
        using var temp = new TempFolder();
        var real = temp.Write("real.txt", Bytes("precious"));
        var link = temp.Combine("settings.json");
        try
        {
            File.CreateSymbolicLink(link, real);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            Assert.Inconclusive("Creating a file symbolic link needs administrator rights or Developer Mode on this PC: " + ex.Message);
            return;
        }

        Assert.ThrowsExactly<IOException>(() => AppDataFile.WriteAtomic(link, Bytes("overwritten")));

        Assert.AreEqual("precious", File.ReadAllText(real));
        Assert.AreEqual(0, Directory.GetFiles(temp.Path, "*.tmp").Length);
    }

    [TestMethod]
    public void A_failed_replace_keeps_the_old_file_intact_removes_only_its_own_temp_file_and_rethrows()
    {
        using var temp = new TempFolder();
        var path = temp.Write("settings.json", Bytes("old"));
        var bystander = temp.Write("other.tmp", Bytes("not ours"));
        var bystander2 = temp.Write("settings.json.mine.tmp", Bytes("also not ours"));

        // Another program holds the target open without allowing it to be replaced: the final move must fail.
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            TempFolder.AssertFileError(() => AppDataFile.WriteAtomic(path, Bytes("new")));
        }

        Assert.AreEqual("old", File.ReadAllText(path), "A failed save leaves the previous file untouched.");
        CollectionAssert.AreEquivalent(
            new[] { path, bystander, bystander2 },
            Directory.GetFiles(temp.Path),
            "Only the temp file this call created was removed; no other file was touched.");
        Assert.AreEqual("not ours", File.ReadAllText(bystander));
        Assert.AreEqual("also not ours", File.ReadAllText(bystander2));
    }
}
