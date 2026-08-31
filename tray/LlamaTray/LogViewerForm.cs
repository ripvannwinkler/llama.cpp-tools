using System.Text;
using System.Windows.Forms;

namespace LlamaTray;

internal sealed class LogViewerForm : Form
{
    private const int TailBytes = 512 * 1024;

    private readonly string _logFile;
    private readonly RichTextBox _logText;
    private readonly System.Windows.Forms.Timer _refreshTimer;
    private long _lastLength = -1;
    private DateTime _lastWriteTimeUtc;

    public LogViewerForm(string logFile)
    {
        _logFile = logFile;
        Text = $"LlamaTray Log — {Path.GetFileName(logFile)}";
        Icon = IconFactory.GetBaseIcon();
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1000, 650);
        MinimumSize = new Size(500, 250);
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        MinimizeBox = true;

        _logText = new RichTextBox
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Black,
            ForeColor = Color.LimeGreen,
            Font = new Font("Consolas", 10F),
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            DetectUrls = false,
            Multiline = true,
            ScrollBars = RichTextBoxScrollBars.Both,
            WordWrap = false,
            HideSelection = false,
        };
        Controls.Add(_logText);

        _refreshTimer = new System.Windows.Forms.Timer { Interval = 500 };
        _refreshTimer.Tick += (_, _) => RefreshLog();
        _refreshTimer.Start();
        RefreshLog();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _refreshTimer.Stop();
        _refreshTimer.Dispose();
        base.OnFormClosed(e);
    }

    private void RefreshLog()
    {
        try
        {
            if (!File.Exists(_logFile))
            {
                SetText("(waiting for log file...)");
                _lastLength = -1;
                return;
            }

            var info = new FileInfo(_logFile);
            if (info.Length == _lastLength && info.LastWriteTimeUtc == _lastWriteTimeUtc)
                return;

            using var stream = new FileStream(
                _logFile,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 64 * 1024,
                options: FileOptions.SequentialScan);
            var start = Math.Max(0, stream.Length - TailBytes);
            stream.Seek(start, SeekOrigin.Begin);

            var length = checked((int)(stream.Length - start));
            var bytes = new byte[length];
            stream.ReadExactly(bytes);
            var text = Encoding.UTF8.GetString(bytes);
            if (start > 0)
            {
                var firstNewline = text.IndexOf('\n');
                text = firstNewline >= 0 ? text[(firstNewline + 1)..] : string.Empty;
            }

            SetText(string.IsNullOrEmpty(text) ? "(log is empty)" : text);
            _lastLength = info.Length;
            _lastWriteTimeUtc = info.LastWriteTimeUtc;
        }
        catch (IOException)
        {
            // The server may be rotating or replacing the file; retry on the next tick.
        }
        catch (UnauthorizedAccessException)
        {
            SetText($"Unable to read log file:\r\n{_logFile}");
        }
    }

    private bool IsScrolledToBottom()
    {
        if (_logText.TextLength == 0) return true;

        var lastCharPosition = _logText.GetPositionFromCharIndex(_logText.TextLength - 1);
        return lastCharPosition.Y + _logText.Font.Height <= _logText.ClientSize.Height;
    }

    private void SetText(string text)
    {
        if (_logText.Text == text)
            return;

        var followTail = IsScrolledToBottom();
        var anchor = followTail
            ? 0
            : _logText.GetCharIndexFromPosition(new Point(0, 0));

        _logText.Text = text;
        if (followTail)
        {
            _logText.SelectionStart = _logText.TextLength;
            _logText.ScrollToCaret();
        }
        else
        {
            _logText.SelectionStart = Math.Min(anchor, _logText.TextLength);
            _logText.ScrollToCaret();
        }
    }
}
