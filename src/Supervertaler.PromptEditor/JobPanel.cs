using System;
using System.Drawing;
using System.Windows.Forms;

namespace Supervertaler.PromptEditor
{
    /// <summary>
    /// What memoQ will use for the next segment: the project, the model, the
    /// prompt, the glossary and the memory bank.
    ///
    /// <para>It sits above the prompt library rather than across the top of the
    /// window, and the pairing is the point: the tree is what you could choose,
    /// this is what is chosen. The column is narrow, which is a feature - it
    /// forces one row per thing with its own label, where a single line across the
    /// window put four long names end to end and greyed out the only words that
    /// said which was which.</para>
    ///
    /// <para>The model is here too. Nothing on the main window used to say which
    /// model would run, so the only way to answer "am I on Fable or Opus?" was to
    /// read the log after the fact.</para>
    ///
    /// <para>Each row is a <see cref="Field"/> carrying the same three properties a
    /// toolbar item did - Text, ForeColor, ToolTipText - so the code that decides
    /// what to say did not have to change when the layout did.</para>
    /// </summary>
    internal sealed class JobPanel : Panel
    {
        private readonly TableLayoutPanel _rows = new TableLayoutPanel();
        private readonly ToolTip _tips = new ToolTip { AutoPopDelay = 30000, InitialDelay = 400 };

        public JobPanel(Action chooseModel, Action choosePrompt, Action chooseGlossary, Action chooseBank, Action syncProject = null)
        {
            if (chooseModel == null) throw new ArgumentNullException(nameof(chooseModel));
            if (choosePrompt == null) throw new ArgumentNullException(nameof(choosePrompt));
            if (chooseGlossary == null) throw new ArgumentNullException(nameof(chooseGlossary));
            if (chooseBank == null) throw new ArgumentNullException(nameof(chooseBank));

            Dock = DockStyle.Top;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(8, 6, 8, 8);

            _rows.AutoSize = true;
            _rows.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            _rows.Dock = DockStyle.Top;
            _rows.ColumnCount = 3;
            _rows.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            _rows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            _rows.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            _rows.Margin = new Padding(0);

            Model = AddRow("Model", chooseModel);
            Prompt = AddRow("Prompt", choosePrompt);
            Glossary = AddRow("Glossary", chooseGlossary);
            Bank = AddRow("Bank", chooseBank);

            // One line, like every row below it, and shortened the same way when
            // it has to be. Wrapping was the first attempt and it cost an evening:
            // an AutoSize label wraps against its MaximumSize, that width has to
            // come from a panel which has not been laid out yet, and the label
            // does not re-measure when the width later changes - so the name stood
            // twelve lines tall for the whole session and pushed the library down
            // the window. A middle cut keeps the client at one end and the case
            // number at the other, and the full name is on the tooltip.
            Project = new Field(this, null, new Label
            {
                // Docked, like everything else here. An undocked child keeps its
                // default (0,0) position and floats over whatever is above it -
                // which put the project name across the panel's own heading.
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoEllipsis = false,
                Margin = new Padding(0, 2, 0, 4),
                Font = new Font(Font, FontStyle.Bold)
            }, null, isProject: true);

            // The name is the way to correct it: a click asks memoQ, through the
            // live link, which project it is showing.
            if (syncProject != null)
            {
                Project.Value.Cursor = Cursors.Hand;
                Project.Value.Click += (s, e) => syncProject();
            }

            // Top-docked children stack in reverse order of addition, so the
            // project name goes in last to end up above the rows.
            Controls.Add(_rows);
            Controls.Add(Project.Value);
        }

        public Field Project { get; }
        public Field Model { get; }
        public Field Prompt { get; }
        public Field Glossary { get; }
        public Field Bank { get; }

        /// <summary>
        /// One line of the panel, presenting the three properties the toolbar items
        /// it replaced presented, so the code that decides what to say is unchanged.
        /// Setting <see cref="Text"/> re-fits it to the column.
        /// </summary>
        internal sealed class Field
        {
            private readonly JobPanel _panel;
            private readonly bool _isProject;

            internal Field(JobPanel panel, Label caption, Label value, Label arrow, bool isProject = false)
            {
                _panel = panel;
                _isProject = isProject;
                Caption = caption;
                Value = value;
                Arrow = arrow;
            }

            internal Label Caption { get; }
            internal Label Value { get; }
            internal Label Arrow { get; }

            /// <summary>What was set, before any shortening. The tooltip and the fitter both use it.</summary>
            internal string Full { get; private set; } = "";

            public string Text
            {
                get => Full;
                set
                {
                    Full = (value ?? "").Trim();

                    if (_isProject)
                    {
                        // Everything below is shortened against this, so it has to
                        // be recorded before anything is re-fitted.
                        _panel._projectName = Full;
                        _panel.Refit();
                    }
                    else
                    {
                        _panel.Fit(this);
                    }
                }
            }

            public Color ForeColor
            {
                get => Value.ForeColor;
                set => Value.ForeColor = value;
            }

            public string ToolTipText
            {
                set
                {
                    // The full name always reaches the tooltip, whether or not the
                    // caller supplied one: shortening must never hide the answer.
                    var text = (value ?? "").Trim();
                    var full = _isProject || string.Equals(Value.Text, Full, StringComparison.Ordinal)
                        ? text
                        : (Full + (text.Length == 0 ? "" : "\r\n\r\n" + text));

                    _panel._tips.SetToolTip(Value, full);
                    if (Caption != null) _panel._tips.SetToolTip(Caption, full);
                }
            }
        }

        private string _projectName = "";

        private Field AddRow(string caption, Action onClick)
        {
            var captionLabel = new Label
            {
                Text = caption,
                AutoSize = true,
                Margin = new Padding(0, 3, 8, 3)
            };

            var valueLabel = new Label
            {
                AutoSize = false,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Cursor = Cursors.Hand,
                Margin = new Padding(0, 3, 4, 3)
            };

            var arrow = new Label
            {
                Text = "˅",
                AutoSize = true,
                ForeColor = SystemColors.GrayText,
                Cursor = Cursors.Hand,
                Margin = new Padding(0, 3, 0, 3)
            };

            valueLabel.Click += (s, e) => onClick();
            arrow.Click += (s, e) => onClick();
            captionLabel.Click += (s, e) => onClick();
            captionLabel.Cursor = Cursors.Hand;

            var line = _rows.RowCount++;
            _rows.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _rows.Controls.Add(captionLabel, 0, line);
            _rows.Controls.Add(valueLabel, 1, line);
            _rows.Controls.Add(arrow, 2, line);

            return new Field(this, captionLabel, valueLabel, arrow);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            Refit();
        }

        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            if (Project != null) Project.Value.Font = new Font(Font, FontStyle.Bold);
            Refit();
        }

        private void Refit()
        {
            if (Project == null) return;

            // The project spans the whole panel; the rows below it lose the label
            // and arrow columns. Both are measured rather than assumed, except
            // before the first layout, when there is nothing to measure.
            Fit(Project, Math.Max(60, ClientSize.Width - Padding.Horizontal), against: "");

            Fit(Model);
            Fit(Prompt);
            Fit(Glossary);
            Fit(Bank);
        }

        private void Fit(Field field)
        {
            if (field?.Value == null) return;

            var width = field.Value.ClientSize.Width;
            if (width <= 0) width = Math.Max(40, ClientSize.Width - Padding.Horizontal - 96);

            Fit(field, width, _projectName);
        }

        private void Fit(Field field, int width, string against)
        {
            if (field?.Value == null) return;

            using (var g = CreateGraphics())
            {
                var font = field.Value.Font;
                Func<string, int> measure = s => TextRenderer.MeasureText(
                    g, s, font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding).Width;

                field.Value.Text = JobLabel.Fit(field.Full, against, width, measure);
            }
        }
    }
}
