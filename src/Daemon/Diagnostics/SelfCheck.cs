using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using MessagePack;
using Microsoft.Data.Sqlite;
using OneRemoteCli.Daemon.Agent;
using OneRemoteCli.Daemon.Chat;
using OneRemoteCli.Daemon.Install;
using OneRemoteCli.Daemon.Shell;
using OneRemoteCli.Daemon.Tray;
using OneRemoteCli.Protocol.Hub;
using static OneRemoteCli.Daemon.Tray.NativeMethods;

namespace OneRemoteCli.Daemon.Diagnostics;

/// <summary>
/// Exercises the parts of the agent that the published build can break without the
/// test suite noticing.
/// <para>
/// The tests run against an untrimmed build, so they prove the code is right and say
/// nothing about the executable people download. Trimming removes what the linker
/// judges unreachable, and everything here reaches its work through something the
/// linker cannot follow: COM vtables, reflection over JSON properties, formatters
/// MessagePack emits at run time. When one of those is trimmed away the code still
/// compiles, still passes every test, and fails on the user's machine.
/// </para>
/// <para>
/// This is what issue #72 was: <c>PublishTrimmed</c> switched built-in COM off, and
/// the first person to run <c>1remote install</c> from the published build got a
/// stack trace. Nothing between the change and that moment could have caught it. So
/// the publish script runs this against the artifact it just produced.
/// </para>
/// <para>
/// Each check does the real thing rather than approximating it - writes an actual
/// shortcut, round-trips an actual settings file - because an approximation is
/// exactly what a test already is, and the whole point here is to stop approximating.
/// Everything is written under a temporary directory that is removed afterwards, so
/// running this never touches the user's installation.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class SelfCheck
{
    /// <summary>
    /// The name of the manifest check, shared with the test suite because it is the one
    /// check whose answer depends on which executable is asking. See <see cref="Chrome"/>.
    /// </summary>
    internal const string ChromeCheckName = "Window chrome (manifest)";

    public static IReadOnlyList<StepResult> Run()
    {
        string scratch = Path.Combine(Path.GetTempPath(), "1remote-selfcheck-" + Guid.NewGuid().ToString("n"));

        try
        {
            Directory.CreateDirectory(scratch);

            return
            [
                Check("Start Menu shortcuts (COM)", () => Shortcuts(scratch)),
                Check("Shortcut round trip (COM)", () => RoundTrip(scratch)),
                Check("File dialog (COM)", FileDialog),
                Check("Machine identity file (JSON)", () => Identity(scratch)),
                Check("Settings file (JSON)", () => Settings(scratch)),
                Check("Copilot session index (SQLite)", () => CopilotSessionIndex(scratch)),
                Check("Hub messages (MessagePack)", Wire),
                Check(ChromeCheckName, Chrome),
            ];
        }
        finally
        {
            Remove(scratch);
        }
    }

    /// <summary>
    /// Removes the scratch directory, giving a transient lock a moment to clear.
    /// <para>
    /// The checks write real shortcuts through the shell, and something — the shell
    /// itself, or a scanner reacting to a newly written file — can still be holding
    /// the directory when the run finishes. That surfaces as an
    /// <see cref="IOException"/>, and this used to give up on the first one, which
    /// left the directory behind for good. Seen twice in a row on a developer machine
    /// and then not once in ten further runs, so it is worth waiting out rather than
    /// worth failing over.
    /// </para>
    /// </summary>
    private static void Remove(string scratch)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                Directory.Delete(scratch, recursive: true);

                return;
            }
            catch (DirectoryNotFoundException)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == 4)
                {
                    // A leftover temp directory is not worth failing a self-check over.
                    return;
                }

                Thread.Sleep(100);
            }
        }
    }

    public static string Summarise(IReadOnlyList<StepResult> checks)
    {
        ArgumentNullException.ThrowIfNull(checks);

        int failed = checks.Count(check => !check.Ok);

        return failed == 0
            ? "This build works. Every check that trimming could have broken passed."
            : $"{failed} check(s) failed. Do not ship this build.";
    }

    /// <summary>
    /// Writes a real shortcut and reads the file back.
    /// <para>
    /// Reading back matters as much as writing: the shell reports no error when a call
    /// lands in the wrong vtable slot, so a shortcut can be written successfully and
    /// point nowhere.
    /// </para>
    /// </summary>
    private static void Shortcuts(string scratch)
    {
        string folder = Path.Combine(scratch, "startmenu");

        StepResult result = StartMenu.Install(Environment.ProcessPath!, folder);

        if (!result.Ok)
        {
            throw new InvalidOperationException(result.Message);
        }

        FileInfo[] links = new DirectoryInfo(folder).GetFiles("*.lnk");

        if (links.Length != 2)
        {
            throw new InvalidOperationException($"Wrote {links.Length} shortcuts, expected 2.");
        }

        if (links.Any(link => link.Length == 0))
        {
            throw new InvalidOperationException("Wrote an empty shortcut.");
        }
    }

    /// <summary>
    /// Writes a shortcut with every field set and reads all of them back.
    /// <para>
    /// Separate from the check above, which only proves two files appeared. Shortcut
    /// wrapping (issue #66) depends on <em>reading</em>, and every getter it uses is a
    /// distinct vtable slot: the shell returns <c>S_OK</c> from a call that landed in
    /// the wrong one and simply writes nothing to the buffer, so a mis-ordered
    /// interface produces a wrapped shortcut that points at an empty string.
    /// </para>
    /// </summary>
    private static void RoundTrip(string scratch)
    {
        string path = Path.Combine(scratch, "round trip.lnk");

        var written = new ShellLinkInfo(
            Environment.ProcessPath!,
            "--name \"Self check\" -- \"C:\\tools\\thing.exe\" --flag",
            scratch,
            Environment.ProcessPath!,
            0,
            "Self check");

        ShellLink.Write(path, written);

        ShellLinkInfo read = ShellLink.Read(path);

        if (!string.Equals(read.Target, written.Target, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Read back the target '{read.Target}', wrote '{written.Target}'.");
        }

        if (read.Arguments != written.Arguments)
        {
            throw new InvalidOperationException($"Read back the arguments '{read.Arguments}', wrote '{written.Arguments}'.");
        }

        if (!string.Equals(read.WorkingDirectory, written.WorkingDirectory, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Read back the working directory '{read.WorkingDirectory}', wrote '{written.WorkingDirectory}'.");
        }

        if (read.RunAsAdministrator)
        {
            // The flags come off a second interface, queried from the same object. A
            // failed query would report every shortcut as elevated, which refuses every
            // wrap on a machine where nothing is wrong.
            throw new InvalidOperationException("A shortcut written without elevation read back as elevated.");
        }
    }

    /// <summary>
    /// Creates the shell's file-open dialog without showing it.
    /// <para>
    /// Activation is the part trimming breaks — the object is created by hand through
    /// <c>CoCreateInstance</c> because built-in COM is off — and it is also the part
    /// that cannot be tested any other way. Showing the dialog is not an option here;
    /// creating it proves the class is registered and the interface is reachable, which
    /// is everything the linker could have taken away.
    /// </para>
    /// </summary>
    private static void FileDialog()
    {
        if (!FilePicker.CanActivate())
        {
            throw new InvalidOperationException("The shell's file-open dialog could not be created.");
        }
    }

    /// <summary>
    /// The most expensive one to get wrong. An identity that cannot be read is silently
    /// replaced with a new one, and the user's phone loses the machine and every session
    /// on it.
    /// </summary>
    private static void Identity(string scratch)
    {
        string path = Path.Combine(scratch, "machine.json");

        var written = new MachineIdentity(Guid.NewGuid().ToString("n"), "Self check");
        written.Save(path);

        MachineIdentity read = MachineIdentity.Load(path);

        if (read.MachineId != written.MachineId || read.DisplayName != written.DisplayName)
        {
            throw new InvalidOperationException(
                $"Read back '{read.MachineId}'/'{read.DisplayName}', wrote '{written.MachineId}'/'{written.DisplayName}'.");
        }
    }

    /// <summary>
    /// A settings file that stops being read costs the user their settings without
    /// saying so, because the loader is deliberately forgiving about malformed ones.
    /// </summary>
    private static void Settings(string scratch)
    {
        string path = Path.Combine(scratch, "settings.json");

        File.WriteAllText(path, """{"awaitingInput":{"quietPeriodSeconds":42,"promptPatterns":["ready?"]}}""");

        AwaitingInputOptions options = AwaitingInputOptions.Load(path);

        if (options.QuietPeriod != TimeSpan.FromSeconds(42))
        {
            throw new InvalidOperationException($"Read a quiet period of {options.QuietPeriod}, expected 42 seconds.");
        }

        if (options.PromptPatterns is not ["ready?"])
        {
            throw new InvalidOperationException("Prompt patterns did not survive the round trip.");
        }
    }

    private static void CopilotSessionIndex(string scratch)
    {
        string path = Path.Combine(scratch, "copilot-data.db");
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false,
        }.ToString();

        using (var connection = new SqliteConnection(connectionString))
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                CREATE TABLE sessions (
                    id TEXT PRIMARY KEY,
                    session_type TEXT,
                    archived_at TEXT
                );
                CREATE TABLE workspaces (id TEXT PRIMARY KEY, session_id TEXT, archived_at TEXT);
                CREATE TABLE workspace_side_chats (workspace_id TEXT, session_id TEXT);
                CREATE TABLE app_state (key TEXT PRIMARY KEY, value TEXT);
                INSERT INTO sessions VALUES ('visible', 'general_chat', NULL);
                INSERT INTO sessions VALUES ('archived', 'general_chat', '2026-08-20');
                INSERT INTO app_state VALUES (
                    'sidebar-project-groups',
                    '{"state":{"viewMode":"all"}}'
                );
                """;
            command.ExecuteNonQuery();
        }

        var index = new CopilotArchiveIndex(databasePath: path);
        HashSet<string> archived = index.ReadArchivedSessionIdsAsync().GetAwaiter().GetResult();

        if (!archived.Contains("archived"))
        {
            throw new InvalidOperationException("The Copilot archive index did not return the archived session.");
        }

        HashSet<string>? visible = index.ReadVisibleSessionIdsAsync().GetAwaiter().GetResult();
        if (visible is null || !visible.Contains("visible") || visible.Contains("archived"))
        {
            throw new InvalidOperationException("The Copilot session index did not match sidebar visibility.");
        }
    }

    /// <summary>
    /// MessagePack emits its formatters with reflection, so a trimmed member comes back
    /// as one message type that no longer carries one of its fields.
    /// </summary>
    private static void Wire()
    {
        var machine = new MachineInfo
        {
            MachineId = "m1",
            DisplayName = "Self check",
            Os = "Windows",
            AgentVersion = "0.00",
            Online = true,
            Sessions =
            [
                new SessionInfo
                {
                    SessionId = "s1",
                    Program = "pwsh",
                    Args = ["-NoLogo"],
                    Cwd = @"C:\",
                    Cols = 120,
                    Rows = 30,
                    StartedAt = DateTimeOffset.UtcNow,
                },
            ],
        };

        MachineInfo read = MessagePackSerializer.Deserialize<MachineInfo>(MessagePackSerializer.Serialize(machine));

        if (read.MachineId != machine.MachineId
            || read.DisplayName != machine.DisplayName
            || read.Online != machine.Online
            || read.Sessions.Length != 1)
        {
            throw new InvalidOperationException("A machine did not survive the round trip.");
        }

        SessionInfo session = read.Sessions[0];

        if (session.SessionId != "s1" || session.Program != "pwsh" || session.Cols != 120 || session.Args is not ["-NoLogo"])
        {
            throw new InvalidOperationException("A session did not survive the round trip.");
        }
    }

    /// <summary>
    /// Proves the application manifest reached the published executable.
    /// <para>
    /// The manifest is the whole reason the settings window stopped looking like a 2005
    /// dialog (issue #105), and it belongs to the same class of failure as everything
    /// else here: it is not code, so nothing compiles it and no test loads it. It is a
    /// native resource attached at publish time, and if <c>ApplicationManifest</c> is
    /// ever dropped from the project the build stays green, the tests stay green, and
    /// the shipped window silently reverts to grey comctl32 v5 controls at the wrong
    /// size on a scaled display.
    /// </para>
    /// <para>
    /// Both questions are asked of the running process rather than of the file, because
    /// a manifest that is present but malformed is loaded by nothing: Windows reports it
    /// as a side-by-side configuration error and the answers below come back as though
    /// there were no manifest at all.
    /// </para>
    /// </summary>
    private static void Chrome()
    {
        DLLVERSIONINFO comctl = default;
        comctl.cbSize = (uint)Marshal.SizeOf<DLLVERSIONINFO>();

        int hr = ComCtlGetVersion(ref comctl);

        if (hr != 0)
        {
            throw new InvalidOperationException($"comctl32!DllGetVersion returned 0x{hr:x8}.");
        }

        if (comctl.dwMajorVersion < 6)
        {
            throw new InvalidOperationException(
                $"comctl32 {comctl.dwMajorVersion}.{comctl.dwMinorVersion} is bound, so visual styles are off. "
                + "The application manifest is missing or malformed.");
        }

        if (!AreDpiAwarenessContextsEqual(GetThreadDpiAwarenessContext(), DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2))
        {
            throw new InvalidOperationException(
                "The process is not per-monitor-v2 DPI aware, so windows will be bitmap-scaled and blurry. "
                + "The application manifest is missing or malformed.");
        }

        // Reading the user's light/dark choice goes through the registry, which trimming
        // can reach, unlike the two answers above.
        using var theme = Theme.Current();

        if (theme.SurfaceBrush == IntPtr.Zero || theme.LayerBrush == IntPtr.Zero || theme.BorderBrush == IntPtr.Zero)
        {
            throw new InvalidOperationException("The theme produced no brushes.");
        }
    }

    private static StepResult Check(string name, Action check)
    {
        try
        {
            check();

            return StepResult.Success(name);
        }
        catch (Exception ex)
        {
            // Every exception, deliberately. The failures this exists to catch arrive as
            // whatever type the runtime happens to throw when something is not there, and
            // a self-check that only catches the ones already thought of is the same
            // mistake this is here to detect.
            return StepResult.Failure($"{name}: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
