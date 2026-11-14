using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenCode.Agents.AI.Turns;
using OpenCode.Sdk;
using OpenCode.Sdk.Models;

namespace OpenCode.Agents.AI;

/// <summary>
/// Represents an <see cref="AIAgent"/> that uses OpenCode to provide agentic capabilities.
/// </summary>
public sealed class OpenCodeAgent : AIAgent, IAsyncDisposable
{
    private const string DefaultName = "OpenCode Agent";
    private const string DefaultDescription = "An AI agent powered by OpenCode";

    private readonly OpenCodeClient _client;
    private readonly string? _id;
    private readonly string _name;
    private readonly string _description;
    private readonly OpenCodeAgentOptions _options;
    private readonly bool _ownsClient;

    /// <summary>Initializes a new instance of the <see cref="OpenCodeAgent"/> class.</summary>
    /// <param name="client">The OpenCode client used to interact with the server.</param>
    /// <param name="options">Optional session configuration and permission callback.</param>
    /// <param name="ownsClient">Whether the agent owns and disposes the client. Defaults to false.</param>
    /// <param name="id">The unique identifier for the agent.</param>
    /// <param name="name">The name of the agent.</param>
    /// <param name="description">The description of the agent.</param>
    public OpenCodeAgent(OpenCodeClient client, OpenCodeAgentOptions? options = null, bool ownsClient = false,
        string? id = null, string? name = null, string? description = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
        _options = options ?? new OpenCodeAgentOptions();
        _ownsClient = ownsClient;
        _id = id;
        _name = name ?? DefaultName;
        _description = description ?? DefaultDescription;
    }

    /// <inheritdoc/>
    protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default) =>
        new(new OpenCodeAgentSession());

    /// <summary>Creates an agent session that continues an existing OpenCode conversation.</summary>
    /// <param name="sessionId">The OpenCode session ID to continue.</param>
    /// <returns>A new agent session referring to that conversation.</returns>
    public ValueTask<AgentSession> CreateSessionAsync(string sessionId) =>
        new(new OpenCodeAgentSession { SessionId = sessionId });

    /// <inheritdoc/>
    protected override ValueTask<JsonElement> SerializeSessionCoreAsync(AgentSession session, JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session is not OpenCodeAgentSession typedSession)
        {
            throw new InvalidOperationException($"The provided session type '{session.GetType().Name}' is not compatible with this agent. Only sessions of type '{nameof(OpenCodeAgentSession)}' can be serialized by this agent.");
        }

        return new(typedSession.Serialize(jsonSerializerOptions));
    }

    /// <inheritdoc/>
    protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(JsonElement serializedState, JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default) =>
        new(OpenCodeAgentSession.Deserialize(serializedState, jsonSerializerOptions));

    /// <inheritdoc/>
    protected override Task<Microsoft.Agents.AI.AgentResponse> RunCoreAsync(IEnumerable<ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null, CancellationToken cancellationToken = default) =>
        RunCoreStreamingAsync(messages, session, options, cancellationToken).ToAgentResponseAsync(cancellationToken);

    /// <inheritdoc/>
    protected override IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        return RunTurnAsync(messages, session, cancellationToken);
    }

    private async IAsyncEnumerable<AgentResponseUpdate> RunTurnAsync(
        IEnumerable<ChatMessage> messages, AgentSession? session,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var texts = messages.Select(message => message.Text).ToArray();
        if (texts.All(string.IsNullOrEmpty))
        {
            yield break;
        }

        session ??= await CreateSessionAsync(cancellationToken).ConfigureAwait(false);
        if (session is not OpenCodeAgentSession typedSession)
        {
            throw new InvalidOperationException($"The provided session type '{session.GetType().Name}' is not compatible with this agent. Only sessions of type '{nameof(OpenCodeAgentSession)}' can be used by this agent.");
        }

        var client = await GetSessionClientAsync(typedSession, cancellationToken).ConfigureAwait(false);
        var pump = EventPump.Start(_client.Events, cancellationToken);
        await using var pumpLifetime = pump.ConfigureAwait(false);
        var events = pump.Reader.ReadAllAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);
        await using var eventLifetime = events.ConfigureAwait(false);
        if (!await events.MoveNextAsync().ConfigureAwait(false) || events.Current.Type != "server.connected")
        {
            throw new InvalidOperationException("The event stream did not start with a server.connected frame.");
        }

        var promptId = $"msg_{Guid.NewGuid():N}";
        await client.PromptAsync(new SessionPromptRequest
        {
            Id = promptId,
            Text = string.Join('\n', texts),
            Delivery = SessionInboxDelivery.Queue,
        }, cancellationToken: cancellationToken).ConfigureAwait(false);
        var mapper = new TurnEventMapper(Id, promptId);
        var delivered = false;
        while (await events.MoveNextAsync().ConfigureAwait(false))
        {
            var current = events.Current;
            var eventSessionId = current switch
            {
                ISessionScopedEvent scoped => scoped.SessionId,
                FormCreated created => created.Data.Form.SessionId,
                FormReplied replied => replied.Data.SessionId,
                FormCancelled cancelled => cancelled.Data.SessionId,
                UnknownEvent unknown when unknown.Payload.TryGetProperty("data", out var data)
                    && data.ValueKind == JsonValueKind.Object
                    && data.TryGetProperty("sessionID", out var id)
                    && id.ValueKind == JsonValueKind.String => id.GetString(),
                _ => null,
            };
            if (eventSessionId is not null && eventSessionId != typedSession.SessionId)
            {
                continue;
            }

            delivered |= current is SessionInboxDelivered delivery && delivery.Data.InboxId == promptId;
            if (!delivered)
            {
                continue;
            }

            if (mapper.Map(current) is { } update)
            {
                yield return update;
            }

            if (eventSessionId == typedSession.SessionId && TurnOutcome.EndsTurn(current))
            {
                if (TurnOutcome.Failure(current) is { } failure)
                {
                    throw failure;
                }

                yield break;
            }
        }

        throw new InvalidOperationException("The event stream ended before the turn ended.");
    }

    /// <inheritdoc/>
    protected override string? IdCore => _id;

    /// <inheritdoc/>
    public override string Name => _name;

    /// <inheritdoc/>
    public override string Description => _description;

    /// <inheritdoc/>
    public override object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        return serviceKey is null && serviceType == typeof(AIAgentMetadata)
            ? new AIAgentMetadata("opencode")
            : base.GetService(serviceType, serviceKey);
    }

    /// <summary>Releases the client when this agent owns it.</summary>
    /// <returns>A value task representing the completed disposal.</returns>
    public ValueTask DisposeAsync()
    {
        if (_ownsClient)
        {
            _client.Dispose();
        }

        return default;
    }

    private async Task<SessionClient> GetSessionClientAsync(OpenCodeAgentSession session, CancellationToken cancellationToken)
    {
        if (session.SessionId is null)
        {
            var created = await _client.Sessions.CreateSessionAsync(CreateSessionRequest(), cancellationToken: cancellationToken).ConfigureAwait(false);
            session.SessionId = created.Session.Id;
        }

        return _client.Sessions.GetSessionClient(session.SessionId);
    }

    private SessionCreateRequest CreateSessionRequest()
    {
        var request = new SessionCreateRequest();
        if (_options.Model is { } model)
        {
            request = request with { Model = model };
        }

        if (_options.Agent is { } agent)
        {
            request = request with { Agent = agent };
        }

        if (_options.Directory is { } directory)
        {
            request = request with { Location = new LocationPublicRef { Directory = directory } };
        }

        if (_options.Title is { } title)
        {
            request = request with { Title = title };
        }

        if (_options.Permissions is { } permissions)
        {
            request = request with { Permissions = permissions.ToList() };
        }

        return request;
    }
}
