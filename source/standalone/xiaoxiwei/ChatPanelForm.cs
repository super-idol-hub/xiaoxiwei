using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading;
using System.Windows.Forms;

namespace XiaoXiWei.Standalone
{
    internal sealed class ChatPanelForm : Form
    {
        private const int PanelWidth = 410;
        private const int PanelHeight = 535;
        private const int MaxHistoryMessages = 12;

        private readonly ChatApiClient _client;
        private readonly List<ChatConversationMessage> _history;
        private readonly RichTextBox _transcript;
        private readonly TextBox _input;
        private readonly Label _status;
        private readonly Button _sendButton;
        private readonly Button _transferButton;
        private readonly Button _closeButton;
        private readonly System.Windows.Forms.Timer _pollTimer;
        private Rectangle _petBounds;
        private bool _requestInFlight;
        private bool _humanMode;
        private string _sessionId;
        private bool _tailOnRight;

        public ChatPanelForm()
        {
            _client = new ChatApiClient();
            _history = new List<ChatConversationMessage>();

            Text = "和小曦薇聊天";
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            ClientSize = new Size(PanelWidth, PanelHeight);
            BackColor = Color.FromArgb(6, 25, 45);
            ForeColor = Color.FromArgb(220, 247, 255);
            Font = new Font(
                "Microsoft YaHei UI",
                9.0f,
                FontStyle.Regular,
                GraphicsUnit.Point);
            DoubleBuffered = true;
            KeyPreview = true;

            Label eyebrow = new Label();
            eyebrow.AutoSize = true;
            eyebrow.Location = new Point(25, 20);
            eyebrow.ForeColor = Color.FromArgb(81, 207, 255);
            eyebrow.Font = new Font(
                "Microsoft YaHei UI",
                8.0f,
                FontStyle.Bold,
                GraphicsUnit.Point);
            eyebrow.Text = "XIAOXIWEI · AI CHAT";
            Controls.Add(eyebrow);

            Label title = new Label();
            title.AutoSize = true;
            title.Location = new Point(23, 42);
            title.ForeColor = Color.White;
            title.Font = new Font(
                "Microsoft YaHei UI",
                16.0f,
                FontStyle.Bold,
                GraphicsUnit.Point);
            title.Text = "和小曦薇聊聊天";
            Controls.Add(title);

            _status = new Label();
            _status.AutoSize = false;
            _status.Location = new Point(25, 76);
            _status.Size = new Size(330, 24);
            _status.ForeColor = Color.FromArgb(128, 220, 244);
            _status.Text = _client.IsConfigured
                ? "AI 在线 · 回复由通义千问生成"
                : "聊天服务尚未配置";
            Controls.Add(_status);

            _closeButton = CreateButton(
                "×",
                new Rectangle(360, 18, 30, 30),
                Color.FromArgb(19, 63, 91),
                Color.White);
            _closeButton.Font = new Font(
                "Microsoft YaHei UI",
                14.0f,
                FontStyle.Regular,
                GraphicsUnit.Point);
            _closeButton.Click += delegate { Hide(); };
            Controls.Add(_closeButton);

            _transcript = new RichTextBox();
            _transcript.Location = new Point(24, 105);
            _transcript.Size = new Size(362, 285);
            _transcript.ReadOnly = true;
            _transcript.BorderStyle = BorderStyle.None;
            _transcript.BackColor = Color.FromArgb(8, 36, 59);
            _transcript.ForeColor = Color.FromArgb(219, 242, 248);
            _transcript.Font = new Font(
                "Microsoft YaHei UI",
                9.5f,
                FontStyle.Regular,
                GraphicsUnit.Point);
            _transcript.DetectUrls = false;
            _transcript.ScrollBars = RichTextBoxScrollBars.Vertical;
            Controls.Add(_transcript);

            _input = new TextBox();
            _input.Location = new Point(24, 405);
            _input.Size = new Size(274, 52);
            _input.Multiline = true;
            _input.MaxLength = 500;
            _input.BorderStyle = BorderStyle.FixedSingle;
            _input.BackColor = Color.FromArgb(7, 29, 49);
            _input.ForeColor = Color.White;
            _input.Font = new Font(
                "Microsoft YaHei UI",
                9.5f,
                FontStyle.Regular,
                GraphicsUnit.Point);
            _input.KeyDown += OnInputKeyDown;
            Controls.Add(_input);

            _sendButton = CreateButton(
                "发送",
                new Rectangle(307, 405, 79, 52),
                Color.FromArgb(0, 137, 203),
                Color.White);
            _sendButton.Click += delegate { SendCurrentMessage(); };
            Controls.Add(_sendButton);

            _transferButton = CreateButton(
                "转人工",
                new Rectangle(24, 472, 104, 38),
                Color.FromArgb(17, 62, 91),
                Color.FromArgb(162, 227, 246));
            _transferButton.Click += delegate
            {
                if (_humanMode)
                {
                    CloseHumanConversation();
                }
                else
                {
                    TransferToHuman();
                }
            };
            Controls.Add(_transferButton);

            Label privacy = new Label();
            privacy.AutoSize = false;
            privacy.Location = new Point(140, 477);
            privacy.Size = new Size(246, 33);
            privacy.TextAlign = ContentAlignment.MiddleRight;
            privacy.ForeColor = Color.FromArgb(99, 158, 180);
            privacy.Font = new Font(
                "Microsoft YaHei UI",
                7.5f,
                FontStyle.Regular,
                GraphicsUnit.Point);
            privacy.Text = "普通聊天不会进入人工控制台";
            Controls.Add(privacy);

            _pollTimer = new System.Windows.Forms.Timer();
            _pollTimer.Interval = 3200;
            _pollTimer.Tick += delegate
            {
                if (_humanMode && !_requestInFlight)
                {
                    PollHumanMessages();
                }
            };

            AppendSystem(
                _client.IsConfigured
                    ? "你好呀，我是 AI 小曦薇。想聊什么都可以告诉我。"
                    : "聊天服务还没有准备好，请稍后再试。");
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            UpdatePanelRegion();
            _input.Focus();
            if (!_humanMode && !_requestInFlight && _client.IsConfigured)
            {
                PollHumanMessages();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

            Rectangle border = new Rectangle(1, 1, Width - 3, Height - 3);
            using (LinearGradientBrush background = new LinearGradientBrush(
                border,
                Color.FromArgb(9, 39, 65),
                Color.FromArgb(3, 18, 34),
                LinearGradientMode.Vertical))
            {
                e.Graphics.FillPath(background, RoundedPath(border, 18));
            }
            using (Pen glow = new Pen(Color.FromArgb(130, 42, 190, 255), 1.5f))
            {
                e.Graphics.DrawPath(glow, RoundedPath(border, 18));
            }
            using (Pen scan = new Pen(Color.FromArgb(15, 83, 197, 232), 1.0f))
            {
                for (int y = 102; y < Height - 20; y += 8)
                {
                    e.Graphics.DrawLine(scan, 18, y, Width - 18, y);
                }
            }

            Point[] tail = _tailOnRight
                ? new Point[]
                {
                    new Point(Width - 2, 72),
                    new Point(Width + 18, 84),
                    new Point(Width - 2, 96)
                }
                : new Point[]
                {
                    new Point(2, 72),
                    new Point(-18, 84),
                    new Point(2, 96)
                };
            using (SolidBrush tailBrush = new SolidBrush(Color.FromArgb(8, 35, 59)))
            {
                e.Graphics.FillPolygon(tailBrush, tail);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_pollTimer != null)
                {
                    _pollTimer.Stop();
                    _pollTimer.Dispose();
                }
            }
            base.Dispose(disposing);
        }

        public void ShowPanel(Rectangle petBounds, bool topMost)
        {
            UpdateAnchor(petBounds, topMost);
            if (!Visible)
            {
                Show();
            }
            else
            {
                BringToFront();
                Activate();
            }
            _input.Focus();
        }

        public void UpdateAnchor(Rectangle petBounds, bool topMost)
        {
            _petBounds = petBounds;
            TopMost = topMost;
            Rectangle workArea = Screen.FromRectangle(petBounds).WorkingArea;
            int gap = 18;
            int x;
            if (petBounds.Left - PanelWidth - gap >= workArea.Left)
            {
                x = petBounds.Left - PanelWidth - gap;
                _tailOnRight = true;
            }
            else
            {
                x = petBounds.Right + gap;
                _tailOnRight = false;
            }
            int y = petBounds.Top + (petBounds.Height - PanelHeight) / 2;
            y = Math.Max(workArea.Top + 8, Math.Min(y, workArea.Bottom - PanelHeight - 8));
            Location = new Point(x, y);
            Invalidate();
        }

        private static Button CreateButton(
            string text,
            Rectangle bounds,
            Color background,
            Color foreground)
        {
            Button button = new Button();
            button.Text = text;
            button.Bounds = bounds;
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 1;
            button.FlatAppearance.BorderColor = Color.FromArgb(62, 184, 230);
            button.BackColor = background;
            button.ForeColor = foreground;
            button.Cursor = Cursors.Hand;
            button.Font = new Font(
                "Microsoft YaHei UI",
                9.0f,
                FontStyle.Bold,
                GraphicsUnit.Point);
            return button;
        }

        private void OnInputKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter && !e.Shift)
            {
                e.SuppressKeyPress = true;
                SendCurrentMessage();
            }
            else if (e.KeyCode == Keys.Escape)
            {
                e.SuppressKeyPress = true;
                Hide();
            }
        }

        private void SendCurrentMessage()
        {
            if (_requestInFlight || !_client.IsConfigured)
            {
                return;
            }
            string message = (_input.Text ?? string.Empty).Trim();
            if (message.Length == 0)
            {
                return;
            }
            _input.Clear();
            AppendUser(message);

            if (_humanMode)
            {
                BeginRequest(
                    "正在发送给人工…",
                    delegate
                    {
                        return _client.SendHumanMessage(message, _sessionId);
                    },
                    delegate(ChatServiceResponse response)
                    {
                        if (!string.IsNullOrWhiteSpace(response.sessionId))
                        {
                            _sessionId = response.sessionId;
                        }
                        _status.Text = "人工会话中 · 等待对方回复";
                    });
                return;
            }

            List<ChatConversationMessage> history = CopyHistoryBeforeLatestUser();
            BeginRequest(
                "小曦薇正在想…",
                delegate { return _client.SendAi(message, history); },
                delegate(ChatServiceResponse response)
                {
                    AppendAssistant(response.reply);
                    _status.Text = "AI 在线 · 回复由通义千问生成";
                });
        }

        private void TransferToHuman()
        {
            if (_requestInFlight || !_client.IsConfigured)
            {
                return;
            }

            string pendingMessage = (_input.Text ?? string.Empty).Trim();
            if (pendingMessage.Length > 0)
            {
                _input.Clear();
                AppendUser(pendingMessage);
            }
            List<ChatConversationMessage> history = CopyHistory();

            BeginRequest(
                "正在请求人工接入…",
                delegate
                {
                    return _client.TransferToHuman(
                        pendingMessage,
                        history,
                        _sessionId);
                },
                delegate(ChatServiceResponse response)
                {
                    _humanMode = true;
                    _sessionId = response.sessionId;
                    _transferButton.Text = "结束人工";
                    _status.Text = "已转人工 · 等待控制台回复";
                    AppendSystem("已把本次对话转给人工，请在这里继续留言。");
                    _pollTimer.Start();
                });
        }

        private void CloseHumanConversation()
        {
            if (_requestInFlight)
            {
                return;
            }
            BeginRequest(
                "正在结束人工会话…",
                delegate { return _client.CloseHuman(_sessionId); },
                delegate(ChatServiceResponse response)
                {
                    _humanMode = false;
                    _sessionId = string.Empty;
                    _transferButton.Text = "转人工";
                    _status.Text = "AI 在线 · 回复由通义千问生成";
                    _pollTimer.Stop();
                    AppendSystem("人工会话已结束，接下来由 AI 小曦薇陪你聊天。");
                });
        }

        private void PollHumanMessages()
        {
            if (_requestInFlight || !_client.IsConfigured)
            {
                return;
            }

            _requestInFlight = true;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    ChatServiceResponse response = _client.PollHuman(_sessionId);
                    SafeBeginInvoke(delegate
                    {
                        _requestInFlight = false;
                        if (response.mode == "human"
                            && !string.IsNullOrWhiteSpace(response.sessionId))
                        {
                            bool wasHuman = _humanMode;
                            _humanMode = true;
                            _sessionId = response.sessionId;
                            _transferButton.Text = "结束人工";
                            _status.Text = "人工会话中 · 控制台已连接";
                            _pollTimer.Start();
                            if (!wasHuman)
                            {
                                AppendSystem("已恢复上次尚未结束的人工会话。");
                            }
                            foreach (HumanChatMessage item in response.messages)
                            {
                                AppendOperator(item.content);
                            }
                        }
                        else if (_humanMode)
                        {
                            _humanMode = false;
                            _sessionId = string.Empty;
                            _transferButton.Text = "转人工";
                            _status.Text = "AI 在线 · 回复由通义千问生成";
                            _pollTimer.Stop();
                            AppendSystem("人工会话已经结束。");
                        }
                    });
                }
                catch
                {
                    SafeBeginInvoke(delegate { _requestInFlight = false; });
                }
            });
        }

        private void BeginRequest(
            string busyText,
            Func<ChatServiceResponse> request,
            Action<ChatServiceResponse> onSuccess)
        {
            _requestInFlight = true;
            SetComposerEnabled(false);
            _status.Text = busyText;

            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    ChatServiceResponse response = request();
                    SafeBeginInvoke(delegate
                    {
                        _requestInFlight = false;
                        SetComposerEnabled(true);
                        onSuccess(response);
                        _input.Focus();
                    });
                }
                catch (Exception exception)
                {
                    SafeBeginInvoke(delegate
                    {
                        _requestInFlight = false;
                        SetComposerEnabled(true);
                        _status.Text = _humanMode
                            ? "人工通道暂时连接不上"
                            : "AI 暂时离线";
                        AppendSystem(
                            string.IsNullOrWhiteSpace(exception.Message)
                                ? "聊天服务暂时不可用，请稍后再试。"
                                : exception.Message);
                        _input.Focus();
                    });
                }
            });
        }

        private void SafeBeginInvoke(MethodInvoker action)
        {
            if (IsDisposed || Disposing || !IsHandleCreated)
            {
                return;
            }
            try
            {
                BeginInvoke(action);
            }
            catch (InvalidOperationException)
            {
                // The pet can close while a request is returning.
            }
        }

        private void SetComposerEnabled(bool enabled)
        {
            _input.Enabled = enabled;
            _sendButton.Enabled = enabled;
            _transferButton.Enabled = enabled;
        }

        private void AppendUser(string text)
        {
            AddHistory("user", text);
            AppendTranscript("你", text, Color.FromArgb(113, 211, 255));
        }

        private void AppendAssistant(string text)
        {
            string value = string.IsNullOrWhiteSpace(text)
                ? "我刚刚走神啦，可以再说一次吗？"
                : text.Trim();
            AddHistory("assistant", value);
            AppendTranscript("AI 小曦薇", value, Color.FromArgb(100, 238, 213));
        }

        private void AppendOperator(string text)
        {
            AppendTranscript(
                "人工回复",
                text,
                Color.FromArgb(255, 205, 112));
        }

        private void AppendSystem(string text)
        {
            AppendTranscript(
                "系统",
                text,
                Color.FromArgb(116, 163, 184));
        }

        private void AppendTranscript(string speaker, string text, Color color)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }
            _transcript.SelectionStart = _transcript.TextLength;
            _transcript.SelectionLength = 0;
            _transcript.SelectionColor = color;
            _transcript.SelectionFont = new Font(
                _transcript.Font,
                FontStyle.Bold);
            _transcript.AppendText(speaker + "\r\n");
            _transcript.SelectionColor = Color.FromArgb(219, 242, 248);
            _transcript.SelectionFont = new Font(
                _transcript.Font,
                FontStyle.Regular);
            _transcript.AppendText(text.Trim() + "\r\n\r\n");
            _transcript.SelectionStart = _transcript.TextLength;
            _transcript.ScrollToCaret();
        }

        private void AddHistory(string role, string content)
        {
            _history.Add(new ChatConversationMessage
            {
                role = role,
                content = content
            });
            while (_history.Count > MaxHistoryMessages)
            {
                _history.RemoveAt(0);
            }
        }

        private List<ChatConversationMessage> CopyHistory()
        {
            List<ChatConversationMessage> copy =
                new List<ChatConversationMessage>();
            foreach (ChatConversationMessage item in _history)
            {
                copy.Add(new ChatConversationMessage
                {
                    role = item.role,
                    content = item.content
                });
            }
            return copy;
        }

        private List<ChatConversationMessage> CopyHistoryBeforeLatestUser()
        {
            List<ChatConversationMessage> copy = CopyHistory();
            if (copy.Count > 0
                && string.Equals(
                    copy[copy.Count - 1].role,
                    "user",
                    StringComparison.Ordinal))
            {
                copy.RemoveAt(copy.Count - 1);
            }
            return copy;
        }

        private void UpdatePanelRegion()
        {
            using (GraphicsPath path = RoundedPath(
                new Rectangle(0, 0, Width, Height),
                18))
            {
                Region = new Region(path);
            }
        }

        private static GraphicsPath RoundedPath(Rectangle rectangle, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            int diameter = radius * 2;
            path.AddArc(
                rectangle.Left,
                rectangle.Top,
                diameter,
                diameter,
                180,
                90);
            path.AddArc(
                rectangle.Right - diameter,
                rectangle.Top,
                diameter,
                diameter,
                270,
                90);
            path.AddArc(
                rectangle.Right - diameter,
                rectangle.Bottom - diameter,
                diameter,
                diameter,
                0,
                90);
            path.AddArc(
                rectangle.Left,
                rectangle.Bottom - diameter,
                diameter,
                diameter,
                90,
                90);
            path.CloseFigure();
            return path;
        }
    }
}
