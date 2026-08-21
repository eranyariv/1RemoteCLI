using System.Text.Json;
using OneRemoteCli.Protocol.Hub;

namespace OneRemoteCli.Daemon.Chat;

internal sealed record AcpElicitation(
    string SessionId,
    string? ToolCallId,
    string Message,
    string FieldName,
    string Title,
    ChatPermissionOption[] Options)
{
    public static AcpElicitation? Parse(JsonElement parameters)
    {
        if (String(parameters, "mode") != "form" ||
            String(parameters, "sessionId") is not { Length: > 0 } sessionId ||
            !parameters.TryGetProperty("requestedSchema", out JsonElement requestedSchema) ||
            !requestedSchema.TryGetProperty("properties", out JsonElement properties) ||
            properties.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        JsonProperty[] fields = [.. properties.EnumerateObject()];
        if (fields.Length != 1 ||
            fields[0].Value.ValueKind != JsonValueKind.Object ||
            String(fields[0].Value, "type") != "string")
        {
            return null;
        }

        JsonProperty field = fields[0];
        ChatPermissionOption[] options = Choices(field.Value);
        if (options.Length == 0)
        {
            return null;
        }

        return new AcpElicitation(
            sessionId,
            String(parameters, "toolCallId"),
            String(parameters, "message") ?? "The agent needs your input.",
            field.Name,
            String(field.Value, "title") ?? "Choose an answer",
            options);
    }

    private static ChatPermissionOption[] Choices(JsonElement field)
    {
        if (field.TryGetProperty("oneOf", out JsonElement oneOf) &&
            oneOf.ValueKind == JsonValueKind.Array)
        {
            return
            [
                .. oneOf.EnumerateArray()
                    .Select(choice => new
                    {
                        Value = String(choice, "const"),
                        Name = String(choice, "title"),
                    })
                    .Where(choice => !string.IsNullOrWhiteSpace(choice.Value))
                    .Select(choice => new ChatPermissionOption
                    {
                        OptionId = choice.Value!,
                        Name = choice.Name ?? choice.Value!,
                        Kind = "select",
                    }),
            ];
        }

        if (!field.TryGetProperty("enum", out JsonElement values) ||
            values.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return
        [
            .. values.EnumerateArray()
                .Where(value => value.ValueKind == JsonValueKind.String)
                .Select(value => value.GetString())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => new ChatPermissionOption
                {
                    OptionId = value!,
                    Name = value!,
                    Kind = "select",
                }),
        ];
    }

    private static string? String(JsonElement value, string property) =>
        value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(property, out JsonElement found) &&
        found.ValueKind == JsonValueKind.String
            ? found.GetString()
            : null;
}
