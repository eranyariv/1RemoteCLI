using System.Text;

using OneRemoteCli.Terminal.Vt;

namespace OneRemoteCli.Terminal.Tests.Vt;

/// <summary>
/// Captures everything the parser reports, as values.
/// <para>
/// Copying out of the spans is the whole point. The parser hands over views into
/// buffers it reuses on the very next sequence, so a sink that kept the spans would
/// silently compare a sequence against itself. Materialising each event as an immutable
/// record also makes the chunking-invariance test a single list equality rather than a
/// hand-written walk.
/// </para>
/// </summary>
internal sealed class RecordingSink : IVtEventSink
{
    public List<VtEvent> Events { get; } = [];

    public void Print(Rune rune) => Events.Add(new PrintEvent(rune));

    public void Execute(byte control) => Events.Add(new ExecuteEvent(control));

    public void CsiDispatch(scoped in VtParams parameters, ReadOnlySpan<byte> intermediates, byte final) =>
        Events.Add(new CsiEvent(parameters.ToArray(), intermediates.ToArray(), final));

    public void EscDispatch(ReadOnlySpan<byte> intermediates, byte final) =>
        Events.Add(new EscEvent(intermediates.ToArray(), final));

    public void OscDispatch(ReadOnlySpan<byte> data) => Events.Add(new OscEvent(data.ToArray()));

    public void Hook(scoped in VtParams parameters, ReadOnlySpan<byte> intermediates, byte final) =>
        Events.Add(new HookEvent(parameters.ToArray(), intermediates.ToArray(), final));

    public void Put(byte data) => Events.Add(new PutEvent(data));

    public void Unhook() => Events.Add(new UnhookEvent());

    /// <summary>Everything printed, as text. The common assertion.</summary>
    public string Text
    {
        get
        {
            var text = new StringBuilder();

            foreach (VtEvent e in Events)
            {
                if (e is PrintEvent print)
                {
                    text.Append(print.Rune);
                }
            }

            return text.ToString();
        }
    }

    /// <summary>A compact rendering of the event stream, so a failure says what differed.</summary>
    public string Describe() => string.Join(" ", Events.Select(e => e.ToString()));
}

internal abstract record VtEvent;

internal sealed record PrintEvent(Rune Rune) : VtEvent
{
    public override string ToString() => $"print(U+{Rune.Value:X4})";
}

internal sealed record ExecuteEvent(byte Control) : VtEvent
{
    public override string ToString() => $"exec(0x{Control:X2})";
}

internal sealed record CsiEvent(int[][] Parameters, byte[] Intermediates, byte Final) : VtEvent
{
    public bool Equals(CsiEvent? other) =>
        other is not null
        && Final == other.Final
        && Intermediates.SequenceEqual(other.Intermediates)
        && SameParams(Parameters, other.Parameters);

    public override int GetHashCode() => Final;

    public override string ToString() =>
        $"csi({Describe(Parameters)}|{Convert.ToHexString(Intermediates)}|{(char)Final})";

    internal static bool SameParams(int[][] left, int[][] right) =>
        left.Length == right.Length
        && left.Zip(right).All(pair => pair.First.SequenceEqual(pair.Second));

    internal static string Describe(int[][] parameters) =>
        string.Join(";", parameters.Select(group => string.Join(":", group)));
}

internal sealed record EscEvent(byte[] Intermediates, byte Final) : VtEvent
{
    public bool Equals(EscEvent? other) =>
        other is not null && Final == other.Final && Intermediates.SequenceEqual(other.Intermediates);

    public override int GetHashCode() => Final;

    public override string ToString() => $"esc({Convert.ToHexString(Intermediates)}|{(char)Final})";
}

internal sealed record OscEvent(byte[] Data) : VtEvent
{
    public bool Equals(OscEvent? other) => other is not null && Data.SequenceEqual(other.Data);

    public override int GetHashCode() => Data.Length;

    public string Text => Encoding.UTF8.GetString(Data);

    public override string ToString() => $"osc({Text})";
}

internal sealed record HookEvent(int[][] Parameters, byte[] Intermediates, byte Final) : VtEvent
{
    public bool Equals(HookEvent? other) =>
        other is not null
        && Final == other.Final
        && Intermediates.SequenceEqual(other.Intermediates)
        && CsiEvent.SameParams(Parameters, other.Parameters);

    public override int GetHashCode() => Final;

    public override string ToString() =>
        $"hook({CsiEvent.Describe(Parameters)}|{Convert.ToHexString(Intermediates)}|{(char)Final})";
}

internal sealed record PutEvent(byte Data) : VtEvent
{
    public override string ToString() => $"put(0x{Data:X2})";
}

internal sealed record UnhookEvent : VtEvent
{
    public override string ToString() => "unhook";
}
