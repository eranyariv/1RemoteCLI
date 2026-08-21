using System.Text.Json;
using OneRemoteCli.Daemon.Chat;

namespace OneRemoteCli.Daemon.Tests;

public sealed class AcpElicitationTests
{
    [Fact]
    public void ParsesOneOfChoicesWithDisplayTitles()
    {
        JsonElement parameters = JsonSerializer.SerializeToElement(new
        {
            sessionId = "session-1",
            toolCallId = "ask-user-1",
            mode = "form",
            message = "Which database?",
            requestedSchema = new
            {
                type = "object",
                properties = new
                {
                    database = new
                    {
                        type = "string",
                        title = "Database",
                        oneOf = new object[]
                        {
                            new { @const = "postgres", title = "PostgreSQL" },
                            new { @const = "sqlite", title = "SQLite" },
                        },
                    },
                },
            },
        });

        AcpElicitation elicitation = Assert.IsType<AcpElicitation>(
            AcpElicitation.Parse(parameters));

        Assert.Equal("session-1", elicitation.SessionId);
        Assert.Equal("ask-user-1", elicitation.ToolCallId);
        Assert.Equal("database", elicitation.FieldName);
        Assert.Collection(
            elicitation.Options,
            option =>
            {
                Assert.Equal("postgres", option.OptionId);
                Assert.Equal("PostgreSQL", option.Name);
            },
            option =>
            {
                Assert.Equal("sqlite", option.OptionId);
                Assert.Equal("SQLite", option.Name);
            });
    }

    [Fact]
    public void RejectsAStringWithoutMenuChoices()
    {
        JsonElement parameters = JsonSerializer.SerializeToElement(new
        {
            sessionId = "session-1",
            mode = "form",
            message = "Name the function",
            requestedSchema = new
            {
                type = "object",
                properties = new
                {
                    name = new { type = "string", title = "Function name" },
                },
            },
        });

        Assert.Null(AcpElicitation.Parse(parameters));
    }

    [Fact]
    public void RejectsFormsWithMultipleFields()
    {
        JsonElement parameters = JsonSerializer.SerializeToElement(new
        {
            sessionId = "session-1",
            mode = "form",
            requestedSchema = new
            {
                type = "object",
                properties = new
                {
                    first = new { type = "string" },
                    second = new { type = "string" },
                },
            },
        });

        Assert.Null(AcpElicitation.Parse(parameters));
    }
}
