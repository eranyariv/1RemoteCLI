using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OneRemoteCli.Hub.Ops;

namespace OneRemoteCli.Hub.Tests;

/// <summary>
/// The tests that make "counts and statistics only" a property of the code rather than a
/// promise in a document.
/// <para>
/// The operator channel reports to one person about everybody else's use of the product.
/// Every other outbound path in the hub — push, the client fan-out — carries a machine or
/// session name <i>to the person who owns it</i>, which is correct there and a disclosure
/// here. The difference is invisible at a call site: <c>SessionAddress</c> is right next
/// to the code that reports a number, its <c>SessionName</c> is one dot away, and nothing
/// about writing that line feels like a mistake.
/// </para>
/// <para>
/// So the rule is enforced structurally, three ways, and this file is two of them.
/// </para>
/// </summary>
public class OperatorVocabularyTests
{
    private static readonly Assembly Hub = typeof(OperatorMessage).Assembly;
    private static readonly Assembly Protocol = typeof(Protocol.Hub.TerminalOutputNotification).Assembly;

    /// <summary>
    /// Types that carry a human-chosen name or a routable identifier, and so may never
    /// appear anywhere in the reporting namespace.
    /// <para>
    /// The whole protocol assembly is banned rather than a list of its types, because a
    /// list only covers the shapes that existed when it was written. The next message
    /// contract someone adds is banned by default, which is the right way round.
    /// </para>
    /// </summary>
    private static readonly HashSet<string> AlsoBanned = new(StringComparer.Ordinal)
    {
        "OneRemoteCli.Hub.Push.PushPayload",
        "OneRemoteCli.Hub.Relay.SessionAddress",
        "OneRemoteCli.Hub.Relay.RegisteredMachine",
    };

    /// <summary>
    /// No member in the reporting namespace so much as mentions a type that carries a
    /// name.
    /// <para>
    /// Signatures rather than bodies, deliberately. A type you cannot name is a type you
    /// cannot read a name out of, and unlike a review of every line this keeps holding
    /// for code nobody has written yet.
    /// </para>
    /// </summary>
    [Fact]
    public void NothingInTheReportingNamespaceCanEvenNameATypeThatCarriesAName()
    {
        List<string> offences = [];

        foreach (Type type in Reporting())
        {
            foreach ((MemberInfo member, Type referenced) in Signature(type))
            {
                if (Banned(referenced))
                {
                    offences.Add($"{type.Name}.{member.Name} mentions {referenced.FullName}");
                }
            }
        }

        Assert.Empty(offences);
    }

    /// <summary>
    /// Every message the operator can receive is declared inside <c>OperatorMessage</c>.
    /// <para>
    /// This is what makes the vocabulary closed rather than merely tidy. The base type's
    /// only constructor is private, so a new message shape cannot be declared next to the
    /// code that wants to send one — it has to be added to the one file where the rule is
    /// written down and where a reviewer is looking for it.
    /// </para>
    /// </summary>
    [Fact]
    public void TheMessageVocabularyIsClosed()
    {
        Type[] shapes = [.. Hub.GetTypes().Where(type => type.IsSubclassOf(typeof(OperatorMessage)))];

        Assert.NotEmpty(shapes);
        Assert.All(shapes, shape => Assert.Equal(typeof(OperatorMessage), shape.DeclaringType));

        // Belt and braces: what actually stops an outside subclass is the private
        // constructor, so assert that rather than trusting the arrangement above.
        Assert.All(
            typeof(OperatorMessage).GetConstructors(BindingFlags.Instance | BindingFlags.Public),
            constructor => Assert.NotEmpty(constructor.GetParameters()));
    }

    /// <summary>
    /// The third enforcement, and the only one that exercises real data: session names go
    /// into the counters on the hot path and must not come out of anything.
    /// <para>
    /// A session id does legitimately enter this namespace — it is the only way to pair an
    /// open with a close and get a duration — so the guarantee is that it is hashed on
    /// arrival and that nothing derived from it is ever rendered or persisted. That is a
    /// claim about behaviour, so it is tested by behaviour.
    /// </para>
    /// </summary>
    [Fact]
    public void SessionIdentifiersSurviveNowhereTheOperatorCanSeeThem()
    {
        const string Canary = "CANARY-a-session-id-nobody-else-should-read";

        string path = Path.Combine(Path.GetTempPath(), $"operator-{Guid.NewGuid():N}.json");
        var time = new ManualTime(new DateTimeOffset(2026, 3, 2, 9, 0, 0, TimeSpan.Zero));
        var options = new OperatorChannelOptions { StatePath = path, MonthlyCost = 13m };
        var notifier = new CollectingNotifier();

        try
        {
            var store = new OperatorStateStore(Options.Create(options), NullLogger<OperatorStateStore>.Instance);
            var counters = new UsageCounters(store, notifier, time);

            counters.AccountSeen("tenant:user", "someone@example.com");
            counters.SessionOpened("tenant:user", Canary);
            counters.BytesRelayed("tenant:user", 4096);
            time.Advance(TimeSpan.FromMinutes(12));
            counters.SessionClosed("tenant:user", Canary);
            counters.Drain();
            store.Flush();

            OperatorMessage.WeeklyDigest digest = store.Read(
                state => DigestBuilder.Build(state, options, time.GetUtcNow()));

            // The numbers did arrive: this is a real recording, not an empty one that
            // would pass the assertions below for the wrong reason.
            Assert.Equal(1, digest.Sessions);
            Assert.Equal(4096, digest.Bytes);
            Assert.Equal(TimeSpan.FromMinutes(12), digest.Duration);

            Assert.DoesNotContain(Canary, digest.Render(), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Canary, File.ReadAllText(path), StringComparison.OrdinalIgnoreCase);

            foreach (OperatorMessage message in notifier.Messages)
            {
                Assert.DoesNotContain(Canary, message.Render(), StringComparison.OrdinalIgnoreCase);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>The digest names accounts, because onboarding needs it — and never keys.</summary>
    [Fact]
    public void ADigestNamesAnAccountRatherThanItsUserKey()
    {
        string path = Path.Combine(Path.GetTempPath(), $"operator-{Guid.NewGuid():N}.json");
        var time = new ManualTime(new DateTimeOffset(2026, 3, 2, 9, 0, 0, TimeSpan.Zero));
        var options = new OperatorChannelOptions { StatePath = path };

        try
        {
            var store = new OperatorStateStore(Options.Create(options), NullLogger<OperatorStateStore>.Instance);
            var counters = new UsageCounters(store, new CollectingNotifier(), time);

            counters.AccountSeen("9f1c-tenant:3ab7-oid", "someone@example.com");
            counters.SessionOpened("9f1c-tenant:3ab7-oid", "session");
            counters.Drain();

            string rendered = store.Read(state => DigestBuilder.Build(state, options, time.GetUtcNow())).Render();

            Assert.Contains("someone@example.com", rendered, StringComparison.Ordinal);
            Assert.DoesNotContain("3ab7-oid", rendered, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static IEnumerable<Type> Reporting() =>
        Hub.GetTypes().Where(type => type.Namespace == "OneRemoteCli.Hub.Ops");

    /// <summary>
    /// Every type mentioned in the declared surface of a type: what its members take,
    /// return and hold.
    /// </summary>
    private static IEnumerable<(MemberInfo Member, Type Referenced)> Signature(Type type)
    {
        const BindingFlags Declared =
            BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.Instance | BindingFlags.Static;

        foreach (FieldInfo field in type.GetFields(Declared))
        {
            foreach (Type referenced in Unwrap(field.FieldType))
            {
                yield return (field, referenced);
            }
        }

        foreach (PropertyInfo property in type.GetProperties(Declared))
        {
            foreach (Type referenced in Unwrap(property.PropertyType))
            {
                yield return (property, referenced);
            }
        }

        foreach (MethodBase method in type.GetMethods(Declared).Cast<MethodBase>().Concat(type.GetConstructors(Declared)))
        {
            foreach (Type referenced in method.GetParameters().SelectMany(parameter => Unwrap(parameter.ParameterType)))
            {
                yield return (method, referenced);
            }

            if (method is MethodInfo function)
            {
                foreach (Type referenced in Unwrap(function.ReturnType))
                {
                    yield return (method, referenced);
                }
            }
        }
    }

    /// <summary>
    /// Opens a type up into everything it is made of, so a banned type cannot hide inside
    /// a <c>List&lt;&gt;</c>, an array, a <c>Task&lt;&gt;</c> or a by-ref parameter.
    /// </summary>
    private static IEnumerable<Type> Unwrap(Type type)
    {
        Type current = type.IsByRef || type.IsArray || type.IsPointer
            ? type.GetElementType() ?? type
            : type;

        yield return current;

        if (!current.IsGenericType)
        {
            yield break;
        }

        foreach (Type argument in current.GetGenericArguments().SelectMany(Unwrap))
        {
            yield return argument;
        }
    }

    private static bool Banned(Type type) =>
        type.Assembly == Protocol || AlsoBanned.Contains(type.FullName ?? string.Empty);
}

/// <summary>Keeps what was sent, so a test can read the operator's chat.</summary>
internal sealed class CollectingNotifier : IOperatorNotifier
{
    private readonly List<OperatorMessage> _messages = [];

    public IReadOnlyList<OperatorMessage> Messages => _messages;

    public void Send(OperatorMessage message) => _messages.Add(message);
}
