using System.Text.Json;
using System.Text.Json.Nodes;
using MessagePack;
using Microsoft.AspNetCore.SignalR;
using OneRemoteCli.Protocol;
using OneRemoteCli.Protocol.Hub;

namespace OneRemoteCli.Protocol.Tests;

/// <summary>
/// Pins the bytes the hub puts on the wire, so the PWA's hand-written decoder is
/// checked against the real serializer rather than against someone's memory of it.
/// <para>
/// This matters more than it looks. <c>[MessagePackObject]</c> with <c>[Key(n)]</c>
/// serialises as a positional <b>array</b>, not a map, so every field the browser
/// reads is identified by an integer that appears nowhere in the payload. Adding a
/// property in the middle of a C# message, or reordering two, silently shifts every
/// later field on the JavaScript side — no exception, no type error, just a machine
/// list where the OS column shows a version number.
/// </para>
/// <para>
/// So this test writes a fixture of real bytes plus the values they decode to, and
/// <c>src/PWA/src/protocol/wire.contract.test.ts</c> decodes the same file. Neither
/// side hand-copies the other's constants: if the contract moves, the C# test fails
/// on the bytes and the TypeScript test fails on the values.
/// </para>
/// </summary>
public sealed class WireContractTests
{
    /// <summary>
    /// Set to 1 to rewrite the fixture after a deliberate protocol change. The
    /// resulting diff is the review: it shows exactly what the browser will now see.
    /// </summary>
    private const string UpdateVariable = "UPDATE_WIRE_FIXTURE";

    /// <summary>
    /// A fixed instant with a non-zero offset. Both parts matter: MessagePack writes
    /// a <see cref="DateTimeOffset"/> as a two-element array of wall-clock time and
    /// offset minutes, and a UTC-only sample would not catch a decoder that ignores
    /// the second element.
    /// </summary>
    private static readonly DateTimeOffset Instant =
        new(2024, 5, 17, 9, 30, 15, TimeSpan.FromHours(3));

    /// <summary>Exactly what SignalR uses, rather than something that resembles it.</summary>
    private static readonly MessagePackSerializerOptions Options =
        new MessagePackHubProtocolOptions().SerializerOptions;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [Fact]
    public void TheFixtureMatchesWhatTheHubActuallySends()
    {
        JsonObject expected = BuildFixture();
        string rendered = expected.ToJsonString(Json) + Environment.NewLine;

        if (Environment.GetEnvironmentVariable(UpdateVariable) == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FixturePath)!);
            File.WriteAllText(FixturePath, rendered);
            return;
        }

        Assert.True(
            File.Exists(FixturePath),
            $"The wire fixture is missing. Re-run with {UpdateVariable}=1 to create it.");

        Assert.True(
            Normalise(File.ReadAllText(FixturePath)) == Normalise(rendered),
            $"""
             The bytes the hub sends no longer match {FixturePath}.

             If you changed a message on purpose, re-run with {UpdateVariable}=1 and
             read the diff: every moved field is a field the PWA decoder now reads
             from the wrong position.
             """);
    }

    [Fact]
    public void EveryMessageTheClientReceivesIsCovered()
    {
        // A message with no fixture entry is a message whose layout nothing checks,
        // which is the failure mode this file exists to prevent.
        string[] covered = [.. BuildFixture()["messages"]!.AsObject().Select(m => m.Key)];

        Assert.Contains("machineList", covered);
        Assert.Contains("machineOnline", covered);
        Assert.Contains("machineOffline", covered);
        Assert.Contains("sessionOpened", covered);
        Assert.Contains("sessionUpdated", covered);
        Assert.Contains("sessionClosed", covered);
        Assert.Contains("terminalOutput", covered);
        Assert.Contains("sessionAwaitingInput", covered);
        Assert.Contains("tokenExpiring", covered);
        Assert.Contains("error", covered);
        Assert.Contains("projectList", covered);
        Assert.Contains("projectResult", covered);
        Assert.Contains("projectCreated", covered);
        Assert.Contains("projectUpdated", covered);
        Assert.Contains("projectDeleted", covered);
    }

    // The fixture.

    private static JsonObject BuildFixture()
    {
        var messages = new JsonObject();

        Add(messages, "machineList", new MachineListNotification
        {
            Machines =
            [
                new MachineInfo
                {
                    MachineId = "5d41402abc4b2a76b9719d911017c592",
                    DisplayName = "Desk",
                    Os = "Microsoft Windows 10.0.26100",
                    AgentVersion = "1.2.3.4",
                    Online = true,
                    Sessions =
                    [
                        new SessionInfo
                        {
                            SessionId = "aab0f1e2c3d4",
                            Program = "claude",
                            Args = ["--resume", "last"],
                            Cwd = @"C:\Projects\1RemoteCLI",
                            Cols = 120,
                            Rows = 30,
                            StartedAt = Instant,
                            DisplayName = "Claude Code",
                            AwaitingInput = true,
                            CliType = CliType.ClaudeCode,
                        },
                    ],
                },
                new MachineInfo
                {
                    MachineId = "0cc175b9c0f1b6a831c399e269772661",
                    DisplayName = "Laptop",
                    Os = "Microsoft Windows 10.0.22631",
                    AgentVersion = "1.2.3.4",
                    Online = false,
                    Sessions = [],
                },
            ],
        });

        Add(messages, "machineOnline", new MachineOnlineNotification
        {
            Machine = new MachineInfo
            {
                MachineId = "5d41402abc4b2a76b9719d911017c592",
                DisplayName = "Desk",
                Os = "Microsoft Windows 10.0.26100",
                AgentVersion = "1.2.3.4",
                Online = true,
                Sessions = [],
            },
        });

        Add(messages, "machineOffline", new MachineOfflineNotification
        {
            MachineId = "5d41402abc4b2a76b9719d911017c592",
        });

        Add(messages, "sessionOpened", new ClientSessionOpenedNotification
        {
            MachineId = "5d41402abc4b2a76b9719d911017c592",
            Session = new SessionInfo
            {
                SessionId = "ff00ff00",
                Program = "pwsh",
                Args = [],
                Cwd = @"C:\Users\eran",
                Cols = 80,
                Rows = 24,
                StartedAt = Instant,
                DisplayName = null,
                AwaitingInput = false,
                CliType = CliType.PowerShell,
            },
        });

        // The correction, which is the same shape as the open and deliberately not
        // the same message: an open counts towards usage, and being told twice what
        // a session is should not look like having started it twice.
        //
        // Also the message that carries a rename, which is why this one is the copy
        // with the label fields set: the appended pair has to be pinned by a fixture
        // somewhere, and this is the message a rename actually travels in.
        Add(messages, "sessionUpdated", new ClientSessionUpdatedNotification
        {
            MachineId = "5d41402abc4b2a76b9719d911017c592",
            Session = new SessionInfo
            {
                SessionId = "ff00ff00",
                Program = "pwsh",
                Args = [],
                Cwd = @"C:\Users\eran",
                Cols = 80,
                Rows = 24,
                StartedAt = Instant,
                DisplayName = null,
                AwaitingInput = false,
                CliType = CliType.CopilotCli,
                CustomName = "The deploy",
                Pinned = true,
            },
        });

        Add(messages, "sessionClosed", new ClientSessionClosedNotification
        {
            MachineId = "5d41402abc4b2a76b9719d911017c592",
            SessionId = "ff00ff00",
            ExitCode = 130,
        });

        Add(messages, "terminalOutput", new TerminalOutputNotification
        {
            SessionId = "ff00ff00",
            Seq = 4294967297,
            Kind = TerminalOutputKind.Snapshot,
            Data = [0x1b, 0x5b, 0x32, 0x4a, 0x68, 0x69, 0x0d, 0x0a],
        });

        Add(messages, "sessionAwaitingInput", new ClientSessionAwaitingInputNotification
        {
            MachineId = "5d41402abc4b2a76b9719d911017c592",
            SessionId = "ff00ff00",
            Hint = "Do you want to proceed?",
        });

        Add(messages, "tokenExpiring", new TokenExpiringNotification { ExpiresAt = Instant });

        Add(messages, "error", new ErrorNotification
        {
            Code = ErrorCodes.AccountNotAllowed,
            Message = "Ask an administrator to add this account.",
            SessionId = null,
        });

        // Client-to-hub shapes, so the encoder is pinned too. A request the hub
        // cannot deserialise fails at the far end, where the browser sees only a
        // generic invocation error.
        Add(messages, "clientHandshakeRequest", new ClientHandshakeRequest
        {
            ProtocolVersion = ProtocolVersion.Current,
            ClientVersion = "pwa/0.1.0",
        });

        Add(messages, "attachSessionRequest", new AttachSessionRequest
        {
            MachineId = "5d41402abc4b2a76b9719d911017c592",
            SessionId = "ff00ff00",
            Cols = 80,
            Rows = 24,
            LastSeq = null,
        });

        Add(messages, "detachSessionRequest", new DetachSessionRequest { SessionId = "ff00ff00" });

        Add(messages, "sendInputRequest", new SendInputRequest
        {
            SessionId = "ff00ff00",
            Data = [0x64, 0x69, 0x72, 0x0d],
        });

        Add(messages, "resizeTerminalRequest", new ResizeTerminalRequest
        {
            SessionId = "ff00ff00",
            Cols = 100,
            Rows = 40,
        });

        Add(messages, "interruptSessionRequest", new InterruptSessionRequest { SessionId = "ff00ff00" });

        // The enum on a *request* matters more than on a notification: the hub
        // validates what arrives, and a client that sent the ordinal instead of the
        // name would be silently rejected with no way to tell why from the browser.
        Add(messages, "setSessionTypeRequest", new SetSessionTypeRequest
        {
            SessionId = "ff00ff00",
            CliType = CliType.ClaudeCode,
        });

        // Carries the machine id, unlike its neighbours: a rename is done from the
        // list, where nothing is attached, so the hub has no attachment to read the
        // machine from. Null is not a missing name, it is the instruction to clear one.
        Add(messages, "setSessionNameRequest", new SetSessionNameRequest
        {
            MachineId = "5d41402abc4b2a76b9719d911017c592",
            SessionId = "ff00ff00",
            Name = "The deploy",
        });

        Add(messages, "setSessionNameClearRequest", new SetSessionNameRequest
        {
            MachineId = "5d41402abc4b2a76b9719d911017c592",
            SessionId = "ff00ff00",
            Name = null,
        });

        Add(messages, "setSessionPinnedRequest", new SetSessionPinnedRequest
        {
            MachineId = "5d41402abc4b2a76b9719d911017c592",
            SessionId = "ff00ff00",
            Pinned = true,
        });

        // The appended field (protocol version 3): a session assigned to a
        // non-General project. sessionUpdated above already pins the label pair
        // (CustomName/Pinned); this entry is the one that pins ProjectId, the
        // field appended after it.
        Add(messages, "sessionOpenedWithProject", new ClientSessionOpenedNotification
        {
            MachineId = "5d41402abc4b2a76b9719d911017c592",
            Session = new SessionInfo
            {
                SessionId = "ff00ff00",
                Program = "pwsh",
                Args = [],
                Cwd = @"C:\Users\eran",
                Cols = 80,
                Rows = 24,
                StartedAt = Instant,
                DisplayName = null,
                AwaitingInput = false,
                CliType = CliType.PowerShell,
                ProjectId = "8277e0910d750195b448797616e091ad",
            },
        });

        Add(messages, "listProjectsRequest", new ListProjectsRequest());

        Add(messages, "createProjectRequest", new CreateProjectRequest
        {
            Name = "1RemoteCLI",
            Description = "Remote CLI sessions from a phone.",
            SiteUrl = "https://1remotecli.example.com",
            RepoUrl = "https://github.com/eranyariv/1RemoteCLI",
        });

        Add(messages, "updateProjectRequest", new UpdateProjectRequest
        {
            ProjectId = "8277e0910d750195b448797616e091ad",
            Name = "1RemoteCLI",
            Description = "Remote CLI sessions from a phone.",
            SiteUrl = "https://1remotecli.example.com",
            RepoUrl = "https://github.com/eranyariv/1RemoteCLI",
        });

        Add(messages, "deleteProjectRequest", new DeleteProjectRequest
        {
            ProjectId = "8277e0910d750195b448797616e091ad",
        });

        Add(messages, "setSessionProjectRequest", new SetSessionProjectRequest
        {
            MachineId = "5d41402abc4b2a76b9719d911017c592",
            SessionId = "ff00ff00",
            ProjectId = "8277e0910d750195b448797616e091ad",
        });

        Add(messages, "setSessionProjectClearRequest", new SetSessionProjectRequest
        {
            MachineId = "5d41402abc4b2a76b9719d911017c592",
            SessionId = "ff00ff00",
            ProjectId = null,
        });

        Add(messages, "projectList", new ProjectListNotification
        {
            Projects =
            [
                new ProjectInfo
                {
                    ProjectId = "general",
                    Name = "General",
                    Description = null,
                    SiteUrl = null,
                    RepoUrl = null,
                    IsGeneral = true,
                    IconVersion = 0,
                    CreatedAt = Instant,
                },
                new ProjectInfo
                {
                    ProjectId = "8277e0910d750195b448797616e091ad",
                    Name = "1RemoteCLI",
                    Description = "Remote CLI sessions from a phone.",
                    SiteUrl = "https://1remotecli.example.com",
                    RepoUrl = "https://github.com/eranyariv/1RemoteCLI",
                    IsGeneral = false,
                    IconVersion = 2,
                    CreatedAt = Instant,
                },
            ],
        });

        Add(messages, "projectResult", new ProjectResult
        {
            Project = new ProjectInfo
            {
                ProjectId = "8277e0910d750195b448797616e091ad",
                Name = "1RemoteCLI",
                Description = "Remote CLI sessions from a phone.",
                SiteUrl = "https://1remotecli.example.com",
                RepoUrl = "https://github.com/eranyariv/1RemoteCLI",
                IsGeneral = false,
                IconVersion = 0,
                CreatedAt = Instant,
            },
            Error = null,
        });

        Add(messages, "projectResultError", new ProjectResult
        {
            Project = null,
            Error = ErrorCodes.DuplicateProjectName,
        });

        Add(messages, "projectCreated", new ProjectCreatedNotification
        {
            Project = new ProjectInfo
            {
                ProjectId = "8277e0910d750195b448797616e091ad",
                Name = "1RemoteCLI",
                Description = "Remote CLI sessions from a phone.",
                SiteUrl = "https://1remotecli.example.com",
                RepoUrl = "https://github.com/eranyariv/1RemoteCLI",
                IsGeneral = false,
                IconVersion = 0,
                CreatedAt = Instant,
            },
        });

        Add(messages, "projectUpdated", new ProjectUpdatedNotification
        {
            Project = new ProjectInfo
            {
                ProjectId = "8277e0910d750195b448797616e091ad",
                Name = "1RemoteCLI",
                Description = "Remote CLI sessions from a phone.",
                SiteUrl = "https://1remotecli.example.com",
                RepoUrl = "https://github.com/eranyariv/1RemoteCLI",
                IsGeneral = false,
                IconVersion = 2,
                CreatedAt = Instant,
            },
        });

        Add(messages, "projectDeleted", new ProjectDeletedNotification
        {
            ProjectId = "8277e0910d750195b448797616e091ad",
        });

        return new JsonObject
        {
            ["comment"] =
                "Generated by tests/Protocol.Tests/WireContractTests.cs. Do not hand-edit: " +
                $"re-run the test with {UpdateVariable}=1 instead.",
            ["protocolVersion"] = ProtocolVersion.Current,
            ["messages"] = messages,
        };
    }

    /// <summary>
    /// Serialises one message and records both the bytes and the values they carry.
    /// <para>
    /// The decoded projection is produced here, from the same object, so the
    /// TypeScript test never hand-copies a constant: it decodes our bytes and
    /// compares against our values.
    /// </para>
    /// </summary>
    private static void Add<T>(JsonObject messages, string name, T message)
    {
        byte[] bytes = MessagePackSerializer.Serialize(message, Options);

        // Round-tripped rather than projected from the original, so a formatter that
        // loses information is caught here instead of being written into the fixture
        // as though it were correct.
        T restored = MessagePackSerializer.Deserialize<T>(bytes, Options);

        messages[name] = new JsonObject
        {
            ["type"] = typeof(T).Name,
            ["base64"] = Convert.ToBase64String(bytes),
            ["decoded"] = JsonSerializer.SerializeToNode(restored, Json),
        };
    }

    private static string Normalise(string text) => text.Replace("\r\n", "\n").TrimEnd();

    /// <summary>
    /// Walks up from the test binary to the repository root. Hard-coding a relative
    /// depth breaks the moment the target framework or configuration changes.
    /// </summary>
    private static string FixturePath
    {
        get
        {
            DirectoryInfo? directory = new(AppContext.BaseDirectory);

            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "1RemoteCLI.slnx")))
            {
                directory = directory.Parent;
            }

            Assert.NotNull(directory);

            return Path.Combine(directory!.FullName, "src", "PWA", "src", "protocol", "wire.fixture.json");
        }
    }
}
