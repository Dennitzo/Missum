using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Missum.BlenderAdapter;

/// <summary>
/// ExtensionHost entrypoint for the Blender integration. The host writes exactly one
/// <c>action.invoke</c> message to standard input and expects exactly one <c>action.result</c>
/// or <c>host.error</c> message back, so the process stays short lived. Everything that must survive
/// between calls — the bound editor instance, the bound scene and the mutation ledger — is kept in
/// the integration state store instead of in this process.
/// </summary>
internal sealed class Program
{
    private const int ProtocolVersion = 1;
    private const string InvokeType = "action.invoke";
    private const string ActionResultType = "action.result";
    private const string ErrorType = "host.error";

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static async Task<int> Main()
    {
        // Window pictures are only correct when window rectangles and bitmaps use real screen pixels.
        // Without this the operating system hands out a shrunken virtual surface and the picture of a
        // scaled Blender window comes back almost empty.
        MakeProcessDpiAware();
        var framing = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        Console.InputEncoding = framing;
        Console.OutputEncoding = framing;
        var requestId = "unknown";
        try
        {
            var line = await Console.In.ReadLineAsync(CancellationToken.None).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(line))
                return await FailAsync("adapter.no_request", "Der ExtensionHost hat keine Action übergeben.", requestId);

            // JsonNode owns its whole tree, so the parsed document stays referenced for the entire
            // call: the arguments handed to the tools are children of this root.
            var root = JsonNode.Parse(line)?.AsObject() ?? new JsonObject();
            requestId = Wire.ReadString(root, "requestId", requestId);
            var type = Wire.ReadString(root, "type", string.Empty);
            if (!string.Equals(type, InvokeType, StringComparison.Ordinal))
                return await FailAsync("adapter.unsupported_message",
                    $"Nachrichtentyp '{type}' wird von diesem Adapter nicht unterstützt.", requestId);

            var payload = root["payload"] as JsonObject ?? new JsonObject();
            var arguments = payload["arguments"] as JsonObject ?? new JsonObject();
            var modelToolName = Wire.ReadString(payload, "modelToolName", string.Empty);
            var actionId = Wire.ReadString(payload, "actionId", string.Empty);
            BlenderChannel.Configure(arguments["_missumBlender"] as JsonObject);
            arguments.Remove("_missumBlender");

            var result = await BlenderTools.DispatchAsync(modelToolName, actionId, arguments).ConfigureAwait(false);
            var response = new JsonObject
            {
                ["protocolVersion"] = ProtocolVersion,
                ["type"] = ActionResultType,
                ["requestId"] = requestId,
                ["payload"] = new JsonObject { ["result"] = result },
            };
            await Console.Out.WriteLineAsync(response.ToJsonString(Json)).ConfigureAwait(false);
            await Console.Out.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            return 0;
        }
        catch (BlenderToolException toolException)
        {
            return await FailAsync(toolException.Code, toolException.Message, requestId);
        }
        catch (OperationCanceledException)
        {
            return await FailAsync("blender.action_timeout",
                "Der Blender-Aufruf wurde vor der Antwort beendet. Vor weiteren Änderungen den Zustand prüfen.", requestId);
        }
        catch (Exception exception) when (exception is JsonException or IOException or InvalidOperationException
            or UnauthorizedAccessException or FormatException or NotSupportedException
            or System.Net.Sockets.SocketException)
        {
            return await FailAsync("adapter.failed", Describe(exception), requestId);
        }
    }

    private static async Task<int> FailAsync(string code, string message, string requestId)
    {
        var response = new JsonObject
        {
            ["protocolVersion"] = ProtocolVersion,
            ["type"] = ErrorType,
            ["requestId"] = requestId,
            ["payload"] = new JsonObject { ["code"] = code, ["message"] = message },
        };
        await Console.Out.WriteLineAsync(response.ToJsonString(Json)).ConfigureAwait(false);
        await Console.Out.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        return 0;
    }

    private static string Describe(Exception exception) => string.IsNullOrWhiteSpace(exception.Message)
        ? exception.GetType().Name
        : exception.Message;

    /// <summary>Per-monitor aware if the system allows it, plain aware otherwise. Failures are harmless.</summary>
    private static void MakeProcessDpiAware()
    {
        try
        {
            // -4 is DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2.
            if (SetProcessDpiAwarenessContext(new nint(-4)) != 0) return;
        }
        catch (EntryPointNotFoundException)
        {
            // Older systems only know the simpler flag.
        }

        try
        {
            _ = SetProcessDPIAware();
        }
        catch (EntryPointNotFoundException)
        {
            // Nothing to do: the process keeps the awareness it was started with.
        }
    }

    [DllImport("user32.dll")]
    private static extern nint SetProcessDpiAwarenessContext(nint context);

    [DllImport("user32.dll")]
    private static extern bool SetProcessDPIAware();
}

/// <summary>An error the model can act on: the code says what is missing, the message says what to do.</summary>
internal sealed class BlenderToolException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>Small tolerant readers for the wire JSON, so a missing field never kills the call.</summary>
internal static class Wire
{
    public static string ReadString(JsonNode? node, string name, string fallback = "")
    {
        if (node is not JsonObject item) return fallback;
        if (!item.TryGetPropertyValue(name, out var value) || value is null) return fallback;
        try
        {
            return value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() ?? fallback : value.ToString();
        }
        catch (Exception exception) when (exception is InvalidOperationException or JsonException)
        {
            return fallback;
        }
    }

    public static int ReadInt(JsonNode? node, string name, int fallback)
    {
        if (node is not JsonObject item) return fallback;
        if (!item.TryGetPropertyValue(name, out var value) || value is null) return fallback;
        try
        {
            return value.GetValueKind() == JsonValueKind.Number ? value.GetValue<int>() : fallback;
        }
        catch (Exception exception) when (exception is InvalidOperationException or FormatException or OverflowException)
        {
            return fallback;
        }
    }

    public static bool ReadBool(JsonNode? node, string name, bool fallback)
    {
        if (node is not JsonObject item) return fallback;
        if (!item.TryGetPropertyValue(name, out var value) || value is null) return fallback;
        return value.GetValueKind() switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => fallback,
        };
    }

    public static IReadOnlyList<string> ReadStringArray(JsonNode? node, string name)
    {
        if (node is not JsonObject item || item[name] is not JsonArray array) return [];
        var values = new List<string>(array.Count);
        foreach (var entry in array)
        {
            if (entry is null) continue;
            try
            {
                if (entry.GetValueKind() == JsonValueKind.String && entry.GetValue<string>() is { Length: > 0 } text)
                    values.Add(text);
            }
            catch (Exception exception) when (exception is InvalidOperationException or JsonException)
            {
                // A single wrong entry must not abort the whole call.
            }
        }

        return values;
    }
}
