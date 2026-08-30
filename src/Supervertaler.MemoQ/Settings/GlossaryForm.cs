using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Supervertaler.MemoQ.Core;

namespace Supervertaler.MemoQ.Settings
{
    /// <summary>
    /// The TB plugin's options dialog, reached from memoQ's terminology plugin
    /// settings. Also reachable from the MT options dialog, because the glossary
    /// feeds both and a user should not have to know which half of the plugin owns
    /// the setting.
    /// </summary>
    internal sealed class GlossaryForm : Form
    {
        private readonly TextBox _path = new TextBox();
        private readonly Label _status = new Label();

        public GlossaryForm()
        {
            Text = "Supervertaler terms";
            Icon = IconLoader.AppIcon;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(620, 250);

            // Straight to the file format: that is what someone opening this
            // dialog needs, and getting the tabs wrong is the usual first mistake.
            HelpLinks.Attach(this, HelpLinks.GlossaryFormat);

            Controls.Add(new Label
            {
                Text = "Glossary file", Left = 14, Top = 20, Width = 110
            });

            _path.Left = 130; _path.Top = 17; _path.Width = 380;
            _path.Text = SharedSettings.GlossaryPath;
            _path.TextChanged += (s, e) => UpdateStatus();
            Controls.Add(_path);

            var browse = new Button { Text = "Browse…", Left = 518, Top = 15, Width = 85, Height = 25 };
            browse.Click += OnBrowse;
            Controls.Add(browse);

            _status.Left = 130; _status.Top = 46; _status.Width = 473; _status.Height = 18;
            _status.ForeColor = SystemColors.GrayText;
            Controls.Add(_status);

            Controls.Add(new Label
            {
                Left = 130, Top = 74, Width = 473, Height = 110,
                ForeColor = SystemColors.GrayText,
                Text =
                    "Tab-separated, one term per line:" + Environment.NewLine + Environment.NewLine +
                    "    source term\ttarget term" + Environment.NewLine +
                    "    source term\tbad target\tforbidden" + Environment.NewLine + Environment.NewLine +
                    "Blank lines and lines starting with # are ignored. Terms appear in memoQ's " +
                    "Translation results, and are also sent to the AI as required or forbidden " +
                    "terminology. The file is re-read whenever you change it."
            });

            var ok = new Button
            {
                Text = "OK", DialogResult = DialogResult.OK,
                Left = ClientSize.Width - 190, Top = ClientSize.Height - 40, Width = 85, Height = 27
            };
            ok.Click += (s, e) => SharedSettings.GlossaryPath = _path.Text.Trim();

            var cancel = new Button
            {
                Text = "Cancel", DialogResult = DialogResult.Cancel,
                Left = ClientSize.Width - 98, Top = ClientSize.Height - 40, Width = 85, Height = 27
            };

            Controls.Add(ok);
            Controls.Add(cancel);
            AcceptButton = ok;
            CancelButton = cancel;

            UpdateStatus();
        }

        private void OnBrowse(object sender, EventArgs e)
        {
            using (var dialog = new OpenFileDialog
            {
                Title = "Select a glossary file",
                Filter = "Glossary files (*.txt;*.tsv;*.tab)|*.txt;*.tsv;*.tab|All files (*.*)|*.*",
                CheckFileExists = true
            })
            {
                if (!string.IsNullOrWhiteSpace(_path.Text))
                {
                    try { dialog.InitialDirectory = Path.GetDirectoryName(_path.Text); } catch { }
                }

                if (dialog.ShowDialog(this) == DialogResult.OK) _path.Text = dialog.FileName;
            }
        }

        /// <summary>
        /// Loads the file and reports the count. A glossary that silently matches
        /// nothing is the likeliest failure here — usually spaces instead of tabs —
        /// so say what was actually parsed rather than merely whether the file exists.
        /// </summary>
        private void UpdateStatus()
        {
            var path = _path.Text.Trim();

            if (string.IsNullOrEmpty(path))
            {
                _status.ForeColor = SystemColors.GrayText;
                _status.Text = "No glossary set. Terminology is not sent to the AI.";
                return;
            }

            if (!File.Exists(path))
            {
                _status.ForeColor = Color.Firebrick;
                _status.Text = "File not found.";
                return;
            }

            try
            {
                TermIndex.Find(path, "warm the index");
                var count = TermIndex.Count;

                if (count == 0)
                {
                    _status.ForeColor = Color.Firebrick;
                    _status.Text = "No terms parsed — check that columns are separated by TABs, not spaces.";
                }
                else
                {
                    _status.ForeColor = Color.FromArgb(0, 120, 40);
                    _status.Text = $"{count} term(s) loaded.";
                }
            }
            catch (Exception ex)
            {
                _status.ForeColor = Color.Firebrick;
                _status.Text = ex.Message;
            }
        }
    }
}
