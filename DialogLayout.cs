using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

sealed partial class ReplayShelf
{
    sealed class ScrollViewport : Panel
    {
        const int BarWidth = 13;
        readonly Panel clip = new Panel();
        readonly ScrollTrack vertical = new ScrollTrack(true);
        readonly ScrollTrack horizontal = new ScrollTrack(false);
        Control body;
        Func<Size, Size> measure;
        bool layingOut;
        public int AvailableWidth { get { return Math.Max(0, ClientSize.Width - (vertical.Visible ? BarWidth : 0)); } }

        public ScrollViewport()
        {
            Controls.Add(clip);
            Controls.Add(vertical);
            Controls.Add(horizontal);
            vertical.Changed += MoveBody;
            horizontal.Changed += MoveBody;
        }

        public void Attach(Control content, Func<Size, Size> measureContent)
        {
            body = content;
            measure = measureContent;
            content.Dock = DockStyle.None;
            clip.Controls.Add(content);
            RefreshLayout();
        }

        public void RefreshLayout()
        {
            if (body == null || layingOut || IsDisposed) return;
            layingOut = true;
            try
            {
                bool v = false, h = false;
                Size space = ClientSize, needed = space;
                for (int attempt = 0; attempt < 4; attempt++)
                {
                    space = new Size(Math.Max(0, ClientSize.Width - (v ? BarWidth : 0)),
                        Math.Max(0, ClientSize.Height - (h ? BarWidth : 0)));
                    needed = measure(space);
                    bool nextV = needed.Height > space.Height;
                    bool nextH = needed.Width > space.Width;
                    if (v == nextV && h == nextH) break;
                    v = nextV;
                    h = nextH;
                }
                clip.Bounds = new Rectangle(Point.Empty, space);
                body.Size = new Size(Math.Max(needed.Width, space.Width), Math.Max(needed.Height, space.Height));
                vertical.Bounds = new Rectangle(space.Width, 0, BarWidth, space.Height);
                horizontal.Bounds = new Rectangle(0, space.Height, space.Width, BarWidth);
                vertical.Visible = v;
                horizontal.Visible = h;
                vertical.SetExtent(space.Height, body.Height);
                horizontal.SetExtent(space.Width, body.Width);
                MoveBody();
            }
            finally { layingOut = false; }
        }

        void MoveBody()
        {
            if (body != null) body.Location = new Point(-horizontal.Value, -vertical.Value);
        }

        public void Reveal(Control control)
        {
            if (control == null || !Contains(control)) return;
            Point point = clip.PointToClient(control.PointToScreen(Point.Empty));
            if (point.X < 0) horizontal.Step(point.X);
            else if (control.Width <= clip.Width && point.X + control.Width > clip.Width) horizontal.Step(point.X + control.Width - clip.Width);
            if (point.Y < 0) vertical.Step(point.Y);
            else if (control.Height <= clip.Height && point.Y + control.Height > clip.Height) vertical.Step(point.Y + control.Height - clip.Height);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            RefreshLayout();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            var track = (ModifierKeys & Keys.Shift) != 0 || vertical.Maximum == 0 ? horizontal : vertical;
            if (track.Maximum == 0) { base.OnMouseWheel(e); return; }
            track.Step(-e.Delta * 54 / 120);
            var handled = e as HandledMouseEventArgs;
            if (handled != null) handled.Handled = true;
        }
    }

    sealed class ScrollTrack : Control
    {
        readonly bool vertical;
        int extent, window, value, dragOffset;
        bool dragging;
        public event Action Changed;
        public int Maximum { get { return Math.Max(0, extent - window); } }
        public int Value { get { return value; } }
        int TrackLength { get { return vertical ? Height : Width; } }
        int ThumbLength { get { return extent == 0 ? TrackLength : Math.Min(TrackLength, Math.Max(18, (int)((long)TrackLength * window / extent))); } }
        int Travel { get { return TrackLength - ThumbLength; } }
        int ThumbStart { get { return Maximum == 0 ? 0 : (int)((long)value * Travel / Maximum); } }

        public ScrollTrack(bool isVertical)
        {
            vertical = isVertical;
            DoubleBuffered = true;
            SetStyle(ControlStyles.Selectable, true);
            TabStop = true;
            AccessibleName = vertical ? "Vertical scroll" : "Horizontal scroll";
            AccessibleRole = AccessibleRole.ScrollBar;
        }

        public void SetExtent(int visible, int full)
        {
            window = visible;
            extent = full;
            SetValue(value);
            Invalidate();
        }

        public void Step(int delta) { SetValue(value + delta); }

        void SetValue(int next)
        {
            next = Math.Max(0, Math.Min(Maximum, next));
            if (next == value) return;
            value = next;
            if (Changed != null) Changed();
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Color.FromArgb(36, 23, 51));
            var color = dragging || Focused ? Color.FromArgb(166, 111, 221) : Color.FromArgb(121, 83, 157);
            using (var brush = new SolidBrush(color))
            {
                if (vertical) e.Graphics.FillRectangle(brush, 2, ThumbStart, Math.Max(1, Width - 4), ThumbLength);
                else e.Graphics.FillRectangle(brush, ThumbStart, 2, ThumbLength, Math.Max(1, Height - 4));
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            Focus();
            int at = vertical ? e.Y : e.X;
            if (at < ThumbStart || at >= ThumbStart + ThumbLength)
            {
                Step(at < ThumbStart ? -window : window);
                return;
            }
            dragging = true;
            dragOffset = at - ThumbStart;
            Capture = true;
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (dragging && Travel > 0) SetValue((int)((long)((vertical ? e.Y : e.X) - dragOffset) * Maximum / Travel));
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left) { Capture = false; dragging = false; Invalidate(); }
        }

        protected override void OnMouseCaptureChanged(EventArgs e) { base.OnMouseCaptureChanged(e); if (!Capture) { dragging = false; Invalidate(); } }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
        protected override bool IsInputKey(Keys key)
        {
            return key == Keys.Left || key == Keys.Right || key == Keys.Up || key == Keys.Down ||
                key == Keys.PageDown || key == Keys.PageUp || key == Keys.Home || key == Keys.End || base.IsInputKey(key);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            switch (e.KeyCode)
            {
                case Keys.Up: case Keys.Left: Step(-32); break;
                case Keys.Down: case Keys.Right: Step(32); break;
                case Keys.PageUp: Step(-window); break;
                case Keys.PageDown: Step(window); break;
                case Keys.Home: SetValue(0); break;
                case Keys.End: SetValue(Maximum); break;
                default: return;
            }
            e.Handled = true;
        }
    }

    static void RevealInputs(ScrollViewport viewport, Control container)
    {
        foreach (Control child in container.Controls)
        {
            child.GotFocus += delegate { viewport.Reveal(child); };
            if (child.HasChildren && !(child is ScrollViewport)) RevealInputs(viewport, child);
        }
    }

    static ScrollViewport WrapDialogBody(Form form, int footerHeight)
    {
        var initial = form.ClientSize;
        var body = new Panel { Size = new Size(initial.Width, Math.Max(0, initial.Height - footerHeight)) };
        var controls = form.Controls.Cast<Control>().ToArray();
        foreach (Control control in controls) body.Controls.Add(control);
        var viewport = new ScrollViewport { Dock = DockStyle.Fill, Tag = body.Size };
        viewport.Attach(body, delegate(Size space) { return (Size)viewport.Tag; });
        RevealInputs(viewport, body);
        form.Controls.Add(viewport);
        return viewport;
    }

    static void EnableDialogResize(Form form)
    {
        form.FormBorderStyle = FormBorderStyle.Sizable;
        form.MinimumSize = new Size(325, 175);
        form.MaximizeBox = true;
        form.SizeGripStyle = SizeGripStyle.Show;
    }
}
