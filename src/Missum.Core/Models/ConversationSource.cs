namespace Missum.Core.Models;

/// <summary>Compact display index; contains no transcript or model context.</summary>
public sealed record ConversationSource(string Id, string Tool, string Title, string Url, DateTimeOffset Time);
