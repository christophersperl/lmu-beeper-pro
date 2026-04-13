using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace LMUWeaver
{
    public partial class BrakeOverlay : Window
    {
        private static readonly Color ColorSafe    = Color.FromRgb(0,   255, 204);
        private static readonly Color ColorWarn    = Color.FromRgb(255, 170,   0);
        private static readonly Color ColorDanger  = Color.FromRgb(255,  40,   0);

        public BrakeOverlay()
        {
            InitializeComponent();
        }

        public void Update(double? distToNext, double warn1, double warn2)
        {
            double w = ProxContainer.ActualWidth;
            if (w <= 0) return;

            if (distToNext == null || distToNext.Value < -5)
            {
                ProxBar.Width = 0;
                ProxEdge.Margin = new Thickness(0, 0, 0, 0);
                ProxEdge.Width = 0;
                return;
            }

            double dist = Math.Max(0, distToNext.Value);

            // Color: teal → orange → red as distance shrinks
            Color col;
            if (dist >= warn1)
                col = ColorSafe;
            else if (dist >= warn2)
                col = LerpColor(ColorWarn, ColorSafe, (dist - warn2) / (warn1 - warn2));
            else
                col = LerpColor(ColorDanger, ColorWarn, dist / Math.Max(1, warn2));

            ProxBrush.Color = col;

            // Bar fills as brake point approaches (0=far, full=at point)
            double fraction = warn1 > 0 ? Math.Clamp(1.0 - dist / warn1, 0, 1) : 0;
            double fillWidth = w * fraction;
            ProxBar.Width = fillWidth;
            ProxEdge.Width = fillWidth > 0 ? 2 : 0;
            ProxEdge.Margin = new Thickness(Math.Max(0, fillWidth - 2), 0, 0, 0);
        }

        // Mouse wheel: adjust background opacity
        private void Window_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            BgLayer.Opacity = Math.Clamp(BgLayer.Opacity + e.Delta / 2400.0, 0.0, 1.0);
        }

        // Right-click: close/hide
        private void Window_RightClick(object sender, MouseButtonEventArgs e) => Hide();

        private void Window_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left) DragMove();
        }

        private void ResizeThumb_DragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
        {
            Width  = Math.Max(MinWidth,  Width  + e.HorizontalChange);
            Height = Math.Max(MinHeight, Height + e.VerticalChange);
        }

        private static Color LerpColor(Color a, Color b, double t)
        {
            t = Math.Clamp(t, 0, 1);
            return Color.FromRgb(
                (byte)(a.R + (b.R - a.R) * t),
                (byte)(a.G + (b.G - a.G) * t),
                (byte)(a.B + (b.B - a.B) * t));
        }
    }
}
