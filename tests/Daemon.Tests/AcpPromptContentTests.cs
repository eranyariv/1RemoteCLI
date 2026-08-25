using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OneRemoteCli.Daemon.Chat;
using OneRemoteCli.Protocol;
using OneRemoteCli.Protocol.Hub;

namespace OneRemoteCli.Daemon.Tests;

/// <summary>
/// Capability negotiation and the ACP prompt content it gates — the layer where a
/// browser-selected file becomes something an agent can actually be sent.
/// </summary>
public sealed class AcpPromptContentTests
{
    private static readonly AcpPromptCapabilities Both = new(Image: true, EmbeddedContext: true);

    [Fact]
    public void CapabilitiesAreReadStrictlyAndDefaultToNothing()
    {
        AcpPromptCapabilities parsed = AcpPromptCapabilities.Parse(Json(
            """
            {
              "protocolVersion": 1,
              "agentCapabilities": {
                "promptCapabilities": { "image": true, "embeddedContext": false }
              }
            }
            """));

        Assert.True(parsed.Image);
        Assert.False(parsed.EmbeddedContext);
        Assert.True(parsed.AllowsAttachments);

        // Truthy-looking values that are not booleans, a missing block, and a missing
        // result are all "no". Nothing here may be inferred.
        Assert.Equal(
            AcpPromptCapabilities.None,
            AcpPromptCapabilities.Parse(Json(
                """
                {
                  "agentCapabilities": {
                    "promptCapabilities": { "image": "yes", "embeddedContext": 1 }
                  }
                }
                """)));
        Assert.Equal(
            AcpPromptCapabilities.None,
            AcpPromptCapabilities.Parse(Json("""{ "agentCapabilities": {} }""")));
        Assert.Equal(AcpPromptCapabilities.None, AcpPromptCapabilities.Parse(Json("{}")));
        Assert.False(AcpPromptCapabilities.None.AllowsAttachments);
    }

    [Fact]
    public void ImagesBecomeAcpImageBlocksTypedFromTheirSignature()
    {
        JsonArray prompt = AcpPromptContent.Build(
            "what does this say?",
            [Attachment("receipt.jpg", "image/png", Jpeg())],
            Both);

        Assert.Equal(2, prompt.Count);
        Assert.Equal("text", prompt[0]!["type"]!.GetValue<string>());
        Assert.Equal("what does this say?", prompt[0]!["text"]!.GetValue<string>());

        JsonNode image = prompt[1]!;
        Assert.Equal("image", image["type"]!.GetValue<string>());

        // The browser said PNG and the bytes say JPEG. The signature wins: a declared
        // type is only the operating system's guess about an extension.
        Assert.Equal("image/jpeg", image["mimeType"]!.GetValue<string>());
        Assert.Equal(Convert.ToBase64String(Jpeg()), image["data"]!.GetValue<string>());
        Assert.StartsWith(
            "attachment://1remotecli/",
            image["uri"]!.GetValue<string>(),
            StringComparison.Ordinal);
        Assert.Null(image["resource"]);
    }

    [Theory]
    [InlineData("image/png")]
    [InlineData("image/gif")]
    [InlineData("image/webp")]
    public void EveryAdvertisedImageTypeIsRecognisedFromItsBytes(string mediaType)
    {
        byte[] bytes = mediaType switch
        {
            "image/png" => Png(),
            "image/gif" => Encoding.ASCII.GetBytes("GIF89a......"),
            _ => WebP(),
        };

        Assert.Equal(mediaType, AcpPromptContent.DetectImageMediaType(bytes));
    }

    [Fact]
    public void TextFilesBecomeEmbeddedResourcesWithASyntheticNonFileUri()
    {
        var attachment = Attachment("notes.md", "text/markdown", Encoding.UTF8.GetBytes("# Title\nbody"));
        JsonArray prompt = AcpPromptContent.Build(string.Empty, [attachment], Both);

        JsonNode block = Assert.Single(prompt)!;
        Assert.Equal("resource", block["type"]!.GetValue<string>());

        JsonNode resource = block["resource"]!;
        Assert.Equal("text/markdown", resource["mimeType"]!.GetValue<string>());
        Assert.Equal("# Title\nbody", resource["text"]!.GetValue<string>());
        Assert.Null(resource["blob"]);

        string uri = resource["uri"]!.GetValue<string>();
        Assert.StartsWith("attachment://1remotecli/", uri, StringComparison.Ordinal);
        Assert.Contains(attachment.AttachmentId, uri, StringComparison.Ordinal);
        Assert.Contains("notes.md", uri, StringComparison.Ordinal);
        Assert.DoesNotContain("file:", uri, StringComparison.OrdinalIgnoreCase);
        Assert.True(Uri.TryCreate(uri, UriKind.Absolute, out _));
    }

    [Fact]
    public void BinaryFilesBecomeBlobResourcesRatherThanMojibake()
    {
        byte[] bytes = [0x00, 0x01, 0xff, 0xfe, 0x42];
        JsonArray prompt = AcpPromptContent.Build(
            string.Empty,
            [Attachment("archive.zip", "application/zip", bytes)],
            Both);

        JsonNode resource = Assert.Single(prompt)!["resource"]!;
        Assert.Equal("application/zip", resource["mimeType"]!.GetValue<string>());
        Assert.Equal(Convert.ToBase64String(bytes), resource["blob"]!.GetValue<string>());
        Assert.Null(resource["text"]);
    }

    [Fact]
    public void AFileClaimingAnUnknownTypeStillTravels()
    {
        JsonArray prompt = AcpPromptContent.Build(
            string.Empty,
            [Attachment("payload.unknown", string.Empty, [0x00, 0x99])],
            Both);

        JsonNode resource = Assert.Single(prompt)!["resource"]!;
        Assert.Equal("application/octet-stream", resource["mimeType"]!.GetValue<string>());
        Assert.NotNull(resource["blob"]);
    }

    [Fact]
    public void AttachmentsAreOrderedAfterTheTextAndKeepTheirSelectionOrder()
    {
        JsonArray prompt = AcpPromptContent.Build(
            "look",
            [
                Attachment("first.png", "image/png", Png()),
                Attachment("second.txt", "text/plain", "hello"u8.ToArray()),
            ],
            Both);

        Assert.Equal(3, prompt.Count);
        Assert.Equal("text", prompt[0]!["type"]!.GetValue<string>());
        Assert.Equal("image", prompt[1]!["type"]!.GetValue<string>());
        Assert.Equal("resource", prompt[2]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void CapabilitiesAreRequiredRatherThanAssumed()
    {
        AcpPromptException image = Assert.Throws<AcpPromptException>(() => AcpPromptContent.Build(
            "look",
            [Attachment("photo.png", "image/png", Png())],
            new AcpPromptCapabilities(Image: false, EmbeddedContext: true)));
        Assert.Equal(ErrorCodes.AttachmentUnsupported, image.Code);

        AcpPromptException resource = Assert.Throws<AcpPromptException>(() => AcpPromptContent.Build(
            "look",
            [Attachment("notes.txt", "text/plain", "hi"u8.ToArray())],
            new AcpPromptCapabilities(Image: true, EmbeddedContext: false)));
        Assert.Equal(ErrorCodes.AttachmentUnsupported, resource.Code);
    }

    [Fact]
    public void AFileThatClaimsToBeAnImageAndIsNotIsRefused()
    {
        AcpPromptException refused = Assert.Throws<AcpPromptException>(() => AcpPromptContent.Build(
            string.Empty,
            [Attachment("fake.png", "image/png", "not a picture"u8.ToArray())],
            Both));

        Assert.Equal(ErrorCodes.AttachmentUnsupported, refused.Code);
        Assert.Contains("fake.png", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyPromptsAreRefusedButAttachmentOnlyPromptsAreNot()
    {
        Assert.Equal(
            ErrorCodes.InvalidRequest,
            Assert.Throws<AcpPromptException>(() => AcpPromptContent.Build(string.Empty, [], Both)).Code);

        JsonArray attachmentOnly = AcpPromptContent.Build(
            string.Empty,
            [Attachment("photo.png", "image/png", Png())],
            Both);

        Assert.Single(attachmentOnly);
        Assert.Equal("image", attachmentOnly[0]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void CountAggregateAndTextLimitsAreEnforcedHereToo()
    {
        ChatAttachmentContent[] tooMany =
        [
            .. Enumerable
                .Range(0, ChatAttachmentLimits.MaxAttachmentCount + 1)
                .Select(index => Attachment($"file-{index}.txt", "text/plain", "x"u8.ToArray())),
        ];
        Assert.Equal(
            ErrorCodes.AttachmentBudgetExceeded,
            Assert.Throws<AcpPromptException>(() => AcpPromptContent.Build("x", tooMany, Both)).Code);

        ChatAttachmentContent[] tooBig =
        [
            .. Enumerable
                .Range(0, 3)
                .Select(index => Attachment(
                    $"photo-{index}.bin",
                    "application/octet-stream",
                    new byte[ChatAttachmentLimits.MaxAttachmentBytes])),
        ];
        Assert.Equal(
            ErrorCodes.AttachmentBudgetExceeded,
            Assert.Throws<AcpPromptException>(() => AcpPromptContent.Build("x", tooBig, Both)).Code);

        Assert.Equal(
            ErrorCodes.InvalidRequest,
            Assert.Throws<AcpPromptException>(() => AcpPromptContent.Build(
                new string('x', ChatAttachmentLimits.MaxPromptTextChars + 1),
                [],
                Both)).Code);
    }

    [Fact]
    public void TheTranscriptSummaryCarriesMetadataAndNeverBytes()
    {
        byte[] bytes = Png();
        ChatContentBlock summary = Assert.Single(
            AcpPromptContent.Summarize([Attachment("../my photo.png", "image/png", bytes)]));

        Assert.Equal("resource_link", summary.Type);
        Assert.Equal("my photo.png", summary.Name);
        Assert.Equal("image/png", summary.MimeType);
        Assert.Equal(bytes.Length, summary.Size);
        Assert.Null(summary.Text);
        Assert.Null(summary.Data);
        Assert.DoesNotContain(
            Convert.ToBase64String(bytes),
            summary.Uri ?? string.Empty,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ABase64PromptStaysBelowWhatTheAggregateLimitPromises()
    {
        // The reason MaxPromptBytes is not simply the per-file limit times the count:
        // everything in an ACP prompt is Base64 by the time it reaches the agent.
        long inflated = (ChatAttachmentLimits.MaxPromptBytes + 2) / 3 * 4;

        Assert.True(inflated < 15 * 1024 * 1024);
        Assert.True(inflated > ChatAttachmentLimits.MaxPromptBytes);
    }

    private static ChatAttachmentContent Attachment(string name, string mediaType, byte[] bytes) =>
        new(Guid.NewGuid().ToString(), name, mediaType, bytes);

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    private static byte[] Png() => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x01];

    private static byte[] Jpeg() => [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10];

    private static byte[] WebP() =>
    [
        .. "RIFF"u8.ToArray(),
        0x00, 0x00, 0x00, 0x00,
        .. "WEBP"u8.ToArray(),
    ];
}
