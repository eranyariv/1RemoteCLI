using System.Reflection;
using System.Text;
using Microsoft.Extensions.Logging;
using OneRemoteCli.Protocol.Diagnostics;

namespace OneRemoteCli.Daemon.Tests;

/// <summary>
/// The tests that hold §7.3: terminal content is never logged, at any level.
/// <para>
/// Two of them, because there are two ways to break the rule. <see
/// cref="LogEventsTests"/> stops someone adding an event that <em>could</em> carry
/// a payload. <see cref="LogRedactionTests"/> proves that what is there today
/// doesn't. Neither alone is enough: a reflection test passes happily while a
/// component logs a payload through some other logger, and a canary test passes
/// happily right up until the day someone adds the leaky event.
/// </para>
/// </summary>
public class LogEventsTests
{
    /// <summary>
    /// The types that can only be terminal content. Nothing in a log line needs to
    /// be a byte array or a character buffer; if an event wants one, the event is
    /// about to write out what somebody typed.
    /// </summary>
    private static readonly Type[] Payloads =
    [
        typeof(byte[]),
        typeof(ReadOnlyMemory<byte>),
        typeof(Memory<byte>),
        typeof(ReadOnlySpan<byte>),
        typeof(Span<byte>),
        typeof(char[]),
        typeof(StringBuilder),
        typeof(Stream),
    ];

    [Fact]
    public void NoLogEventCanBeHandedAPayload()
    {
        foreach (MethodInfo method in Events())
        {
            foreach (ParameterInfo parameter in method.GetParameters())
            {
                Assert.DoesNotContain(parameter.ParameterType, Payloads);
            }
        }
    }

    [Fact]
    public void NoLogEventTakesAFreeFormMessage()
    {
        // The one parameter name that would undo the whole design. A `string
        // message` is a hole big enough for an entire screen, and the caller does
        // the interpolating, so nothing downstream can tell what went into it.
        foreach (MethodInfo method in Events())
        {
            foreach (ParameterInfo parameter in method.GetParameters())
            {
                if (parameter.ParameterType != typeof(string))
                {
                    continue;
                }

                Assert.False(
                    parameter.Name is "message" or "text" or "content" or "output" or "line",
                    $"{method.Name} takes a free-form string '{parameter.Name}'.");
            }
        }
    }

    [Fact]
    public void EveryLogEventHasAFixedTemplate()
    {
        // The templates are what make the vocabulary closed. An event without one is
        // a source-generator error rather than a silent hole, but assert it anyway:
        // this test is also the place a future reviewer looks to learn the rule.
        foreach (MethodInfo method in Events())
        {
            LoggerMessageAttribute? attribute = method.GetCustomAttribute<LoggerMessageAttribute>();

            Assert.NotNull(attribute);
            Assert.False(string.IsNullOrWhiteSpace(attribute.Message));
        }
    }

    [Fact]
    public void EveryEventIdIsUnique()
    {
        // A duplicated id makes two different events indistinguishable in a log, and
        // silently breaks AgentHubClient.Once, which dedupes by id.
        int[] ids = [.. Events()
            .Select(m => m.GetCustomAttribute<LoggerMessageAttribute>()!.EventId)];

        Assert.Equal(ids.Length, ids.Distinct().Count());
    }

    [Fact]
    public void ThereAreEventsToFind()
    {
        // Guards the three tests above: reflection that silently matches nothing
        // passes forever, including after someone renames or moves the class.
        Assert.True(Events().Count >= 15);
    }

    private static IReadOnlyList<MethodInfo> Events() =>
    [
        .. typeof(LogEvents)
            .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttribute<LoggerMessageAttribute>() is not null),
    ];
}
