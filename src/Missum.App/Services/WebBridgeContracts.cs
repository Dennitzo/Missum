using System.Text.Json;

namespace Missum.App.Services;

public sealed record WebBridgeEnvelope(
    int Version,
    string Type,
    string RequestId,
    JsonElement Payload,
    string? ClientId = null,
    string? TabId = null);
