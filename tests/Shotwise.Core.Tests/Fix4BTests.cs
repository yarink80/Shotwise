using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Shotwise.Core;
using Shotwise.Core.Capture;
using Shotwise.Core.Platform;
using Shotwise.Core.Serialization;
using Xunit;

namespace Shotwise.Core.Tests;

public class Fix4BTests
{
    [Fact]
    public void AddingRasterEditsEvictsDecodedImages()
    {
        var store = new Shotwise.Core.Documents.AssetStore { MaxDecodedCacheEntries = 2 };
        var first = new Shotwise.Core.Imaging.BgraImage(1, 1);
        first.Pixels[3] = 255;
        var id = store.AddImage(first);
        for (var i = 1; i <= 4; i++)
        {
            var image = new Shotwise.Core.Imaging.BgraImage(1, 1);
            image.Pixels[0] = (byte)i;
            image.Pixels[3] = 255;
            store.AddImage(image);
        }
        Assert.NotSame(first, store.GetImage(id));
    }
    [Theory]
    [InlineData("CON.svg")]
    [InlineData("a/NUL.png")]
    [InlineData("Lpt9.txt")]
    public void DeviceNamesAreRejected(string name) => Assert.False(ZipSafety.IsSafeEntryName(name));

    [Fact]
    public void LogsRedactSecretsAndEmails()
    {
        var value = LogRedaction.Redact("Authorization: Bearer private\nhttps://host/?t=pairing&access_token=secret user@example.com password=hunter2");
        foreach (var secret in new[] { "private", "pairing", "secret", "user@example.com", "hunter2" }) Assert.DoesNotContain(secret, value);
    }

    [Fact]
    public async Task ImportedPresetsAreSanitizedAndReviewFlagPersists()
    {
        using var temp = new TempDir();
        var path = Path.Combine(temp.Path, "presets.json");
        var pack = Path.Combine(temp.Path, "pack.shotpresets");
        var preset = new CapturePreset { Id = "untrusted", OpenInProgramId = "evil.exe", Outputs = new() { new("program", JsonSerializer.SerializeToElement(new { executable = "evil.exe" })) } };
        await File.WriteAllTextAsync(pack, JsonSerializer.Serialize(new PresetFile { Presets = new() { preset } }, ShotwiseJson.Compact));
        var repository = new PresetRepository(path);
        await repository.ImportAsync(pack);
        var imported = new PresetRepository(path).Find("untrusted")!;
        Assert.True(imported.RequiresReview);
        Assert.Null(imported.OpenInProgramId);
        Assert.Null(imported.Outputs.Single().Options);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepeatedPagesAreRejected(bool snagx)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            void Write(string name, string json) { using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false)); writer.Write(json); }
            Write(snagx ? "index.json" : "manifest.json", snagx ? "{\"Pages\":[\"p.json\",\"p.json\"]}" : "{\"format\":\"shotx\",\"pages\":[\"pages/p.json\",\"pages/p.json\"]}");
            Write(snagx ? "p.json" : "pages/p.json", "{}");
        }
        stream.Position = 0;
        if (snagx)
        {
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
            Assert.Throws<ShotwiseException>(() => new SnagxImporter().Import(zip));
        }
        else await Assert.ThrowsAsync<ShotwiseException>(() => new ShotxSerializer().LoadAsync(stream));
    }
}
