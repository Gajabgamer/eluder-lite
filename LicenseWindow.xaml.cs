using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace MyOverlayPOC
{
    // ──────────────────────────────────────────────
    //  Supabase config — hardcoded
    // ──────────────────────────────────────────────
    internal static class SupabaseConfig
    {
        public const string Url     = "https://yyqqpbobmsbkhutcdwqy.supabase.co";
        public const string AnonKey = "sb_publishable_bKFL3oxenFBE7Chp8QZGug_BpC4Artl";
    }

    // ──────────────────────────────────────────────
    //  Error types
    // ──────────────────────────────────────────────
    public enum LicenseError
    {
        None,
        InvalidToken,
        Revoked,
        HwidMismatch,   // ← shows Telegram button
        NetworkError,
        Unknown
    }

    // ──────────────────────────────────────────────
    //  DTOs
    // ──────────────────────────────────────────────
    public class LicenseFile
    {
        [JsonPropertyName("token")]
        public string Token { get; set; } = "";
    }

    internal class LicenseRow
    {
        [JsonPropertyName("hwid")]
        public string? Hwid { get; set; }

        [JsonPropertyName("revoked")]
        public bool Revoked { get; set; }
    }

    // ──────────────────────────────────────────────
    //  LicenseManager — talks directly to Supabase REST
    // ──────────────────────────────────────────────
    public static class LicenseManager
    {
        private static readonly string LicensePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Eluder", "license.json");

        private static readonly HttpClient Http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(12)
        };

        static LicenseManager()
        {
            Http.DefaultRequestHeaders.Add("apikey", SupabaseConfig.AnonKey);
            Http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", SupabaseConfig.AnonKey);
        }

        public static string? LoadSavedToken()
        {
            if (!File.Exists(LicensePath)) return null;
            try
            {
                var lf = JsonSerializer.Deserialize<LicenseFile>(File.ReadAllText(LicensePath));
                return string.IsNullOrWhiteSpace(lf?.Token) ? null : lf.Token;
            }
            catch { return null; }
        }

        public static void SaveToken(string token)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LicensePath)!);
            File.WriteAllText(LicensePath,
                JsonSerializer.Serialize(new LicenseFile { Token = token }));
        }

        /// <summary>
        /// Returns (true, msg, None) on success or (false, reason, errorType) on failure.
        /// </summary>
        public static async Task<(bool ok, string message, LicenseError error)> ValidateAsync(string token)
        {
            try
            {
                token = token.Trim().ToUpperInvariant();
                string hwid = HardwareId.Get();

                string url = $"{SupabaseConfig.Url}/rest/v1/licenses" +
                             $"?token=eq.{Uri.EscapeDataString(token)}" +
                             $"&select=hwid,revoked&limit=1";

                var resp = await Http.GetAsync(url);
                string body = await resp.Content.ReadAsStringAsync();
                var rows = JsonSerializer.Deserialize<LicenseRow[]>(body);

                if (rows == null || rows.Length == 0)
                    return (false, "Invalid access token.", LicenseError.InvalidToken);

                var row = rows[0];

                if (row.Revoked)
                    return (false, "This token has been revoked.", LicenseError.Revoked);

                // First activation — bind HWID
                if (row.Hwid == null)
                {
                    bool bound = await BindHwidAsync(token, hwid);
                    if (!bound)
                        return (false, "Failed to bind device. Try again.", LicenseError.Unknown);
                    return (true, "Activated.", LicenseError.None);
                }

                // Same device — OK
                if (row.Hwid == hwid)
                    return (true, "OK", LicenseError.None);

                // Different device
                return (false,
                    "This token is already activated on a different device.",
                    LicenseError.HwidMismatch);
            }
            catch (TaskCanceledException)
            {
                return (false, "Connection timed out. Check your internet.", LicenseError.NetworkError);
            }
            catch (HttpRequestException ex)
            {
                return (false, $"Cannot reach license server: {ex.Message}", LicenseError.NetworkError);
            }
            catch (Exception ex)
            {
                return (false, $"Error: {ex.Message}", LicenseError.Unknown);
            }
        }

        private static async Task<bool> BindHwidAsync(string token, string hwid)
        {
            string url = $"{SupabaseConfig.Url}/rest/v1/licenses" +
                         $"?token=eq.{Uri.EscapeDataString(token)}";

            var payload = JsonSerializer.Serialize(new { hwid });
            var content = new StringContent(payload, Encoding.UTF8, "application/json");
            content.Headers.Add("Prefer", "return=minimal");

            var resp = await Http.PatchAsync(url, content);
            return resp.IsSuccessStatusCode;
        }
    }

    // ──────────────────────────────────────────────
    //  LicenseWindow — two modes:
    //    normal  → token input (first launch)
    //    blocked → HWID mismatch screen (returning user, wrong device)
    // ──────────────────────────────────────────────
    public partial class LicenseWindow : Window
    {
        public bool Activated { get; private set; } = false;
        private readonly bool _blockedMode;

        /// <param name="blockedMode">True = skip token input, just show blocked screen.</param>
        public LicenseWindow(bool blockedMode = false)
        {
            InitializeComponent();
            _blockedMode = blockedMode;

            if (blockedMode)
                ShowBlockedScreen("This token is already activated on a different device.");
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Application.Current.Shutdown();
        }

        private void TokenInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) _ = TryActivateAsync();
        }

        private void ActivateButton_Click(object sender, RoutedEventArgs e)
        {
            _ = TryActivateAsync();
        }

        private async Task TryActivateAsync()
        {
            string token = TokenInput.Text.Trim();
            if (string.IsNullOrEmpty(token))
            {
                SetStatus("Please enter a token.", LicenseError.Unknown);
                return;
            }

            SetStatus("Validating…", LicenseError.None);
            ActivateButton.IsEnabled = false;
            TokenInput.IsEnabled = false;
            TelegramButton.Visibility = Visibility.Collapsed;

            var (ok, message, error) = await LicenseManager.ValidateAsync(token);

            if (ok)
            {
                LicenseManager.SaveToken(token.ToUpperInvariant());
                Activated = true;
                SetStatus("✓ Activated — launching…", LicenseError.None);
                await Task.Delay(700);
                Close();
            }
            else
            {
                SetStatus($"✗ {message}", error);
                ActivateButton.IsEnabled = true;
                TokenInput.IsEnabled = true;
                TokenInput.Focus();
            }
        }

        // Called when app detects HWID mismatch on a returning user
        public void ShowBlockedScreen(string msg)
        {
            // Hide input area, show blocked state
            TokenInput.Visibility        = Visibility.Collapsed;
            ActivateButton.Visibility    = Visibility.Collapsed;
            InputLabel.Visibility        = Visibility.Collapsed;
            SubLabel.Text = "Your license is locked to a different device.\nContact support to reset your device binding.";

            SetStatus($"✗ {msg}", LicenseError.HwidMismatch);
        }

        private void SetStatus(string msg, LicenseError error)
        {
            StatusLabel.Text = msg;

            if (error == LicenseError.None && msg.StartsWith("✓"))
            {
                StatusLabel.Foreground = new SolidColorBrush(Color.FromRgb(0x32, 0xCD, 0x32));
                TelegramButton.Visibility = Visibility.Collapsed;
            }
            else if (error == LicenseError.HwidMismatch)
            {
                StatusLabel.Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0x33, 0x33));
                TelegramButton.Visibility = Visibility.Visible;   // ← show Telegram button
            }
            else if (error == LicenseError.None)
            {
                StatusLabel.Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
                TelegramButton.Visibility = Visibility.Collapsed;
            }
            else
            {
                StatusLabel.Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0x33, 0x33));
                TelegramButton.Visibility = Visibility.Collapsed;
            }
        }

        private void TelegramButton_Click(object sender, RoutedEventArgs e)
        {
            Process.Start(new ProcessStartInfo
            {
                FileName        = "https://t.me/Hackerseb",
                UseShellExecute = true
            });
        }
    }
}
