using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Eluder2
{
    /// <summary>
    /// Clean, modern license key entry dialog.
    /// Returns DialogResult.OK only after a successful remote validation.
    /// </summary>
    public sealed class LicenseDialog : Form
    {
        private readonly Label      _lblMessage;
        private readonly TextBox    _txtKey;
        private readonly Button     _btnActivate;
        private readonly Label      _lblStatus;
        private readonly PictureBox _spinner;

        public LicenseDialog(string message)
        {
            // ── Form ──────────────────────────────────────────────────────
            Text            = "Eluder 2.0 — Activation";
            Size            = new Size(480, 310);
            MinimumSize     = Size;
            MaximumSize     = Size;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox     = false;
            MinimizeBox     = false;
            StartPosition   = FormStartPosition.CenterScreen;
            BackColor       = Color.FromArgb(18, 18, 24);
            ForeColor       = Color.WhiteSmoke;
            Font            = new Font("Segoe UI", 10f);

            // ── Logo label ───────────────────────────────────────────────
            var logo = new Label
            {
                Text      = "⚡ ELUDER 2.0",
                Font      = new Font("Segoe UI", 16f, FontStyle.Bold),
                ForeColor = Color.FromArgb(99, 179, 237),
                AutoSize  = true,
                Location  = new Point(28, 24)
            };

            // ── Message ───────────────────────────────────────────────────
            _lblMessage = new Label
            {
                Text      = message,
                AutoSize  = false,
                Size      = new Size(424, 40),
                Location  = new Point(28, 72),
                ForeColor = Color.FromArgb(180, 180, 200)
            };

            // ── Key input ─────────────────────────────────────────────────
            var lblKeyHint = new Label
            {
                Text      = "LICENSE KEY",
                Font      = new Font("Segoe UI", 8f, FontStyle.Bold),
                ForeColor = Color.FromArgb(100, 120, 160),
                AutoSize  = true,
                Location  = new Point(28, 124)
            };

            _txtKey = new TextBox
            {
                Location        = new Point(28, 144),
                Size            = new Size(424, 32),
                BackColor       = Color.FromArgb(30, 32, 42),
                ForeColor       = Color.WhiteSmoke,
                BorderStyle     = BorderStyle.FixedSingle,
                Font            = new Font("Consolas", 11f),
                PlaceholderText = "XXXX-XXXX-XXXX-XXXX",
                CharacterCasing = CharacterCasing.Upper
            };
            _txtKey.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) ActivateAsync(); };

            // ── Status label ──────────────────────────────────────────────
            _lblStatus = new Label
            {
                Text      = "",
                AutoSize  = false,
                Size      = new Size(300, 24),
                Location  = new Point(28, 192),
                ForeColor = Color.FromArgb(252, 100, 100)
            };

            // ── Activate button ───────────────────────────────────────────
            _btnActivate = new Button
            {
                Text      = "Activate",
                Location  = new Point(324, 186),
                Size      = new Size(128, 36),
                BackColor = Color.FromArgb(99, 179, 237),
                ForeColor = Color.FromArgb(10, 10, 20),
                FlatStyle = FlatStyle.Flat,
                Font      = new Font("Segoe UI", 10f, FontStyle.Bold),
                Cursor    = Cursors.Hand
            };
            _btnActivate.FlatAppearance.BorderSize = 0;
            _btnActivate.Click += (_, _) => ActivateAsync();

            // ── Spinner (simple label used as text spinner) ───────────────
            _spinner = new PictureBox
            {
                Location = new Point(28, 240),
                Size     = new Size(424, 24),
                BackColor = Color.Transparent
            };
            var spinLabel = new Label
            {
                Text      = "Need a key? Visit github.com/Gajabgamer/eluder-lite",
                ForeColor = Color.FromArgb(80, 100, 140),
                AutoSize  = false,
                Size      = new Size(424, 24),
                Location  = new Point(28, 244),
                Font      = new Font("Segoe UI", 8.5f)
            };

            Controls.AddRange(new Control[] {
                logo, _lblMessage, lblKeyHint, _txtKey,
                _lblStatus, _btnActivate, spinLabel
            });
        }

        private async void ActivateAsync()
        {
            var key = _txtKey.Text.Trim();
            if (string.IsNullOrEmpty(key))
            {
                SetStatus("Please enter your license key.", error: true);
                return;
            }

            SetBusy(true);
            SetStatus("Validating…", error: false);

            var result = await LicenseManager.ValidateAsync(key).ConfigureAwait(true);

            if (result.Valid)
            {
                LicenseManager.SaveKey(key);
                SetStatus("✔ Activated!", error: false);
                _lblStatus.ForeColor = Color.FromArgb(72, 199, 142);
                await Task.Delay(600).ConfigureAwait(true);
                DialogResult = DialogResult.OK;
                Close();
            }
            else
            {
                SetStatus($"✗ {result.Message}", error: true);
                SetBusy(false);
            }
        }

        private void SetStatus(string text, bool error)
        {
            _lblStatus.ForeColor = error
                ? Color.FromArgb(252, 100, 100)
                : Color.FromArgb(180, 200, 220);
            _lblStatus.Text = text;
        }

        private void SetBusy(bool busy)
        {
            _btnActivate.Enabled = !busy;
            _txtKey.Enabled      = !busy;
            _btnActivate.Text    = busy ? "…" : "Activate";
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // If user X'd without activating, treat as cancel
            if (DialogResult != DialogResult.OK)
                DialogResult = DialogResult.Cancel;
            base.OnFormClosing(e);
        }
    }
}
