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

    public SessionClient AddSession(string sessionId, string? parentId)
    {
        var client = Substitute.For<SessionClient>();
        Sessions.GetSessionClient(sessionId).Returns(client);
        client.GetAsync(Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(new SessionResponse { Status = 200, Session = SessionInfo(sessionId, parentId) });
        client.ReplyToPermissionAsync(Arg.Any<string>(), Arg.Any<SessionPermissionReplyRequest>(), Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(new SessionPermissionReplyResponse { Status = 204 });
        return client;
    }

    public void OnPrompt(Func<SessionPromptRequest, IEnumerable<IEvent>> turn)
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
                        Time = new SessionInboxUserTime { Created = 1 },
                        Payload = new SessionInboxUserPayload { Text = request.Text },
                        Delivery = SessionInboxDelivery.Queue,
                    },
                };
            });
    }

    public void Emit(IEvent @event) => _bus.Writer.TryWrite(@event);

    public void EndBus() => _bus.Writer.TryComplete();

    public void FailBus(Exception failure) => _bus.Writer.TryComplete(failure);

    private static string PromptId(SessionPromptRequest request) =>
        request.Id.Value ?? throw new InvalidOperationException("The prompt must provide its id.");

    private static SessionInfo SessionInfo(string sessionId, string? parentId) => new()
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
        Location = new LocationPublicRef { Directory = "test-directory" },
    };
}
