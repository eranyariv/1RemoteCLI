using System.Runtime.Versioning;
using System.Text;
using System.Threading.Channels;
using OneRemoteCli.Daemon.Cli;
using OneRemoteCli.Daemon.Pty;
using OneRemoteCli.Daemon.Wrapper;

namespace OneRemoteCli.Daemon.Tests;

[SupportedOSPlatform("windows")]
public class WrapperSessionTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task PaintsChildOutputOnTheDeskTerminal()
    {
        await using var fixture = await Fixture.StartAsync("cmd.exe /c echo desk-marker");

        int exitCode = await fixture.RunAsync();

        Assert.Equal(0, exitCode);
        Assert.Contains("desk-marker", fixture.Terminal.WrittenText);
    }

    /// <summary>
    /// The phone must see the same bytes the desk does, unfiltered — the agent is
    /// what turns them into a screen, so anything dropped here is lost for good.
    /// </summary>
    [Fact]
    public async Task ForwardsTheSameBytesToTheAgent()
    {
        await using var fixture = await Fixture.StartAsync("cmd.exe /c echo shared-marker");

        await fixture.RunAsync();

        Assert.Equal(fixture.Terminal.Written, fixture.Agent.Forwarded);
        Assert.Contains("shared-marker", fixture.Agent.ForwardedText);
    }

    [Fact]
    public async Task WritesDeskKeystrokesIntoTheChild()
    {
        await using var fixture = await Fixture.StartAsync("cmd.exe /q");

        fixture.Terminal.TypeAtTheDesk("echo typed-at-desk\r");
        fixture.Terminal.TypeAtTheDesk("exit\r");

        await fixture.RunAsync();

        Assert.Contains("typed-at-desk", fixture.Terminal.WrittenText);
    }

    [Fact]
    public async Task WritesRemoteInputIntoTheChild()
    {
        await using var fixture = await Fixture.StartAsync("cmd.exe /q");

        fixture.Agent.SendFromPhone(new AgentCommand.Input(Encoding.UTF8.GetBytes("echo typed-on-phone\r")));
        fixture.Agent.SendFromPhone(new AgentCommand.Input(Encoding.UTF8.GetBytes("exit\r")));

        await fixture.RunAsync();

        Assert.Contains("typed-on-phone", fixture.Agent.ForwardedText);
    }

    [Fact]
    public async Task ResizesThePseudoConsoleWhenThePhoneAsks()
    {
        await using var fixture = await Fixture.StartAsync("cmd.exe /q");

        fixture.Agent.SendFromPhone(new AgentCommand.Resize(100, 30));
        fixture.Agent.SendFromPhone(new AgentCommand.Input(Encoding.UTF8.GetBytes("exit\r")));

        await fixture.RunAsync();

        Assert.Equal(100, fixture.Pty.Cols);
        Assert.Equal(30, fixture.Pty.Rows);
    }

    /// <summary>Exit codes must survive, or the wrapper cannot be used in a script.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(42)]
    public async Task ExitsWithTheChildsExitCode(int expected)
    {
        await using var fixture = await Fixture.StartAsync($"cmd.exe /c exit {expected}");

        Assert.Equal(expected, await fixture.RunAsync());
    }

    [Fact]
    public async Task TellsTheAgentTheSessionIsOverAndWhy()
    {
        await using var fixture = await Fixture.StartAsync("cmd.exe /c exit 7");

        await fixture.RunAsync();

        Assert.Equal(7, fixture.Agent.ClosedWithExitCode);
    }

    /// <summary>
    /// A dead phone link must not take the desk session with it. The user keeps
    /// working locally; they are told once, in words, that sharing has stopped.
    /// </summary>
    [Fact]
    public async Task KeepsTheDeskSessionAliveWhenTheAgentLinkBreaks()
    {
        await using var fixture = await Fixture.StartAsync("cmd.exe /c echo still-working");
        fixture.Agent.FailOnSend = true;

        int exitCode = await fixture.RunAsync();

        Assert.Equal(0, exitCode);
        Assert.Contains("still-working", fixture.Terminal.WrittenText);
        Assert.Contains(fixture.Warnings, w => w.Contains("no longer shareable"));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(PseudoConsoleSession pty, FakeTerminal terminal, FakeAgent agent)
        {
            Pty = pty;
            Terminal = terminal;
            Agent = agent;
        }

        public PseudoConsoleSession Pty { get; }

        public FakeTerminal Terminal { get; }

        public FakeAgent Agent { get; }

        public List<string> Warnings { get; } = [];

        public static Task<Fixture> StartAsync(string commandLine)
        {
            var terminal = new FakeTerminal();
            var agent = new FakeAgent();
            var pty = PseudoConsoleSession.Start(commandLine, workingDirectory: null, terminal.Cols, terminal.Rows);

            return Task.FromResult(new Fixture(pty, terminal, agent));
        }

        public Task<int> RunAsync()
        {
            var session = new WrapperSession(Pty, Terminal, Agent, w => Warnings.Add(w));
            return session.RunAsync().WaitAsync(Timeout);
        }

        public async ValueTask DisposeAsync()
        {
            await Pty.DisposeAsync();
            Terminal.Dispose();
            await Agent.DisposeAsync();
        }
    }

    /// <summary>A desk terminal made of memory, so the tee can be asserted on.</summary>
    private sealed class FakeTerminal : ILocalTerminal
    {
        private readonly BlockingStream _input = new();
        private readonly MemoryStream _output = new();

        public int Cols => 80;

        public int Rows => 24;

        public Stream Input => _input;

        public Stream Output => _output;

        public byte[] Written
        {
            get
            {
                lock (_output)
                {
                    return _output.ToArray();
                }
            }
        }

        public string WrittenText => Encoding.UTF8.GetString(Written);

        public void TypeAtTheDesk(string text) => _input.Post(Encoding.UTF8.GetBytes(text));

        public void Dispose()
        {
            _input.Dispose();
            _output.Dispose();
        }
    }

    /// <summary>
    /// A stream that blocks on read until bytes are posted, the way a console does.
    /// A <see cref="MemoryStream"/> would return zero immediately and the input pump
    /// would exit before the test had typed anything.
    /// </summary>
    private sealed class BlockingStream : Stream
    {
        private readonly Queue<byte[]> _pending = new();
        private readonly SemaphoreSlim _available = new(0);
        private byte[]? _current;
        private int _offset;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public void Post(byte[] data)
        {
            lock (_pending)
            {
                _pending.Enqueue(data);
            }

            _available.Release();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_current is null || _offset >= _current.Length)
            {
                _available.Wait();

                lock (_pending)
                {
                    _current = _pending.Dequeue();
                }

                _offset = 0;
            }

            int taken = Math.Min(count, _current.Length - _offset);
            Array.Copy(_current, _offset, buffer, offset, taken);
            _offset += taken;
            return taken;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _available.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>Records what the wrapper sent, and plays back what the phone sends.</summary>
    private sealed class FakeAgent : IAgentConnection
    {
        private readonly Channel<AgentCommand> _commands = Channel.CreateUnbounded<AgentCommand>();
        private readonly List<byte> _forwarded = [];

        public bool FailOnSend { get; set; }

        public int? ClosedWithExitCode { get; private set; }

        public ChannelReader<AgentCommand> Commands => _commands.Reader;

        public byte[] Forwarded
        {
            get
            {
                lock (_forwarded)
                {
                    return _forwarded.ToArray();
                }
            }
        }

        public string ForwardedText => Encoding.UTF8.GetString(Forwarded);

        public void SendFromPhone(AgentCommand command) => _commands.Writer.TryWrite(command);

        public Task<string> OpenSessionAsync(SessionStartInfo info, CancellationToken cancellationToken) =>
            Task.FromResult("session-1");

        public ValueTask SendOutputAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
        {
            if (FailOnSend)
            {
                throw new IOException("pipe broken");
            }

            lock (_forwarded)
            {
                _forwarded.AddRange(bytes.ToArray());
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask CloseSessionAsync(int exitCode, CancellationToken cancellationToken)
        {
            ClosedWithExitCode = exitCode;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            _commands.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
