using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.Time.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
public class BrokerPipeHardeningTests
{
    static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

    public TestContext TestContext { get; set; } = null!;

    [SupportedOSPlatform("windows")]
    static SecurityIdentifier CurrentUser()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User!;
    }

    [SupportedOSPlatform("windows")]
    static PipeSecurity Descriptor(AccessControlType type, params SecurityIdentifier[] principals)
    {
        var security = new PipeSecurity();
        foreach (var principal in principals)
        {
            security.AddAccessRule(new PipeAccessRule(principal, PipeAccessRights.ReadWrite, type));
        }

        return security;
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    [SupportedOSPlatform("windows")]
    public void IsRestrictedToUser_UserOnlyDescriptorOwnedByAdministrators_Accepts()
    {
        // The elevated broker's token owner is Administrators while its user SID is the same as the
        // unelevated client's. The check must key on the user SID in the DACL, never on the owner.
        var user = CurrentUser();
        var security = Descriptor(AccessControlType.Allow, user);
        security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));

        Assert.IsTrue(BrokerPipeAccess.IsRestrictedToUser(security, user));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    [SupportedOSPlatform("windows")]
    public void IsRestrictedToUser_AnyOtherPrincipalDenyRuleOrNoRule_Rejects()
    {
        var user = CurrentUser();
        var everyone = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);

        Assert.IsFalse(BrokerPipeAccess.IsRestrictedToUser(Descriptor(AccessControlType.Allow, user, everyone), user));
        Assert.IsFalse(BrokerPipeAccess.IsRestrictedToUser(Descriptor(AccessControlType.Allow, user, system), user));
        Assert.IsFalse(BrokerPipeAccess.IsRestrictedToUser(Descriptor(AccessControlType.Allow, system), user));
        Assert.IsFalse(BrokerPipeAccess.IsRestrictedToUser(Descriptor(AccessControlType.Deny, user), user));
        Assert.IsFalse(BrokerPipeAccess.IsRestrictedToUser(new PipeSecurity(), user));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    public void ClientOptions_OnWindows_DoNotUseTheTokenOwnerCheck()
    {
        // PipeOptions.CurrentUserOnly compares the pipe owner with the token owner, which an
        // elevated token (owner Administrators) fails against an unelevated server
        // (dotnet/runtime issue 123903). The DACL check replaces it.
        Assert.AreEqual(PipeOptions.Asynchronous, BrokerPipeAccess.ClientOptions);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    [SupportedOSPlatform("windows")]
    public async Task LaunchAsync_ControlPipe_IsOpenToTheCurrentUserOnly()
    {
        using var cancellation = new CancellationTokenSource(HangGuard);
        var token = cancellation.Token;
        Task? checkTask = null;
        var launchBroker = new Func<string, bool>(arguments =>
        {
            var pipeName = arguments.Split(' ')[2];
            checkTask = Task.Run(async () =>
            {
                await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
                    BrokerPipeAccess.ClientOptions);
                await client.ConnectAsync(token);
                Assert.IsTrue(BrokerPipeAccess.IsRestrictedToUser(client.GetAccessControl(), CurrentUser()));
            }, TestContext.CancellationToken);
            return true;
        });

        await using var process = await BrokerProcess.LaunchAsync(launchBroker, null,
            BrokerProcess.CreateRealDriveBlockSection, cancellation.Token);
        await checkTask!.WaitAsync(cancellation.Token);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    [SupportedOSPlatform("windows")]
    public async Task Listen_DrivePipe_IsOpenToTheCurrentUserOnly()
    {
        using var cancellation = new CancellationTokenSource(HangGuard);
        var pipeName = "mftlib-hardening-test-" + Guid.NewGuid().ToString("N");
        await using var listener = new NamedPipeBrokerPipeFactory().Listen(pipeName);
        var accepted = listener.WaitForConnectionAsync(cancellation.Token);

        await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
            BrokerPipeAccess.ClientOptions);
        await client.ConnectAsync(cancellation.Token);
        await accepted;

        Assert.IsTrue(BrokerPipeAccess.IsRestrictedToUser(client.GetAccessControl(), CurrentUser()));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    [SupportedOSPlatform("windows")]
    public async Task ConnectDrivePipeAsync_ServerGrantingEveryoneAccess_IsRefused()
    {
        using var cancellation = new CancellationTokenSource(HangGuard);
        var pipeName = "mftlib-hardening-test-" + Guid.NewGuid().ToString("N");
        await using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var accepted = server.WaitForConnectionAsync(cancellation.Token);

        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() =>
            DefaultElevatedEntryRunner.ConnectDrivePipeAsync(pipeName, DefaultElevatedEntryRunner.ConnectTimeout,
                TimeProvider.System, cancellation.Token));
        await accepted;
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    [SupportedOSPlatform("windows")]
    public async Task ConnectDrivePipeAsync_OffersOnlyIdentificationToTheServer()
    {
        using var cancellation = new CancellationTokenSource(HangGuard);
        var pipeName = "mftlib-hardening-test-" + Guid.NewGuid().ToString("N");
        await using var server = BrokerPipeAccess.CreateServer(pipeName);
        var accepted = server.WaitForConnectionAsync(cancellation.Token);

        await using var client = await DefaultElevatedEntryRunner.ConnectDrivePipeAsync(pipeName,
            DefaultElevatedEntryRunner.ConnectTimeout, TimeProvider.System, cancellation.Token);
        await accepted;
        // The server can only impersonate once the client has written.
        var received = server.ReadAsync(new byte[1], cancellation.Token).AsTask();
        await client.WriteAsync(new byte[] { 1 }, cancellation.Token);
        _ = await received;

        var level = TokenImpersonationLevel.None;
        server.RunAsClient(() => level = WindowsIdentity.GetCurrent(true)?.ImpersonationLevel ?? level);

        Assert.AreEqual(TokenImpersonationLevel.Identification, level);
    }

    [TestMethod]
    public async Task ConnectDrivePipeAsync_NoServerAnswers_TimesOutOnTheInjectedClock()
    {
        var pipeName = "mftlib-hardening-test-" + Guid.NewGuid().ToString("N");
        var clock = new TimerSignalingClock();
        var timeout = TimeSpan.FromSeconds(7);
        var connect = DefaultElevatedEntryRunner.ConnectDrivePipeAsync(pipeName, timeout, clock,
            TestContext.CancellationToken);

        await clock.TimerCreated.WaitAsync(HangGuard, TestContext.CancellationToken);
        Assert.IsFalse(connect.IsCompleted, "The connect must keep waiting until the injected timeout elapses.");
        clock.Advance(timeout);

        await Assert.ThrowsExactlyAsync<TimeoutException>(() => connect.WaitAsync(HangGuard,
            TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task RunBroker_NoServerAnswers_ExitsWithFailureOnceTheInjectedTimeoutElapses()
    {
        var pipeName = "mftlib-runner-test-" + Guid.NewGuid().ToString("N");
        var clock = new TimerSignalingClock();
        var timeout = TimeSpan.FromSeconds(7);
        var exitCodes = new List<int>();
        var runTask = Task.Run(
            () => new DefaultElevatedEntryRunner(clock, exitCodes.Add, timeout).RunBroker(pipeName),
            TestContext.CancellationToken);

        await clock.TimerCreated.WaitAsync(HangGuard, TestContext.CancellationToken);
        Assert.IsFalse(runTask.IsCompleted, "The runner must keep waiting until the injected timeout elapses.");
        clock.Advance(timeout);
        await runTask.WaitAsync(HangGuard, TestContext.CancellationToken);

        CollectionAssert.AreEqual(new[] { 1 }, exitCodes);
    }

    sealed class TimerSignalingClock : FakeTimeProvider
    {
        readonly TaskCompletionSource _timerCreated = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task TimerCreated => _timerCreated.Task;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            _timerCreated.TrySetResult();
            return timer;
        }
    }
}
