using System;
using System.IO;
using System.Management;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Eluder2
{
    public struct LicenseResult
    {
        public bool Valid;
        public string Message;
    }

    public static class LicenseManager
    {
        private const string SupabaseUrl     = "https://yyqqpbobmsbkhutcdwqy.supabase.co/functions/v1/validate-license";
        private const string SupabaseAnonKey = "sb_publishable_bKFL3oxenFBE7Chp8QZGug_BpC4Artl";
        private static readonly string LicenseDir  = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Eluder");
        private static readonly string LicenseFile = Path.Combine(LicenseDir, "license.key");

        // ── Hardware ID ────────────────────────────────────────────────────
        public static string GetHardwareId()
        {
            var parts = new System.Collections.Generic.List<string>();
            try
            {
                using var cs = new ManagementObjectSearcher("SELECT SerialNumber FROM Win32_BIOS");
                foreach (ManagementObject o in cs.Get())
                    parts.Add(o["SerialNumber"]?.ToString() ?? "");
            }
            catch { }
            try
            {
                using var cs = new ManagementObjectSearcher("SELECT ProcessorId FROM Win32_Processor");
                foreach (ManagementObject o in cs.Get())
                    parts.Add(o["ProcessorId"]?.ToString() ?? "");
            }
            catch { }
            try
            {
                using var cs = new ManagementObjectSearcher("SELECT SerialNumber FROM Win32_DiskDrive");
                foreach (ManagementObject o in cs.Get())
                    parts.Add(o["SerialNumber"]?.ToString() ?? "");
            }
            catch { }

            var raw = string.Join("|", parts);
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
            return Convert.ToHexString(hash)[..16];  // 16-char hex
        }

        // ── Saved key helpers ──────────────────────────────────────────────
        public static string? LoadSavedKey()
        {
            try
            {
                if (File.Exists(LicenseFile))
                {
                    var key = File.ReadAllText(LicenseFile).Trim();
                    return string.IsNullOrEmpty(key) ? null : key;
                }
            }
            catch { }
            return null;
        }

        public static void SaveKey(string key)
        {
            try
            {
                Directory.CreateDirectory(LicenseDir);
                File.WriteAllText(LicenseFile, key.Trim());
            }
            catch { }
        }

        public static void ClearKey()
        {
            try { File.Delete(LicenseFile); } catch { }
        }

        // ── Remote validation ──────────────────────────────────────────────
        public static async Task<LicenseResult> ValidateAsync(string licenseKey)
        {
            var hwid = GetHardwareId();
            var body = JsonSerializer.Serialize(new { license_key = licenseKey, hardware_id = hwid });

            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            client.DefaultRequestHeaders.Add("apikey", SupabaseAnonKey);
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {SupabaseAnonKey}");

            try
            {
                var content  = new StringContent(body, Encoding.UTF8, "application/json");
                var response = await client.PostAsync(SupabaseUrl, content).ConfigureAwait(false);
                var json     = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                using var doc = JsonDocument.Parse(json);
                var root      = doc.RootElement;

                bool valid = root.TryGetProperty("valid", out var v) && v.GetBoolean();
                string msg = root.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";

                return new LicenseResult { Valid = valid, Message = msg };
            }
            catch (Exception ex)
            {
                return new LicenseResult { Valid = false, Message = $"Network error: {ex.Message}" };
            }
        }

        // ── Entry point called at startup ──────────────────────────────────
        /// <summary>
        /// Checks saved key; if none/invalid, shows dialog.
        /// Returns false → caller should exit.
        /// </summary>
        public static LicenseResult CheckOnStartup()
        {
            var savedKey = LoadSavedKey();
            if (savedKey != null)
            {
                // Validate cached key (async over sync at startup)
                var result = Task.Run(() => ValidateAsync(savedKey)).GetAwaiter().GetResult();
                if (result.Valid)
                    return result;
                // Key rejected — clear it and fall through to dialog
                ClearKey();
            }

            // Show dialog on UI thread (called before Application.Run)
            using var dlg = new LicenseDialog(savedKey != null
                ? "Your license key was rejected. Please re-enter it."
                : "Welcome to Eluder 2.0. Enter your license key to continue.");

            if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                return new LicenseResult { Valid = true, Message = "Licensed" };

            return new LicenseResult { Valid = false, Message = "No valid license." };
        }
    }
}
