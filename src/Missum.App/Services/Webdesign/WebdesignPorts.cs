using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Missum.App.Services.Webdesign;

/// <summary>
/// Port rules for Webdesign servers. The built-in server binds port 0 and lets Windows choose,
/// so no other program can take the port between choosing and binding. A remembered port is
/// tried first only when it is not one of Missum's own fixed ports and no other project uses it.
/// </summary>
public static class WebdesignPorts
{
    // Gateway 8080, native runtime 8081/8082 (AssistantRuntimeProfile), meetings 8090-8099.
    private static readonly (int From, int To)[] Reserved = [(8080, 8082), (8090, 8099)];

    public static bool IsReserved(int port) =>
        port is < 1024 or > 65535 || Reserved.Any(range => port >= range.From && port <= range.To);

    /// <summary>True when nothing listens on the port right now (IPv4 and IPv6).</summary>
    public static bool IsFree(int port)
    {
        if (IsReserved(port)) return false;
        try
        {
            var listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
            if (listeners.Any(endpoint => endpoint.Port == port)) return false;
        }
        catch (NetworkInformationException) { }
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            socket.ExclusiveAddressUse = true;
            socket.Bind(new IPEndPoint(IPAddress.Any, port));
            return true;
        }
        catch (SocketException) { return false; }
    }

    /// <summary>
    /// A port for a project dev server, which binds by itself. Windows picks an unused ephemeral
    /// port; the caller passes it with a strict-port flag and retries on a collision.
    /// </summary>
    public static int ReserveForChildProcess(IReadOnlySet<int> taken)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            if (!taken.Contains(port) && !IsReserved(port)) return port;
        }
        throw new InvalidOperationException("Es wurde kein freier Port für den Webdesign-Server gefunden.");
    }

    /// <summary>The IPv4 address other devices in the local network can reach, if any.</summary>
    public static IPAddress? LanAddress()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(item => item.OperationalStatus == OperationalStatus.Up
                    && item.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                    && !item.Description.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase)
                    && !item.Description.Contains("WSL", StringComparison.OrdinalIgnoreCase)
                    && !item.Description.Contains("VirtualBox", StringComparison.OrdinalIgnoreCase)
                    && !item.Description.Contains("VMware", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(item => item.GetIPProperties().GatewayAddresses.Count > 0)
                .SelectMany(item => item.GetIPProperties().UnicastAddresses)
                .Select(item => item.Address)
                .FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork
                    && !IPAddress.IsLoopback(address) && !address.ToString().StartsWith("169.254.", StringComparison.Ordinal));
        }
        catch (NetworkInformationException) { return null; }
    }
}
