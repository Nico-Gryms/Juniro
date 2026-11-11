// =============================================================================
// HangFireAuthFilter.cs — ACCÈS AU TABLEAU DE BORD HANGFIRE
// -----------------------------------------------------------------------------
// Rôle : n'autoriser l'affichage de /hangfire qu'aux appareils du réseau local
//        (127.0.0.1, 10.x, 172.16-31.x, 192.168.x) et du réseau Tailscale (100.64-127.x).
// Utilisé par : Program.cs (UseHangfireDashboard).
// Pour ouvrir à une autre plage d'adresses : ajouter une condition dans Authorize.
// =============================================================================
using Hangfire.Dashboard;
using System.Net;
using System.Net.Sockets;

namespace Juniro.ScraperService.Configuration;

/// <summary>
/// Le dashboard Hangfire permet de declencher et supprimer des jobs : il ne doit pas etre
/// ouvert a tout le monde. Ce filtre n'autorise que la machine locale, les plages privees
/// (LAN) et le reseau Tailscale (100.64.0.0/10).
/// </summary>
public class LocalNetworkAuthFilter : IDashboardAuthorizationFilter
{
    /// <summary>Vrai si l'adresse IP du visiteur appartient à un réseau privé autorisé.</summary>
    public bool Authorize(DashboardContext context)
    {
        var ip = context.GetHttpContext().Connection.RemoteIpAddress;
        if (ip is null) return false;

        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.AddressFamily != AddressFamily.InterNetwork) return false;

        byte[] b = ip.GetAddressBytes();

        return b[0] switch
        {
            10  => true,                      // 10.0.0.0/8      - LAN
            172 => b[1] is >= 16 and <= 31,   // 172.16.0.0/12   - LAN
            192 => b[1] == 168,               // 192.168.0.0/16  - LAN
            100 => b[1] is >= 64 and <= 127,  // 100.64.0.0/10   - Tailscale / CGNAT
            169 => b[1] == 254,               // 169.254.0.0/16  - link-local
            _   => false
        };
    }
}
