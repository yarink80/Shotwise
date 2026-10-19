using System.Security.Cryptography;
using Shotwise.Core;
using Shotwise.Core.Sharing;
using Shotwise.Sharing.Auth;
using Shotwise.Sharing.Destinations;

namespace Shotwise.Sharing.Tests;

/// <summary>Vectors from the public AWS Signature V4 documentation / test suite.</summary>
public class SigV4Tests
{
    private static readonly DateTimeOffset Suite = new(2015, 8, 30, 12, 36, 0, TimeSpan.Zero);

    [Fact]
    public void AwsTestSuiteGetVanilla()
    {
        var r = SigV4.Sign("GET", new Uri("https://example.amazonaws.com/"), new Dictionary<string, string>(), SigV4.EmptyPayloadHash,
            "AKIDEXAMPLE", "wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY", "us-east-1", "service", Suite);
        Assert.Equal("GET\n/\n\nhost:example.amazonaws.com\nx-amz-date:20150830T123600Z\n\nhost;x-amz-date\n" + SigV4.EmptyPayloadHash, r.CanonicalRequest);
        Assert.Equal("AWS4-HMAC-SHA256 Credential=AKIDEXAMPLE/20150830/us-east-1/service/aws4_request, SignedHeaders=host;x-amz-date, " +
                     "Signature=5fa00fa31553b73ebf1942676e86291e8372ff2a2260956d9b8aae1d763fbf31", r.Authorization);
    }

    [Fact]
    public void AwsTestSuitePostVanilla()
    {
        var r = SigV4.Sign("POST", new Uri("https://example.amazonaws.com/"), new Dictionary<string, string>(), SigV4.EmptyPayloadHash,
            "AKIDEXAMPLE", "wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY", "us-east-1", "service", Suite);
        Assert.EndsWith("Signature=5da7c1a2acd57cee7505fc6676e4e544621c30862966e37dddb68e92efbe5d6b", r.Authorization);
    }

    [Fact]
    public void AwsTestSuiteQueryParametersAreSorted()
    {
        var r = SigV4.Sign("GET", new Uri("https://example.amazonaws.com/?Param2=value2&Param1=value1"), new Dictionary<string, string>(), SigV4.EmptyPayloadHash,
            "AKIDEXAMPLE", "wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY", "us-east-1", "service", Suite);
        Assert.Contains("\nParam1=value1&Param2=value2\n", r.CanonicalRequest);
        Assert.EndsWith("Signature=b97d918cfa904a5beff61c982a1b6f458b799221646efd99d3219ec94cdf2500", r.Authorization);
    }

    // https://docs.aws.amazon.com/AmazonS3/latest/API/sig-v4-header-based-auth.html — "Example: GET Object"
    [Fact]
    public void S3DocumentationGetObjectWithRange()
    {
        var r = SigV4.Sign("GET", new Uri("https://examplebucket.s3.amazonaws.com/test.txt"), new Dictionary<string, string> { ["Range"] = "bytes=0-9" },
            SigV4.EmptyPayloadHash, "AKIAIOSFODNN7EXAMPLE", "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY", "us-east-1", "s3",
            new DateTimeOffset(2013, 5, 24, 0, 0, 0, TimeSpan.Zero), s3: true, includeContentSha256Header: true);
        Assert.Equal("host;range;x-amz-content-sha256;x-amz-date", r.SignedHeaders);
        Assert.EndsWith("Signature=f0e8bdb87c964420e857bd35b5d6ed310bd44f0170aba48dd91039c6036bdb41", r.Authorization);
    }

    // "Example: PUT Object" (object key with a $ that must be percent-encoded in the canonical URI)
    [Fact]
    public void S3DocumentationPutObject()
    {
        var payload = SigV4.HashPayload("Welcome to Amazon S3."u8);
        Assert.Equal("44ce7dd67c959e0d3524ffac1771dfbba87d2b6b4b4e99e42034a8b803f8b072", payload);
        var r = SigV4.Sign("PUT", new Uri("https://examplebucket.s3.amazonaws.com/test$file.text"),
            new Dictionary<string, string> { ["Date"] = "Fri, 24 May 2013 00:00:00 GMT", ["x-amz-storage-class"] = "REDUCED_REDUNDANCY" },
            payload, "AKIAIOSFODNN7EXAMPLE", "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY", "us-east-1", "s3",
            new DateTimeOffset(2013, 5, 24, 0, 0, 0, TimeSpan.Zero), s3: true, includeContentSha256Header: true);
        Assert.Contains("\n/test%24file.text\n", r.CanonicalRequest);
        Assert.EndsWith("Signature=98ad721746da40c64f1a55b78f14c238d841ea1380cd77a1b5971af0ece108bd", r.Authorization);
    }

    // "Example: Presigned URL" from the S3 query-string authentication documentation.
    [Fact]
    public void S3DocumentationPresignedUrl()
    {
        var url = SigV4.Presign("GET", new Uri("https://examplebucket.s3.amazonaws.com/test.txt"), "AKIAIOSFODNN7EXAMPLE",
            "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY", "us-east-1", "s3", new DateTimeOffset(2013, 5, 24, 0, 0, 0, TimeSpan.Zero), TimeSpan.FromSeconds(86400));
        Assert.Contains("X-Amz-Signature=aeeed9bbccd4d02ee5c0109b86d86835f995330da4c265957d157751f604d404", url.Query);
    }

    [Fact]
    public void SessionTokenIsSigned()
    {
        var r = SigV4.Sign("GET", new Uri("https://example.amazonaws.com/"), new Dictionary<string, string>(), SigV4.EmptyPayloadHash,
            "AK", "SK", "us-east-1", "service", Suite, sessionToken: "TOKEN");
        Assert.Contains("x-amz-security-token", r.SignedHeaders);
    }
}

public class S3DestinationTests
{
    [Fact]
    public void BuildsPathAndVirtualHostedUris()
    {
        var path = new S3Settings { Endpoint = "http://127.0.0.1:9000", Bucket = "bkt", PathStyle = true };
        Assert.Equal("http://127.0.0.1:9000/bkt/a/b%20c.png", S3Destination.ObjectUri(path, "a/b c.png").AbsoluteUri);
        var virt = new S3Settings { Endpoint = "s3.eu-west-1.amazonaws.com", Bucket = "bkt" };
        Assert.Equal("https://bkt.s3.eu-west-1.amazonaws.com/x.png", S3Destination.ObjectUri(virt, "x.png").AbsoluteUri);
    }

    [Fact]
    public void ExpandsPublicUrlTemplate()
    {
        var s = new S3Settings { Bucket = "bkt", Endpoint = "ep" };
        Assert.Equal("https://cdn.example.com/shots/a%20b.png?b=bkt", S3Destination.ExpandTemplate("https://cdn.example.com/{key}?b={bucket}", s, "shots/a b.png"));
    }

    [Fact]
    public async Task PutsSignedObjectAndReturnsTemplateLink()
    {
        using var dir = new TempDir();
        using var stub = new StubServer(_ => new StubResponse(200, ""));
        var creds = new InMemoryCredentialStore();
        creds.Write(CredentialKeys.Make("s3", "secret"), "SECRET");
        var ctx = Ctx.Make(dir, creds);
        ctx.Store.Save("s3", new S3Settings
        {
            Endpoint = stub.BaseUrl, Bucket = "bkt", PathStyle = true, AccessKeyId = "AKID", Region = "eu-central-1",
            KeyPrefix = "shots", AutoName = false, PublicUrlTemplate = "https://cdn.example.com/{key}", Acl = "public-read",
        });
        var file = dir.File("hello.png", "PNGDATA");
        var dest = new S3Destination(ctx);

        Assert.True(dest.IsConfigured);
        var result = await dest.ShareAsync(new[] { Ctx.PngFile(file) }, null, CancellationToken.None);

        Assert.Equal(ShareOutcome.Succeeded, result.Outcome);
        Assert.Equal("https://cdn.example.com/shots/hello.png", result.Link);
        var req = Assert.Single(stub.Snapshot());
        Assert.Equal("PUT", req.Method);
        Assert.Equal("/bkt/shots/hello.png", req.Path);
        Assert.Equal("PNGDATA", req.BodyText);
        Assert.Equal(SigV4.HashPayload("PNGDATA"u8), req.Headers["x-amz-content-sha256"]);
        Assert.Equal("public-read", req.Headers["x-amz-acl"]);
        Assert.StartsWith("AWS4-HMAC-SHA256 Credential=AKID/", req.Headers["Authorization"]);
        Assert.Contains("/eu-central-1/s3/aws4_request", req.Headers["Authorization"]);
    }

    [Fact]
    public async Task LargeFilesUseMultipartUpload()
    {
        using var dir = new TempDir();
        using var stub = new StubServer(r =>
        {
            if (r.Method == "POST" && r.Query.Contains("uploads")) return new StubResponse(200, "<InitiateMultipartUploadResult><UploadId>UP1</UploadId></InitiateMultipartUploadResult>", "application/xml");
            if (r.Method == "PUT") return new StubResponse(200, "", Headers: new() { ["ETag"] = "\"etag" + r.Query.Length + "\"" });
            return new StubResponse(200, "<CompleteMultipartUploadResult/>", "application/xml");
        });
        var creds = new InMemoryCredentialStore();
        creds.Write(CredentialKeys.Make("s3", "secret"), "SECRET");
        var ctx = Ctx.Make(dir, creds);
        ctx.Store.Save("s3", new S3Settings { Endpoint = stub.BaseUrl, Bucket = "b", PathStyle = true, AccessKeyId = "AK", AutoName = false, PresignHours = 0 });
        var big = dir.FileBytes("big.mp4", S3Destination.PartSize + 1024);

        var result = await new S3Destination(ctx).ShareAsync(new[] { new ShareFile(big, Shotwise.Core.Library.MediaKind.Video, "mp4", "v", 1, 1, TimeSpan.FromSeconds(1), false) }, null, CancellationToken.None);

        Assert.Equal(ShareOutcome.Succeeded, result.Outcome);
        var reqs = stub.Snapshot();
        Assert.Equal(new[] { "POST", "PUT", "PUT", "POST" }, reqs.Select(r => r.Method).ToArray());
        Assert.Contains("partNumber=1", reqs[1].Query);
        Assert.Contains("partNumber=2", reqs[2].Query);
        Assert.Contains("<PartNumber>2</PartNumber>", reqs[3].BodyText);
        Assert.Equal(1024, reqs[2].Body.Length);
    }

    [Fact]
    public async Task ServerErrorsSurfaceAsShotwiseExceptions()
    {
        using var dir = new TempDir();
        using var stub = new StubServer(_ => new StubResponse(403, "<Error><Code>AccessDenied</Code><Message>nope</Message></Error>", "application/xml"));
        var creds = new InMemoryCredentialStore();
        creds.Write(CredentialKeys.Make("s3", "secret"), "S");
        var ctx = Ctx.Make(dir, creds);
        ctx.Store.Save("s3", new S3Settings { Endpoint = stub.BaseUrl, Bucket = "b", PathStyle = true, AccessKeyId = "AK" });
        var ex = await Assert.ThrowsAsync<ShotwiseException>(() => new S3Destination(ctx).ShareAsync(new[] { Ctx.PngFile(dir.File("a.png")) }, null, CancellationToken.None));
        Assert.Contains("403", ex.Message);
    }
}
