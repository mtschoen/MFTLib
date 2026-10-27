using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace MFTLib;

/// <summary>
///     Who may reach the broker's pipes. The unelevated client creates every pipe server with a
///     DACL naming only its own user SID, and the elevated broker, after connecting, checks that
///     the server's DACL names only the broker's own user SID. <c>PipeOptions.CurrentUserOnly</c>
///     is not used on the elevated end: .NET compares the pipe owner with the token's owner SID,
///     which is Administrators in an elevated token and the user in the unelevated one, so it
///     refuses the connection this design exists to make (dotnet/runtime issue 123903).
/// </summary>
static class BrokerPipeAccess
{
    /// <summary>Options for the broker's client ends; Windows relies on <see cref="VerifyServerRestrictedToCurrentUser" /> instead of the owner check.</summary>
    internal static PipeOptions ClientOptions =>
        OperatingSystem.IsWindows() ? PipeOptions.Asynchronous : PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly;

    /// <summary>Creates a single-instance server pipe that only the current user can open.</summary>
    internal static NamedPipeServerStream CreateServer(string pipeName)
    {
        return OperatingSystem.IsWindows()
            ? CreateWindowsServer(pipeName)
            : new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    }

    [SupportedOSPlatform("windows")]
    static NamedPipeServerStream CreateWindowsServer(string pipeName)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new InvalidOperationException("The current user has no security identifier.");
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
            AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, 0, 0, security);
    }

    /// <summary>Throws when the connected server pipe grants access to anyone but the current user.</summary>
    /// <exception cref="UnauthorizedAccessException">The pipe's DACL names a principal other than the current user.</exception>
    internal static void VerifyServerRestrictedToCurrentUser(NamedPipeClientStream client)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        VerifyWindows(client);
    }

    [SupportedOSPlatform("windows")]
    static void VerifyWindows(NamedPipeClientStream client)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new InvalidOperationException("The current user has no security identifier.");
        if (!IsRestrictedToUser(client.GetAccessControl(), user))
        {
            throw new UnauthorizedAccessException(
                "The broker pipe grants access to a principal other than the current user, so it was not connected.");
        }
    }

    /// <summary>True when every access rule in the descriptor allows exactly <paramref name="user" /> and at least one exists.</summary>
    [SupportedOSPlatform("windows")]
    internal static bool IsRestrictedToUser(PipeSecurity security, SecurityIdentifier user)
    {
        var restricted = false;
        foreach (PipeAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow || !user.Equals(rule.IdentityReference))
            {
                return false;
            }

            restricted = true;
        }

        return restricted;
    }
}
