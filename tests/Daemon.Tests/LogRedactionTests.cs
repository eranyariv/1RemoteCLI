using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using OneRemoteCli.Daemon.Diagnostics;
using OneRemoteCli.Protocol.Diagnostics;

namespace OneRemoteCli.Daemon.Tests;

/// <summary>
/// The canary: a secret goes through the terminal, and no log anywhere has it.
/// <para>
/// This is the only test that can catch the real failure. §7.3 promises that terminal
/// content is never logged, and the vocabulary in <see cref="LogEvents"/> is what
/// enforces it — but the vocabulary only governs code that uses it. Any component
/// holding an <see cref="ILogger"/> can still call the framework's own extension
/// methods and interpolate whatever it likes. So rather than inspecting the code,
/// this runs the product end to end with a distinctive string flowing through it and
/// asserts the string appears in nothing that was written down.
/// </para>
/// <para>
/// The secret is deliberately shaped like a real one. A canary of "hello" would be
/// matched by accident by half the framework's log lines; a canary that looks like an
/// API key is a string that can only have come from the terminal.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class LogRedactionTests : IAsyncLifetime
{
    private const string Secret = "sk-canary-9f2b7a41d3e84c06-DO-NOT-LOG";

    private EndToEndHarness _harness = null!;

    public async Task InitializeAsync() => _harness = await EndToEndHarness.StartAsync();

    public async Task DisposeAsync() => await _harness.DisposeAsync();

    [Fact]
    public async Task WhatComesOutOfTheTerminalIsNeverWrittenToALog()
    {
        WrappedShell shell = await _harness.StartShellAsync(displayName: Secret + "-name");
        PhoneClient phone = await _harness.ConnectPhoneAsync();

        await EndToEndHarness.WaitUntilAsync(
            async () => (await phone.ListMachinesAsync()).Machines.Any(m => m.Sessions.Any(s => s.SessionId == shell.SessionId)),
            "the session to be listed");

        Assert.Null(await phone.AttachAsync(_harness.MachineId, shell.SessionId));

        // Output: the shell prints the secret, and the phone sees it. Waiting for the
        // screen matters — asserting on logs before the bytes have travelled would
        // pass without proving anything.
        await shell.Pty.WriteAsync($"echo {Secret}\r");
        await phone.WaitForScreenAsync(Secret);

        // Input: the same string typed from the phone, which travels the other way
        // through a different set of code paths.
        Assert.Null(await phone.TypeAsync(shell.SessionId, $"echo in-{Secret}\r"));
        await phone.WaitForScreenAsync($"in-{Secret}");

        string logs = _harness.Logs.All();

        Assert.DoesNotContain(Secret, logs, StringComparison.OrdinalIgnoreCase);

        // And prove the sink was actually collecting, so the assertion above cannot
        // pass by virtue of an empty string.
        Assert.NotEmpty(_harness.Logs.Records);
        Assert.Contains(_harness.MachineId, logs, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheDisplayNameTheUserTypedIsNotLoggedEither()
    {
        // A session's display name is content in the same sense: the user typed it,
        // it can hold anything, and it is on screen. The program name is different —
        // we chose to record that, because it is how you tell one session from
        // another in a log and it comes from the command line, not from the terminal.
        WrappedShell shell = await _harness.StartShellAsync(displayName: Secret);
        PhoneClient phone = await _harness.ConnectPhoneAsync();

        await EndToEndHarness.WaitUntilAsync(
            async () => (await phone.ListMachinesAsync()).Machines.Any(m => m.Sessions.Any(s => s.SessionId == shell.SessionId)),
            "the session to be listed");

        string logs = _harness.Logs.All();

        Assert.DoesNotContain(Secret, logs, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cmd.exe", logs, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLogFileOnDiskHasNothingInItEither()
    {
        // The in-memory sink proves nothing about what FileLogger writes: it has its
        // own formatter, and a formatter is exactly where a payload would be
        // reintroduced by someone rendering structured values "for readability".
        string directory = Path.Combine(Path.GetTempPath(), $"1remote-logtest-{Guid.NewGuid():n}");

        try
        {
            // The provider directly rather than through AgentLogging, because the two
            // events that carry sizes are Debug and the factory's default level would
            // filter them out — leaving the test asserting on lines that were never
            // written, which is the shape of a redaction test that proves nothing.
            using (var provider = new FileLogger(directory))
            {
                ILogger logger = provider.CreateLogger("canary");

                logger.SessionOpened("machine-1", "session-1", "cmd.exe");
                logger.OutputRelayed("session-1", 42, Secret.Length);
                logger.InputDelivered("session-1", Secret.Length);
                logger.SessionClosed("machine-1", "session-1", 0);
            }

            string written = string.Concat(
                Directory.EnumerateFiles(directory).Select(File.ReadAllText));

            Assert.DoesNotContain(Secret, written, StringComparison.OrdinalIgnoreCase);

            // Sizes, not content: what you need to debug framing, and nothing more.
            Assert.Contains($"Relayed {Secret.Length} bytes", written, StringComparison.Ordinal);
            Assert.Contains($"Delivered {Secret.Length} bytes", written, StringComparison.Ordinal);
            Assert.Contains("session-1", written, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
