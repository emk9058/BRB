using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using QRCoder;

public static class HostDesk
{
    public static IReadOnlyList<string> PhoneUrls(int port)
    {
        var found = new List<(int Rank, string Url)>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up)
                continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                continue;

            var label = $"{nic.Name} {nic.Description}";
            if (IsVirtual(label))
                continue;

            var rank = nic.NetworkInterfaceType switch
            {
                NetworkInterfaceType.Wireless80211 => 0,
                NetworkInterfaceType.Ethernet => 1,
                _ => 2
            };

            foreach (var address in nic.GetIPProperties().UnicastAddresses)
            {
                var ip = address.Address;
                if (ip.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(ip))
                    continue;
                if (!IsPrivate(ip))
                    continue;

                found.Add((rank, $"http://{ip}:{port}"));
            }
        }

        return found
            .OrderBy(item => item.Rank)
            .Select(item => item.Url)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static byte[]? Qr(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(url, QRCodeGenerator.ECCLevel.Q);
        return new PngByteQRCode(data).GetGraphic(12);
    }

    private static bool IsVirtual(string label)
    {
        return label.Contains("virtual", StringComparison.OrdinalIgnoreCase)
            || label.Contains("vethernet", StringComparison.OrdinalIgnoreCase)
            || label.Contains("hyper-v", StringComparison.OrdinalIgnoreCase)
            || label.Contains("wsl", StringComparison.OrdinalIgnoreCase)
            || label.Contains("vmware", StringComparison.OrdinalIgnoreCase)
            || label.Contains("virtualbox", StringComparison.OrdinalIgnoreCase)
            || label.Contains("bluetooth", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPrivate(IPAddress ip)
    {
        var bytes = ip.GetAddressBytes();
        if (bytes[0] == 10)
            return true;
        if (bytes[0] == 192 && bytes[1] == 168)
            return true;
        if (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
            return true;
        return false;
    }
}
