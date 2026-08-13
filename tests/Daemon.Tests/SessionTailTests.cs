using OneRemoteCli.Daemon.Agent;
using OneRemoteCli.Protocol.Hub;

namespace OneRemoteCli.Daemon.Tests;

/// <summary>
/// The tail buffer's whole job is to answer one question — can this client be sent
/// what it missed, or does it need a repaint? Both answers are correct, so the tests
/// are about the boundary between them, not about preferring one.
/// </summary>
public sealed class SessionTailTests
{
    [Fact]
    public void SequenceNumbersStartAtOneAndNeverRepeat()
    {
        var tail = new SessionTail();

        long first = tail.Record(TerminalOutputKind.Delta, [1]);
        long second = tail.Record(TerminalOutputKind.Delta, [2]);
        long third = tail.Record(TerminalOutputKind.Snapshot, [3]);

        Assert.Equal(1, first);
        Assert.Equal(2, second);
        Assert.Equal(3, third);
        Assert.Equal(3, tail.LastSeq);
    }

    [Fact]
    public void AClientThatMissedNothingResumesWithNothing()
    {
        var tail = new SessionTail();
        tail.Record(TerminalOutputKind.Delta, [1]);
        tail.Record(TerminalOutputKind.Delta, [2]);

        bool resumable = tail.TryReplayFrom(2, out IReadOnlyList<TailFrame> missed);

        Assert.True(resumable);
        Assert.Empty(missed);
    }

    [Fact]
    public void AShortGapIsAnsweredWithExactlyTheFramesThatWereMissed()
    {
        var tail = new SessionTail();
        tail.Record(TerminalOutputKind.Delta, [1]);
        tail.Record(TerminalOutputKind.Delta, [2]);
        tail.Record(TerminalOutputKind.Delta, [3]);
        tail.Record(TerminalOutputKind.Delta, [4]);

        Assert.True(tail.TryReplayFrom(2, out IReadOnlyList<TailFrame> missed));

        Assert.Equal([3L, 4L], missed.Select(frame => frame.Seq));
        Assert.Equal([(byte)3, (byte)4], missed.Select(frame => frame.Data[0]));
    }

    [Fact]
    public void ReplayedFramesKeepTheirOriginalSequenceNumbers()
    {
        var tail = new SessionTail();

        for (int i = 0; i < 5; i++)
        {
            tail.Record(TerminalOutputKind.Delta, [(byte)i]);
        }

        Assert.True(tail.TryReplayFrom(1, out IReadOnlyList<TailFrame> missed));

        // Renumbering would be invisible in a single test run and catastrophic on a
        // reattach: the client would see the same sequence twice and report a gap.
        Assert.Equal([2L, 3L, 4L, 5L], missed.Select(frame => frame.Seq));
    }

    [Fact]
    public void AClientFromBeforeTheBufferHasToBeRepainted()
    {
        var tail = new SessionTail();

        // Enough to evict the beginning several times over.
        for (int i = 0; i < 64; i++)
        {
            tail.Record(TerminalOutputKind.Delta, new byte[16 * 1024]);
        }

        Assert.False(tail.TryReplayFrom(1, out IReadOnlyList<TailFrame> missed));
        Assert.Empty(missed);
    }

    [Fact]
    public void AClientFromAFutureThisSessionNeverHadIsRepainted()
    {
        var tail = new SessionTail();
        tail.Record(TerminalOutputKind.Delta, [1]);

        // What a client looks like after the agent restarted underneath it.
        Assert.False(tail.TryReplayFrom(9_000, out _));
    }

    [Fact]
    public void TheBufferStopsGrowingNoMatterHowMuchIsWritten()
    {
        var tail = new SessionTail();

        for (int i = 0; i < 4_000; i++)
        {
            tail.Record(TerminalOutputKind.Delta, new byte[4 * 1024]);

            // Asserted every time rather than at the end, because a bound that only
            // holds once the flood stops is not a bound.
            Assert.True(
                tail.RetainedBytes <= SessionTail.MaxBytes,
                $"retained {tail.RetainedBytes} bytes after {i + 1} frames");
        }
    }

    [Fact]
    public void WhatSurvivesEvictionIsStillContiguous()
    {
        var tail = new SessionTail();

        for (int i = 0; i < 200; i++)
        {
            tail.Record(TerminalOutputKind.Delta, new byte[8 * 1024]);
        }

        // Find the oldest sequence that still resumes, then check that resuming from
        // it hands back an unbroken run up to the present.
        long oldest = Enumerable.Range(0, 201).First(candidate => tail.TryReplayFrom(candidate, out _));

        Assert.True(tail.TryReplayFrom(oldest, out IReadOnlyList<TailFrame> missed));
        Assert.Equal(tail.LastSeq, missed[^1].Seq);
        Assert.Equal(oldest + 1, missed[0].Seq);

        for (int i = 1; i < missed.Count; i++)
        {
            Assert.Equal(missed[i - 1].Seq + 1, missed[i].Seq);
        }
    }
}
