using OneRemoteCli.Hub.Push;

namespace OneRemoteCli.Hub.Tests;

/// <summary>
/// Who gets pushed to, and what they are handed.
/// <para>
/// Both halves are pure, and both are otherwise only observable on a phone. A
/// duplicated subscription means every notification arrives twice; a mangled deep
/// link means a notification opens the wrong session, or none.
/// </para>
/// </summary>
public sealed class PushStoreTests
{
    private static PushSubscription Sub(string endpoint) => new(endpoint, "p256dh", "auth");

    [Fact]
    public void ARegisteredSubscriptionComesBack()
    {
        PushSubscriptionStore store = new();

        Assert.True(store.Register("alice", Sub("https://push/1")));

        PushSubscription only = Assert.Single(store.For("alice"));
        Assert.Equal("https://push/1", only.Endpoint);
    }

    [Fact]
    public void TheSamePhoneRegisteringAgainDoesNotBecomeTwoPhones()
    {
        // A phone re-registers on every reconnect. Keyed by connection instead of
        // endpoint, an overnight phone would accumulate an entry per wake-up and
        // then buzz once per entry.
        PushSubscriptionStore store = new();
        store.Register("alice", Sub("https://push/1"));

        Assert.False(store.Register("alice", Sub("https://push/1")));

        Assert.Single(store.For("alice"));
    }

    [Fact]
    public void RotatedKeysForTheSameEndpointReplaceTheOldOnes()
    {
        PushSubscriptionStore store = new();
        store.Register("alice", Sub("https://push/1"));

        Assert.True(store.Register("alice", new PushSubscription("https://push/1", "new-p256dh", "new-auth")));

        PushSubscription only = Assert.Single(store.For("alice"));
        Assert.Equal("new-p256dh", only.P256dh);
    }

    [Fact]
    public void OnePersonCanHaveSeveralDevices()
    {
        PushSubscriptionStore store = new();
        store.Register("alice", Sub("https://push/phone"));
        store.Register("alice", Sub("https://push/tablet"));

        Assert.Equal(2, store.For("alice").Count);
    }

    [Fact]
    public void SubscriptionsAreNotSharedBetweenUsers()
    {
        PushSubscriptionStore store = new();
        store.Register("alice", Sub("https://push/1"));

        Assert.Empty(store.For("bob"));
    }

    [Fact]
    public void ForgettingRemovesOnlyThatDevice()
    {
        PushSubscriptionStore store = new();
        store.Register("alice", Sub("https://push/phone"));
        store.Register("alice", Sub("https://push/tablet"));

        Assert.True(store.Forget("alice", "https://push/phone"));

        PushSubscription only = Assert.Single(store.For("alice"));
        Assert.Equal("https://push/tablet", only.Endpoint);
    }

    [Fact]
    public void ForgettingSomethingUnknownIsNotAnError()
    {
        // Two notifications to a dead endpoint can race, and both get a 410.
        PushSubscriptionStore store = new();
        store.Register("alice", Sub("https://push/1"));

        Assert.False(store.Forget("alice", "https://push/other"));
        Assert.False(store.Forget("nobody", "https://push/1"));
    }

    [Fact]
    public void AUserWithNoDevicesLeftIsNotKeptAround()
    {
        // Otherwise the hub holds an entry for every account that ever registered:
        // a slow leak in a process meant to run for months.
        PushSubscriptionStore store = new();
        store.Register("alice", Sub("https://push/1"));
        store.Forget("alice", "https://push/1");

        Assert.Equal(0, store.UserCount);
    }

    [Fact]
    public void ARegistrationNeedsAUser()
    {
        PushSubscriptionStore store = new();

        Assert.Throws<ArgumentException>(() => store.Register(" ", Sub("https://push/1")));
    }
}

public sealed class PushPayloadTests
{
    [Fact]
    public void TheDeepLinkNamesTheSession()
    {
        Assert.Equal("/?machine=m1&session=s1", PushPayload.DeepLink("m1", "s1"));
    }

    [Fact]
    public void TheDeepLinkEscapesIdsSoTheyCannotForgeTheQuery()
    {
        // Session ids come from a machine's own naming, not from the hub. An id
        // containing "&" or "=" would otherwise rewrite the rest of the link.
        string link = PushPayload.DeepLink("a&b", "c=d e");

        Assert.Equal("/?machine=a%26b&session=c%3Dd%20e", link);
    }

    [Fact]
    public void AWaitingNotificationLeadsWithThePrompt()
    {
        // The prompt is the most useful thing on a lock screen: the user knows what
        // they started; what they do not know is what it decided to ask.
        PushPayload payload = PushPayload.AwaitingInput("desk", "claude", "Allow file edit?", "/?machine=m&session=s");

        Assert.Equal("claude is waiting", payload.Title);
        Assert.Equal("Allow file edit?", payload.Body);
        Assert.Equal("/?machine=m&session=s", payload.Url);
    }

    [Fact]
    public void AWaitingNotificationWithNoPromptStillSaysWhere()
    {
        PushPayload payload = PushPayload.AwaitingInput("desk", "claude", "   ", "/?machine=m&session=s");

        Assert.Equal("On desk.", payload.Body);
    }

    [Fact]
    public void AWaitingNotificationIsPerishableAndAFinishedOneIsNot()
    {
        // A question that has since been answered is a lie on a lock screen. A
        // program that finished is still true however late it arrives.
        Assert.True(PushPayload.AwaitingInput("desk", "claude", "?", "/").Perishable);
        Assert.False(PushPayload.Finished("desk", "claude", 0, "/").Perishable);
    }

    [Fact]
    public void AFailureSaysSo()
    {
        PushPayload payload = PushPayload.Finished("desk", "build", 1, "/?machine=m&session=s");

        Assert.Equal("build failed", payload.Title);
        Assert.Contains("Exit code 1", payload.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void NotificationsAboutOneSessionShareATagAndDifferentSessionsDoNot()
    {
        // The tag is what makes a session that asks twice occupy one row on the lock
        // screen showing the current question, rather than two the user must read in
        // order to work out which is still true.
        string first = PushPayload.DeepLink("m", "a");
        string second = PushPayload.DeepLink("m", "b");

        Assert.Equal(
            PushPayload.AwaitingInput("desk", "claude", "one?", first).Tag,
            PushPayload.AwaitingInput("desk", "claude", "two?", first).Tag);
        Assert.NotEqual(
            PushPayload.AwaitingInput("desk", "claude", "?", first).Tag,
            PushPayload.AwaitingInput("desk", "claude", "?", second).Tag);
    }

    [Fact]
    public void TheJsonIsTheShapeTheServiceWorkerReads()
    {
        // The service worker's reader is in another language and another repo folder;
        // this is the only place the two can be checked against each other.
        string json = PushPayload.AwaitingInput("desk", "claude", "Allow?", "/?machine=m&session=s").ToJson();

        Assert.Contains("\"title\":\"claude is waiting\"", json, StringComparison.Ordinal);
        Assert.Contains("\"body\":\"Allow?\"", json, StringComparison.Ordinal);
        Assert.Contains("\"url\":\"/?machine=m\\u0026session=s\"", json, StringComparison.Ordinal);
        Assert.Contains("\"perishable\":true", json, StringComparison.Ordinal);
    }
}

/// <summary>
/// The keypair that identifies this hub to the push services.
/// <para>
/// Generated once and then pasted into app settings, which is exactly why it is
/// tested: a key with the wrong shape is accepted by every tool that touches it and
/// only fails on a real phone, silently.
/// </para>
/// </summary>
public sealed class VapidKeysTests
{
    [Fact]
    public void ThePublicKeyIsAnUncompressedP256Point()
    {
        // 0x04 then X then Y. A compressed point is half the length, valid EC, and
        // rejected by every browser.
        (string publicKey, _) = VapidKeys.Generate();

        byte[] decoded = Decode(publicKey);
        Assert.Equal(65, decoded.Length);
        Assert.Equal(0x04, decoded[0]);
    }

    [Fact]
    public void ThePrivateKeyIsTheThirtyTwoByteScalar()
    {
        (_, string privateKey) = VapidKeys.Generate();

        Assert.Equal(32, Decode(privateKey).Length);
    }

    [Fact]
    public void BothAreBase64UrlWithoutPadding()
    {
        // Padded base64 survives a round trip through most code and then fails at
        // the browser, which is the worst place to find out.
        (string publicKey, string privateKey) = VapidKeys.Generate();

        foreach (string key in new[] { publicKey, privateKey })
        {
            Assert.DoesNotContain('=', key);
            Assert.DoesNotContain('+', key);
            Assert.DoesNotContain('/', key);
        }
    }

    [Fact]
    public void EveryKeypairIsANewOne()
    {
        Assert.NotEqual(VapidKeys.Generate().PublicKey, VapidKeys.Generate().PublicKey);
    }

    private static byte[] Decode(string base64Url)
    {
        string padded = base64Url.Replace('-', '+').Replace('_', '/');

        return Convert.FromBase64String(padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '='));
    }
}
