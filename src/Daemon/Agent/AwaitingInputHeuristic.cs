using System.Text.RegularExpressions;

namespace OneRemoteCli.Daemon.Agent;

/// <summary>What the agent knows about one session at the moment it is examined.</summary>
/// <param name="Age">How long the session has existed.</param>
/// <param name="Quiet">How long since the program last wrote anything.</param>
/// <param name="Posture">What the screen looks like it is doing.</param>
public readonly record struct IdleSignals(TimeSpan Age, TimeSpan Quiet, ScreenPosture Posture);

/// <summary>Why the heuristic decided a session is waiting, or that it is not.</summary>
public enum AwaitingInputVerdict
{
    /// <summary>Not waiting, or not yet.</summary>
    No,

    /// <summary>Quiet for long enough, with the screen in the shape of a prompt.</summary>
    Quiet,

    /// <summary>One of the user's own patterns matched, so the quiet period was skipped.</summary>
    Pattern,
}

/// <summary>
/// Decides whether a session looks like it is waiting for the user.
/// <para>
/// Windows exposes no way to ask whether a process is blocked reading its console, so
/// this is a guess, and it is written to be wrong in the safe direction. A missed
/// prompt costs the user a few minutes; a false one costs the feature, because a
/// notification that fires when nothing is waiting teaches its recipient to ignore
/// every notification that follows it. Where the two conflict, this stays quiet.
/// </para>
/// <para>
/// Kept pure - no clock, no state, no session - so that the awkward cases can be
/// tested as data rather than as timing.
/// </para>
/// </summary>
public static class AwaitingInputHeuristic
{
    public static AwaitingInputVerdict Evaluate(
        in IdleSignals signals,
        AwaitingInputOptions options,
        IReadOnlyList<Regex>? patterns = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Programs are quiet while they start, and a session that has existed for two
        // seconds has nothing to say yet.
        if (signals.Age < options.MinimumUptime)
        {
            return AwaitingInputVerdict.No;
        }

        // A hidden cursor is a program mid-render. Nothing hides the cursor and then
        // waits for an answer, and full-screen tools hide it constantly while painting.
        if (!signals.Posture.CursorVisible)
        {
            return AwaitingInputVerdict.No;
        }

        if (Matches(patterns, signals.Posture.LastLine))
        {
            // The user told us what their tool prints when it asks. Believe them, and
            // do not make them wait out the quiet period for something already certain.
            return AwaitingInputVerdict.Pattern;
        }

        if (signals.Quiet < options.QuietPeriod)
        {
            return AwaitingInputVerdict.No;
        }

        // The load-bearing condition, and the one only a screen model can answer. A
        // prompt leaves the cursor sitting just past its own text: "Continue? (y/n) _".
        // A build that is merely slow finished its last line with a newline, which puts
        // the cursor at column zero of an empty row - quiet for just as long, and not
        // waiting for anybody.
        return signals.Posture.CursorAfterText ? AwaitingInputVerdict.Quiet : AwaitingInputVerdict.No;
    }

    private static bool Matches(IReadOnlyList<Regex>? patterns, string line)
    {
        if (patterns is null || patterns.Count == 0 || line.Length == 0)
        {
            return false;
        }

        foreach (Regex pattern in patterns)
        {
            try
            {
                if (pattern.IsMatch(line))
                {
                    return true;
                }
            }
            catch (RegexMatchTimeoutException)
            {
                // One expensive pattern must not stall the sweep for every other
                // session on the machine. Treated as "did not match".
            }
        }

        return false;
    }

    /// <summary>
    /// A short phrase for the notification body.
    /// <para>
    /// The last line of the screen, which for a prompt is the question itself - far
    /// more use on a lock screen than the name of the program, since the user knows
    /// what they started and not what it decided to ask.
    /// </para>
    /// </summary>
    public static string? Hint(in ScreenPosture posture, int maximumLength = 120)
    {
        string line = posture.LastLine.Trim();
        if (line.Length == 0)
        {
            return null;
        }

        return line.Length <= maximumLength ? line : line[..maximumLength].TrimEnd() + "\u2026";
    }
}
