using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Missum.App.Services.Meetings;

/// <summary>
/// A shareable join link: <c>missum-&lt;profil&gt;://join/&lt;host&gt;:&lt;port&gt;/&lt;meeting&gt;?t=&lt;token&gt;</c>.
/// The scheme reuses the protocol Missum already registers for session links,
/// so clicking the link on another PC opens (or focuses) Missum there.
/// </summary>
public sealed record MeetingLink(string Host, int Port, Guid MeetingId, string Token)
{
    private const string JoinHost = "join";

    public Uri WebSocketUri(string participantName, Guid participantId, string? hostKey = null)
    {
        var query = $"t={Uri.EscapeDataString(Token)}&name={Uri.EscapeDataString(participantName)}&pid={participantId:N}";
        if (!string.IsNullOrEmpty(hostKey)) query += "&hk=" + Uri.EscapeDataString(hostKey);
        return new Uri($"ws://{FormatHost(Host)}:{Port}/v1/meetings/{MeetingId:N}/ws?{query}");
    }

    public string ToShareString(string scheme) =>
        $"{scheme}://{JoinHost}/{FormatHost(Host)}:{Port}/{MeetingId:N}?t={Uri.EscapeDataString(Token)}";

    public static bool TryParse(string? text, out MeetingLink? link)
    {
        link = null;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var value = text.Trim().Trim('"', '\'', '<', '>');
        var marker = "://" + JoinHost + "/";
        var start = value.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start <= 0) return false;
        var scheme = value[..start];
        if (!scheme.StartsWith("missum", StringComparison.OrdinalIgnoreCase)) return false;
        var rest = value[(start + marker.Length)..];
        var queryStart = rest.IndexOf('?');
        if (queryStart < 0) return false;
        var path = rest[..queryStart];
        var query = rest[(queryStart + 1)..];
        var slash = path.LastIndexOf('/');
        if (slash <= 0) return false;
        var endpoint = path[..slash];
        if (!Guid.TryParseExact(path[(slash + 1)..], "N", out var meetingId) || meetingId == Guid.Empty) return false;
        var colon = endpoint.LastIndexOf(':');
        if (colon <= 0 || !int.TryParse(endpoint[(colon + 1)..], out var port) || port is < 1 or > 65535) return false;
        var host = endpoint[..colon].Trim('[', ']');
        if (Uri.CheckHostName(host) == UriHostNameType.Unknown) return false;
        string? token = null;
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            if (part.StartsWith("t=", StringComparison.Ordinal)) token = Uri.UnescapeDataString(part[2..]);
        if (string.IsNullOrWhiteSpace(token) || token.Length > 128) return false;
        link = new MeetingLink(host, port, meetingId, token);
        return true;
    }

    public static bool TryParse(Uri? uri, out MeetingLink? link)
    {
        link = null;
        return uri is not null && TryParse(uri.OriginalString, out link);
    }

    private static string FormatHost(string host) =>
        IPAddress.TryParse(host, out var address) && address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{host}]" : host;

    /// <summary>The address other PCs in the same network most likely reach this PC under.</summary>
    public static string LocalNetworkAddress()
    {
        try
        {
            var candidates = NetworkInterface.GetAllNetworkInterfaces()
                .Where(nic => nic.OperationalStatus == OperationalStatus.Up
                    && nic.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
                .Select(nic => (Nic: nic, Properties: nic.GetIPProperties()))
                .SelectMany(item => item.Properties.UnicastAddresses.Select(address => (item.Nic, item.Properties, address.Address)))
                .Where(item => item.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(item.Address)
                    && !item.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                // Interfaces with a default gateway are the real LAN; virtual switches usually have none.
                .OrderByDescending(item => item.Properties.GatewayAddresses.Any(g => !g.Address.Equals(IPAddress.Any)))
                .ThenBy(item => item.Nic.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                .Select(item => item.Address.ToString())
                .ToArray();
            return candidates.FirstOrDefault() ?? "127.0.0.1";
        }
        catch (NetworkInformationException)
        {
            return "127.0.0.1";
        }
    }
}
