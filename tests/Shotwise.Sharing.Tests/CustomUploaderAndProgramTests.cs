using System.Text.Json;
using Shotwise.Core;
using Shotwise.Core.Library;
using Shotwise.Core.Sharing;
using Shotwise.Sharing.Auth;
using Shotwise.Sharing.Destinations;

namespace Shotwise.Sharing.Tests;

public class JsonPathAndTemplateTests
{
    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement;

    [Theory]
    [InlineData("$.data.link", "https://i.example/a.png")]
    [InlineData("data.link", "https://i.example/a.png")]
    [InlineData("data.items[1].id", "2")]
    [InlineData("data['odd-key']", "yes")]
    [InlineData("data.flag", "true")]
    [InlineData("data.missing", null)]
    [InlineData("data.items[9]", null)]
    [InlineData("data.nothing", null)]
    public void SelectsValues(string path, string? expected)
    {
        var doc = Json("""{"data":{"link":"https://i.example/a.png","items":[{"id":1},{"id":2}],"odd-key":"yes","flag":true,"nothing":null}}""");
        Assert.Equal(expected, JsonPathLite.Select(doc, path));
    }

    [Fact]
    public void RendersResponseTemplates()
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Location"] = "https://x/y" };
        const string body = """{"id":"abc","data":{"ext":"png"}} trailing id=77;""";
        Assert.Equal("https://host/abc.png", ResponseTemplate.Render("https://host/{json:id}.{json:data.ext}", 200, headers, body.Split(" trailing")[0]));
        Assert.Equal("https://x/y", ResponseTemplate.Render("{header:location}", 201, headers, ""));
        Assert.Equal("77", ResponseTemplate.Render("{regex:id=(\\d+);}", 200, headers, body));
        Assert.Equal("plain", ResponseTemplate.Render("{response}", 200, headers, "  plain \n"));
        Assert.Equal("201", ResponseTemplate.Render("{status}", 201, headers, ""));
        Assert.Equal("", ResponseTemplate.Render("{json:a}", 200, headers, "not json"));
        Assert.Equal("", ResponseTemplate.Render("{regex:(unclosed}", 200, headers, "x"));
    }
}

public class CustomUploaderTests
{
    private const string Definition = """
    {
      "name": "Local test host",
      "requestUrl": "{URL}/upload",
      "method": "POST",
      "headers": { "Authorization": "Bearer {secret:token}", "X-App": "shotwise" },
      "arguments": { "title": "{title}", "kind": "shot" },
      "body": "multipart",
      "fileFormName": "image",
      "url": "{json:data.link}",
      "errorMessage": "{json:error}"
    }
    """;

    [Fact]
    public void ParsesAndValidatesDefinitions()
    {
        var d = CustomUploaderParser.Parse(Definition.Replace("{URL}", "https://example.com"));
        Assert.Equal("Local test host", d.Name);
        Assert.Equal("multipart", d.Body);
        Assert.Equal("shot", d.Arguments["kind"]);

        Assert.Throws<ShotwiseException>(() => CustomUploaderParser.Parse("{}"));
        Assert.Throws<ShotwiseException>(() => CustomUploaderParser.Parse("[1,2"));
        Assert.Throws<ShotwiseException>(() => CustomUploaderParser.Parse("""{"name":"x","requestUrl":"ftp://a/b"}"""));
        Assert.Throws<ShotwiseException>(() => CustomUploaderParser.Parse("""{"name":"x","requestUrl":"https://a/b","method":"DELETE"}"""));
        Assert.Throws<ShotwiseException>(() => CustomUploaderParser.Parse("""{"name":"x","requestUrl":"https://a/b","body":"weird"}"""));
        Assert.Throws<ShotwiseException>(() => CustomUploaderParser.Parse("""{"name":"x","requestUrl":"https://a/b","headers":{"Bad Name":"v"}}"""));
        Assert.Throws<ShotwiseException>(() => CustomUploaderParser.Parse("""{"name":"x","requestUrl":"https://a/b","headers":{"X":"a\r\nInjected: 1"}}"""));
    }

    [Fact]
    public async Task UploadsMultipartToLocalStubAndExtractsUrl()
    {
        using var dir = new TempDir();
        using var stub = new StubServer(_ => new StubResponse(200, """{"data":{"link":"https://img.example/xyz.png"}}"""));
        var creds = new InMemoryCredentialStore();
        var ctx = Ctx.Make(dir, creds);
        var dest = new CustomHttpDestination(ctx);
        dest.Import(Definition.Replace("{URL}", stub.BaseUrl));
        dest.SetSecret("Local test host", "token", "s3cr3t");
        var file = dir.File("pic.png", "PNGBYTES");

        Assert.True(dest.IsConfigured);
        Assert.True(await dest.PrepareAsync(new FakeShareUi(), new[] { Ctx.PngFile(file, "My Title") }, CancellationToken.None));
        var result = await dest.ShareAsync(new[] { Ctx.PngFile(file, "My Title") }, null, CancellationToken.None);

        Assert.Equal(ShareOutcome.Succeeded, result.Outcome);
        Assert.Equal("https://img.example/xyz.png", result.Link);
        Assert.Equal("https://img.example/xyz.png", result.ClipboardText);
        var req = Assert.Single(stub.Snapshot());
        Assert.Equal("/upload", req.Path);
        Assert.Equal("Bearer s3cr3t", req.Headers["Authorization"]);
        Assert.Equal("shotwise", req.Headers["X-App"]);
        Assert.StartsWith("multipart/form-data", req.Headers["Content-Type"]);
        var text = req.BodyText;
        Assert.Matches("name=\"?title\"?", text);
        Assert.Contains("My Title", text);
        Assert.Matches("name=\"?image\"?", text);
        Assert.Matches("filename=\"?pic.png\"?", text);
        Assert.Contains("PNGBYTES", text);
        // The secret lives in the credential store only.
        Assert.DoesNotContain("s3cr3t", File.ReadAllText(Directory.EnumerateFiles(dest.DefinitionsFolder).Single()));
    }

    [Fact]
    public async Task BinaryAndJsonBodiesAndErrorMessages()
    {
        using var dir = new TempDir();
        using var stub = new StubServer(r => r.Path == "/fail" ? new StubResponse(500, """{"error":"disk full"}""") : new StubResponse(200, "https://plain.example/ok"));
        var ctx = Ctx.Make(dir);
        var dest = new CustomHttpDestination(ctx);
        dest.Import($$"""{"name":"bin","requestUrl":"{{stub.BaseUrl}}/raw","method":"PUT","body":"binary","arguments":{"n":"{name}"},"url":"{response}"}""");
        var file = dir.File("a.png", "RAW");
        var res = await dest.ShareAsync(new[] { Ctx.PngFile(file) }, null, CancellationToken.None);
        Assert.Equal("https://plain.example/ok", res.Link);
        var req = stub.Snapshot().Single();
        Assert.Equal("PUT", req.Method);
        Assert.Equal("RAW", req.BodyText);
        Assert.Equal("?n=a", req.Query);
        Assert.Equal("image/png", req.Headers["Content-Type"]);

        dest.Remove("bin");
        dest.Import($$"""{"name":"js","requestUrl":"{{stub.BaseUrl}}/fail","body":"json","fileFormName":"data","errorMessage":"{json:error}"}""");
        var ex = await Assert.ThrowsAsync<ShotwiseException>(() => dest.ShareAsync(new[] { Ctx.PngFile(file) }, null, CancellationToken.None));
        Assert.Contains("500", ex.Message);
        Assert.DoesNotContain("disk full", ex.Message);
        var sent = JsonDocument.Parse(stub.Snapshot().Last().BodyText).RootElement;
        Assert.Equal(Convert.ToBase64String("RAW"u8.ToArray()), sent.GetProperty("data").GetString());
    }

    [Fact]
    public async Task EmptyUrlResultIsAnError()
    {
        using var dir = new TempDir();
        using var stub = new StubServer(_ => new StubResponse(200, "{}"));
        var dest = new CustomHttpDestination(Ctx.Make(dir));
        dest.Import($$"""{"name":"n","requestUrl":"{{stub.BaseUrl}}/u","url":"{json:data.link}"}""");
        await Assert.ThrowsAsync<ShotwiseException>(() => dest.ShareAsync(new[] { Ctx.PngFile(dir.File("a.png")) }, null, CancellationToken.None));
    }

    [Fact]
    public void InvalidDefinitionFilesAreIgnored()
    {
        using var dir = new TempDir();
        var dest = new CustomHttpDestination(Ctx.Make(dir));
        Directory.CreateDirectory(dest.DefinitionsFolder);
        File.WriteAllText(Path.Combine(dest.DefinitionsFolder, "bad.json"), "{nope");
        Assert.Empty(dest.Definitions());
        Assert.False(dest.IsConfigured);
    }
}

public class ProgramArgumentTests
{
    [Fact]
    public void TokenizesWithQuotes()
    {
        Assert.Equal(new[] { "--open", "{file}", "-x", "two words" }, ProgramArguments.Tokenize("--open \"{file}\"  -x \"two words\""));
        Assert.Empty(ProgramArguments.Tokenize("   "));
        Assert.Equal(new[] { "" }, ProgramArguments.Tokenize("\"\""));
    }

    [Fact]
    public void FileNamesCannotInjectArguments()
    {
        var evil = @"C:\shots\a b"" --delete-all & calc.png";
        var args = ProgramArguments.Expand("--input \"{file}\" --name {name}", evil);
        Assert.Equal(4, args.Count);
        Assert.Equal("--input", args[0]);
        Assert.Equal(evil, args[1]); // one argument, byte-for-byte
        Assert.Equal("--name", args[2]);
        Assert.Equal(@"a b"" --delete-all & calc", args[3]);
    }

    [Fact]
    public void EmptyTemplateMeansJustTheFile()
        => Assert.Equal(new[] { @"C:\x.png" }, ProgramArguments.Expand("", @"C:\x.png"));

    [Fact]
    public void ExpandsAllPlaceholdersAndFilesList()
    {
        var args = ProgramArguments.Expand("{dir}|{name}|{ext}|{title}", @"C:\d\f.png", "T");
        Assert.Equal(@"C:\d|f|png|T", args[0].Replace("\\", "\\"));
        var multi = ProgramArguments.Expand("--add {files} --done", new[] { "a.png", "b.png" });
        Assert.Equal(new[] { "--add", "a.png", "b.png", "--done" }, multi);
    }

    [Fact]
    public void QuoteIsOnlyForDisplay()
        => Assert.Equal("a \"b c\" \"say \\\"hi\\\"\"", ProgramArguments.Quote(new[] { "a", "b c", "say \"hi\"" }));

    [Fact]
    public void ScriptsAreRefused()
    {
        Assert.True(ProgramDestination.IsScript(@"C:\x\run.bat"));
        Assert.True(ProgramDestination.IsScript("RUN.CMD"));
        Assert.False(ProgramDestination.IsScript("app.exe"));
    }

    [Fact]
    public void ResolvesExistingExecutablesAndRejectsMissingOnes()
    {
        using var dir = new TempDir();
        var exe = dir.File("tool.exe");
        Assert.Equal(exe, ProgramDestination.ResolveExecutable(exe));
        var ex = Assert.Throws<ShotwiseException>(() => ProgramDestination.ResolveExecutable(Path.Combine(dir.Path, "missing.exe")));
        Assert.Equal(ErrorCode.ProgramNotFound, ex.Code);
    }

    [Fact]
    public async Task EligibleProgramsFollowMediaKind()
    {
        using var dir = new TempDir();
        var ctx = Ctx.Make(dir);
        ctx.Store.Save("program", new ProgramDestinationSettings
        {
            Programs = { ProgramEntry.VideoEditor("Editor", "editor.exe"), new ProgramEntry { Name = "Paint", Executable = "paint.exe", AcceptVideo = false, AcceptGif = false } },
        });
        var dest = new ProgramDestination(ctx);
        var ui = new FakeShareUi();
        var video = new ShareFile("v.mp4", MediaKind.Video, "mp4", "v", 1, 1, TimeSpan.FromSeconds(1), false);
        // Only the video editor accepts video -> chosen without a prompt.
        Assert.True(await dest.PrepareAsync(ui, new[] { video }, CancellationToken.None));
        Assert.Empty(ui.Messages);
        // Two programs accept images? No: the editor does not. Paint alone -> chosen directly.
        Assert.True(await dest.PrepareAsync(ui, new[] { Ctx.PngFile("a.png") }, CancellationToken.None));
    }
}
