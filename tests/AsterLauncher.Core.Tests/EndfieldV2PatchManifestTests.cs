using System.Text;
using System.Text.Json.Nodes;
using AsterLauncher.Core;
using AsterLauncher.Infrastructure;

namespace AsterLauncher.Core.Tests;

public sealed class EndfieldV2PatchManifestTests
{
    private const string VfsRoot = "Endfield_Data/StreamingAssets/VFS";
    private const string Version = "1.6.3";
    private const string TargetName = "AA/new.chk";
    private const string TargetHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string BaseHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Fact]
    public void ReadsFullFileAndReturnsAuthoritativeManifestObject()
    {
        var row = FileRow();
        row["local_path"] = "vfs_files/files/" + VfsRoot + "/" + TargetName;
        var target = Target();

        var result = Read([row], [target]);

        var mapped = Assert.Single(result).Value;
        Assert.Same(target, mapped.File);
        Assert.Equal("vfs_files/files/" + target.Path, mapped.ArchiveEntry);
        Assert.Empty(mapped.Sources);
    }

    [Fact]
    public void ReadsNativeDeltaSourceAndPrefixesItsArchiveEntry()
    {
        var row = FileRow();
        row["patch"] = new JsonArray(Source());

        var mapped = Assert.Single(Read([row])).Value;

        Assert.Null(mapped.ArchiveEntry);
        var source = Assert.Single(mapped.Sources);
        Assert.Equal(VfsRoot + "/AA/old.chk", source.BaseFile.Path);
        Assert.Equal(BaseHash, source.BaseFile.Md5);
        Assert.Equal(5, source.BaseFile.Size);
        Assert.Equal("vfs_files/vfs_patch/diff_1.6.3_1.5.3/AA_new.chk_patch", source.ArchiveEntry);
        Assert.Equal(3, source.PatchSize);
    }

    [Fact]
    public void PreservesAllAlternativeBaseFilesForLaterHashSelection()
    {
        var alternate = Source();
        alternate["base_file"] = "BB/alternate.chk";
        alternate["base_md5"] = new string('c', 32);
        var row = FileRow();
        row["patch"] = new JsonArray(Source(), alternate);

        var mapped = Assert.Single(Read([row])).Value;

        Assert.Equal(2, mapped.Sources.Count);
        Assert.Equal(VfsRoot + "/BB/alternate.chk", mapped.Sources[1].BaseFile.Path);
    }

    [Fact]
    public void InvalidLaterBaseCandidateIsRejectedEvenWhenFirstCandidateIsValid()
    {
        var alternate = Source();
        alternate["base_md5"] = "invalid";
        var row = FileRow();
        row["patch"] = new JsonArray(Source(), alternate);

        Assert.Throws<InvalidDataException>(() => Read([row]));
    }

    [Fact]
    public void BlcIndexWithTypeTwoIsAFullFileWhenPresentInAuthoritativeManifest()
    {
        var row = FileRow("AA/AA.blc");
        row["diffType"] = 2;
        row["local_path"] = "vfs_files/files/" + VfsRoot + "/AA/AA.blc";
        var target = Target("AA/AA.blc");

        var mapped = Assert.Single(Read([row], [target])).Value;

        Assert.Same(target, mapped.File);
        Assert.Equal("vfs_files/files/" + VfsRoot + "/AA/AA.blc", mapped.ArchiveEntry);
        Assert.Empty(mapped.Sources);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void MetadataOnlyEntryCannotInstallOrDeleteUnlistedFile(int type)
    {
        var row = FileRow(type == 1 ? "BB/unlisted.chk" : "BB/BB.blc");
        row["diffType"] = type;
        row["local_path"] = "vfs_files/files/" + VfsRoot + "/" + row["name"]!.GetValue<string>();

        var result = Read([FileRow(), row]);

        Assert.Single(result);
        Assert.True(result.ContainsKey(VfsRoot + "/" + TargetName));
    }

    [Fact]
    public void UnchangedEntryMayHaveNoArchiveOrPatchSources()
    {
        var mapped = Assert.Single(Read([FileRow()])).Value;

        Assert.Null(mapped.ArchiveEntry);
        Assert.Empty(mapped.Sources);
    }

    [Fact]
    public void MappingVersionMustMatchRequestedTarget()
    {
        var root = Metadata([FileRow()]);
        root["version"] = "1.7.0";

        Assert.Throws<InvalidDataException>(() => Parse(root));
    }

    [Theory]
    [InlineData("OtherGame/StreamingAssets/VFS")]
    [InlineData("../Endfield_Data/StreamingAssets/VFS")]
    public void VfsRootCannotEscapeSupportedNamespace(string path)
    {
        var root = Metadata([FileRow()]);
        root["vfs_base_path"] = path;

        Assert.Throws<InvalidDataException>(() => Parse(root));
    }

    [Theory]
    [InlineData("../new.chk")]
    [InlineData("AA/../new.chk")]
    [InlineData("D:/new.chk")]
    [InlineData("AA/NUL.chk")]
    [InlineData("AA/new.chk:stream")]
    [InlineData("AA/new.chk ")]
    [InlineData("AA/new.exe")]
    public void UnsafeOrUnsupportedVfsTargetIsRejected(string name)
    {
        Assert.Throws<InvalidDataException>(() => Read([FileRow(name)]));
    }

    [Theory]
    [InlineData("../old.chk")]
    [InlineData("AA/../../old.chk")]
    [InlineData("D:/old.chk")]
    [InlineData("AA/old.exe")]
    public void UnsafeOrUnsupportedBaseFileIsRejected(string name)
    {
        var row = FileRow();
        var source = Source();
        source["base_file"] = name;
        row["patch"] = new JsonArray(source);

        Assert.Throws<InvalidDataException>(() => Read([row]));
    }

    [Theory]
    [InlineData("../payload.chk_patch")]
    [InlineData("diff_1.6.3_1.5.3/../../payload.chk_patch")]
    [InlineData("D:/payload.chk_patch")]
    [InlineData("other/payload.chk_patch")]
    [InlineData("diff_1.6.3_1.5.3/payload.exe")]
    public void UnsafeOrUnsupportedDeltaArchiveEntryIsRejected(string path)
    {
        var row = FileRow();
        var source = Source();
        source["patch"] = path;
        row["patch"] = new JsonArray(source);

        Assert.Throws<InvalidDataException>(() => Read([row]));
    }

    [Theory]
    [InlineData("../new.chk")]
    [InlineData("vfs_files/files/OtherGame/AA/new.chk")]
    [InlineData("vfs_files/files/Endfield_Data/StreamingAssets/VFS/BB/another.chk")]
    public void FullArchiveEntryMustExactlyCorrespondToTarget(string entry)
    {
        var row = FileRow();
        row["local_path"] = entry;

        Assert.Throws<InvalidDataException>(() => Read([row]));
    }

    [Fact]
    public void MetadataTargetsCannotRepeatUnderWindowsCaseRules()
    {
        Assert.Throws<InvalidDataException>(() => Read([FileRow(), FileRow("aa/NEW.CHK")]));
    }

    [Fact]
    public void CompleteManifestTargetsCannotRepeatUnderWindowsCaseRules()
    {
        Assert.Throws<InvalidDataException>(() => Read([FileRow()], [Target(), Target("aa/NEW.CHK")]));
    }

    [Fact]
    public void TargetSizeMustMatchAuthoritativeManifest()
    {
        var row = FileRow();
        row["size"] = 8;

        Assert.Throws<InvalidDataException>(() => Read([row]));
    }

    [Fact]
    public void TargetHashMustMatchAuthoritativeManifest()
    {
        var row = FileRow();
        row["md5"] = new string('c', 32);

        Assert.Throws<InvalidDataException>(() => Read([row]));
    }

    [Fact]
    public void HashCaseDoesNotChangeIntegrityIdentity()
    {
        var row = FileRow();
        row["md5"] = TargetHash.ToUpperInvariant();

        Assert.Single(Read([row]));
    }

    [Theory]
    [InlineData("base_md5", "invalid")]
    [InlineData("base_size", "-1")]
    [InlineData("patch_size", "-1")]
    [InlineData("patch_size", "9223372036854775808")]
    public void EverySourceIntegrityFieldIsValidated(string key, string value)
    {
        var row = FileRow();
        var source = Source();
        source[key] = value;
        row["patch"] = new JsonArray(source);

        Assert.Throws<InvalidDataException>(() => Read([row]));
    }

    [Fact]
    public void NumericStringsFromPublisherAreAcceptedWithoutLosingLongPrecision()
    {
        var row = FileRow();
        row["size"] = "7";
        var source = Source();
        source["base_size"] = "5000000000";
        source["patch_size"] = "3";
        row["patch"] = new JsonArray(source);

        var mapped = Assert.Single(Read([row])).Value;

        Assert.Equal(5000000000L, Assert.Single(mapped.Sources).BaseFile.Size);
    }

    [Fact]
    public void EmptyTargetManifestDoesNotMakeMetadataIntoInstallInstructions()
    {
        Assert.Empty(Read([FileRow()], []));
    }

    [Fact]
    public void UnknownFileKindIsRejected()
    {
        var row = FileRow();
        row["diffType"] = 99;

        Assert.Throws<InvalidDataException>(() => Read([row]));
    }

    [Fact]
    public void MoreThanEightMiBIsRejectedBeforeParsing()
    {
        Assert.Throws<InvalidDataException>(() =>
            EndfieldV2PatchManifest.Read(new byte[8 * 1024 * 1024 + 1], Version, [Target()]));
    }

    [Fact]
    public void MoreThanTwoHundredThousandEntriesIsRejected()
    {
        // Keep the count-limit probe below 1 MiB; no files are created.
        var text = "{\"version\":\"1.6.3\",\"vfs_base_path\":\"" + VfsRoot
            + "\",\"files\":[" + string.Join(',', Enumerable.Repeat("{}", 200_001)) + "]}";

        Assert.Throws<InvalidDataException>(() =>
            EndfieldV2PatchManifest.Read(Encoding.UTF8.GetBytes(text), Version, [Target()]));
    }

    [Fact]
    public void MalformedJsonBecomesDataValidationFailure()
    {
        Assert.Throws<InvalidDataException>(() =>
            EndfieldV2PatchManifest.Read(Encoding.UTF8.GetBytes("{"), Version, [Target()]));
    }

    private static EndfieldManifestFile Target(string name = TargetName) =>
        new(VfsRoot + "/" + name, 7, TargetHash);

    private static JsonObject FileRow(string name = TargetName) => new()
    {
        ["name"] = name, ["md5"] = TargetHash, ["size"] = 7, ["diffType"] = 1
    };

    private static JsonObject Source() => new()
    {
        ["base_file"] = "AA/old.chk",
        ["base_md5"] = BaseHash,
        ["base_size"] = 5,
        ["patch"] = "diff_1.6.3_1.5.3/AA_new.chk_patch",
        ["patch_size"] = 3
    };

    private static JsonObject Metadata(IEnumerable<JsonObject> rows) => new()
    {
        ["version"] = Version,
        ["vfs_base_path"] = VfsRoot,
        ["files"] = new JsonArray(rows.Select(row => (JsonNode)row).ToArray())
    };

    private static IReadOnlyDictionary<string, EndfieldV2PatchFile> Read(
        IEnumerable<JsonObject> rows, IReadOnlyList<EndfieldManifestFile>? targets = null) =>
        Parse(Metadata(rows), targets);

    private static IReadOnlyDictionary<string, EndfieldV2PatchFile> Parse(
        JsonObject root, IReadOnlyList<EndfieldManifestFile>? targets = null) =>
        EndfieldV2PatchManifest.Read(Encoding.UTF8.GetBytes(root.ToJsonString()), Version, targets ?? [Target()]);
}