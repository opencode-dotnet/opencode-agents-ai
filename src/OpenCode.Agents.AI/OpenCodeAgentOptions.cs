using OpenCode.Sdk.Models;

namespace OpenCode.Agents.AI;

/// <summary>
/// Configures new OpenCode sessions and the caller's permission handler.
/// </summary>
public sealed class OpenCodeAgentOptions
{
    /// <summary>Gets or sets the model used when creating an OpenCode session.</summary>
    public ModelRef? Model { get; set; }

    /// <summary>Gets or sets the OpenCode agent profile used for a new session.</summary>
    public string? Agent { get; set; }

    /// <summary>Gets or sets the working directory of a new OpenCode session.</summary>
    public string? Directory { get; set; }

    /// <summary>Gets or sets the permission rules applied to a new session.</summary>
    public IReadOnlyList<PermissionRule>? Permissions { get; set; }

    /// <summary>Gets or sets the title of a new OpenCode session.</summary>
    public string? Title { get; set; }

    /// <summary>Gets or sets the callback that answers an OpenCode permission request.</summary>
    public Func<PermissionRequest, CancellationToken, ValueTask<SessionPermissionReplyRequest>>? OnPermissionRequest { get; set; }
}
