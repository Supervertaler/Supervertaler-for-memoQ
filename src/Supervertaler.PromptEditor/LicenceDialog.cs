using System;
using System.Drawing;
using System.Windows.Forms;
using Supervertaler.Core;

namespace Supervertaler.PromptEditor
{
    /// <summary>
    /// What the licence window shows, read once. The dialog is built from this
    /// rather than from the licence itself so the layout can be measured in every
    /// state - trial, licensed, lapsed, unreadable, damaged - without a test ever
    /// reading or writing anybody's real licence.
    /// </summary>
    internal sealed class LicenceView
    {
        public LicenceState State;
        public int TrialDaysRemaining;
        public DateTime TrialEndsUtc;
        public bool HasKey;
        public string MaskedKey = "";
        public DateTime LastValidatedUtc;
        public bool DamagedFileFound;

        /// <summary>The licence as it stands. Unknown if it cannot be read at all, which is never a refusal.</summary>
        internal static LicenceView Current()
        {
            try
            {
                var l = SupervertalerLicence.Instance;
                return new LicenceView
                {
                    State = l.State,
                    TrialDaysRemaining = l.TrialDaysRemaining,
                    TrialEndsUtc = l.TrialEndsUtc,
                    HasKey = l.HasKey,
                    MaskedKey = l.MaskedKey ?? "",
                    LastValidatedUtc = l.LastValidatedUtc,
                    DamagedFileFound = l.DamagedFileFound,
                };
            }
            catch
            {
                return new LicenceView { State = LicenceState.Unknown };
            }
        }

        /// <summary>One line for the editor's status bar, or null when there is nothing worth saying.</summary>
        internal string StatusLine()
        {
            switch (State)
            {
                case LicenceState.Trial:
                    return TrialDaysRemaining == 1
                        ? "Supervertaler free trial: 1 day left."
                        : "Supervertaler free trial: " + TrialDaysRemaining + " days left.";
                case LicenceState.Expired:
                    return "AI translation is paused – see Settings, Licence.";
                default:
                    return null;
            }
        }
    }

    /// <summary>
    /// The licence, from inside the editor - the one Supervertaler window in
    /// memoQ that can hold it, since memoQ gives an add-in nowhere else.
    ///
    /// <para>The same licence as Supervertaler for Trados: the window says so,
    /// because someone who bought for Trados should not think memoQ needs a
    /// second purchase, and a key already activated here in Trados does not use
    /// a second activation.</para>
    ///
    /// <para>After any action the dialog closes with <see cref="DialogResult.Retry"/>
    /// and the caller opens it again on a fresh <see cref="LicenceView"/>. That
    /// keeps the layout built once and measured once, rather than rearranged in
    /// place - which is where every clipped dialog in this product has come
    /// from.</para>
    /// </summary>
    internal sealed class LicenceDialog : Form
    {
        private const string BuyUrl = "https://supervertaler.com";

        private readonly TextBox _key;

        public LicenceDialog(LicenceView view)
        {
            Font = Ui.Default;
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;

            Text = "Licence – Supervertaler";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            AppIcon.Apply(this);

            // Width is a choice, height is measured, and every control is added to
            // the form before it is measured - an unparented control measures in
            // the wrong font.
            const int width = 480;
            const int margin = 14;
            const int inner = width - margin * 2;
            var y = margin;

            Label Paragraph(string text, Color? colour = null, bool bold = false)
            {
                var label = new Label
                {
                    Text = text,
                    AutoSize = true,
                    MaximumSize = new Size(inner, 0),
                    Location = new Point(margin, y),
                };
                if (colour.HasValue) label.ForeColor = colour.Value;
                if (bold) label.Font = new Font(Ui.Default, FontStyle.Bold);
                Controls.Add(label);
                y += label.Height + 8;
                return label;
            }

            Button AddButton(string text, int x)
            {
                var b = new Button { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Font = Font };
                Controls.Add(b);
                b.Location = new Point(x, y);
                return b;
            }

            Paragraph(Headline(view), bold: true);
            Paragraph(Detail(view));

            // Every start while it is true, until a key is activated again: a
            // warning shown once is a warning missed, and the customer is then
            // simply told their licence has lapsed with no idea why.
            if (view.DamagedFileFound && view.State != LicenceState.Licensed)
            {
                Paragraph(
                    "The licence file on this computer was damaged and has been replaced. Enter your " +
                    "licence key again to restore it – it is in the email you received when you bought " +
                    "Supervertaler.",
                    Color.Firebrick);
            }

            Paragraph(
                "One Supervertaler licence covers Supervertaler for Trados and Supervertaler for memoQ. " +
                "A key already activated on this computer counts once, whichever of them it was entered in.",
                SystemColors.GrayText);

            y += 4;

            if (view.HasKey)
            {
                var check = AddButton("Check now", margin);
                check.Click += async (s, e) =>
                {
                    check.Enabled = false;
                    var result = await SupervertalerLicence.Instance.ValidateOnlineAsync().ConfigureAwait(true);
                    Report(result.Ok, result.Message);
                };

                var deactivate = AddButton("Deactivate this computer", check.Right + 8);
                deactivate.Click += async (s, e) =>
                {
                    var sure = MessageBox.Show(this,
                        "Remove the licence from this computer? It stops Supervertaler for Trados using it " +
                        "here as well, and frees the activation for another computer.",
                        Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
                    if (sure != DialogResult.OK) return;

                    deactivate.Enabled = false;
                    var result = await SupervertalerLicence.Instance.DeactivateAsync().ConfigureAwait(true);
                    Report(result.Ok, result.Message);
                };

                y += Math.Max(check.Height, deactivate.Height) + 12;
                _key = null;
            }
            else
            {
                var caption = Paragraph("Licence key");
                caption.ForeColor = SystemColors.GrayText;

                _key = new TextBox { Location = new Point(margin, y), Width = inner };
                Controls.Add(_key);
                y += _key.Height + 8;

                var activate = AddButton("Activate", margin);
                activate.Click += async (s, e) =>
                {
                    var key = (_key.Text ?? "").Trim();
                    if (key.Length == 0) { _key.Focus(); return; }

                    activate.Enabled = false;
                    var result = await SupervertalerLicence.Instance.ActivateAsync(key).ConfigureAwait(true);
                    Report(result.Ok, result.Message);
                };
                AcceptButton = activate;

                var buy = new LinkLabel
                {
                    Text = "Buy a licence",
                    AutoSize = true,
                };
                Controls.Add(buy);
                buy.Location = new Point(activate.Right + 14, y + (activate.Height - buy.Height) / 2);
                buy.LinkClicked += (s, e) =>
                {
                    try { System.Diagnostics.Process.Start(BuyUrl); }
                    catch { MessageBox.Show(this, BuyUrl, Text); }
                };

                y += activate.Height + 12;
            }

            var close = new Button { Text = "Close", DialogResult = DialogResult.Cancel, Font = Font };
            Controls.Add(close);
            close.Size = new Size(Math.Max(close.PreferredSize.Width, 84), close.PreferredSize.Height);
            close.Location = new Point(width - margin - close.Width, y);
            CancelButton = close;

            ClientSize = new Size(width, close.Bottom + margin);
        }

        /// <summary>Says what happened, then reopens on the new state.</summary>
        private void Report(bool ok, string message)
        {
            MessageBox.Show(this, string.IsNullOrWhiteSpace(message) ? (ok ? "Done." : "That did not work.") : message,
                Text, MessageBoxButtons.OK, ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            DialogResult = DialogResult.Retry;
        }

        private static string Headline(LicenceView v)
        {
            switch (v.State)
            {
                case LicenceState.Licensed:
                    return "Licensed";
                case LicenceState.Trial:
                    return v.TrialDaysRemaining == 1
                        ? "Free trial – 1 day left"
                        : "Free trial – " + v.TrialDaysRemaining + " days left";
                case LicenceState.Expired:
                    return v.HasKey ? "Licence not confirmed for 30 days" : "Free trial ended";
                default:
                    return "Licence could not be read";
            }
        }

        private static string Detail(LicenceView v)
        {
            switch (v.State)
            {
                case LicenceState.Licensed:
                    return "Key " + v.MaskedKey + "."
                        + (v.LastValidatedUtc > DateTime.MinValue
                            ? " Last confirmed online on " + v.LastValidatedUtc.ToLocalTime().ToString("d MMMM yyyy") + "."
                            : "");
                case LicenceState.Trial:
                    return "Everything works until " + v.TrialEndsUtc.ToLocalTime().ToString("d MMMM yyyy")
                        + ". After that, AI translation pauses until a licence key is entered.";
                case LicenceState.Expired:
                    return v.HasKey
                        ? "AI translation is paused until the licence is confirmed online. Connect to the " +
                          "internet and press Check now. Terminology, termbases, prompts and memory banks " +
                          "keep working."
                        : "AI translation is paused until a licence key is entered. Terminology, termbases, " +
                          "prompts and memory banks keep working.";
                default:
                    return "Everything keeps working. Supervertaler will try again next time it starts.";
            }
        }
    }
}
