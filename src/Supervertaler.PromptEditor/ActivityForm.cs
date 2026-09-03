using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace Supervertaler.PromptEditor
{
    /// <summary>
    /// What the plugin is doing, live.
    ///
    /// memoQ owns the Pre-translate progress dialog and gives an add-in no way to
    /// write into it, so a run shows a bar and the word "Processing" and nothing
    /// else — no batch count, no glossary hits, no errors until the job ends. The
    /// Trados plugin has its own dockable panel to say all this in; memoQ has
    /// none. This window is the substitute, and it deliberately lives in the
    /// editor rather than in the plugin: memoQ's dialog is modal, so during a run
    /// the editor is the only Supervertaler surface you can actually look at.
    ///
    /// It reads the plugin's log file. That is the whole design: the log already
    /// exists, is already written one line per meaningful event, and already has
    /// timestamps — so there is no event buffer in the plugin to fill, drain, cap
    /// or leak, and no endpoint to keep in step. The cost is that this parses text
    /// the plugin was not writing for it, which is why an unrecognised line is
    /// shown verbatim rather than dropped: a reworded log message degrades to
    /// looking raw, never to vanishing.
    /// </summary>
    internal sealed class ActivityForm : Form
    {
        private readonly ListBox _list = new ListBox
        {
            Dock = DockStyle.Fill,
            IntegralHeight = false,
            HorizontalScrollbar = true,
            Font = new Font("Consolas", 9F)
        };

        private readonly Label _totals = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 34,
            Padding = new Padding(10, 8, 10, 0),
            ForeColor = SystemColors.GrayText
        };

        private readonly CheckBox _everything = new CheckBox { Text = "Show everything", AutoSize = true };
        private readonly CheckBox _onTop = new CheckBox { Text = "Keep on top", AutoSize = true };
        private readonly Timer _poll = new Timer { Interval = 1000 };

        private long _offset;
        private int _batches;
        private int _segments;
        private int _problems;

        /// <summary>
        /// How much of the log to show when the window opens.
        ///
        /// Enough to cover a run already in progress — which is the normal case,
        /// since you open this because something is happening — without pausing to
        /// read a log that has been accumulating for weeks.
        /// </summary>
        private const int OpeningTailBytes = 128 * 1024;

        /// <summary>
        /// The most lines to keep on screen. A ListBox holding an unbounded log
        /// is a slow leak that only shows up on the longest jobs, which are
        /// exactly the ones worth watching.
        /// </summary>
        private const int MaxLines = 2000;

        public ActivityForm()
        {
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Text = "Supervertaler activity";
            AppIcon.Apply(this);
            StartPosition = FormStartPosition.Manual;
            ClientSize = new Size(620, 420);
            MinimumSize = new Size(360, 220);
            ShowInTaskbar = true;

            var bar = new Panel { Dock = DockStyle.Top, Height = 32, Padding = new Padding(8, 6, 8, 0) };
            _everything.Left = 8; _everything.Top = 7;
            _onTop.Left = 130; _onTop.Top = 7;
            bar.Controls.Add(_everything);
            bar.Controls.Add(_onTop);

            Controls.Add(_list);
            Controls.Add(_totals);
            Controls.Add(bar);

            _everything.CheckedChanged += (s, e) => Reload();
            _onTop.CheckedChanged += (s, e) => TopMost = _onTop.Checked;

            // Ctrl+C copies the selection, because the reason to look at a line is
            // usually to send it to someone.
            _list.KeyDown += (s, e) =>
            {
                if (e.Control && e.KeyCode == Keys.C && _list.SelectedItem != null)
                {
                    try { Clipboard.SetText(_list.SelectedItem.ToString()); } catch { }
                    e.Handled = true;
                }
                if (e.KeyCode == Keys.Escape) Close();
            };

            _poll.Tick += (s, e) => ReadNew();
            Shown += (s, e) => { Place(); Reload(); _poll.Start(); };
            FormClosing += (s, e) => { _poll.Stop(); Remember(); };
        }

        // ── reading ──────────────────────────────────────────────────────

        private static string LogPath
        {
            get
            {
                try
                {
                    return Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "Supervertaler.memoQ", "plugin.log");
                }
                catch (Exception) { return null; }
            }
        }

        private void Reload()
        {
            _list.BeginUpdate();
            _list.Items.Clear();
            _list.EndUpdate();

            _offset = 0;
            _batches = 0;
            _segments = 0;
            _problems = 0;

            try
            {
                var path = LogPath;
                if (path == null || !File.Exists(path))
                {
                    Say("No activity yet. This fills in while memoQ is translating.");
                    UpdateTotals();
                    return;
                }

                using (var f = Open(path))
                {
                    if (f.Length > OpeningTailBytes)
                    {
                        f.Seek(f.Length - OpeningTailBytes, SeekOrigin.Begin);
                        ReadLine(f);   // the seek lands mid-line; discard the remnant
                    }
                    Consume(f);
                }
            }
            catch (Exception ex)
            {
                Say("Could not read the activity log: " + ex.Message);
            }

            UpdateTotals();
            ScrollToEnd();
        }

        private void ReadNew()
        {
            try
            {
                var path = LogPath;
                if (path == null || !File.Exists(path)) return;

                using (var f = Open(path))
                {
                    // A shorter file than last time means it was rotated or
                    // truncated, so the offset points at nothing meaningful.
                    if (f.Length < _offset) { Reload(); return; }
                    if (f.Length == _offset) return;

                    f.Seek(_offset, SeekOrigin.Begin);
                    Consume(f);
                }

                UpdateTotals();
                ScrollToEnd();
            }
            catch (Exception)
            {
                // memoQ writing while we read, or the file briefly gone. The next
                // tick tries again; a dialog here would fire once a second.
            }
        }

        /// <summary>
        /// Shared read, and non-blocking: memoQ holds this file open for appending
        /// the whole time it runs, so anything less permissive than
        /// ReadWrite sharing fails on every tick of a live job.
        /// </summary>
        private static FileStream Open(string path)
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }

        private void Consume(FileStream f)
        {
            var added = new List<string>();

            string line;
            while ((line = ReadLine(f)) != null)
            {
                var shown = Render(line);
                if (shown != null) added.Add(shown);
            }

            _offset = f.Position;
            if (added.Count == 0) return;

            _list.BeginUpdate();
            foreach (var a in added) _list.Items.Add(a);
            while (_list.Items.Count > MaxLines) _list.Items.RemoveAt(0);
            _list.EndUpdate();
        }

        private static string ReadLine(FileStream f)
        {
            var bytes = new List<byte>(160);
            int b;
            while ((b = f.ReadByte()) >= 0)
            {
                if (b == '\n') return Encoding.UTF8.GetString(bytes.ToArray()).TrimEnd('\r');
                bytes.Add((byte)b);
            }

            // No newline yet: the plugin is mid-write. Rewind so the whole line is
            // picked up next tick rather than delivered in halves.
            if (bytes.Count > 0) f.Seek(-bytes.Count, SeekOrigin.Current);
            return null;
        }

        // ── rendering ────────────────────────────────────────────────────

        private static readonly Regex Entry =
            new Regex(@"^\[(?<time>[\d\-: .]+)\]\s+\[\d+\]\s+(?<body>.*)$", RegexOptions.Compiled);

        private static readonly Regex Batch =
            new Regex(@"^batch: (?<sent>\d+) segment\(s\) sent, (?<back>\d+) returned(?: \| terms: (?<terms>\d+))?(?: \| recall: (?<recall>\d+))?",
                      RegexOptions.Compiled);

        /// <summary>
        /// One log line as something worth reading, or null to hide it.
        ///
        /// Only lines known to be diagnostic are hidden, and only at the default
        /// level. Everything else is shown — verbatim if it is not recognised —
        /// because a line quietly dropped is worse than a line that looks raw.
        /// </summary>
        private string Render(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;

            var m = Entry.Match(raw);
            if (!m.Success) return _everything.Checked ? raw : null;

            var time = Clock(m.Groups["time"].Value);
            var body = m.Groups["body"].Value.Trim();

            if (IsProblem(body)) _problems++;

            var batch = Batch.Match(body);
            if (batch.Success)
            {
                _batches++;
                var sent = int.Parse(batch.Groups["sent"].Value, CultureInfo.InvariantCulture);
                var back = int.Parse(batch.Groups["back"].Value, CultureInfo.InvariantCulture);
                _segments += sent;

                var text = time + "  Batch " + _batches.ToString().PadLeft(3) + "   "
                         + sent + " segment" + (sent == 1 ? "" : "s");

                if (batch.Groups["terms"].Success) text += "  ·  " + batch.Groups["terms"].Value + " terms";
                if (batch.Groups["recall"].Success && batch.Groups["recall"].Value != "0")
                    text += "  ·  " + batch.Groups["recall"].Value + " recalled";

                // The count coming back short means segments went missing, which
                // shifts every translation after it. Never quiet about that.
                if (sent != back) text += "   ⚠ only " + back + " returned";

                return text;
            }

            if (!_everything.Checked && IsDiagnostic(body)) return null;

            return time + "  " + Friendly(body);
        }

        /// <summary>
        /// Lines that exist for debugging and say nothing a translator can act on.
        /// One appears per request, so at default level they would bury everything.
        /// </summary>
        private static bool IsDiagnostic(string body)
        {
            return body.StartsWith("CreateLookupSession", StringComparison.Ordinal)
                || body.StartsWith("metadata:", StringComparison.Ordinal)
                || body.StartsWith("HasCapability", StringComparison.Ordinal);
        }

        private static bool IsProblem(string body)
        {
            return body.IndexOf("failed", StringComparison.OrdinalIgnoreCase) >= 0
                || body.IndexOf("exception", StringComparison.OrdinalIgnoreCase) >= 0
                || body.IndexOf("could not", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string Friendly(string body)
        {
            if (body.StartsWith("CreateEngine: ", StringComparison.Ordinal))
                return "Engine     " + body.Substring("CreateEngine: ".Length)
                    .Replace("->", "→").Replace(", provider=", " · ").Replace(", model=", " ");

            if (body.StartsWith("TB CreateEngine: ", StringComparison.Ordinal))
                return "Terminology  " + body.Substring("TB CreateEngine: ".Length).Replace("->", "→");

            if (body.StartsWith("TermIndex: ", StringComparison.Ordinal))
                return "Glossary   " + body.Substring("TermIndex: ".Length);

            if (body.StartsWith("translate: ", StringComparison.Ordinal))
                return "Segment    " + body.Substring("translate: ".Length);

            if (body.StartsWith("AutoPrompt", StringComparison.Ordinal))
                return "AutoPrompt " + body.Substring("AutoPrompt".Length).TrimStart(':', ' ');

            if (body.StartsWith("DocumentMemory: ", StringComparison.Ordinal))
                return "Memory     " + body.Substring("DocumentMemory: ".Length);

            return body;
        }

        private static string Clock(string stamp)
        {
            // "2026-09-03 23:51:22.456" reduced to the part anyone reads.
            var space = stamp.IndexOf(' ');
            var t = space >= 0 ? stamp.Substring(space + 1) : stamp;
            var dot = t.IndexOf('.');
            return dot >= 0 ? t.Substring(0, dot) : t;
        }

        private void Say(string message)
        {
            _list.Items.Add(message);
        }

        private void UpdateTotals()
        {
            if (_batches == 0 && _segments == 0)
            {
                _totals.Text = _problems > 0 ? _problems + " problem(s) – see above" : "Waiting for memoQ.";
                return;
            }

            _totals.Text = _segments.ToString("N0") + " segment" + (_segments == 1 ? "" : "s")
                         + " in " + _batches + " batch" + (_batches == 1 ? "" : "es")
                         + (_problems > 0 ? "  ·  " + _problems + " problem(s)" : "  ·  no errors");
        }

        private void ScrollToEnd()
        {
            if (_list.Items.Count > 0) _list.TopIndex = _list.Items.Count - 1;
        }

        // ── where the window sits ────────────────────────────────────────

        private static string GeometryPath
        {
            get
            {
                try
                {
                    var dir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "Supervertaler.PromptEditor");
                    Directory.CreateDirectory(dir);
                    return Path.Combine(dir, "activity-window.txt");
                }
                catch (Exception) { return null; }
            }
        }

        /// <summary>
        /// Restores the last position, but only onto a screen that still exists -
        /// this window is meant to be parked beside memoQ, and a laptop
        /// undocked from a second monitor would otherwise reopen it off-screen.
        /// </summary>
        private void Place()
        {
            try
            {
                var path = GeometryPath;
                if (path == null || !File.Exists(path)) { CentreOnOwner(); return; }

                var parts = File.ReadAllText(path).Split(',');
                if (parts.Length < 5) { CentreOnOwner(); return; }

                var bounds = new Rectangle(
                    int.Parse(parts[0], CultureInfo.InvariantCulture),
                    int.Parse(parts[1], CultureInfo.InvariantCulture),
                    int.Parse(parts[2], CultureInfo.InvariantCulture),
                    int.Parse(parts[3], CultureInfo.InvariantCulture));

                var visible = false;
                foreach (var screen in Screen.AllScreens)
                    if (screen.WorkingArea.IntersectsWith(bounds)) visible = true;

                if (!visible) { CentreOnOwner(); return; }

                Bounds = bounds;
                _onTop.Checked = parts[4].Trim() == "1";
            }
            catch (Exception)
            {
                CentreOnOwner();
            }
        }

        private void CentreOnOwner()
        {
            var area = Screen.FromControl(Owner ?? (Control)this).WorkingArea;
            Location = new Point(area.Right - Width - 40, area.Bottom - Height - 60);
        }

        private void Remember()
        {
            try
            {
                var path = GeometryPath;
                if (path == null) return;

                var b = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
                File.WriteAllText(path, string.Join(",", new[]
                {
                    b.X.ToString(CultureInfo.InvariantCulture),
                    b.Y.ToString(CultureInfo.InvariantCulture),
                    b.Width.ToString(CultureInfo.InvariantCulture),
                    b.Height.ToString(CultureInfo.InvariantCulture),
                    _onTop.Checked ? "1" : "0"
                }));
            }
            catch (Exception)
            {
                // A window that forgets where it was is not worth an error dialog.
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _poll.Dispose();
            base.Dispose(disposing);
        }
    }
}
