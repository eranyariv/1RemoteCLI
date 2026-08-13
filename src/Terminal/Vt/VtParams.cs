namespace OneRemoteCli.Terminal.Vt;

/// <summary>
/// The numeric parameters of a CSI or DCS sequence, grouped by separator.
/// <para>
/// Parameters separated by <c>;</c> are distinct; parameters separated by <c>:</c>
/// belong to the same one. That distinction is not pedantry — it is the difference
/// between <c>SGR 38;2;255;0;0</c> and <c>SGR 38:2:255:0:0</c>, and a parser that
/// flattens both to the same list cannot tell 24-bit foreground red from a sequence
/// that sets five unrelated attributes. Both spellings are in the wild, and the
/// colon form is what modern programs emit.
/// </para>
/// <para>
/// A ref struct so that dispatch costs nothing: parameters are read during the
/// callback and never outlive it, which is true of every real consumer.
/// </para>
/// </summary>
public readonly ref struct VtParams
{
    private readonly ReadOnlySpan<int> _values;
    private readonly ReadOnlySpan<int> _starts;

    internal VtParams(ReadOnlySpan<int> values, ReadOnlySpan<int> starts)
    {
        _values = values;
        _starts = starts;
    }

    /// <summary>How many semicolon-separated parameters the sequence carried.</summary>
    public int Count => _starts.Length;

    /// <summary>
    /// True when the sequence carried no parameters at all, which for most sequences
    /// means "apply the default", not "apply zero".
    /// </summary>
    public bool IsEmpty => _starts.Length == 0;

    /// <summary>
    /// The first value of parameter <paramref name="index"/>, or <c>0</c> if there is
    /// no such parameter. Callers that need to distinguish an absent parameter from an
    /// explicit zero should use <see cref="Get"/>.
    /// </summary>
    public int this[int index] => Get(index, 0);

    /// <summary>
    /// The first value of parameter <paramref name="index"/>, or
    /// <paramref name="fallback"/> when the parameter is absent, written empty, or
    /// written as zero.
    /// <para>
    /// Zero folding into the default is not a shortcut. There is no bit distinguishing
    /// an omitted parameter from an explicit <c>0</c> once it has been parsed, and
    /// every sequence that takes a count defines them identically anyway — <c>CSI 0 A</c>
    /// and <c>CSI A</c> both move the cursor up one line. Sequences where zero is a real
    /// value, such as SGR, read <see cref="this"/> or <see cref="SubParams"/> instead
    /// and use <see cref="Count"/> to tell "no parameters" from "parameter zero".
    /// </para>
    /// </summary>
    public int Get(int index, int fallback)
    {
        if ((uint)index >= (uint)_starts.Length)
        {
            return fallback;
        }

        int value = _values[_starts[index]];
        return value == 0 ? fallback : value;
    }

    /// <summary>
    /// The colon-separated values of parameter <paramref name="index"/>, including the
    /// first. Empty when there is no such parameter.
    /// </summary>
    public ReadOnlySpan<int> SubParams(int index)
    {
        if ((uint)index >= (uint)_starts.Length)
        {
            return [];
        }

        int start = _starts[index];
        int end = index + 1 < _starts.Length ? _starts[index + 1] : _values.Length;

        return _values[start..end];
    }

    /// <summary>Flattens to an array. For diagnostics and tests, not for the hot path.</summary>
    public int[][] ToArray()
    {
        var groups = new int[_starts.Length][];

        for (int i = 0; i < _starts.Length; i++)
        {
            groups[i] = SubParams(i).ToArray();
        }

        return groups;
    }
}
