using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace XiaoXiWei.Standalone
{
    internal sealed class SpeechBubbleForm : Form
    {
        private const int WsExLayered = 0x00080000;
        private const int WsExToolWindow = 0x00000080;
        private const int WsExNoActivate = 0x08000000;
        private const int UlwAlpha = 0x00000002;
        private const byte AcSrcOver = 0x00;
        private const byte AcSrcAlpha = 0x01;
        private const int MaxQueueLength = 20;

        private readonly Queue<string> _messages;
        private readonly Timer _hideTimer;
        private Rectangle _petBounds;
        private bool _tailOnRight;

        public SpeechBubbleForm()
        {
            _messages = new Queue<string>();
            _hideTimer = new Timer();
            _hideTimer.Tick += delegate
            {
                _hideTimer.Stop();
                Hide();
                ShowNextMessage();
            };

            Text = "小曦薇消息";
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            ControlBox = false;
            AutoScaleMode = AutoScaleMode.None;
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams parameters = base.CreateParams;
                parameters.ExStyle |= WsExLayered | WsExToolWindow | WsExNoActivate;
                return parameters;
            }
        }

        public void Enqueue(string text)
        {
            string value = (text ?? string.Empty).Trim();
            if (value.Length == 0)
            {
                return;
            }
            if (value.Length > 300)
            {
                value = value.Substring(0, 300);
            }

            while (_messages.Count >= MaxQueueLength)
            {
                _messages.Dequeue();
            }
            _messages.Enqueue(value);
            if (!Visible && !_hideTimer.Enabled)
            {
                ShowNextMessage();
            }
        }

        public void UpdateAnchor(Rectangle petBounds, bool topMost)
        {
            _petBounds = petBounds;
            TopMost = topMost;
            if (Visible)
            {
                PositionNearPet();
                RenderBubble(Tag as string ?? string.Empty);
            }
        }

        private void ShowNextMessage()
        {
            if (_messages.Count == 0 || IsDisposed)
            {
                return;
            }

            string text = _messages.Dequeue();
            Tag = text;
            CalculatePositionAndRender(text);
            if (!Visible)
            {
                Show();
            }
            RenderBubble(text);
            _hideTimer.Interval = Math.Max(
                4200,
                Math.Min(12000, 3800 + text.Length * 85));
            _hideTimer.Start();
        }

        private void CalculatePositionAndRender(string text)
        {
            // Render once to learn the window size, place it, then render the
            // correct left/right tail orientation.
            RenderBubble(text);
            PositionNearPet();
            RenderBubble(text);
        }

        private void PositionNearPet()
        {
            if (_petBounds.Width <= 0 || _petBounds.Height <= 0)
            {
                return;
            }

            Rectangle area = Screen.FromRectangle(_petBounds).WorkingArea;
            int overlap = Math.Max(64, _petBounds.Width / 3);
            int preferredLeft = _petBounds.Left - Width + overlap;
            _tailOnRight = preferredLeft >= area.Left + 8;
            int x = _tailOnRight
                ? preferredLeft
                : _petBounds.Right - overlap;
            int y = _petBounds.Top + Math.Max(4, _petBounds.Height / 48);
            x = Math.Max(area.Left + 8, Math.Min(x, area.Right - Width - 8));
            y = Math.Max(area.Top + 8, Math.Min(y, area.Bottom - Height - 8));
            Location = new Point(x, y);
        }

        private void RenderBubble(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            const int maximumTextWidth = 280;
            const int horizontalPadding = 20;
            const int verticalPadding = 16;
            const int tailWidth = 18;
            const int shadowPadding = 9;

            using (Font font = new Font(
                "Microsoft YaHei UI",
                14.0f,
                FontStyle.Bold,
                GraphicsUnit.Pixel))
            using (Bitmap measureBitmap = new Bitmap(1, 1))
            using (Graphics measureGraphics = Graphics.FromImage(measureBitmap))
            {
                measureGraphics.TextRenderingHint =
                    System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                SizeF measured = measureGraphics.MeasureString(
                    text,
                    font,
                    maximumTextWidth,
                    StringFormat.GenericTypographic);
                int textWidth = Math.Max(
                    74,
                    Math.Min(maximumTextWidth, (int)Math.Ceiling(measured.Width)));
                int textHeight = Math.Max(22, (int)Math.Ceiling(measured.Height));
                int bodyWidth = textWidth + horizontalPadding * 2;
                int bodyHeight = textHeight + verticalPadding * 2;
                int bitmapWidth = bodyWidth + tailWidth + shadowPadding * 2;
                int bitmapHeight = bodyHeight + shadowPadding * 2;

                ClientSize = new Size(bitmapWidth, bitmapHeight);
                using (Bitmap bitmap = new Bitmap(
                    bitmapWidth,
                    bitmapHeight,
                    PixelFormat.Format32bppPArgb))
                using (Graphics graphics = Graphics.FromImage(bitmap))
                {
                    graphics.Clear(Color.Transparent);
                    graphics.CompositingMode = CompositingMode.SourceOver;
                    graphics.CompositingQuality = CompositingQuality.HighQuality;
                    graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    graphics.TextRenderingHint =
                        System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

                    int bodyX = shadowPadding + (_tailOnRight ? 0 : tailWidth);
                    int bodyY = shadowPadding;
                    Rectangle body =
                        new Rectangle(bodyX, bodyY, bodyWidth, bodyHeight);
                    Point[] tail = _tailOnRight
                        ? new Point[]
                        {
                            new Point(
                                body.Right - 1,
                                body.Top + body.Height * 54 / 100),
                            new Point(
                                body.Right + tailWidth,
                                body.Top + body.Height * 66 / 100),
                            new Point(
                                body.Right - 1,
                                body.Top + body.Height * 76 / 100)
                        }
                        : new Point[]
                        {
                            new Point(
                                body.Left + 1,
                                body.Top + body.Height * 54 / 100),
                            new Point(
                                body.Left - tailWidth,
                                body.Top + body.Height * 66 / 100),
                            new Point(
                                body.Left + 1,
                                body.Top + body.Height * 76 / 100)
                        };

                    using (GraphicsPath shadowPath = CreateRoundedRectangle(
                        new Rectangle(
                            body.X + 2,
                            body.Y + 4,
                            body.Width,
                            body.Height),
                        15))
                    using (SolidBrush shadow =
                        new SolidBrush(Color.FromArgb(35, 38, 28, 38)))
                    {
                        graphics.FillPath(shadow, shadowPath);
                    }

                    using (GraphicsPath bodyPath =
                        CreateRoundedRectangle(body, 15))
                    using (SolidBrush fill =
                        new SolidBrush(Color.FromArgb(250, 255, 255, 255)))
                    using (Pen border =
                        new Pen(Color.FromArgb(255, 242, 148, 166), 1.2f))
                    {
                        graphics.FillPath(fill, bodyPath);
                        graphics.FillPolygon(fill, tail);
                        graphics.DrawPath(border, bodyPath);
                        graphics.DrawLines(
                            border,
                            new Point[] { tail[0], tail[1], tail[2] });
                    }

                    RectangleF textArea = new RectangleF(
                        body.X + horizontalPadding,
                        body.Y + verticalPadding,
                        textWidth,
                        textHeight + 2);
                    using (SolidBrush textBrush =
                        new SolidBrush(Color.FromArgb(255, 52, 55, 66)))
                    using (StringFormat format =
                        new StringFormat(StringFormat.GenericTypographic))
                    {
                        format.Trimming = StringTrimming.EllipsisCharacter;
                        graphics.DrawString(
                            text,
                            font,
                            textBrush,
                            textArea,
                            format);
                    }
                    UpdateLayeredBitmap(bitmap);
                }
            }
        }

        private static GraphicsPath CreateRoundedRectangle(
            Rectangle rectangle,
            int radius)
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

        protected override void OnMouseDown(MouseEventArgs eventArgs)
        {
            base.OnMouseDown(eventArgs);
            _hideTimer.Stop();
            Hide();
            ShowNextMessage();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _hideTimer.Stop();
                _hideTimer.Dispose();
            }
            base.Dispose(disposing);
        }

        private void UpdateLayeredBitmap(Bitmap bitmap)
        {
            if (!IsHandleCreated || IsDisposed)
            {
                return;
            }

            IntPtr screenDeviceContext = NativeMethods.GetDC(IntPtr.Zero);
            IntPtr memoryDeviceContext =
                NativeMethods.CreateCompatibleDC(screenDeviceContext);
            IntPtr bitmapHandle = IntPtr.Zero;
            IntPtr oldBitmap = IntPtr.Zero;
            try
            {
                bitmapHandle = bitmap.GetHbitmap(Color.FromArgb(0));
                oldBitmap = NativeMethods.SelectObject(
                    memoryDeviceContext,
                    bitmapHandle);
                NativePoint source = new NativePoint(0, 0);
                NativePoint destination = new NativePoint(Left, Top);
                NativeSize size = new NativeSize(bitmap.Width, bitmap.Height);
                BlendFunction blend = new BlendFunction();
                blend.BlendOp = AcSrcOver;
                blend.BlendFlags = 0;
                blend.SourceConstantAlpha = 255;
                blend.AlphaFormat = AcSrcAlpha;
                NativeMethods.UpdateLayeredWindow(
                    Handle,
                    screenDeviceContext,
                    ref destination,
                    ref size,
                    memoryDeviceContext,
                    ref source,
                    0,
                    ref blend,
                    UlwAlpha);
            }
            finally
            {
                if (oldBitmap != IntPtr.Zero)
                {
                    NativeMethods.SelectObject(memoryDeviceContext, oldBitmap);
                }
                if (bitmapHandle != IntPtr.Zero)
                {
                    NativeMethods.DeleteObject(bitmapHandle);
                }
                if (memoryDeviceContext != IntPtr.Zero)
                {
                    NativeMethods.DeleteDC(memoryDeviceContext);
                }
                if (screenDeviceContext != IntPtr.Zero)
                {
                    NativeMethods.ReleaseDC(
                        IntPtr.Zero,
                        screenDeviceContext);
                }
            }
        }
    }
}
