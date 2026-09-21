using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Supervertaler.Core;

namespace Supervertaler.PromptEditor
{
    /// <summary>
    /// Connecting an AI assistant to memoQ.
    ///
    /// <para>Two assistants, two ways in, and only one of them needs a button.
    /// Claude Desktop installs the <c>.mcpb</c> bundle itself; ChatGPT desktop
    /// has no equivalent, so the editor fetches the server and writes the entry
    /// into the file ChatGPT shares with Codex. Both may be set up at once, and
    /// both talk to the same plugin.</para>
    ///
    /// <para>It lives in the editor because memoQ gives an add-in no UI beyond
    /// the MT options dialog, and because none of this has anything to do with
    /// translating - it works with memoQ closed.</para>
    /// </summary>
    internal sealed class ChatGptSetupDialog : Form
    {
        private readonly Button _setUp;
        private readonly Label _status;

        /// <summary>
        /// Where the Claude Desktop extension file and its instructions live.
        /// The dialog cannot fetch that one, so the least it can do is not leave
        /// the reader to search for it.
        /// </summary>
        private const string DocsUrl = "https://docs.supervertaler.com/memoq/mcp-server/";

        /// <summary>
        /// What this product is, as far as the shared setup is concerned. The
        /// environment variable is the whole of what makes the shared server
        /// memoQ's rather than Trados's: it decides which handshake file the
        /// server looks for, so the two can be registered side by side and each
        /// reaches its own CAT tool.
        /// </summary>
        internal static ChatGptMcpSetup.Options Options() => new ChatGptMcpSetup.Options
        {
            ProductName = "memoQ",
            BlockName = "mcp_servers.supervertaler_memoq",

            // The shared data folder, not the Addins folder: Addins is under
            // Program Files, where an ordinary user cannot write, and an
            // installer run rewrites it - which would break the path ChatGPT has
            // stored.
            ServerDir = Path.Combine(SupervertalerPaths.Root, "memoq", "mcp"),

            Environment = new Dictionary<string, string> { { "SUPERVERTALER_HOST", "memoq" } },

            BlockComment =
                "# Supervertaler for memoQ – live connection to the open memoQ project.\n" +
                "# Local stdio server; it reaches memoQ on this machine only.",

            // Always download. Trados decides this by comparing version numbers,
            // which it can do because its plugin and the server ship from one
            // release and share a version tail; this product's version has no
            // such relationship to that server, so a comparison here would be a
            // guess dressed as a rule. Pressing a button called "Set up ChatGPT
            // desktop" is explicit and rare, and the honest answer to it is the
            // current server - which costs a download and no remembered state.
            IsOutdated = _ => true,
        };

        public ChatGptSetupDialog()
        {
            // The shell's own dialog font, before anything else is built, so
            // every control below inherits it. See Ui.Default.
            Font = Ui.Default;
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;

            Text = "Connect AI assistant";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            AppIcon.Apply(this);

            // Width is a choice, height is measured. Every control reports the
            // height it actually wants and the next one is placed below it, so a
            // larger interface font moves the buttons down rather than pushing
            // them through the bottom edge. Three separate reports of clipped
            // dialogs in this product came from fixed numbers.
            const int width = 520;
            const int margin = 14;
            const int inner = width - margin * 2;
            var y = margin;

            Label Paragraph(string text, bool bold = false)
            {
                var label = new Label
                {
                    Text = text,
                    AutoSize = true,
                    MaximumSize = new Size(inner, 0),
                    Location = new Point(margin, y),
                };
                if (bold) label.Font = new Font(Ui.Default, FontStyle.Bold);
                label.Size = label.PreferredSize;
                Controls.Add(label);
                y += label.Height + 8;
                return label;
            }

            // Says what it is FOR before it says what to press. The first version
            // opened with "this downloads the Supervertaler MCP server", which
            // assumes the reader knows what an MCP server is and why they would
            // want one - and translators do not, nor should they have to.
            Paragraph(
                "An AI assistant can work with the memoQ project you have open: read the " +
                "document, see the segment you are on, look words up in your termbases, and put " +
                "translations ready for your next Pre-translate. Set up either assistant, or both.");

            Paragraph("ChatGPT desktop", bold: true);
            Paragraph(
                "Press the button. Supervertaler fetches what ChatGPT needs, keeps it in your " +
                "Supervertaler folder and points ChatGPT at it. It adds one line to ChatGPT's own " +
                "settings file, leaves anything else in there alone, and keeps a dated copy of it " +
                "first.");

            _setUp = new Button
            {
                Text = "Set up ChatGPT desktop",
                Location = new Point(margin, y),
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
            };
            _setUp.Click += async (s, e) => await RunAsync().ConfigureAwait(true);
            Controls.Add(_setUp);
            y += _setUp.PreferredSize.Height + 8;

            _status = new Label
            {
                Text = Describe(),
                AutoSize = true,
                MaximumSize = new Size(inner, 0),
                Location = new Point(margin, y),
                ForeColor = SystemColors.GrayText,
            };
            _status.Size = _status.PreferredSize;
            Controls.Add(_status);
            y += _status.Height + 16;

            Paragraph("Claude Desktop", bold: true);

            // "Claude Desktop installs itself" was the first wording, and it was
            // reported as confusing on sight: it says nothing about what the
            // reader is meant to do, and it leaves the obvious question - why
            // does one of these have a button and the other not - unanswered.
            // Say what to do, then why there is no button, then where the file
            // comes from, which is the question the old text walked straight
            // past.
            Paragraph(
                "Claude Desktop adds connections from a file you choose yourself, so there is no " +
                "button for it here. In Claude Desktop, open Settings, then Extensions, then " +
                "Advanced settings, then Install extension, and pick the Supervertaler for memoQ " +
                "file.");

            var where = new LinkLabel
            {
                Text = "Where to get that file, with pictures",
                AutoSize = true,
                MaximumSize = new Size(inner, 0),
                Location = new Point(margin, y),
            };
            where.LinkClicked += (s, e) =>
            {
                try { System.Diagnostics.Process.Start(DocsUrl); }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "The page could not be opened: " + ex.Message +
                        Environment.NewLine + Environment.NewLine + DocsUrl,
                        "Connect AI assistant", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            };
            where.Size = where.PreferredSize;
            Controls.Add(where);
            y += where.Height + 12;

            var close = new Button
            {
                Text = "Close",
                DialogResult = DialogResult.Cancel,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
            };
            close.Location = new Point(width - margin - Math.Max(close.PreferredSize.Width, 84), y);
            close.Size = new Size(Math.Max(close.PreferredSize.Width, 84),
                                  close.PreferredSize.Height);
            Controls.Add(close);

            CancelButton = close;
            ClientSize = new Size(width, close.Bottom + margin);
        }

        /// <summary>Where things stand right now, in one line under the button.</summary>
        private string Describe()
        {
            if (ChatGptMcpSetup.IsConfigured(Options()))
                return "Already set up. Press the button again to update the server.";

            return ChatGptMcpSetup.IsChatGptInstalled()
                ? "ChatGPT desktop found on this computer."
                : "ChatGPT desktop was not found. You can still set this up – the same entry " +
                  "serves the Codex command line and the Codex editor extension.";
        }

        private async System.Threading.Tasks.Task RunAsync()
        {
            _setUp.Enabled = false;
            var wasCursor = Cursor;
            Cursor = Cursors.WaitCursor;
            try
            {
                _status.Text = "Working…";
                var result = await ChatGptMcpSetup
                    .RunAsync(Options(), line => _status.Text = line)
                    .ConfigureAwait(true);

                _status.Text = result.Success
                    ? "Done. Restart ChatGPT."
                    : "It did not work – see the message.";

                MessageBox.Show(this, result.Message,
                    result.Success ? "ChatGPT desktop" : "Could not set up ChatGPT desktop",
                    MessageBoxButtons.OK,
                    result.Success ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            }
            finally
            {
                // On the failure path as much as the success path: a button left
                // disabled by an exception is a dialog the user has to close and
                // reopen to try again.
                Cursor = wasCursor;
                _setUp.Enabled = true;
            }
        }
    }
}
