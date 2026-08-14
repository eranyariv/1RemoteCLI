using System.Text;

namespace OneRemoteCli.E2E.Script;

/// <summary>
/// A program that behaves the same way every time it is run.
///
/// <para>
/// The end-to-end tests drive a browser against a real terminal, and a real terminal
/// normally contains a shell prompt with a machine name, a path and a blinking cursor
/// in it. Asserting on that means asserting on whoever's machine happens to be running
/// the suite, which is how a test suite becomes something people rerun until it passes.
/// So the tests do not run a shell. They run this.
/// </para>
/// <para>
/// Everything it prints is fixed, and every marker it prints is a string that could not
/// plausibly appear by accident, so a test can wait for one and know what it means. It
/// answers three questions a browser cannot otherwise ask a terminal: did my keystroke
/// arrive, did my <c>Ctrl+C</c> arrive, and did my resize arrive.
/// </para>
/// </summary>
internal static class Program
{
    /// <summary>Printed once, at start-up. The signal that a session is alive.</summary>
    private const string Ready = "E2E-READY";

    private static int Main(string[] args)
    {
        // No buffering anywhere: every marker has to reach the pseudoconsole at the
        // moment it is written, because the test on the other side is waiting for it
        // and a flush that happens "eventually" is a flake.
        var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false))
        {
            AutoFlush = true,
        };

        Console.SetOut(stdout);

        // Ctrl+C is a scenario, not a shutdown. The whole point is to prove the phone's
        // interrupt button reached a program that was running at the desk, and a program
        // that dies on arrival cannot say so.
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Interrupted();
        };

        Banner(args);
        Prompt();

        return Loop();
    }

    private static long _lastInterrupt;

    /// <summary>
    /// Reports an interrupt, once, however it arrived.
    /// <para>
    /// A pseudoconsole may deliver <c>0x03</c> to the hosted program as a console
    /// control event or as a byte on standard input, depending on the input modes in
    /// force. Both are the interrupt arriving, so both are accepted; the guard is there
    /// because a delivery by both routes would otherwise print the marker twice and a
    /// test counting occurrences would be flaky for a reason that has nothing to do
    /// with the product.
    /// </para>
    /// </summary>
    private static void Interrupted()
    {
        long now = Environment.TickCount64;

        if (now - Interlocked.Exchange(ref _lastInterrupt, now) < 250)
        {
            return;
        }

        Console.WriteLine();
        Console.WriteLine("E2E-INTERRUPTED");
        Prompt();
    }

    /// <summary>
    /// The opening screen: fixed text, in colour, at a known place.
    /// <para>
    /// Colour and cursor positioning are here deliberately. A test that attaches
    /// mid-session is really asking whether the emulator understood what it was sent
    /// and whether the re-serializer could put it back, and plain ASCII would pass that
    /// test without either of them working properly.
    /// </para>
    /// </summary>
    private static void Banner(string[] args)
    {
        Console.Write("\u001b[2J\u001b[H");

        Console.WriteLine("\u001b[1;36m1RemoteCLI end-to-end script\u001b[0m");
        Console.WriteLine("\u001b[32mgreen\u001b[0m \u001b[31mred\u001b[0m \u001b[1mbold\u001b[0m \u001b[4munderline\u001b[0m");

        if (args.Length > 0)
        {
            Console.WriteLine($"args: {string.Join(' ', args)}");
        }

        Console.WriteLine(Ready);
    }

    private static void Prompt() => Console.Write("Continue? (y/n) ");

    /// <summary>
    /// Reads one key at a time and answers each one with a marker.
    /// <para>
    /// A key at a time, not a line at a time: the phone sends keystrokes as they are
    /// typed, and the interesting failure is a keystroke that never arrives — which a
    /// program waiting for a newline would report as nothing happening at all, on a
    /// build where everything worked.
    /// </para>
    /// <para>
    /// <c>intercept</c> is true so that what appears on the screen is what this program
    /// decided to print rather than what the console echoed on its behalf. A test
    /// asserting on an echo is asserting on the console's behaviour; asserting on a
    /// marker is asserting that the byte was actually delivered to the program.
    /// </para>
    /// </summary>
    private static int Loop()
    {
        while (true)
        {
            ConsoleKeyInfo pressed;

            try
            {
                pressed = Console.ReadKey(intercept: true);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                // The pseudoconsole went away — the session was closed at the desk,
                // which is a scenario rather than a fault.
                return 0;
            }

            char key = pressed.KeyChar;

            switch (char.ToLowerInvariant(key))
            {
                case 'y':
                    Console.WriteLine("y");
                    Console.WriteLine("E2E-PROCEEDING");
                    Prompt();
                    break;

                case 'n':
                    Console.WriteLine("n");
                    Console.WriteLine("E2E-ABORTED");
                    Prompt();
                    break;

                case 'q':
                    Console.WriteLine("q");
                    Console.WriteLine("E2E-BYE");
                    return 0;

                case 'w':
                    // How wide the pseudoconsole believes it is. Reported rather than
                    // assumed, because a resize that reflowed the display without
                    // reaching the program would look identical from the browser — and
                    // a coding agent that lays out its own output is exactly the kind
                    // of program that would then get it wrong.
                    Console.WriteLine("w");
                    Console.WriteLine($"E2E-WIDTH {Width()} buffer={Buffer()}");
                    Prompt();
                    break;

                case 't':
                    // The instant this keystroke arrived, in UTC ticks, printed by the
                    // program that received it.
                    //
                    // This is how the latency measurements get an honest number for each
                    // leg separately. A test can time a round trip on its own, but a
                    // round trip is input latency plus output latency plus however long
                    // the program took to answer, and the three are not separable from
                    // outside. With a stamp taken inside the program the caller can
                    // subtract: its own send time to this stamp is the input leg, this
                    // stamp to the frame landing is the output leg. Both processes are on
                    // one machine, so the clock is shared and the subtraction is valid.
                    Console.WriteLine($"E2E-TS {DateTime.UtcNow.Ticks}");
                    break;

                case '\u0003':
                    Interrupted();
                    break;

                case '\r':
                case '\n':
                    Console.WriteLine();
                    Console.WriteLine("E2E-RETURN");
                    Prompt();
                    break;

                default:
                    // Echoed rather than swallowed: the assertion "what I typed appeared
                    // on the screen" is the simplest proof that input reached the desk,
                    // and it should work for any key, not only the ones with meanings.
                    if (key != '\0')
                    {
                        Console.Write(key);
                    }

                    break;
            }
        }
    }

    private static int Buffer()
    {
        try
        {
            return Console.BufferWidth;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    private static int Width()
    {
        try
        {
            return Console.WindowWidth;
        }
        catch (IOException)
        {
            return 0;
        }
    }
}
