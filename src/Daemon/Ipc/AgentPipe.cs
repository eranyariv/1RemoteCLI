using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace OneRemoteCli.Daemon.Ipc;

/// <summary>
/// Naming and access control for the local channel between the wrapper processes
/// and the tray agent.
/// </summary>
[SupportedOSPlatform("windows")]
public static class AgentPipe
{
    private const string Prefix = "1remotecli-agent-";

    /// <summary>
    /// How long a wrapper keeps trying before giving up. Long enough to cover an
    /// agent that is still starting up at logon, short enough that a user who
    /// genuinely has no agent is told so rather than left watching a blank terminal.
    /// </summary>
    public static readonly TimeSpan ConnectRetryWindow = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Pipe name for the user this process is running as.
    /// <para>
    /// The SID is part of the name, not just the ACL, so two people signed in to the
    /// same machine get one agent each instead of contending for a single pipe.
    /// </para>
    /// </summary>
    public static string NameForCurrentUser() => NameFor(CurrentUserSid());

    public static string NameFor(SecurityIdentifier user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return Prefix + user.Value;
    }

    public static SecurityIdentifier CurrentUserSid() =>
        WindowsIdentity.GetCurrent().User
        ?? throw new InvalidOperationException("The current Windows identity has no user SID.");

    /// <summary>
    /// Builds the pipe's security descriptor: full control for the owning user, and
    /// nothing for anyone else.
    /// <para>
    /// This is a security control rather than a detail. Everything a session contains
    /// crosses this pipe in the clear — output that may include secrets, and input
    /// that is executed. Any local process able to open the pipe could read that
    /// output or type into a live shell. Administrators and SYSTEM are deliberately
    /// left out: they can take ownership anyway, so granting them access would only
    /// widen the set of processes that reach the pipe by accident.
    /// </para>
    /// </summary>
    public static PipeSecurity SecurityForCurrentUser()
    {
        SecurityIdentifier user = CurrentUserSid();

        var security = new PipeSecurity();
        security.SetOwner(user);
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));

        return security;
    }
}
