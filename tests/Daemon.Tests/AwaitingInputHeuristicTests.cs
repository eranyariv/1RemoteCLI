using System.Text.RegularExpressions;
using OneRemoteCli.Daemon.Agent;
using OneRemoteCli.Protocol.Hub;

namespace OneRemoteCli.Daemon.Tests;

/// <summary>
/// The decision, tested as data.
/// <para>
/// Every case here is a screen the heuristic will meet on somebody's machine. The
/// negative ones matter more than the positive ones: a missed prompt is a delay, but
/// a notification that fires while a build is running teaches its recipient to swipe
/// the next one away without reading it, and there is no recovering from that.
/// </para>
/// </summary>
public sealed class AwaitingInputHeuristicTests
{
    private static readonly AwaitingInputOptions Options = AwaitingInputOptions.Default;

    private static readonly TimeSpan Old = TimeSpan.FromMinutes(1);

    [Fact]
    public void Quiet_with_the_cursor_after_text_is_waiting()
    {
        var signals = new IdleSignals(
            Old,
            TimeSpan.FromSeconds(20),
            new ScreenPosture(true, true, "Do you want to allow this edit? (y/n)"));

        Assert.Equal(AwaitingInputVerdict.Quiet, AwaitingInputHeuristic.Evaluate(signals, Options));
    }

    [Fact]
    public void An_agent_pausing_to_think_is_not_waiting()
    {
        // A coding agent stops printing for as long as the model takes to answer, and
        // leaves a visible cursor in its input box the whole time -- by the rules
        // above, indistinguishable from a screen waiting for a person. Twenty seconds
        // of that is thinking, not a question, and notifying about it produced one
        // push per pause that the user could do nothing with.
        var signals = new IdleSignals(
            Old,
            TimeSpan.FromSeconds(20),
            new ScreenPosture(true, true, "▶▶ auto mode on (shift+tab to cycle)"));

        AwaitingInputOptions agent = Options.ForCliType(CliType.ClaudeCode);

        Assert.Equal(AwaitingInputVerdict.No, AwaitingInputHeuristic.Evaluate(signals, agent));

        // The same screen in a shell still is a prompt: eight seconds of silence there
        // means the command finished. Only the agent gets the longer benefit of doubt.
        Assert.Equal(
            AwaitingInputVerdict.Quiet,
            AwaitingInputHeuristic.Evaluate(signals, Options.ForCliType(CliType.PowerShell)));
    }

    [Fact]
    public void An_agent_quiet_for_long_enough_is_still_reported()
    {
        // The other half: erring quiet must not mean going silent. A prompt nobody has
        // answered for a minute is a prompt, whatever is running.
        var signals = new IdleSignals(
            Old,
            TimeSpan.FromMinutes(1),
            new ScreenPosture(true, true, "Do you want to proceed? (y/n)"));

        Assert.Equal(
            AwaitingInputVerdict.Quiet,
            AwaitingInputHeuristic.Evaluate(signals, Options.ForCliType(CliType.ClaudeCode)));
    }

    [Fact]
    public void A_slow_build_is_not_waiting()
    {
        // The acceptance criterion this whole design exists for. Compiling for a
        // minute is exactly as quiet as a prompt; the difference is that the compiler
        // ended its last line with a newline, so the cursor is on a blank row.
        var signals = new IdleSignals(
            Old,
            TimeSpan.FromMinutes(2),
            new ScreenPosture(true, false, "  Determining projects to restore..."));

        Assert.Equal(AwaitingInputVerdict.No, AwaitingInputHeuristic.Evaluate(signals, Options));
    }

    [Fact]
    public void Silence_shorter_than_the_quiet_period_is_not_waiting()
    {
        var signals = new IdleSignals(
            Old,
            Options.QuietPeriod - TimeSpan.FromMilliseconds(1),
            new ScreenPosture(true, true, "> "));

        Assert.Equal(AwaitingInputVerdict.No, AwaitingInputHeuristic.Evaluate(signals, Options));
    }

    [Fact]
    public void A_hidden_cursor_is_never_waiting()
    {
        // What a full-screen renderer does while it paints.
        var signals = new IdleSignals(Old, Old, new ScreenPosture(false, true, "Continue? (y/n)"));

        Assert.Equal(AwaitingInputVerdict.No, AwaitingInputHeuristic.Evaluate(signals, Options));
    }

    [Fact]
    public void A_session_that_has_only_just_started_is_never_waiting()
    {
        var signals = new IdleSignals(
            TimeSpan.FromSeconds(1),
            Old,
            new ScreenPosture(true, true, "> "));

        Assert.Equal(AwaitingInputVerdict.No, AwaitingInputHeuristic.Evaluate(signals, Options));
    }

    [Fact]
    public void A_matching_pattern_skips_the_quiet_period()
    {
        var options = Options with { PromptPatterns = ["press any key"] };
        var signals = new IdleSignals(
            Old,
            TimeSpan.Zero,
            new ScreenPosture(true, false, "Press any key to continue . . ."));

        Assert.Equal(
            AwaitingInputVerdict.Pattern,
            AwaitingInputHeuristic.Evaluate(signals, options, options.CompilePatterns()));
    }

    [Fact]
    public void A_matching_pattern_still_needs_the_minimum_uptime()
    {
        // The override is about how long to wait for silence, not about believing a
        // program that has not finished starting.
        var options = Options with { PromptPatterns = ["press any key"] };
        var signals = new IdleSignals(
            TimeSpan.FromSeconds(1),
            Old,
            new ScreenPosture(true, true, "Press any key to continue . . ."));

        Assert.Equal(
            AwaitingInputVerdict.No,
            AwaitingInputHeuristic.Evaluate(signals, options, options.CompilePatterns()));
    }

    [Fact]
    public void Patterns_are_matched_against_the_last_line_not_the_cursor_line()
    {
        // "Press any key" is often followed by a newline, which leaves the cursor on a
        // blank row. Matching the cursor's row would miss every such prompt.
        var options = Options with { PromptPatterns = [@"\(y/n\)"] };
        var signals = new IdleSignals(Old, TimeSpan.Zero, new ScreenPosture(true, false, "Overwrite? (y/n)"));

        Assert.Equal(
            AwaitingInputVerdict.Pattern,
            AwaitingInputHeuristic.Evaluate(signals, options, options.CompilePatterns()));
    }

    [Fact]
    public void A_pattern_that_does_not_compile_is_dropped_rather_than_fatal()
    {
        var complaints = new List<string>();
        var options = Options with { PromptPatterns = ["(unclosed", "allow"] };

        IReadOnlyList<Regex> compiled = options.CompilePatterns(complaints.Add);

        Assert.Single(compiled);
        Assert.Contains(complaints, c => c.Contains("(unclosed", StringComparison.Ordinal));
        Assert.Matches(compiled[0], "Allow this?");
    }

    [Fact]
    public void The_hint_is_the_last_line_trimmed_and_bounded()
    {
        Assert.Equal("Allow?", AwaitingInputHeuristic.Hint(new ScreenPosture(true, true, "  Allow?   ")));
        Assert.Null(AwaitingInputHeuristic.Hint(new ScreenPosture(true, true, "    ")));

        string? hint = AwaitingInputHeuristic.Hint(new ScreenPosture(true, true, new string('x', 500)), 20);
        Assert.NotNull(hint);
        Assert.Equal(21, hint!.Length);
        Assert.EndsWith("\u2026", hint, StringComparison.Ordinal);
    }
}
