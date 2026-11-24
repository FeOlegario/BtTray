using System;
using System.Drawing;
using System.Windows.Forms;

namespace BtTray
{
    /// <summary>
    /// Screen reader support (Narrator, NVDA…) for the owner-drawn panel. Every Target of the current view is
    /// exposed as an accessible child with its name, role, state, screen position and default action, so the
    /// panel can be explored and used without seeing it. PopupForm raises the focus events as the keyboard moves.
    /// </summary>
    class PanelAccessible : Control.ControlAccessibleObject
    {
        readonly PopupForm form;

        public PanelAccessible(PopupForm form) : base(form) { this.form = form; }

        public override string Name { get { return form.AccessibleTitle; } set { } }

        public override AccessibleRole Role { get { return AccessibleRole.Dialog; } }

        public override int GetChildCount() { return form.Targets.Count; }

        public override AccessibleObject GetChild(int index)
        {
            if (index < 0 || index >= form.Targets.Count) return null;
            return new TargetAccessible(form, this, index);
        }

        public override AccessibleObject GetFocused()
        {
            int i = form.FocusIndex;
            return i >= 0 && form.ContainsFocus ? GetChild(i) : base.GetFocused();
        }

        public override AccessibleObject GetSelected() { return GetFocused(); }

        public override AccessibleObject HitTest(int x, int y)
        {
            Point p = form.PointToClient(new Point(x, y));
            var t = form.Targets;
            for (int i = 0; i < t.Count; i++)
                if (t[i].VisibleAt(p)) return GetChild(i);
            return form.ClientRectangle.Contains(p) ? this : null;
        }
    }

    /// <summary>One target (button, option, link, text) as seen by assistive technology.</summary>
    class TargetAccessible : AccessibleObject
    {
        readonly PopupForm form;
        readonly AccessibleObject parent;
        readonly int index;

        public TargetAccessible(PopupForm form, AccessibleObject parent, int index)
        {
            this.form = form; this.parent = parent; this.index = index;
        }

        Target T
        {
            get
            {
                var t = form.Targets;
                return index >= 0 && index < t.Count ? t[index] : null;
            }
        }

        public override AccessibleObject Parent { get { return parent; } }

        public override string Name { get { Target t = T; return t != null ? t.Name : ""; } set { } }

        public override string Description { get { Target t = T; return t != null ? t.Description : null; } }

        public override string Help { get { Target t = T; return t != null && t.Tip != t.Name ? t.Tip : null; } }

        public override AccessibleRole Role { get { Target t = T; return t != null ? t.Role : AccessibleRole.None; } }

        public override Rectangle Bounds
        {
            get
            {
                Target t = T;
                if (t == null || !form.IsHandleCreated) return Rectangle.Empty;
                Rectangle b = t.Clip.IsEmpty ? t.Bounds : Rectangle.Intersect(t.Bounds, t.Clip);
                return form.RectangleToScreen(b);
            }
        }

        public override AccessibleStates State
        {
            get
            {
                Target t = T;
                if (t == null) return AccessibleStates.Unavailable;
                AccessibleStates st = AccessibleStates.None;
                if (t.Focusable) st |= AccessibleStates.Focusable | AccessibleStates.Selectable;
                if (t.Focusable && form.FocusIndex == index && form.ContainsFocus) st |= AccessibleStates.Focused | AccessibleStates.Selected;
                if (!t.Enabled) st |= AccessibleStates.Unavailable;
                if (t.Checked) st |= AccessibleStates.Checked;
                if (t.Offscreen) st |= AccessibleStates.Offscreen;
                if (t.Role == AccessibleRole.StaticText) st |= AccessibleStates.ReadOnly;
                if (t.Role == AccessibleRole.Link) st |= AccessibleStates.Linked;
                return st;
            }
        }

        public override string DefaultAction
        {
            get
            {
                Target t = T;
                if (t == null || !t.Focusable) return null;
                if (t.Role == AccessibleRole.RadioButton) return L.T("Selecionar", "Select");
                if (t.Role == AccessibleRole.CheckButton) return L.T("Alternar", "Toggle");
                if (t.Role == AccessibleRole.Link) return L.T("Abrir", "Open");
                return L.T("Pressionar", "Press");
            }
        }

        public override void DoDefaultAction() { form.InvokeTarget(index); }

        public override void Select(AccessibleSelection flags)
        {
            if ((flags & (AccessibleSelection.TakeFocus | AccessibleSelection.TakeSelection)) != 0) form.FocusTarget(index, true);
        }

        public override AccessibleObject Navigate(AccessibleNavigation dir)
        {
            int n = form.Targets.Count;
            switch (dir)
            {
                case AccessibleNavigation.Next:
                case AccessibleNavigation.Down:
                case AccessibleNavigation.Right:
                    return index + 1 < n ? parent.GetChild(index + 1) : null;
                case AccessibleNavigation.Previous:
                case AccessibleNavigation.Up:
                case AccessibleNavigation.Left:
                    return index > 0 ? parent.GetChild(index - 1) : null;
            }
            return null;
        }
    }
}
