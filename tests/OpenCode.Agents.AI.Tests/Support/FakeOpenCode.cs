using System.Threading.Channels;
using NSubstitute;
using OpenCode.Sdk;
using OpenCode.Sdk.Models;

namespace OpenCode.Agents.AI.Tests.Support;

internal sealed class FakeOpenCode
{
    public const string SessionId = "ses_test";

    private readonly Channel<IEvent> _bus = Channel.CreateUnbounded<IEvent>();

    public FakeOpenCode()
    {
        Client = Substitute.For<OpenCodeClient>();
        Sessions = Substitute.For<SessionsClient>();
        Session = AddSession(SessionId, parentId: null);
        var events = Substitute.For<EventsClient>();
        Client.Sessions.Returns(Sessions);
        Client.Events.Returns(events);
        Shells = Substitute.For<ShellsClient>();
        Client.Shells.Returns(Shells);
        AddShell("shell_test");
#pragma warning disable OPENCODE001 // OpenCodeClient.Experimental and ExperimentalClient expose the SDK's experimental mocking seam.
        Experimental = Substitute.For<ExperimentalClient>();
        Client.Experimental.Returns(Experimental);
        Experimental.ListJobsAsync(Arg.Any<ExperimentalJobListRequest>(), Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(new ExperimentalJobListResponse { Status = 200, Jobs = [] });
        Experimental.WaitForSessionAsync(Arg.Any<string>(), Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(new ExperimentalSessionWaitResponse { Status = 204 });
#pragma warning restore OPENCODE001 // OpenCodeClient.Experimental and ExperimentalClient arrangement ends here.
        Active(new Dictionary<string, SessionActive>(StringComparer.Ordinal));
        Sessions.CreateSessionAsync(Arg.Any<SessionCreateRequest>(), Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(new SessionCreateResponse
            {
                Status = 200,
                Session = SessionInfo(SessionId, parentId: null),
            });
        events.SubscribeAsync(Arg.Any<EventSubscribeRequest>(), Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(call => _bus.Reader.ReadAllAsync(call.Arg<CancellationToken>()));
        Emit(TurnEvents.Connected());
        OnPrompt(request => [TurnEvents.Delivered(PromptId(request)), TurnEvents.Succeeded()]);
    }

    public OpenCodeClient Client { get; }

    public SessionsClient Sessions { get; }

    public SessionClient Session { get; }

    public ShellsClient Shells { get; }

    public ShellClient AddShell(string shellId)
    {
        var client = Substitute.For<ShellClient>();
        Shells.GetShellClient(shellId).Returns(client);
        client.RemoveAsync(Arg.Any<ShellRemoveRequest>(), Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(new ShellRemoveResponse { Status = 204 });
        return client;
    }

#pragma warning disable OPENCODE001 // ExperimentalClient is the documented experimental test seam.
    public ExperimentalClient Experimental { get; }
#pragma warning restore OPENCODE001 // ExperimentalClient property declaration ends here.

    public SessionClient AddSession(string sessionId, string? parentId, string directory = "test-directory")
    {
        var client = Substitute.For<SessionClient>();
        Sessions.GetSessionClient(sessionId).Returns(client);
        client.GetAsync(Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(new SessionResponse { Status = 200, Session = SessionInfo(sessionId, parentId, directory) });
        client.ReplyToPermissionAsync(Arg.Any<string>(), Arg.Any<SessionPermissionReplyRequest>(), Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(new SessionPermissionReplyResponse { Status = 204 });
        client.ListRequestsAsync(Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(new SessionPermissionListResponse { Status = 200, Requests = [] });
        client.ListInboxAsync(Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(new SessionInboxListResponse { Status = 200, Inbox = [] });
        client.CancelInboxAsync(Arg.Any<string>(), Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(new SessionInboxCancelResponse { Status = 204 });
        client.InterruptAsync(Arg.Any<SessionInterruptRequest>(), Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(new SessionInterruptResponse { Status = 200, Interrupt = new SessionInterruptOutcome { Interrupted = true } });
        return client;
    }

    public void OnPrompt(Func<SessionPromptRequest, IEnumerable<IEvent>> turn) => OnPrompt(turn, admittedAt: 1);

    public void OnPrompt(Func<SessionPromptRequest, IEnumerable<IEvent>> turn, double admittedAt)
    {
        Session.PromptAsync(Arg.Any<SessionPromptRequest>(), Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var request = call.Arg<SessionPromptRequest>();
                foreach (var item in turn(request))
                {
                    Emit(item);
                }

                return new SessionPromptResponse
                {
                    Status = 200,
                    Prompt = new SessionInboxUser
                    {
                        Id = PromptId(request),
                        SessionId = SessionId,
                        Time = new SessionInboxUserTime { Created = admittedAt },
                        Payload = new SessionInboxUserPayload { Text = request.Text },
                        Delivery = SessionInboxDelivery.Queue,
                    },
                };
            });
    }

    public void Emit(IEvent @event) => _bus.Writer.TryWrite(@event);

    public void EndBus() => _bus.Writer.TryComplete();

    public void FailBus(Exception failure) => _bus.Writer.TryComplete(failure);

    public void PendingPermissions(string sessionId, IReadOnlyList<PermissionRequest> requests) =>
        Sessions.GetSessionClient(sessionId).ListRequestsAsync(Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(new SessionPermissionListResponse { Status = 200, Requests = requests });

#pragma warning disable OPENCODE001 // IJobInfo and ExperimentalClient.ListJobsAsync declare the job snapshot arrangement.
    public void Jobs(IReadOnlyList<IJobInfo> jobs) =>
        Experimental.ListJobsAsync(Arg.Any<ExperimentalJobListRequest>(), Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(new ExperimentalJobListResponse { Status = 200, Jobs = jobs });
#pragma warning restore OPENCODE001 // IJobInfo and ExperimentalClient.ListJobsAsync arrangement ends here.

    public void Active(IReadOnlyDictionary<string, SessionActive> active) =>
        Sessions.GetActiveAsync(Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(new SessionActiveResponse { Status = 200, Active = active });

    private static string PromptId(SessionPromptRequest request) =>
        request.Id.Value ?? throw new InvalidOperationException("The prompt must provide its id.");

    internal static SessionInfo SessionInfo(string sessionId, string? parentId, string directory = "test-directory") => new()
    {
        Id = sessionId,
        ParentId = parentId,
        ProjectId = "project_test",
        Cost = 0,
        Tokens = new TokenUsageInfo
        {
            Input = 0,
            Output = 0,
            Reasoning = 0,
            Cache = new TokenUsageInfoCache { Read = 0, Write = 0 },
        },
        Time = new SessionInfoTime { Created = 1, Updated = 1 },
        Location = new LocationPublicRef { Directory = directory },
    };
}
