using System.Runtime.Versioning;
using Microsoft.AspNetCore.SignalR.Client;
using OneRemoteCli.Daemon.Agent;
using OneRemoteCli.Protocol;
using OneRemoteCli.Protocol.Hub;

namespace OneRemoteCli.Daemon.Tests;

/// <summary>
/// The product, end to end, in one process.
/// <para>
/// Every other test in this repository stops at a component boundary: the agent's hub
/// client talks to a stand-in server, the hub talks to a fake caller, the pseudoconsole
/// has nothing above it, and the phone's decoder reads fixture bytes rather than a
/// socket. Each of those is correct in isolation and none of them can catch the bug
/// that matters most — two halves that both pass their own tests and still cannot talk
/// to each other. This file is the one place where a keystroke starts at the phone and
/// arrives at a real Windows pseudoconsole.
/// </para>
/// <para>
/// It is deliberately slow. Real sockets, real named pipes, real conhost. Anything
/// faster would be measuring something else.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
[Collection("End-to-end")]
public sealed class EndToEndTests : IAsyncLifetime
{
    private EndToEndHarness _harness = null!;

    public async Task InitializeAsync() => _harness = await EndToEndHarness.StartAsync();

    public async Task DisposeAsync() => await _harness.DisposeAsync();

    /// <summary>
    /// The phone can find the machine and the session without being told either id.
    /// This is the discovery leg: everything else in the product depends on it.
    /// </summary>
    [Fact]
    public async Task ThePhoneDiscoversTheMachineAndItsRunningSession()
    {
        WrappedShell shell = await _harness.StartShellAsync(displayName: "desk shell");
        PhoneClient phone = await _harness.ConnectPhoneAsync();

        MachineInfo machine = await WaitForMachineAsync(phone);

        Assert.Equal(_harness.MachineId, machine.MachineId);
        Assert.Equal("desk", machine.DisplayName);
        Assert.True(machine.Online);
        Assert.NotEmpty(machine.Os);
        Assert.NotEmpty(machine.AgentVersion);

        SessionInfo session = Assert.Single(machine.Sessions);

        Assert.Equal(shell.SessionId, session.SessionId);
        Assert.Equal("cmd.exe", session.Program);
        Assert.Equal("desk shell", session.DisplayName);
        Assert.Equal(80, session.Cols);
        Assert.Equal(25, session.Rows);
        Assert.True(
            session.StartedAt > DateTimeOffset.UtcNow.AddMinutes(-5),
            "the session's start time should be roughly now, not the epoch");
    }

    /// <summary>
    /// A session that starts while the phone is already watching is announced, rather
    /// than only appearing on the next poll. Without this the phone would show a stale
    /// list until the user pulled to refresh.
    /// </summary>
    [Fact]
    public async Task ASessionThatStartsWhileWatchingIsAnnounced()
    {
        PhoneClient phone = await _harness.ConnectPhoneAsync();

        WrappedShell shell = await _harness.StartShellAsync(displayName: "late starter");

        await EndToEndHarness.WaitUntilAsync(
            () => phone.Opened.Count > 0,
            "the phone to be told about the new session");

        ClientSessionOpenedNotification opened = phone.Opened[0];

        Assert.Equal(_harness.MachineId, opened.MachineId);
        Assert.Equal(shell.SessionId, opened.Session.SessionId);
        Assert.Equal("late starter", opened.Session.DisplayName);
    }

    /// <summary>
    /// <b>The exit criterion for Stage 1, automated.</b>
    /// <para>
    /// A keystroke typed on the phone reaches a real pseudoconsole, the shell runs it,
    /// and the bytes come back to the phone — and to the desk, because the wrapper's
    /// whole reason to exist is that the two see the same thing.
    /// </para>
    /// </summary>
    [Fact]
    public async Task AKeystrokeFromThePhoneRunsOnTheRealShellAndComesBack()
    {
        WrappedShell shell = await _harness.StartShellAsync();
        PhoneClient phone = await _harness.ConnectPhoneAsync();

        await AttachAsync(phone, shell);

        // A token unique to this run, so a stale byte from an earlier test could never
        // be mistaken for a pass.
        string token = $"e2e-{Guid.NewGuid():n}";

        Assert.Null(await phone.TypeAsync(shell.SessionId, $"echo {token}\r"));

        await phone.WaitForScreenAsync(token);

        // The desk sees it too. If this half ever fails, the wrapper has stopped being
        // a tee and has quietly become a redirect.
        await EndToEndHarness.WaitUntilAsync(
            () => shell.Desk.Screen.Contains(token, StringComparison.Ordinal),
            "the same bytes to reach the desk terminal");

        // Sequence numbers exist so Stage 3 can detect a gap after a reconnect. What
        // matters is that they never go backwards and never skip: a skip is what makes
        // the phone tell the user it missed something.
        //
        // Two frames may share a number. A snapshot is labelled with the sequence it
        // depicts rather than being given one of its own, because it is sent to one
        // client and a number consumed by one client's repaint would look like a hole
        // to everybody else watching the same session.
        IReadOnlyList<long> sequences = phone.Sequences;
        Assert.NotEmpty(sequences);

        for (int i = 1; i < sequences.Count; i++)
        {
            long step = sequences[i] - sequences[i - 1];
            Assert.True(step is 0 or 1, $"Sequence jumped from {sequences[i - 1]} to {sequences[i]}.");
        }
    }

    /// <summary>
    /// Real sockets, named pipes, and pseudoconsoles need the runner to themselves.
    /// Hosted runners otherwise starve repaint delivery while other test classes are busy.
    /// </summary>
    [CollectionDefinition("End-to-end", DisableParallelization = true)]
    public sealed class EndToEndCollection
    {
    }

    /// <summary>
    /// Resizing on the phone reshapes the real pseudoconsole, which is what makes a
    /// full-screen program redraw itself to fit the phone rather than wrap into noise.
    /// </summary>
    [Fact]
    public async Task ResizingOnThePhoneReshapesTheRealPseudoconsole()
    {
        WrappedShell shell = await _harness.StartShellAsync();
        PhoneClient phone = await _harness.ConnectPhoneAsync();

        await AttachAsync(phone, shell);

        Assert.Null(await phone.ResizeAsync(shell.SessionId, cols: 100, rows: 40));

        // Asserted on the pseudoconsole itself rather than on anything the hub said,
        // because the claim being tested is that the resize survived four hops and
        // landed on the Windows handle.
        await EndToEndHarness.WaitUntilAsync(
            () => shell.Pty.Cols == 100 && shell.Pty.Rows == 40,
            "the resize to reach the pseudoconsole");

        // And the hosted program agrees. mode.com reads the console it is actually
        // attached to, so this is the child's own view of its terminal.
        phone.ClearScreen();
        Assert.Null(await phone.TypeAsync(shell.SessionId, "mode con\r"));

        await phone.WaitForScreenAsync("100");
    }

    /// <summary>
    /// Ctrl+C from the phone stops a running program. The single most time-critical
    /// action in the product: it is the reason someone opens the app from a bus.
    /// </summary>
    [Fact]
    public async Task InterruptFromThePhoneStopsARunningProgram()
    {
        WrappedShell shell = await _harness.StartShellAsync();
        PhoneClient phone = await _harness.ConnectPhoneAsync();

        await AttachAsync(phone, shell);

        // Something long enough that it cannot finish on its own before the interrupt,
        // and chatty enough that the test can tell it really started.
        Assert.Null(await phone.TypeAsync(shell.SessionId, "ping -n 60 127.0.0.1\r"));
        await phone.WaitForScreenAsync("Pinging");

        Assert.Null(await phone.InterruptAsync(shell.SessionId));

        // The proof that the program actually died is that the shell answers again.
        string token = $"alive-{Guid.NewGuid():n}";

        await EndToEndHarness.WaitUntilAsync(
            () => shell.Pty.TryGetExitCode() is null,
            "the shell itself to survive the interrupt");

        phone.ClearScreen();

        await RetryUntilAsync(
            async () =>
            {
                await phone.TypeAsync(shell.SessionId, $"echo {token}\r");
                return phone.Screen.Contains(token, StringComparison.Ordinal);
            },
            "the shell to accept a command again after the interrupt");
    }

    /// <summary>
    /// Detaching stops delivery. A phone in someone's pocket must not keep a
    /// megabyte-a-second build streaming over cellular.
    /// </summary>
    [Fact]
    public async Task DetachingStopsTheStream()
    {
        WrappedShell shell = await _harness.StartShellAsync();
        PhoneClient phone = await _harness.ConnectPhoneAsync();

        await AttachAsync(phone, shell);

        string before = $"before-{Guid.NewGuid():n}";
        Assert.Null(await phone.TypeAsync(shell.SessionId, $"echo {before}\r"));
        await phone.WaitForScreenAsync(before);

        Assert.Null(await phone.DetachAsync(shell.SessionId));

        phone.ClearScreen();

        // Typed at the desk, because a detached client is no longer allowed to send
        // input either — which is itself the next assertion.
        string after = $"after-{Guid.NewGuid():n}";
        await shell.Pty.WriteAsync($"echo {after}\r");

        await EndToEndHarness.WaitUntilAsync(
            () => shell.Desk.Screen.Contains(after, StringComparison.Ordinal),
            "the desk to run the command typed at the keyboard");

        // The desk saw it and the phone did not. Give the relay a moment to be wrong
        // before concluding it was right.
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        Assert.DoesNotContain(after, phone.Screen, StringComparison.Ordinal);

        ErrorNotification? refusal = await phone.TypeAsync(shell.SessionId, "echo nope\r");

        Assert.NotNull(refusal);
        Assert.Equal(ErrorCodes.NotAttached, refusal.Code);
    }

    /// <summary>
    /// When the shell exits, the phone is told, with the real exit code — so a user
    /// who left a build running learns whether it passed.
    /// </summary>
    [Fact]
    public async Task ThePhoneIsToldWhenTheShellExitsAndWithWhatCode()
    {
        WrappedShell shell = await _harness.StartShellAsync();
        PhoneClient phone = await _harness.ConnectPhoneAsync();

        await AttachAsync(phone, shell);

        const int Code = 7;
        Assert.Equal(Code, await EndToEndHarness.ExitShellAsync(shell, Code));

        await EndToEndHarness.WaitUntilAsync(
            () => phone.Closed.Count > 0,
            "the phone to be told the session ended");

        ClientSessionClosedNotification closed = phone.Closed[0];

        Assert.Equal(_harness.MachineId, closed.MachineId);
        Assert.Equal(shell.SessionId, closed.SessionId);
        Assert.Equal(Code, closed.ExitCode);

        // And it is gone from the list, not merely marked closed. A session that
        // lingers is one the user will tap and find dead.
        MachineListNotification machines = await phone.ListMachinesAsync();
        Assert.Empty(machines.Machines.SelectMany(m => m.Sessions));
    }

    /// <summary>
    /// A different signed-in person sees none of this and cannot reach it even holding
    /// the ids. The partition is derived from the token, never from a parameter, so
    /// there is no request a client can construct that crosses it.
    /// </summary>
    [Fact]
    public async Task AnotherSignedInPersonSeesNothingAndCannotAttach()
    {
        WrappedShell shell = await _harness.StartShellAsync();

        // The owner first, to prove the session really is visible to somebody.
        PhoneClient owner = await _harness.ConnectPhoneAsync();
        await WaitForMachineAsync(owner);

        PhoneClient stranger = await _harness.ConnectPhoneAsync(TestIdentities.Stranger);

        MachineListNotification machines = await stranger.ListMachinesAsync();
        Assert.Empty(machines.Machines);

        // Handed both ids on a plate, and still refused.
        ErrorNotification? refusal = await stranger.AttachAsync(_harness.MachineId, shell.SessionId);

        Assert.NotNull(refusal);
        Assert.Equal(ErrorCodes.MachineNotFound, refusal.Code);

        // Nothing has leaked to the stranger's socket either.
        Assert.Empty(stranger.Screen);
        Assert.Empty(stranger.Opened);
    }

    /// <summary>
    /// An identity that is not on the hub's allowlist cannot get a connection at all.
    /// Rejection at admission rather than per request is what keeps the isolation
    /// story simple enough to reason about.
    /// </summary>
    [Fact]
    public async Task AnAccountThatIsNotAllowlistedCannotConnect()
    {
        PhoneClient uninvited = _harness.NewPhone(TestIdentities.Uninvited);

        await Assert.ThrowsAnyAsync<Exception>(uninvited.ConnectAsync);
    }

    /// <summary>
    /// A token from an allowlisted account still fails without the scope. Being the
    /// right person is not the same as having consented to this app.
    /// </summary>
    [Fact]
    public async Task ATokenWithoutTheRequiredScopeCannotConnect()
    {
        PhoneClient unscoped = _harness.NewPhone(TestIdentities.Unscoped);

        await Assert.ThrowsAnyAsync<Exception>(unscoped.ConnectAsync);
    }

    /// <summary>
    /// A client from the future is turned away by the handshake, not by a strange
    /// failure two messages later.
    /// </summary>
    [Fact]
    public async Task AClientSpeakingAnUnsupportedVersionIsTurnedAwayAtTheHandshake()
    {
        PhoneClient phone = _harness.NewPhone(TestIdentities.Owner);
        await phone.ConnectAsync();

        ErrorNotification? refusal = await phone.HandshakeAsync(ProtocolVersion.Current + 1);

        Assert.NotNull(refusal);
        Assert.Equal(ErrorCodes.UnsupportedProtocolVersion, refusal.Code);
    }

    /// <summary>
    /// Attaching to something already running shows what is on its screen.
    /// <para>
    /// This is the product's whole premise. The phone in this test never saw the
    /// output arrive — a different client typed the command and left — so the only
    /// way the text can be on screen is if the agent reconstructed it from the
    /// emulator. Replaying recent bytes could not do this: the shell wrote a prompt,
    /// echoed a command and printed a result, and what matters is the resulting
    /// screen, not the transcript.
    /// </para>
    /// </summary>
    [Fact]
    public async Task AttachingShowsTheScreenThatIsAlreadyThere()
    {
        WrappedShell shell = await _harness.StartShellAsync();

        // The first phone does the work and goes away, so nothing about the second
        // phone's view can come from having watched it happen.
        string token = $"snap-{Guid.NewGuid():n}";

        await using (PhoneClient typist = await _harness.ConnectPhoneAsync())
        {
            await AttachAsync(typist, shell);
            Assert.Null(await typist.TypeAsync(shell.SessionId, $"echo {token}\r"));
            await typist.WaitForScreenAsync(token);
            Assert.Null(await typist.DetachAsync(shell.SessionId));
        }

        PhoneClient latecomer = await _harness.ConnectPhoneAsync();
        await AttachAsync(latecomer, shell);

        await latecomer.WaitForScreenAsync(token);

        // The first thing it was sent, not merely something it was sent. A snapshot
        // arriving after a delta would be applied over newer output and undo it.
        OutputFrame first = Assert.Single(latecomer.Frames.Take(1));
        Assert.Equal(TerminalOutputKind.Snapshot, first.Kind);
    }

    /// <summary>
    /// The snapshot reproduces the agent's screen, not merely its text.
    /// <para>
    /// Checking that a word appeared would pass on a stream that put every line in
    /// the wrong column. Rendering the frames the phone received through the same
    /// emulator the agent used and comparing the grids is the assertion that actually
    /// covers cursor position, wrapping and blank runs.
    /// </para>
    /// </summary>
    [Fact]
    public async Task TheSnapshotRedrawsTheAgentsScreenExactly()
    {
        WrappedShell shell = await _harness.StartShellAsync();
        PhoneClient phone = await _harness.ConnectPhoneAsync();

        await AttachAsync(phone, shell);

        string token = $"grid-{Guid.NewGuid():n}";
        Assert.Null(await phone.TypeAsync(shell.SessionId, $"echo {token}\r"));
        await phone.WaitForScreenAsync(token);

        // Re-attaching is the cheapest way to ask for a fresh snapshot of a screen
        // that now has real content on it.
        phone.ClearScreen();
        Assert.Null(await phone.AttachAsync(shell.MachineIdHint, shell.SessionId, cols: 80, rows: 25));

        await EndToEndHarness.WaitUntilAsync(
            () => phone.Frames.Any(f => f.Kind == TerminalOutputKind.Snapshot),
            "a snapshot to arrive");

        Assert.True(_harness.Sessions.TryGet(shell.SessionId, out TerminalSession session));

        // Compared as text rather than cell by cell because the shell keeps writing —
        // a prompt redraw between the snapshot and the comparison is normal, and the
        // useful claim is that the phone's grid says the same thing the agent's does.
        await EndToEndHarness.WaitUntilAsync(
            () => Normalize(phone.Render(80, 25)) == Normalize(session.Screen.Text()),
            "the phone's rendering to match the agent's screen");
    }

    /// <summary>
    /// Attaching from a phone-shaped screen reshapes the emulator too, not just the
    /// pseudoconsole. If only the console were resized, the snapshot would still be
    /// drawn at the desk's width and every wrapped line would land in the wrong place.
    /// </summary>
    [Fact]
    public async Task AttachingAtThePhonesSizeReshapesTheEmulatorAsWell()
    {
        WrappedShell shell = await _harness.StartShellAsync();
        PhoneClient phone = await _harness.ConnectPhoneAsync();

        await WaitForSessionAsync(phone, shell.SessionId);
        Assert.Null(await phone.AttachAsync(shell.MachineIdHint, shell.SessionId, cols: 45, rows: 30));

        await EndToEndHarness.WaitUntilAsync(
            () => shell.Pty.Cols == 45 && shell.Pty.Rows == 30,
            "the attach geometry to reach the pseudoconsole");

        Assert.True(_harness.Sessions.TryGet(shell.SessionId, out TerminalSession session));

        await EndToEndHarness.WaitUntilAsync(
            () => session.Screen.Cols == 45 && session.Screen.Rows == 30,
            "the attach geometry to reach the emulator");
    }

    /// <summary>
    /// Output produced while no phone is connected still reaches the screen.
    /// <para>
    /// It does not reach the wire — there is no client attached to send it to — but it
    /// is still numbered and kept in the session's tail, and it is still fed to the
    /// emulator. Either route would be enough to make the next attach correct; having
    /// both is what lets a short absence be answered with a replay and a long one with
    /// a repaint.
    /// </para>
    /// </summary>
    [Fact]
    public async Task WorkDoneWhileNobodyWasWatchingIsStillOnTheScreen()
    {
        WrappedShell shell = await _harness.StartShellAsync();

        string token = $"away-{Guid.NewGuid():n}";

        // Typed straight into the pseudoconsole rather than through the hub, which is
        // how a program that keeps working after the user walks away behaves.
        await shell.Pty.WriteAsync($"echo {token}\r");

        await EndToEndHarness.WaitUntilAsync(
            () => shell.Desk.Screen.Contains(token, StringComparison.Ordinal),
            "the command to run at the desk");

        PhoneClient phone = await _harness.ConnectPhoneAsync();
        await AttachAsync(phone, shell);

        await phone.WaitForScreenAsync(token);
    }

    /// <summary>
    /// A brief drop costs a replay of what was missed, not a repaint.
    /// <para>
    /// This is the case the tail buffer exists for and the one that happens constantly:
    /// a lift, a tunnel, a screen that locked for ten seconds. The claim is specific —
    /// after resuming, no snapshot arrived and the sequence numbers run unbroken, which
    /// is what the client uses to decide whether it missed anything.
    /// </para>
    /// <para>
    /// Unbroken means no gap. It does not mean no repeat: delivery is at-least-once,
    /// because reattaching registers the phone for live output at the hub before the
    /// agent works out what it missed, so a frame flushed inside that window is
    /// broadcast and then replayed (issue #57). The client is idempotent about that —
    /// see <c>stream.ts</c> — and a gap is the failure that actually costs the user
    /// output, so a gap is what this asserts.
    /// </para>
    /// </summary>
    [Fact]
    public async Task AShortDropIsResumedFromTheTailWithoutARepaint()
    {
        WrappedShell shell = await _harness.StartShellAsync();
        PhoneClient phone = await _harness.ConnectPhoneAsync();

        await AttachAsync(phone, shell);

        string before = $"before-{Guid.NewGuid():n}";
        Assert.Null(await phone.TypeAsync(shell.SessionId, $"echo {before}\r"));
        await phone.WaitForScreenAsync(before);

        (int seen, long resumeFrom) = await phone.ResumePointAsync();

        Assert.Null(await phone.DetachAsync(shell.SessionId));

        // Typed at the desk, because the phone is supposed to be unreachable.
        string during = $"during-{Guid.NewGuid():n}";
        await shell.Pty.WriteAsync($"echo {during}\r");

        await EndToEndHarness.WaitUntilAsync(
            () => shell.Desk.Screen.Contains(during, StringComparison.Ordinal),
            "the command to run while the phone was away");

        // Re-read immediately before resuming rather than reusing the position from
        // before the detach. A detach is not instantaneous — it travels phone, hub,
        // agent — and output already on its way is still delivered, so a phone can
        // legitimately receive a frame or two after asking to leave. A real client
        // resumes from the last frame it actually received, which is what this models.
        // Resuming from the earlier position instead would ask the agent to replay a
        // frame the phone already had, and the duplicate would look like a fault in the
        // stream rather than the test misreporting where it had got to.
        (seen, resumeFrom) = await phone.ResumePointAsync();

        Assert.Null(await phone.AttachAsync(
            shell.MachineIdHint,
            shell.SessionId,
            cols: 80,
            rows: 25,
            lastSeq: resumeFrom));

        await phone.WaitForScreenAsync(during);

        IReadOnlyList<OutputFrame> resumed = [.. phone.Frames.Skip(seen)];

        Assert.NotEmpty(resumed);
        Assert.All(resumed, frame => Assert.Equal(TerminalOutputKind.Delta, frame.Kind));

        // Unbroken numbering starting exactly where the client left off. A gap here is
        // what makes the phone tell the user it missed something.
        Assert.Equal(resumeFrom + 1, resumed[0].Seq);

        for (int i = 1; i < resumed.Count; i++)
        {
            long previous = resumed[i - 1].Seq;
            long current = resumed[i].Seq;

            Assert.True(
                current == previous || current == previous + 1,
                $"Sequence jumped from {previous} to {current}, so the phone would report "
                    + "output it never received. A repeat is tolerated here and discarded by "
                    + "the client; a gap is lost output.");
        }
    }

    /// <summary>
    /// A drop long enough to overflow the tail is answered with a repaint, and the
    /// repaint is right.
    /// <para>
    /// Falling out of the buffer is not an error path to be avoided; it is the other
    /// half of the design, and the only thing that keeps the memory bound absolute. So
    /// the test asserts the screen ends up correct, not that the fast path was taken.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ADropLongEnoughToOverflowTheTailIsAnsweredWithASnapshot()
    {
        WrappedShell shell = await _harness.StartShellAsync();
        PhoneClient phone = await _harness.ConnectPhoneAsync();

        await AttachAsync(phone, shell);

        string before = $"before-{Guid.NewGuid():n}";
        Assert.Null(await phone.TypeAsync(shell.SessionId, $"echo {before}\r"));
        await phone.WaitForScreenAsync(before);

        (int seen, long resumeFrom) = await phone.ResumePointAsync();

        Assert.Null(await phone.DetachAsync(shell.SessionId));

        // Comfortably more than the tail holds, produced the way a build produces it:
        // as fast as the program can write.
        string flood = Path.Combine(Path.GetTempPath(), $"1remote-flood-{Guid.NewGuid():n}.txt");
        await File.WriteAllTextAsync(
            flood,
            string.Join("\r\n", Enumerable.Repeat(new string('x', 78), 6_000)));

        try
        {
            string after = $"after-{Guid.NewGuid():n}";
            await shell.Pty.WriteAsync($"type \"{flood}\"\r");
            await shell.Pty.WriteAsync($"echo {after}\r");

            await EndToEndHarness.WaitUntilAsync(
                () => shell.Desk.Screen.Contains(after, StringComparison.Ordinal),
                "the flood to finish at the desk");

            Assert.True(_harness.Sessions.TryGet(shell.SessionId, out TerminalSession session));

            await EndToEndHarness.WaitUntilAsync(
                () => session.Tail.LastSeq > resumeFrom + 8,
                "the session's tail to move well past where the phone left off");

            Assert.Null(await phone.AttachAsync(
                shell.MachineIdHint,
                shell.SessionId,
                cols: 80,
                rows: 25,
                lastSeq: resumeFrom));

            await EndToEndHarness.WaitUntilAsync(
                () => phone.Frames.Skip(seen).Any(frame => frame.Kind == TerminalOutputKind.Snapshot),
                "a repaint to arrive");

            Assert.True(phone.Frames.Skip(seen).First(
                frame => frame.Kind == TerminalOutputKind.Snapshot).ContinuityLost);

            await EndToEndHarness.WaitUntilAsync(
                () => Normalize(phone.Render(80, 25)) == Normalize(session.Screen.Text()),
                "the phone's rendering to match the agent's screen");
        }
        finally
        {
            File.Delete(flood);
        }
    }

    /// <summary>
    /// Resuming onto a differently shaped screen is refused in favour of a repaint.
    /// <para>
    /// The frames a client missed were produced for its old geometry. Replaying them
    /// after a reshape would place every wrapped line where it used to belong, which is
    /// worse than a repaint precisely because it looks plausible.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ResumingAtANewSizeIsRepaintedInstead()
    {
        WrappedShell shell = await _harness.StartShellAsync();
        PhoneClient phone = await _harness.ConnectPhoneAsync();

        await AttachAsync(phone, shell);

        string token = $"size-{Guid.NewGuid():n}";
        Assert.Null(await phone.TypeAsync(shell.SessionId, $"echo {token}\r"));
        await phone.WaitForScreenAsync(token);

        (int seen, long resumeFrom) = await phone.ResumePointAsync();

        Assert.Null(await phone.AttachAsync(
            shell.MachineIdHint,
            shell.SessionId,
            cols: 52,
            rows: 31,
            lastSeq: resumeFrom));

        await EndToEndHarness.WaitUntilAsync(
            () => phone.Frames.Skip(seen).Any(frame => frame.Kind == TerminalOutputKind.Snapshot),
            "a repaint at the new size");
    }

    /// <summary>
    /// One phone reattaching leaves the other phone's screen alone.
    /// <para>
    /// Issue #42. Several phones can watch the same session, and an attach is answered
    /// with either a repaint or a replay of frames the attaching phone missed. Both are
    /// answers to one phone's question. Broadcast to the others, the repaint is a
    /// pointless flash and the replay is real corruption: it writes bytes they already
    /// have onto the screen a second time.
    /// </para>
    /// </summary>
    [Fact]
    public async Task OnePhoneReattachingDoesNotDisturbAnother()
    {
        WrappedShell shell = await _harness.StartShellAsync();
        PhoneClient watcher = await _harness.ConnectPhoneAsync();
        PhoneClient rejoiner = await _harness.ConnectPhoneAsync();

        await AttachAsync(watcher, shell);
        await AttachAsync(rejoiner, shell);

        string before = $"before-{Guid.NewGuid():n}";
        Assert.Null(await watcher.TypeAsync(shell.SessionId, $"echo {before}\r"));

        await watcher.WaitForScreenAsync(before);
        await rejoiner.WaitForScreenAsync(before);

        Assert.Null(await rejoiner.DetachAsync(shell.SessionId));

        string during = $"during-{Guid.NewGuid():n}";
        Assert.Null(await watcher.TypeAsync(shell.SessionId, $"echo {during}\r"));
        await watcher.WaitForScreenAsync(during);

        // Captured after the detach and once each stream is quiet, so both positions are
        // the ones the phones really left off at. Anything either of them receives from
        // here on is a consequence of the rejoiner attaching, which is the whole subject
        // of this test.
        (int watcherAfterOutput, long watcherHighest) = await watcher.ResumePointAsync();
        (int rejoinerSaw, long resumeFrom) = await rejoiner.ResumePointAsync();

        Assert.Null(await rejoiner.AttachAsync(
            shell.MachineIdHint,
            shell.SessionId,
            cols: 80,
            rows: 25,
            lastSeq: resumeFrom));

        await rejoiner.WaitForScreenAsync(during);

        // The frames the returning phone was sent to catch it up. Counted from its own
        // frames, not the watcher's: the two connections have received different
        // numbers of frames by this point, and skipping one list by the other's length
        // silently drops or keeps the wrong ones depending on which happens to be
        // longer.
        IReadOnlyList<OutputFrame> caughtUp = [.. rejoiner.Frames.Skip(rejoinerSaw)];
        Assert.NotEmpty(caughtUp);

        // Give anything mistakenly fanned out time to arrive, so this is an assertion
        // rather than a race the wrong way round.
        await Task.Delay(500);

        // The watcher's frame count is pinned at a quiet moment above, so anything after
        // it arrived because the other phone attached. What must never appear is the
        // signature of somebody else's attach being answered on this connection: a
        // sequence number it has already seen, which is a replayed frame, or a snapshot,
        // which is a repaint it never asked for.
        foreach (OutputFrame frame in watcher.Frames.Skip(watcherAfterOutput))
        {
            Assert.Equal(TerminalOutputKind.Delta, frame.Kind);
            Assert.True(
                frame.Seq > watcherHighest,
                $"The watcher was sent sequence {frame.Seq}, which it had already seen.");

            watcherHighest = frame.Seq;
        }

        Assert.True(_harness.Sessions.TryGet(shell.SessionId, out TerminalSession session));

        await EndToEndHarness.WaitUntilAsync(
            () => Normalize(watcher.Render(80, 25)) == Normalize(session.Screen.Text())
                && Normalize(rejoiner.Render(80, 25)) == Normalize(session.Screen.Text()),
            "both phones to be showing the agent's screen");
    }

    /// <summary>
    /// Restarting the hub brings every machine and session back, with nobody touching
    /// the desk.
    /// <para>
    /// The hub's registry is in memory, so a deployment leaves a hub that has never
    /// heard of anyone. If the agent did not put itself back, a routine deploy would
    /// silently strand every machine until each user noticed and restarted something —
    /// and the sessions themselves would still be running perfectly well at their
    /// desks, which is what makes the failure so confusing to diagnose.
    /// </para>
    /// </summary>
    [Fact]
    public async Task RestartingTheHubBringsTheMachineAndItsSessionsBack()
    {
        WrappedShell shell = await _harness.StartShellAsync();

        PhoneClient before = await _harness.ConnectPhoneAsync();
        await WaitForSessionAsync(before, shell.SessionId);

        await _harness.RestartHubAsync();

        // A fresh phone, because a phone that reconnects is the other half of 3.4 and
        // this test is about the agent. Nothing here touched the desk.
        PhoneClient after = await _harness.ConnectPhoneAsync();

        await EndToEndHarness.WaitUntilAsync(
            async () =>
            {
                MachineListNotification list = await after.ListMachinesAsync();
                return list.Machines.Any(m => m.MachineId == _harness.MachineId)
                    && list.Machines.SelectMany(m => m.Sessions).Any(s => s.SessionId == shell.SessionId);
            },
            "the machine and its session to reappear on the replacement hub");

        // Registered is not the same as usable. The session has to actually work.
        Assert.Null(await after.AttachAsync(shell.MachineIdHint, shell.SessionId));

        string token = $"survived-{Guid.NewGuid():n}";

        await RetryUntilAsync(
            async () => await after.TypeAsync(shell.SessionId, $"echo {token}\r") is null,
            "the session to accept input again");

        await after.WaitForScreenAsync(token);
    }

    /// <summary>
    /// A connection whose token runs out is asked to refresh, and dropped if it does
    /// not.
    /// <para>
    /// This is the gap every SignalR design leaves open. The token is checked during
    /// the handshake and never again, so without this a socket outlives its token for
    /// as long as it stays open — which for a phone left attached overnight is longer
    /// than any token's lifetime. Revoking somebody's access would not touch the
    /// connection they already had.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ATokenThatIsNeverRefreshedEndsTheConnection()
    {
        // Already past expiry when it arrives. The test handler does not check
        // lifetimes — that is the signature-checking seam production owns — so this is
        // the state a connection reaches by sitting still, without waiting for it.
        PhoneClient phone = _harness.NewPhone(
            TestIdentities.Owner.TokenExpiringAt(DateTimeOffset.UtcNow - TimeSpan.FromHours(1)));

        await phone.StartAsync();
        Assert.Equal(HubConnectionState.Connected, phone.ConnectionState);

        await _harness.Sweeper.SweepAsync();

        await EndToEndHarness.WaitUntilAsync(
            () => Task.FromResult(phone.ConnectionState == HubConnectionState.Disconnected),
            "the connection with the expired token to be dropped");
    }

    /// <summary>
    /// A connection is warned before its token runs out, and a refresh in time saves
    /// it.
    /// </summary>
    [Fact]
    public async Task ARefreshInTimeKeepsTheConnection()
    {
        DateTimeOffset soon = DateTimeOffset.UtcNow.AddMinutes(2);

        PhoneClient phone = _harness.NewPhone(TestIdentities.Owner.TokenExpiringAt(soon));
        await phone.StartAsync();

        await _harness.Sweeper.SweepAsync();

        await EndToEndHarness.WaitUntilAsync(
            () => Task.FromResult(phone.ExpiryWarnings.Count == 1),
            "the hub to ask for a fresh token");

        Assert.Null(await phone.RefreshTokenAsync(
            TestIdentities.Owner.TokenExpiringAt(DateTimeOffset.UtcNow.AddHours(4))));

        // The sweep that would otherwise have ended it.
        await _harness.Sweeper.SweepAsync();
        await _harness.Sweeper.SweepAsync();

        Assert.Equal(HubConnectionState.Connected, phone.ConnectionState);

        // And it is still a working connection, not merely an open socket.
        MachineListNotification list = await phone.ListMachinesAsync();
        Assert.Contains(list.Machines, m => m.MachineId == _harness.MachineId);
    }

    /// <summary>
    /// A refresh that resolves to a different person ends the connection.
    /// <para>
    /// Not corrected, ended. A connection carries attachments, and quietly moving it
    /// from one account to another would hand whatever it is watching to somebody who
    /// was never granted it. There is no state on such a connection that is safe to
    /// keep once its owner is in question — including the channel that would have
    /// carried the explanation, which is why the caller sees a disconnection rather
    /// than a message.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ARefreshFromADifferentAccountEndsTheConnection()
    {
        PhoneClient phone = await _harness.ConnectPhoneAsync();

        await Assert.ThrowsAnyAsync<Exception>(
            () => phone.RefreshTokenAsync(TestIdentities.Stranger.Token));

        await EndToEndHarness.WaitUntilAsync(
            () => Task.FromResult(phone.ConnectionState == HubConnectionState.Disconnected),
            "the connection whose identity changed to be dropped");
    }

    /// <summary>
    /// A token the hub will not accept is refused, and the connection survives to be
    /// told so.
    /// <para>
    /// Killing it here would destroy the one channel over which the holder could learn
    /// what went wrong, and would bring forward a disconnection that the token's own
    /// expiry was already going to cause. The deadline is unchanged: refuse to refresh
    /// and the sweeper ends it at expiry.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ARefreshWithATokenTheHubRejectsIsRefusedWithAReason()
    {
        DateTimeOffset soon = DateTimeOffset.UtcNow.AddMinutes(2);

        PhoneClient phone = _harness.NewPhone(TestIdentities.Owner.TokenExpiringAt(soon));
        await phone.StartAsync();

        ErrorNotification? refusal = await phone.RefreshTokenAsync(TestIdentities.Uninvited.Token);

        Assert.NotNull(refusal);
        Assert.Equal(ErrorCodes.TokenExpired, refusal!.Code);
        Assert.Equal(HubConnectionState.Connected, phone.ConnectionState);

        // The deadline it failed to move is still the deadline.
        Assert.Equal(soon.ToUnixTimeSeconds(), _harness.Tokens.ExpiryOf(phone.ConnectionId)?.ToUnixTimeSeconds());
    }

    /// <summary>Trailing blanks differ harmlessly between two renderings of the same screen.</summary>
    private static string Normalize(string screen) =>
        string.Join(
            '\n',
            screen.Replace("\r\n", "\n", StringComparison.Ordinal)
                .Split('\n')
                .Select(line => line.TrimEnd()))
            .TrimEnd();

    /// <summary>Attaches, once the session the agent opened has crossed the hub and become visible.</summary>
    private static async Task AttachAsync(PhoneClient phone, WrappedShell shell)
    {
        await WaitForSessionAsync(phone, shell.SessionId);

        Assert.Null(await phone.AttachAsync(shell.MachineIdHint, shell.SessionId));
    }

    private static Task WaitForSessionAsync(PhoneClient phone, string sessionId) =>
        EndToEndHarness.WaitUntilAsync(
            async () =>
            {
                MachineListNotification list = await phone.ListMachinesAsync();
                return list.Machines.SelectMany(m => m.Sessions).Any(s => s.SessionId == sessionId);
            },
            $"session {sessionId} to become visible to the phone");

    private static async Task<MachineInfo> WaitForMachineAsync(PhoneClient phone)
    {
        MachineInfo? found = null;

        await EndToEndHarness.WaitUntilAsync(
            async () =>
            {
                MachineListNotification list = await phone.ListMachinesAsync();
                found = list.Machines.FirstOrDefault(m => m.Sessions.Length > 0);
                return found is not null;
            },
            "the machine and its session to appear in the phone's list");

        return found!;
    }

    /// <summary>
    /// Retries an action until it reports success. Used where a single attempt would be
    /// a race rather than an assertion — a shell that has just been interrupted may not
    /// be reading its input for a moment.
    /// </summary>
    private static async Task RetryUntilAsync(Func<Task<bool>> attempt, string what)
    {
        DateTime deadline = DateTime.UtcNow + EndToEndHarness.Patience;

        while (DateTime.UtcNow < deadline)
        {
            if (await attempt())
            {
                return;
            }

            await Task.Delay(250);
        }

        throw new TimeoutException($"Timed out waiting for {what}.");
    }
}
