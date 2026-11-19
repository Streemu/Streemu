using System.Net;
using Streemu.Util;

namespace Streemu.Net;

/// <summary>
/// Pergunta ao roteador (UPnP IGD, pela API do Windows) qual é o IP WAN dele.
/// Atrás de CGNAT, esse IP é algo como 100.90.x.x: é o endereço com que a operadora entrega
/// os seus pacotes para outros clientes dela.
/// </summary>
public static class Upnp
{
    public static Task<IPAddress?> GetWanIpAsync(TimeSpan timeout)
    {
        var tcs = new TaskCompletionSource<IPAddress?>();
        var t = new Thread(() =>
        {
            try { tcs.TrySetResult(Query()); }
            catch (Exception e) { Log.Info("UPnP falhou: " + e.Message); tcs.TrySetResult(null); }
        }) { IsBackground = true, Name = "upnp" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        return Task.WhenAny(tcs.Task, Task.Delay(timeout)).ContinueWith(w => w.Result == tcs.Task ? tcs.Task.Result : null);
    }

    static IPAddress? Query()
    {
        var type = Type.GetTypeFromProgID("UPnP.UPnPDeviceFinder");
        if (type == null) return null;
        dynamic finder = Activator.CreateInstance(type)!;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        dynamic devices = finder.FindByType("urn:schemas-upnp-org:device:InternetGatewayDevice:1", 0);
        Log.Info($"UPnP: busca de roteadores levou {sw.Elapsed.TotalSeconds:0.0} s, {(int)devices.Count} encontrado(s).");
        IPAddress? best = null;
        foreach (dynamic d in devices)
        {
            string model = "";
            try { model = $"{d.ManufacturerName} {d.ModelName}"; } catch { }
            var ip = FindWanIp(d);
            Log.Info($"UPnP: roteador {model} -> IP WAN {(ip == null ? "(não informou)" : Tunnel.Mask(new IPEndPoint(ip, 0)).Replace(":0", ""))}");
            if (ip != null && best == null) best = ip;
        }
        return best;
    }

    static IPAddress? FindWanIp(dynamic device)
    {
        try
        {
            foreach (dynamic s in device.Services)
            {
                string st = s.ServiceTypeIdentifier;
                if (!st.Contains("WANIPConnection") && !st.Contains("WANPPPConnection")) continue;
                object? outArgs = null;
                s.InvokeAction("GetExternalIPAddress", new object[0], ref outArgs);
                if (outArgs is object[] arr && arr.Length > 0 && IPAddress.TryParse(arr[0]?.ToString(), out var ip) && !ip.Equals(IPAddress.Any))
                    return ip;
            }
        }
        catch { }
        try
        {
            if ((bool)device.HasChildren)
                foreach (dynamic c in device.Children)
                {
                    var r = FindWanIp(c);
                    if (r != null) return r;
                }
        }
        catch { }
        return null;
    }

    /// <summary>IP da faixa de CGNAT (100.64/10) ou privado: não é alcançável pela internet, mas pode ser dentro da operadora.</summary>
    public static bool IsCarrierOrPrivate(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        if (b.Length != 4) return false;
        return (b[0] == 100 && b[1] >= 64 && b[1] <= 127) || b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168);
    }

    public static bool IsCarrier(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return b.Length == 4 && b[0] == 100 && b[1] >= 64 && b[1] <= 127;
    }
}
