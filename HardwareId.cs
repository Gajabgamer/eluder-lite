using System;
using System.Management;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using System.Linq;

namespace MyOverlayPOC
{
    /// <summary>
    /// Generates a stable, machine-unique hardware fingerprint by hashing
    /// CPU ID + first fixed-disk serial number + first physical MAC address.
    /// The result is a 16-char uppercase hex string (truncated SHA-256).
    /// </summary>
    public static class HardwareId
    {
        private static string? _cached;

        public static string Get()
        {
            if (_cached != null) return _cached;

            var sb = new StringBuilder();

            // 1. CPU processor ID
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT ProcessorId FROM Win32_Processor");
                foreach (ManagementObject obj in searcher.Get())
                {
                    sb.Append(obj["ProcessorId"]?.ToString() ?? "");
                    break;
                }
            }
            catch { sb.Append("CPU_ERR"); }

            // 2. First fixed-disk serial number
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT SerialNumber FROM Win32_DiskDrive WHERE MediaType='Fixed hard disk media'");
                foreach (ManagementObject obj in searcher.Get())
                {
                    sb.Append(obj["SerialNumber"]?.ToString()?.Trim() ?? "");
                    break;
                }
            }
            catch { sb.Append("DISK_ERR"); }

            // 3. First physical (non-virtual, non-loopback) MAC address
            try
            {
                var mac = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                             && n.NetworkInterfaceType != NetworkInterfaceType.Tunnel
                             && n.OperationalStatus == OperationalStatus.Up)
                    .OrderBy(n => n.Name)
                    .Select(n => n.GetPhysicalAddress().ToString())
                    .FirstOrDefault(m => m.Length >= 12 && m != "000000000000");

                sb.Append(mac ?? "MAC_ERR");
            }
            catch { sb.Append("MAC_ERR"); }

            // Hash with SHA-256 and return first 16 hex chars
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
            _cached = Convert.ToHexString(hash)[..16];
            return _cached;
        }
    }
}
