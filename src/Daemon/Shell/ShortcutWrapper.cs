using System.Runtime.Versioning;
using OneRemoteCli.Daemon.Cli;

namespace OneRemoteCli.Daemon.Shell;

/// <summary>What kind of program a shortcut points at, as far as a terminal is concerned.</summary>
public enum ProgramKind
{
    /// <summary>Not on this machine, or not a file this can read. Never used to refuse anything.</summary>
    Unknown,

    /// <summary>Writes to a console. The case this whole feature exists for.</summary>
    Console,

    /// <summary>A windowed program. Wrapping one produces a session with nothing in it.</summary>
    Graphical,
}

/// <summary>
/// The outcome of planning a wrap: either a shortcut to write, or a reason not to.
/// </summary>
/// <param name="Problem">Why this shortcut cannot be wrapped, or null when it can.</param>
/// <param name="Warning">
/// Something the user should know about a wrap that still went ahead. Separate from
/// <paramref name="Problem"/> on purpose: refusing and proceeding-with-a-caveat are
/// different answers, and collapsing them means either a useless refusal or a silent
/// surprise.
/// </param>
/// <param name="OutputPath">Where the wrapped shortcut goes.</param>
/// <param name="Link">What to write there.</param>
/// <param name="DisplayName">What the session will be called on the phone.</param>
public readonly record struct WrapPlan(
    string? Problem,
    string? Warning,
    string OutputPath,
    ShellLinkInfo Link,
    string DisplayName)
{
    public bool Ok => Problem is null;

    internal static WrapPlan Refused(string problem) =>
        new(problem, null, string.Empty, default, string.Empty);
}

/// <summary>
/// Turning a shortcut the user already has into one that starts the same program
/// inside a shareable session.
/// <para>
/// Making a session shareable otherwise means remembering to type <c>1remote</c> in
/// front of a command, and anyone who starts their CLI from a desktop shortcut — which
/// is how most tools that ship one are actually launched — never gets the chance.
/// </para>
/// <para>
/// Pure, and separate from both the COM that reads shortcuts and the window that
/// offers the button, because the interesting part of this feature is entirely in the
/// refusals. Each one is a shortcut that would otherwise be written successfully and
/// then fail on double-click, in a way whose cause is invisible from the desktop.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class ShortcutWrapper
{
    /// <summary>
    /// What a wrapped shortcut is called. Also how one is recognised on the way back
    /// in, so that wrapping the same tool twice does not produce
    /// <c>Claude Code (1Remote) (1Remote).lnk</c>.
    /// </summary>
    public const string Suffix = " (1Remote)";

    /// <summary>
    /// How many numbered variants to try before giving up.
    /// <para>
    /// A bound rather than a loop that cannot end. Somebody with twenty of these has a
    /// different problem, and a hang while the user waits for a file dialog to close is
    /// worse than a message.
    /// </para>
    /// </summary>
    private const int Variants = 99;

    /// <summary>Plans a wrap, or explains why there is not one.</summary>
    /// <param name="sourcePath">The <c>.lnk</c> the user picked.</param>
    /// <param name="source">What it contains.</param>
    /// <param name="agentPath">Full path to <c>1remote.exe</c>.</param>
    /// <param name="outputPath">Where to write, or null to pick a free name beside the source.</param>
    /// <param name="exists">
    /// Whether a path is taken. Injected so the collision rule can be tested without a
    /// filesystem, which is the rule most likely to be got wrong.
    /// </param>
    /// <param name="classify">How to tell a console program from a windowed one.</param>
    public static WrapPlan Plan(
        string sourcePath,
        ShellLinkInfo source,
        string agentPath,
        string? outputPath = null,
        Func<string, bool>? exists = null,
        Func<string, ProgramKind>? classify = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentPath);

        exists ??= File.Exists;
        classify ??= Classify;

        if (!source.HasProgram)
        {
            // Store and MSIX shortcuts carry an AppUserModelID and no file. There is
            // nothing to hand to a pseudoconsole, and writing a shortcut anyway would
            // produce one that fails on double-click with no clue as to why.
            return WrapPlan.Refused(
                $"'{Path.GetFileName(sourcePath)}' does not name a program to run — it is a Store or packaged app shortcut, "
                + "which 1remote cannot start. Wrap the tool's own .exe or .cmd instead.");
        }

        if (source.RunAsAdministrator)
        {
            // The agent runs as the user, unelevated, and its pipe is ACL'd to that
            // user's SID. An elevated child cannot connect to it, so the wrapped
            // shortcut would launch and then immediately report the agent as missing.
            return WrapPlan.Refused(
                $"'{Path.GetFileName(sourcePath)}' runs as administrator. The 1remote agent is per-user and unelevated, "
                + "so an elevated session could not reach it.");
        }

        if (IsAgent(source.Target, agentPath))
        {
            return WrapPlan.Refused(
                $"'{Path.GetFileName(sourcePath)}' already starts a 1remote session. Wrapping it again would nest one inside the other.");
        }

        string displayName = NameOf(sourcePath);
        string? destination = outputPath ?? Choose(Path.GetDirectoryName(Path.GetFullPath(sourcePath)) ?? ".", displayName, exists);

        if (destination is null)
        {
            return WrapPlan.Refused(
                $"There are already {Variants} wrapped copies of '{displayName}' beside it. Delete some, or pass --output.");
        }

        string? warning = classify(source.Target) == ProgramKind.Graphical
            ? $"'{Path.GetFileName(source.Target)}' is a windowed program, so its session will show an empty terminal. "
              + "Wrapped anyway, in case that is what you meant."
            : null;

        return new WrapPlan(
            null,
            warning,
            destination,
            new ShellLinkInfo(
                agentPath,
                Arguments(displayName, source),
                // The original's working directory, which for a great many tools is the
                // only reason they work at all. Falling back to the target's own folder
                // rather than to nothing: an empty working directory means "inherit",
                // and what would be inherited is Explorer's, which is unpredictable.
                source.WorkingDirectory.Length > 0
                    ? source.WorkingDirectory
                    : Path.GetDirectoryName(source.Target) ?? string.Empty,
                // Copied so the wrapped shortcut still looks like the tool it launches.
                // A desktop full of identical 1remote icons is a feature nobody uses.
                source.IconPath.Length > 0 ? source.IconPath : source.Target,
                source.IconPath.Length > 0 ? source.IconIndex : 0,
                $"Runs {displayName} in a 1RemoteCLI session you can reach from your phone."),
            displayName);
    }

    /// <summary>
    /// The wrapped command line: our options, then everything the original asked for,
    /// untouched, behind <c>--</c>.
    /// <para>
    /// The original's arguments go through as the single string the shell stored rather
    /// than being split and rejoined. They are already quoted the way the child expects,
    /// and re-quoting them is two more chances to change what the program is asked to
    /// do — against no benefit, since this is copying a command line, not reading one.
    /// </para>
    /// </summary>
    private static string Arguments(string displayName, ShellLinkInfo source)
    {
        string wrapped = $"--name {CommandLine.Quote(displayName)} -- {CommandLine.Quote(source.Target)}";

        return source.Arguments.Length > 0 ? $"{wrapped} {source.Arguments}" : wrapped;
    }

    /// <summary>
    /// What to call the session, taken from the shortcut's own file name.
    /// <para>
    /// The shortcut's name rather than the program's: the user named it, and
    /// <em>Claude Code</em> is what they are looking for on their phone, not
    /// <em>node</em>.
    /// </para>
    /// </summary>
    public static string NameOf(string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        string name = Path.GetFileNameWithoutExtension(sourcePath);

        // Trailing suffixes are stripped rather than kept, so that wrapping a wrapped
        // shortcut - which the target check above already refuses - could never leave
        // behind a name that grows every time.
        while (name.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^Suffix.Length];
        }

        return name.Trim();
    }

    /// <summary>
    /// The first free name, so wrapping twice never silently overwrites the first
    /// result. Returns null when there is no free name left.
    /// </summary>
    private static string? Choose(string directory, string displayName, Func<string, bool> exists)
    {
        string first = Path.Combine(directory, $"{displayName}{Suffix}.lnk");

        if (!exists(first))
        {
            return first;
        }

        for (int n = 2; n <= Variants; n++)
        {
            string candidate = Path.Combine(directory, $"{displayName}{Suffix} ({n}).lnk");

            if (!exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static bool IsAgent(string target, string agentPath) =>
        string.Equals(Path.GetFileName(target), Path.GetFileName(agentPath), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether a program writes to a console, read out of the PE header.
    /// <para>
    /// The subsystem field is the only honest answer available: an extension says
    /// nothing (plenty of console tools are <c>.exe</c>, and so is Notepad), and
    /// starting the program to find out is not an option.
    /// </para>
    /// <para>
    /// Anything unreadable is <see cref="ProgramKind.Unknown"/> rather than a guess.
    /// This drives a warning, and a warning nobody can act on — "we could not read your
    /// program" — trains people to dismiss the ones that matter.
    /// </para>
    /// </summary>
    public static ProgramKind Classify(string programPath)
    {
        if (string.IsNullOrWhiteSpace(programPath))
        {
            return ProgramKind.Unknown;
        }

        // Batch files and scripts have no PE header. They are run by a console host
        // regardless of what interprets them.
        if (Path.GetExtension(programPath) is ".cmd" or ".bat" or ".ps1" or ".com")
        {
            return ProgramKind.Console;
        }

        try
        {
            using FileStream file = File.OpenRead(programPath);
            using var reader = new BinaryReader(file);

            if (reader.ReadUInt16() != 0x5A4D)
            {
                // No "MZ". Not an executable image at all.
                return ProgramKind.Unknown;
            }

            file.Position = 0x3C;
            uint headers = reader.ReadUInt32();

            file.Position = headers;

            if (reader.ReadUInt32() != 0x00004550)
            {
                // No "PE\0\0" where the stub said it would be.
                return ProgramKind.Unknown;
            }

            // Past the 20-byte COFF header, then to the subsystem field. It sits at the
            // same offset in the 32-bit and 64-bit optional headers, which is why the
            // magic does not need reading first.
            const int CoffHeaderBytes = 20;
            const int SubsystemOffset = 68;

            file.Position = headers + 4 + CoffHeaderBytes + SubsystemOffset;

            const int WindowsGui = 2;
            const int WindowsConsole = 3;

            return reader.ReadUInt16() switch
            {
                WindowsGui => ProgramKind.Graphical,
                WindowsConsole => ProgramKind.Console,
                _ => ProgramKind.Unknown,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return ProgramKind.Unknown;
        }
    }
}
