namespace OneRemoteCli.Protocol;

/// <summary>
/// What an agent-chat attachment is allowed to be, shared so the browser, the hub,
/// and the agent refuse the same things.
/// <para>
/// The browser's declared media type is a hint and nothing more — it comes from the
/// operating system's guess about a file extension. The agent re-derives the type
/// from the file's own bytes and this table before anything is put into an ACP
/// prompt; the hub uses the same table only to refuse an obviously wrong request
/// before it relays a single chunk.
/// </para>
/// </summary>
public static class ChatAttachmentPolicy
{
    /// <summary>Fallback for anything whose type cannot be established.</summary>
    public const string OctetStream = "application/octet-stream";

    /// <summary>
    /// Image types an ACP <c>image</c> content block may carry.
    /// <para>
    /// These four cover every camera and photo library the PWA can reach and are all
    /// identifiable from an unambiguous byte signature. AVIF is deliberately absent:
    /// it shares the ISO-BMFF <c>ftyp</c> header with video containers, so accepting
    /// it would mean trusting a brand string rather than a signature.
    /// </para>
    /// </summary>
    public static readonly IReadOnlySet<string> ImageMediaTypes =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "image/png",
            "image/jpeg",
            "image/webp",
            "image/gif",
        };

    /// <summary>Lower-cased, parameters removed. An empty or malformed type becomes an empty string.</summary>
    public static string Normalize(string? mediaType)
    {
        if (string.IsNullOrWhiteSpace(mediaType))
        {
            return string.Empty;
        }

        string value = mediaType;
        int parameters = value.IndexOf(';', StringComparison.Ordinal);
        if (parameters >= 0)
        {
            value = value[..parameters];
        }

        value = value.Trim().ToLowerInvariant();
        return IsWellFormed(value) ? value : string.Empty;
    }

    /// <summary><c>type/subtype</c> of RFC 2045 token characters, and nothing else.</summary>
    public static bool IsWellFormed(string mediaType)
    {
        if (string.IsNullOrEmpty(mediaType) || mediaType.Length > ChatAttachmentLimits.MaxMimeTypeChars)
        {
            return false;
        }

        int slash = mediaType.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0 || slash == mediaType.Length - 1 ||
            mediaType.IndexOf('/', slash + 1) >= 0)
        {
            return false;
        }

        foreach (char character in mediaType)
        {
            if (character != '/' && !IsTokenCharacter(character))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Whether the browser is claiming this is a picture, supported or not.</summary>
    public static bool IsImageMediaType(string? mediaType) =>
        Normalize(mediaType).StartsWith("image/", StringComparison.Ordinal);

    public static bool IsSupportedImageMediaType(string? mediaType) =>
        ImageMediaTypes.Contains(Normalize(mediaType));

    /// <summary>
    /// Whether a resource of this type is worth trying to send as ACP <c>text</c>.
    /// The content still has to decode as UTF-8; this only says the type is one where
    /// text would be the useful representation.
    /// </summary>
    public static bool IsTextMediaType(string? mediaType)
    {
        string value = Normalize(mediaType);

        return value.StartsWith("text/", StringComparison.Ordinal) ||
            value.EndsWith("+json", StringComparison.Ordinal) ||
            value.EndsWith("+xml", StringComparison.Ordinal) ||
            value is
                "application/json" or
                "application/xml" or
                "application/javascript" or
                "application/typescript" or
                "application/x-sh" or
                "application/x-yaml" or
                "application/yaml" or
                "application/toml" or
                "application/sql" or
                "application/x-httpd-php";
    }

    /// <summary>
    /// The type an embedded resource is sent as: the file's own extension first,
    /// the browser's guess only when it is well formed and not claiming to be an
    /// image, and <see cref="OctetStream"/> when neither says anything useful.
    /// </summary>
    public static string ResolveDocumentMediaType(string? fileName, string? declaredMediaType)
    {
        string extension = ExtensionOf(fileName);
        if (extension.Length > 0 && Extensions.TryGetValue(extension, out string? known))
        {
            return known;
        }

        string declared = Normalize(declaredMediaType);
        return declared.Length > 0 && !declared.StartsWith("image/", StringComparison.Ordinal)
            ? declared
            : OctetStream;
    }

    private static string ExtensionOf(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return string.Empty;
        }

        int dot = fileName.LastIndexOf('.');
        return dot < 0 || dot == fileName.Length - 1
            ? string.Empty
            : fileName[(dot + 1)..].ToLowerInvariant();
    }

    private static bool IsTokenCharacter(char character) =>
        char.IsAsciiLetterOrDigit(character) ||
        character is '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-' or '.' or '^' or '_' or '`' or '|' or '~';

    /// <summary>
    /// Extensions worth naming. Everything absent from this table still travels — as
    /// <see cref="OctetStream"/> — so the table decides presentation, never permission.
    /// </summary>
    private static readonly Dictionary<string, string> Extensions = new(StringComparer.Ordinal)
    {
        ["txt"] = "text/plain",
        ["log"] = "text/plain",
        ["md"] = "text/markdown",
        ["csv"] = "text/csv",
        ["json"] = "application/json",
        ["xml"] = "application/xml",
        ["html"] = "text/html",
        ["htm"] = "text/html",
        ["css"] = "text/css",
        ["js"] = "text/javascript",
        ["mjs"] = "text/javascript",
        ["ts"] = "text/plain",
        ["tsx"] = "text/plain",
        ["jsx"] = "text/plain",
        ["cs"] = "text/plain",
        ["py"] = "text/x-python",
        ["ps1"] = "text/plain",
        ["sh"] = "application/x-sh",
        ["yml"] = "application/yaml",
        ["yaml"] = "application/yaml",
        ["toml"] = "application/toml",
        ["ini"] = "text/plain",
        ["sql"] = "application/sql",
        ["patch"] = "text/x-diff",
        ["diff"] = "text/x-diff",
        ["pdf"] = "application/pdf",
        ["zip"] = "application/zip",
        ["png"] = "image/png",
        ["jpg"] = "image/jpeg",
        ["jpeg"] = "image/jpeg",
        ["webp"] = "image/webp",
        ["gif"] = "image/gif",
        ["svg"] = "image/svg+xml",
    };
}
