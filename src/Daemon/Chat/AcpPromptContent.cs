using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OneRemoteCli.Protocol;
using OneRemoteCli.Protocol.Hub;

namespace OneRemoteCli.Daemon.Chat;

/// <summary>
/// What an ACP agent said it accepts in a prompt, read once per ACP process from
/// <c>initialize</c>.
/// <para>
/// Strict on purpose: a missing, non-boolean, or misspelled capability is
/// <see langword="false"/>. Inferring support from an agent's name or version would
/// let a phone offer a camera button whose photo the agent rejects after it was
/// taken, which is worse than never offering it.
/// </para>
/// </summary>
public sealed record AcpPromptCapabilities(bool Image, bool EmbeddedContext)
{
    public static readonly AcpPromptCapabilities None = new(false, false);

    public bool AllowsAttachments => Image || EmbeddedContext;

    /// <summary>Reads <c>agentCapabilities.promptCapabilities</c> out of an initialize result.</summary>
    public static AcpPromptCapabilities Parse(JsonElement initialized)
    {
        if (initialized.ValueKind != JsonValueKind.Object ||
            !initialized.TryGetProperty("agentCapabilities", out JsonElement agent) ||
            agent.ValueKind != JsonValueKind.Object ||
            !agent.TryGetProperty("promptCapabilities", out JsonElement prompt) ||
            prompt.ValueKind != JsonValueKind.Object)
        {
            return None;
        }

        return new AcpPromptCapabilities(Flag(prompt, "image"), Flag(prompt, "embeddedContext"));
    }

    public ChatCapabilities ToChatCapabilities() =>
        new() { Image = Image, EmbeddedContext = EmbeddedContext };

    private static bool Flag(JsonElement value, string property) =>
        value.TryGetProperty(property, out JsonElement found) &&
        found.ValueKind == JsonValueKind.True;
}

/// <summary>
/// A prompt that cannot be built, carrying the stable code the phone needs to explain
/// itself. Distinct from a transport failure: nothing was sent, and the user's
/// selection is still worth keeping.
/// </summary>
public sealed class AcpPromptException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// Turns optional text and staged attachments into the ordered ACP
/// <c>session/prompt</c> content array, and into the metadata-only summary the local
/// transcript echoes.
/// <para>
/// The agent's declared type is never trusted for an image: the bytes have to carry
/// the signature of one of the four types ACP clients can actually render. Everything
/// else becomes an embedded resource under a synthetic <c>attachment:</c> URI —
/// deliberately not a <c>file:</c> one, because the file is in a browser on a phone
/// and inventing a path for it would be a lie the agent could act on.
/// </para>
/// </summary>
internal static class AcpPromptContent
{
    private const string UriScheme = "attachment";
    private const string UriAuthority = "1remotecli";

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    /// <summary>
    /// Builds the prompt array: the text block first when there is text, then the
    /// attachments in the order the user selected them.
    /// </summary>
    public static JsonArray Build(
        string text,
        IReadOnlyList<ChatAttachmentContent> attachments,
        AcpPromptCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(attachments);
        ArgumentNullException.ThrowIfNull(capabilities);

        if (text.Length == 0 && attachments.Count == 0)
        {
            throw new AcpPromptException(
                ErrorCodes.InvalidRequest,
                "A prompt needs text, an attachment, or both.");
        }

        if (text.Length > ChatAttachmentLimits.MaxPromptTextChars)
        {
            throw new AcpPromptException(
                ErrorCodes.InvalidRequest,
                $"A chat message is limited to {ChatAttachmentLimits.MaxPromptTextChars:N0} characters.");
        }

        if (attachments.Count > ChatAttachmentLimits.MaxAttachmentCount)
        {
            throw new AcpPromptException(
                ErrorCodes.AttachmentBudgetExceeded,
                $"A prompt carries at most {ChatAttachmentLimits.MaxAttachmentCount} attachments.");
        }

        long total = attachments.Sum(item => item.Bytes.LongLength);
        if (total > ChatAttachmentLimits.MaxPromptBytes)
        {
            throw new AcpPromptException(
                ErrorCodes.AttachmentBudgetExceeded,
                $"All attachments on one prompt are limited to {ChatAttachmentLimits.MaxPromptBytes / (1024 * 1024)} MB.");
        }

        var prompt = new JsonArray();
        if (text.Length > 0)
        {
            prompt.Add((JsonNode)new JsonObject
            {
                ["type"] = "text",
                ["text"] = text,
            });
        }

        foreach (ChatAttachmentContent attachment in attachments)
        {
            prompt.Add((JsonNode)BuildBlock(attachment, capabilities));
        }

        return prompt;
    }

    /// <summary>
    /// The transcript's own record of what was attached: name, type, and size, and
    /// nothing that could carry a byte of the file itself.
    /// </summary>
    public static ChatContentBlock[] Summarize(IReadOnlyList<ChatAttachmentContent> attachments)
    {
        ArgumentNullException.ThrowIfNull(attachments);

        return
        [
            .. attachments.Select(attachment => new ChatContentBlock
            {
                // An older cached PWA renders `resource_link` as a labelled row, so
                // the bubble still says what was sent rather than showing nothing.
                Type = "resource_link",
                Name = SafeTempFiles.SanitizeFileName(attachment.FileName),
                MimeType = ResolveMediaType(attachment),
                Size = attachment.Bytes.LongLength,
                Uri = SyntheticUri(attachment).ToString(),
            }),
        ];
    }

    private static JsonObject BuildBlock(
        ChatAttachmentContent attachment,
        AcpPromptCapabilities capabilities)
    {
        if (attachment.Bytes.LongLength == 0)
        {
            throw new AcpPromptException(
                ErrorCodes.AttachmentFailed,
                $"{Describe(attachment)} is empty.");
        }

        if (attachment.Bytes.LongLength > ChatAttachmentLimits.MaxAttachmentBytes)
        {
            throw new AcpPromptException(
                ErrorCodes.AttachmentTooLarge,
                $"{Describe(attachment)} is larger than {ChatAttachmentLimits.MaxAttachmentBytes / (1024 * 1024)} MB.");
        }

        string? signature = DetectImageMediaType(attachment.Bytes);

        if (signature is not null)
        {
            if (!capabilities.Image)
            {
                throw new AcpPromptException(
                    ErrorCodes.AttachmentUnsupported,
                    "This agent does not accept images.");
            }

            return new JsonObject
            {
                ["type"] = "image",
                ["mimeType"] = signature,
                ["data"] = Convert.ToBase64String(attachment.Bytes),
                ["uri"] = SyntheticUri(attachment).ToString(),
            };
        }

        if (ChatAttachmentPolicy.IsImageMediaType(attachment.DeclaredMediaType))
        {
            // Claimed to be a picture and is not one of the four an ACP image block
            // may carry. Sending it as a resource instead would hand the agent bytes
            // under a type nothing verified.
            throw new AcpPromptException(
                ErrorCodes.AttachmentUnsupported,
                $"{Describe(attachment)} is not a PNG, JPEG, WebP, or GIF image.");
        }

        if (!capabilities.EmbeddedContext)
        {
            throw new AcpPromptException(
                ErrorCodes.AttachmentUnsupported,
                "This agent does not accept file attachments.");
        }

        var resource = new JsonObject
        {
            ["uri"] = SyntheticUri(attachment).ToString(),
        };

        string mediaType = ResolveMediaType(attachment);
        if (TryReadText(attachment.Bytes, out string? content))
        {
            resource["mimeType"] = mediaType == ChatAttachmentPolicy.OctetStream
                ? "text/plain"
                : mediaType;
            resource["text"] = content;
        }
        else
        {
            resource["mimeType"] = mediaType;
            resource["blob"] = Convert.ToBase64String(attachment.Bytes);
        }

        return new JsonObject
        {
            ["type"] = "resource",
            ["resource"] = resource,
        };
    }

    /// <summary>
    /// The type an embedded resource travels under. An image type is never used here:
    /// by this point the bytes have already failed image detection, so claiming one
    /// would be repeating the browser's wrong guess.
    /// </summary>
    private static string ResolveMediaType(ChatAttachmentContent attachment)
    {
        if (DetectImageMediaType(attachment.Bytes) is { } signature)
        {
            return signature;
        }

        string resolved = ChatAttachmentPolicy.ResolveDocumentMediaType(
            attachment.FileName,
            attachment.DeclaredMediaType);

        return ChatAttachmentPolicy.IsImageMediaType(resolved)
            ? ChatAttachmentPolicy.OctetStream
            : resolved;
    }

    /// <summary>
    /// A URI that names the attachment without pointing anywhere. Absolute, so an
    /// agent that parses it gets a URI rather than an error, and unmistakably not a
    /// path: nothing on this machine or any other can be opened through it.
    /// </summary>
    private static Uri SyntheticUri(ChatAttachmentContent attachment)
    {
        string name = Uri.EscapeDataString(SafeTempFiles.SanitizeFileName(attachment.FileName));
        string id = Uri.EscapeDataString(attachment.AttachmentId);

        return new Uri($"{UriScheme}://{UriAuthority}/{id}/{name}", UriKind.Absolute);
    }

    /// <summary>Signature detection, because a declared type is only the sender's opinion.</summary>
    internal static string? DetectImageMediaType(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 8 &&
            bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47 &&
            bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A)
        {
            return "image/png";
        }

        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            return "image/jpeg";
        }

        if (bytes.Length >= 6 &&
            bytes[0] == (byte)'G' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' &&
            bytes[3] == (byte)'8' && (bytes[4] == (byte)'7' || bytes[4] == (byte)'9') &&
            bytes[5] == (byte)'a')
        {
            return "image/gif";
        }

        if (bytes.Length >= 12 &&
            bytes[0] == (byte)'R' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'F' &&
            bytes[8] == (byte)'W' && bytes[9] == (byte)'E' && bytes[10] == (byte)'B' && bytes[11] == (byte)'P')
        {
            return "image/webp";
        }

        return null;
    }

    /// <summary>
    /// Whether these bytes are text worth sending as text. Strict UTF-8, and no NUL:
    /// a file that decodes only because the decoder substitutes replacement characters
    /// would reach the agent as mojibake, which is less useful than an honest blob.
    /// </summary>
    internal static bool TryReadText(byte[] bytes, out string? text)
    {
        text = null;

        if (bytes.AsSpan().IndexOf((byte)0) >= 0)
        {
            return false;
        }

        try
        {
            ReadOnlySpan<byte> content = bytes.Length >= 3 &&
                bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF
                    ? bytes.AsSpan(3)
                    : bytes;

            text = StrictUtf8.GetString(content);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    /// <summary>Names the file, never its contents — these strings reach the phone.</summary>
    private static string Describe(ChatAttachmentContent attachment) =>
        SafeTempFiles.SanitizeFileName(attachment.FileName);
}
