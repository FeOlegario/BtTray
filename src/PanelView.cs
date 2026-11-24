using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace BtTray
{
    /// <summary>
    /// Something the user can point at, click, reach with the keyboard or have read by a screen reader: a button,
    /// a list option, a link or a piece of text. Each view rebuilds its targets from its current layout, and
    /// PopupForm uses the same list for hover, clicks, tooltips, keyboard focus and accessibility.
    /// </summary>
    class Target
    {
        public string Key;                    // stable identity ("toggle:ADDRESS"), kept across rebuilds
        public Rectangle Bounds;              // client coordinates
        public Rectangle Clip;                // scrolling area the target lives in; empty = always visible
        public int Row = -1;                  // list row it belongs to (row highlight), or -1
        public string Name;                   // what a screen reader says
        public string Description;            // extra context for screen readers (device status, …)
        public string Tip;                    // mouse tooltip, or null
        public AccessibleRole Role = AccessibleRole.PushButton;
        public bool Enabled = true;
        public bool Checked;
        public Action Invoke;                 // null = not interactive (text only)

        public bool Focusable { get { return Invoke != null; } }

        public bool VisibleAt(Point p)
        {
            return Bounds.Contains(p) && (Clip.IsEmpty || Clip.Contains(p));
        }

        public bool Offscreen
        {
            get { return !Clip.IsEmpty && !Clip.IntersectsWith(Bounds); }
        }
    }

    /// <summary>One screen of the panel (device list, settings, search). PopupForm draws the frame and the footer.</summary>
    abstract class PanelView
    {
        protected readonly PopupForm F;

        protected PanelView(PopupForm form) { F = form; }

        protected int Sc(float v) { return F.Sc(v); }

        /// <summary>Header title, also the panel's accessible name while this view is shown.</summary>
        public abstract string Title { get; }

        /// <summary>Height of the content area between the header and the footer.</summary>
        public abstract int ContentHeight { get; }

        /// <summary>Paints the header and the content.</summary>
        public abstract void Paint(Graphics g);

        /// <summary>Adds the targets in keyboard (Tab) order: header first, then the content.</summary>
        public abstract void BuildTargets(List<Target> targets);

        /// <summary>List row under the point (row highlight), or -1.</summary>
        public virtual int RowAt(Point p) { return -1; }

        /// <summary>Scrolls the content by dy pixels (clamped). Returns true if it moved.</summary>
        public virtual bool ScrollBy(int dy) { return false; }

        /// <summary>Mouse wheel: scrolls one row per notch. Returns true if it moved.</summary>
        public virtual bool Wheel(int delta) { return ScrollBy(-Math.Sign(delta) * F.RowH); }

        /// <summary>The view was shown.</summary>
        public virtual void Enter() { }

        /// <summary>The view is going away or the panel is closing; save = keep pending edits.</summary>
        public virtual void Leave(bool save) { }

        public virtual void MouseDown(Point p) { }
        public virtual void DoubleClick(Point p) { }
        public virtual void F5() { }

        /// <summary>Extra keys (e.g. F2) with the focused target, or null. Returns true when handled.</summary>
        public virtual bool Key(Keys key, Target focused) { return false; }

        /// <summary>True while a child control (the rename box) owns the keyboard.</summary>
        public virtual bool CapturesKeyboard { get { return false; } }

        // Thin scrollbar on the right of a scrolling area.
        protected void PaintScrollHint(Graphics g, Rectangle area, int total, int scroll)
        {
            if (total <= area.Height) return;
            int th = Math.Max(Sc(20), area.Height * area.Height / total);
            int ty = area.Top + (area.Height - th) * scroll / Math.Max(1, total - area.Height);
            PopupForm.FillRound(g, new Rectangle(F.W - Sc(5), ty, Sc(3), th), Sc(1), F.Th.Border);
        }
    }
}
