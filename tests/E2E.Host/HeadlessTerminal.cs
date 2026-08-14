namespace OneRemoteCli.E2E.Host;

/// <summary>
/// The terminal the wrapper thinks it is running in, with nobody at it.
/// <para>
/// The end-to-end host starts sessions on behalf of a test, so there is no console to
/// tee to and no keyboard to read from. Output is discarded rather than captured —
/// what the phone was sent is the thing under test, and the desk's copy of it proves
/// nothing extra — and input parks forever so the wrapper's local-input pump has
/// something to block on instead of spinning.
/// </para>
/// </summary>
internal sealed class HeadlessTerminal(int cols, int rows) : Daemon.Wrapper.ILocalTerminal
{
    private readonly ParkedStream _input = new();

    public int Cols { get; } = cols;

    public int Rows { get; } = rows;

    public Stream Input => _input;

    public Stream Output { get; } = Stream.Null;

    public void Dispose() => _input.Dispose();

    /// <summary>A stream whose reads never return until it is disposed.</summary>
    private sealed class ParkedStream : Stream
    {
        private readonly ManualResetEventSlim _released = new(false);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            _released.Wait();
            return 0;
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
                _released.Set();
                _released.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
