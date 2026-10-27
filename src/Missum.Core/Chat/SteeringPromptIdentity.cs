using System.Security.Cryptography;
using System.Text;

namespace Missum.Core.Chat;

/// <summary>Stable display identity for a persisted steering input inside an assistant message.</summary>
public static class SteeringPromptIdentity
{
    public static Guid Create(Guid parentMessageId, string stepId)
    {
        if (parentMessageId == Guid.Empty) throw new ArgumentException("Die Umlenkung benötigt eine Nachricht.", nameof(parentMessageId));
        ArgumentException.ThrowIfNullOrWhiteSpace(stepId);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(parentMessageId.ToString("D") + "/" + stepId));
        return new Guid(hash.AsSpan(0, 16));
    }
}
