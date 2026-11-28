namespace Missum.Ai.Contracts;

/// <summary>
/// Dynamic client tool of ChatGPT runs: a read-only search over the user's earlier Missum chats
/// across all modes and projects. The app offers it only while the cross-session memory setting is on.
/// </summary>
public static class SessionMemoryToolContract
{
    /// <summary>Lowercase dotted segments, as the gateway requires for dynamic client tool names.</summary>
    public const string Name = "memory.sessions.search";
}
