using System.Net;
using System.Net.NetworkInformation;

namespace Missum.App.Services;

internal static class ComputerLocalEndpoint
{
    // A remote gateway could otherwise receive desktop pixels even when its own
    // configured model runtime is local to that remote machine. Do not resolve
    // arbitrary DNS names: accept loopback or an IP assigned to this host only.
    public static bool IsLocal(Uri? address)
    {
        if (address is null || address.Scheme is not ("http" or "https")) return false;
        if (address.IsLoopback) return true;
        if (!IPAddress.TryParse(address.Host, out var ip)) return false;
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .SelectMany(network => network.GetIPProperties().UnicastAddresses)
                .Any(local => local.Address.Equals(ip));
        }
        catch (NetworkInformationException) { return false; }
    }
}
