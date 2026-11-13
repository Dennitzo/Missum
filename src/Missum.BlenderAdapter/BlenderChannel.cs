using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Missum.BlenderAdapter;

/// <summary>
/// One running Blender instance launched by Missum. <see cref="InstanceId"/> is stable
/// while the process lives, <see cref="SessionStamp"/> also changes when the file is reloaded, so
/// the pair identifies exactly one open scene.
/// </summary>
internal sealed record BlenderInstance(
    int Port,
    string InstanceId,
    string SessionStamp,
    string ApplicationVersion,
    int ProcessId,
    string BlendPath,
    string SceneName,
    bool IsDirty,
    IReadOnlyList<string> Capabilities)
{
    /// <summary>
    /// True only when something inside this Blender answers requests, which is the runtime Missum
    /// started itself. A window that was already open is seen but not controllable.
    /// </summary>
    public bool Controllable { get; init; } = true;

    /// <summary><c>runtime</c> for a Missum-started instance, <c>window</c> for an already open one.</summary>
    public string Detection { get; init; } = "runtime";

    public string WindowTitle { get; init; } = string.Empty;

    public bool HasWindow { get; init; }

    public nint WindowHandle { get; init; }

    /// <summary>File name read out of the window title; a hint, never a verified path.</summary>
    public string? ObservedProjectFile { get; init; }

    public string ProjectName => !string.IsNullOrEmpty(BlendPath)
        ? Path.GetFileName(BlendPath)
        : ObservedProjectFile ?? "ungespeicherte Szene";

    public JsonObject ToJson() => new()
    {
        ["instanceId"] = InstanceId,
        ["sessionStamp"] = SessionStamp,
        ["application"] = "blender",
        ["applicationVersion"] = ApplicationVersion,
        ["processId"] = ProcessId,
        ["port"] = Port,
        ["projectPath"] = BlendPath,
        ["projectName"] = ProjectName,
        ["sceneName"] = SceneName,
        ["unsavedChanges"] = IsDirty,
        ["detection"] = Detection,
        ["controllable"] = Controllable,
        ["hasWindow"] = HasWindow,
        ["windowTitle"] = WindowTitle,
        ["capabilities"] = new JsonArray([.. Capabilities.Select(static value => JsonValue.Create(value))]),
    };

    /// <summary>
    /// A Blender window that was already open when the run started. It is named after its window so
    /// the model can say which one it means, and it carries no scene identity because nothing inside
    /// Blender could report one.
    /// </summary>
    public static BlenderInstance FromWindow(ObservedBlenderWindow window) => new(
        0,
        $"fenster-{window.ProcessId}",
        string.Empty,
        window.ApplicationVersion,
        window.ProcessId,
        string.Empty,
        string.Empty,
        false,
        [])
    {
        Controllable = false,
        Detection = "window",
        WindowTitle = window.Title,
        HasWindow = true,
        WindowHandle = window.WindowHandle,
        ObservedProjectFile = window.DetectedProjectFile,
    };
}

/// <summary>
/// Talks only to the local Blender instance selected by the desktop launcher.
/// </summary>
internal static class BlenderChannel
{
    private const int MaximumReplyBytes = 64 * 1024 * 1024;
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMilliseconds(1_200);
    private static int _port;
    private static int _processId;
    private static string _token = "";

    public static void Configure(JsonObject? endpoint)
    {
        _port = Wire.ReadInt(endpoint, "port", 0);
        _processId = Wire.ReadInt(endpoint, "processId", 0);
        _token = Wire.ReadString(endpoint, "token");
    }

    public static bool HasEndpoint => _port is >= 1 and <= 65_535 && _processId > 0 && _token.Length >= 32;

    public static bool IsEndpointFor(string instanceId, int port) => HasEndpoint && port == _port
        && instanceId.StartsWith(FormattableString.Invariant($"blender-{_processId}-"), StringComparison.Ordinal);

    /// <summary>
    /// Every Blender this run can see: first the instance Missum started and that answers requests,
    /// then the Blender windows that were already open on this machine. Found windows carry
    /// <c>controllable: false</c>, because nothing inside them was installed or started by Missum.
    /// </summary>
    public static async Task<IReadOnlyList<BlenderInstance>> DiscoverAsync(CancellationToken cancellationToken)
    {
        BlenderInstance? runtime = null;
        if (HasEndpoint)
            runtime = await ProbeAsync(_port, cancellationToken).ConfigureAwait(false);

        var found = new List<BlenderInstance>();
        if (runtime is not null) found.Add(runtime);
        foreach (var window in BlenderProcessWatch.Observe())
        {
            var index = found.FindIndex(item => item.ProcessId == window.ProcessId);
            if (index < 0)
            {
                found.Add(BlenderInstance.FromWindow(window));
                continue;
            }

            // Runtime instance and its own window are one editor: report both facts on one entry.
            found[index] = found[index] with
            {
                WindowHandle = window.WindowHandle,
                WindowTitle = window.Title,
                HasWindow = true,
            };
        }

        return found;
    }

    private static async Task<BlenderInstance?> ProbeAsync(int port, CancellationToken cancellationToken)
    {
        var reply = await SendAsync(port, new JsonObject { ["cmd"] = "hello" }, ProbeTimeout, cancellationToken)
            .ConfigureAwait(false);
        if (reply is null || !Wire.ReadBool(reply, "ok", false)) return null;
        if (reply["result"] is not JsonObject result) return null;

        var instanceId = Wire.ReadString(result, "instanceId");
        if (instanceId.Length == 0 || Wire.ReadInt(result, "processId", 0) != _processId) return null;
        return new BlenderInstance(
            port,
            instanceId,
            Wire.ReadString(result, "sessionStamp"),
            Wire.ReadString(result, "applicationVersion", "unbekannt"),
            Wire.ReadInt(result, "processId", 0),
            Wire.ReadString(result, "blendPath"),
            Wire.ReadString(result, "sceneName"),
            Wire.ReadBool(result, "isDirty", false),
            Wire.ReadStringArray(result, "capabilities"));
    }

    /// <summary>Sends one command and returns the Blender result, or throws a tool error the model can act on.</summary>
    public static async Task<JsonObject> CommandAsync(
        BlenderInstance instance,
        string command,
        JsonObject arguments,
        TimeSpan timeout,
        string? operationId,
        CancellationToken cancellationToken)
    {
        var request = new JsonObject
        {
            ["cmd"] = command,
            ["args"] = Clone(arguments),
            ["target"] = Target(instance),
        };
        if (!string.IsNullOrWhiteSpace(operationId)) request["operationId"] = operationId;

        var reply = await SendAsync(instance.Port, request, timeout, cancellationToken).ConfigureAwait(false);
        if (reply is null)
        {
            var seconds = ((int)Math.Round(timeout.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
            throw new BlenderToolException("blender.no_answer",
                $"Für '{command}' liegt nach dem Verbindungsversuch mit Blender {instance.InstanceId} "
                + $"(Zeitfenster {seconds} Sekunden) keine Antwort vor. Der Ausführungsstand ist unklar. "
                + "Blender-Status und Szene prüfen, bevor eine Änderung erneut ausgeführt wird.");
        }

        if (Wire.ReadBool(reply, "ok", false))
        {
            // Take the result out of its envelope: a JsonNode keeps its parent, and the caller puts
            // it into a fresh reply envelope later on.
            var result = reply["result"];
            reply.Remove("result");
            return result as JsonObject ?? new JsonObject();
        }

        var error = reply["error"] as JsonObject;
        var code = Wire.ReadString(error, "code", "blender.request_rejected");
        var message = Wire.ReadString(error, "message", "Blender hat die Anfrage abgelehnt.");
        throw new BlenderToolException(code, message);
    }

    /// <summary>Best-effort lookup used after an unclear outcome; never throws.</summary>
    public static async Task<JsonObject?> TryOperationStatusAsync(
        BlenderInstance instance,
        string operationId,
        CancellationToken cancellationToken)
    {
        var reply = await SendAsync(instance.Port, new JsonObject
            {
                ["cmd"] = "operation.status",
                ["operationId"] = operationId,
                ["target"] = Target(instance),
            },
                ProbeTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        if (reply is null || !Wire.ReadBool(reply, "ok", false)) return null;
        return reply["result"] as JsonObject;
    }

    private static JsonObject Target(BlenderInstance instance) => new()
    {
        ["instanceId"] = instance.InstanceId,
        ["sessionStamp"] = instance.SessionStamp,
        ["projectPath"] = instance.BlendPath,
        ["sceneName"] = instance.SceneName,
    };

    private static async Task<JsonObject?> SendAsync(
        int port,
        JsonObject request,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        using var client = new TcpClient();
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, port, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception exception) when (exception is SocketException or InvalidOperationException or ArgumentException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }

        try
        {
            var stream = client.GetStream();
            request["token"] = _token;
            var payload = Encoding.UTF8.GetBytes(request.ToJsonString(Program.Json) + "\n");
            await stream.WriteAsync(payload, deadline.Token).ConfigureAwait(false);
            await stream.FlushAsync(deadline.Token).ConfigureAwait(false);
            var line = await ReadLineAsync(stream, deadline.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(line)) return null;
            return JsonNode.Parse(line)?.AsObject();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception exception) when (exception is SocketException or IOException or JsonException
            or InvalidOperationException or FormatException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }
    }

    private static async Task<string?> ReadLineAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        // Collect the raw bytes first and decode once at the end: a UTF-8 sequence may be split
        // across two socket reads, which would corrupt umlauts in scene or object names.
        using var collected = new MemoryStream();
        var buffer = new byte[8 * 1024];
        while (true)
        {
            var bytesRead = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (bytesRead == 0)
                return collected.Length == 0 ? null : Encoding.UTF8.GetString(collected.ToArray()).TrimEnd('\r');
            var newline = Array.IndexOf(buffer, (byte)'\n', 0, bytesRead);
            if (newline >= 0)
            {
                collected.Write(buffer, 0, newline);
                return Encoding.UTF8.GetString(collected.ToArray()).TrimEnd('\r');
            }

            collected.Write(buffer, 0, bytesRead);
            if (collected.Length > MaximumReplyBytes)
                throw new BlenderToolException("blender.reply_too_large", "Die Antwort von Blender ist zu groß.");
        }
    }

    private static JsonObject Clone(JsonObject source) =>
        JsonSerializer.Deserialize<JsonObject>(source.ToJsonString(Program.Json), Program.Json) ?? new JsonObject();
}
