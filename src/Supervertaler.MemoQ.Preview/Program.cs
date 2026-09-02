using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using MemoQ.PreviewInterfaces;
using MemoQ.PreviewInterfaces.Entities;
using MemoQ.PreviewInterfaces.Interfaces;
using Newtonsoft.Json;

namespace Supervertaler.MemoQ.Preview
{
    /// <summary>
    /// Spike: register Supervertaler with memoQ as a preview tool and write
    /// down everything memoQ sends. No UI beyond a tray icon to quit.
    ///
    /// The point is to learn, from a real session, what a "preview part" is
    /// (a segment? a paragraph?), what the IDs look like, what content
    /// complexity levels deliver, and what arrives on a cursor move. Once that
    /// is known the real tool forwards these to the plugin's bridge instead of
    /// a log file. Every request is serialised whole, so nothing is lost to a
    /// field I forgot to print.
    /// </summary>
    internal static class Program
    {
        // Fixed for life: memoQ keys its "allow this tool" decision on it.
        internal static readonly Guid ToolId = new Guid("7a3c5a52-3f0e-4b7b-9d2a-5e6f2c1a9b41");

        internal static readonly string LogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Supervertaler.memoQ", "preview-spike.log");

        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath));
            Log("start; args = " + string.Join(" ", args));

            var callback = new LoggingCallback();
            PreviewServiceProxy proxy = null;

            using (var icon = new NotifyIcon
            {
                Icon = System.Drawing.SystemIcons.Application,
                Visible = true,
                Text = "Supervertaler preview spike"
            })
            {
                var menu = new ContextMenuStrip();
                menu.Items.Add("Request content update (all known parts)", null, (s, e) => TryRequestContent(proxy, callback));
                menu.Items.Add("Open log", null, (s, e) => System.Diagnostics.Process.Start("notepad.exe", LogPath));
                menu.Items.Add("Quit", null, (s, e) => Application.Exit());
                icon.ContextMenuStrip = menu;

                // Connect on a worker so a slow memoQ never freezes the tray.
                var worker = new Thread(() =>
                {
                    try
                    {
                        proxy = new PreviewServiceProxy(callback, "MQ_PREVIEW_PIPE", CommunicationProtocols.NamedPipe);

                        var reg = proxy.Register(new RegistrationRequest(
                            ToolId,
                            "Supervertaler",
                            "Supervertaler for memoQ — live document view for the MCP bridge (spike)",
                            "\"" + Application.ExecutablePath + "\"",
                            ".*",
                            false,
                            ContentComplexityLevel.PlainWithInterpretedFormatting,
                            new[] { PropertyNames.WordCount, PropertyNames.CharCount }));
                        Log("Register -> " + JsonConvert.SerializeObject(reg));

                        // Learned from the first run: accepting the registration
                        // dialog in memoQ already establishes the connection, and
                        // a Connect() afterwards throws PreviewToolAlreadyConnected.
                        // Connect() is for a tool that is already registered and
                        // starts up later (memoQ's own auto-start path).
                        if (!(reg?.RequestAccepted ?? false))
                        {
                            var con = proxy.Connect(ToolId);
                            Log("Connect -> " + JsonConvert.SerializeObject(con));
                        }

                        // Ask for every part id memoQ knows, then for all their
                        // content — the whole document with target text, in one
                        // pull, which is what the bridge will do on startup.
                        var ids = proxy.RequestPreviewPartIdUpdate();
                        Log("RequestPreviewPartIdUpdate -> " + JsonConvert.SerializeObject(ids));
                        Thread.Sleep(1500);
                        TryRequestContent(proxy, callback);
                    }
                    catch (Exception ex)
                    {
                        Log("FAILED: " + ex);
                    }
                }) { IsBackground = true, Name = "preview-connect" };
                worker.Start();

                Application.Run();

                try { proxy?.Disconnect(); proxy?.Dispose(); } catch (Exception ex) { Log("disconnect: " + ex.Message); }
                Log("exit");
            }
        }

        private static void TryRequestContent(PreviewServiceProxy proxy, LoggingCallback callback)
        {
            if (proxy == null) { Log("no proxy yet"); return; }
            try
            {
                var ids = callback.KnownPartIds.ToArray();
                Log("RequestContentUpdate for " + ids.Length + " part(s)");
                var r = proxy.RequestContentUpdate(new ContentUpdateRequestFromPreviewTool(ids, new string[0]));
                Log("RequestContentUpdate -> " + JsonConvert.SerializeObject(r));
            }
            catch (Exception ex) { Log("RequestContentUpdate FAILED: " + ex); }
        }

        internal static readonly object LogLock = new object();

        internal static void Log(string line)
        {
            lock (LogLock)
            {
                File.AppendAllText(LogPath,
                    DateTime.Now.ToString("HH:mm:ss.fff") + "  " + line + Environment.NewLine,
                    new UTF8Encoding(false));
            }
        }
    }

    /// <summary>Writes every callback from memoQ to the log, whole.</summary>
    internal sealed class LoggingCallback : IPreviewToolCallback
    {
        public readonly System.Collections.Generic.HashSet<string> KnownPartIds =
            new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);

        private static readonly JsonSerializerSettings Pretty = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
            NullValueHandling = NullValueHandling.Include
        };

        public void HandleContentUpdateRequest(ContentUpdateRequestFromMQ r)
        {
            var parts = r?.PreviewParts ?? new PreviewPart[0];
            foreach (var p in parts) if (p?.PreviewPartId != null) KnownPartIds.Add(p.PreviewPartId);
            Program.Log("CONTENT UPDATE: " + parts.Length + " part(s)\n" + JsonConvert.SerializeObject(r, Pretty));
        }

        public void HandleChangeHighlightRequest(ChangeHighlightRequestFromMQ r)
        {
            Program.Log("HIGHLIGHT CHANGE\n" + JsonConvert.SerializeObject(r, Pretty));
        }

        public void HandlePreviewPartIdUpdateRequest(PreviewPartIdUpdateRequestFromMQ r)
        {
            var ids = r?.PreviewPartIds ?? new string[0];
            foreach (var id in ids) KnownPartIds.Add(id);
            Program.Log("PART ID UPDATE: " + ids.Length + " id(s)\n" + JsonConvert.SerializeObject(r, Pretty));
        }

        public void HandleDisconnect()
        {
            Program.Log("DISCONNECT from memoQ");
        }
    }
}
