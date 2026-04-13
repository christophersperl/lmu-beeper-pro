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
        private static readonly Color ColorInactive = Color.FromRgb(80,  80,  80);

        public BrakeOverlay()
        {
            InitializeComponent();
        }

        public void Update(double? distToNext, double warn1, double warn2)
        {
            if (distToNext == null || distToNext.Value < -10)
            {
                TxtDist.Text = "--- m";
                TxtDist.Foreground = new SolidColorBrush(ColorInactive);
                ProxBar.Width = 0;
                return;
            }

            double dist = Math.Max(0, distToNext.Value);
            TxtDist.Text = dist < 10 ? $"{dist:F1} m" : $"{dist:F0} m";

            // Smooth color transition: safe → warn → danger
            Color col;
            if (dist >= warn1)
                col = ColorSafe;
            else if (dist >= warn2)
                col = LerpColor(ColorWarn, ColorSafe, (dist - warn2) / (warn1 - warn2));
            else
                col = LerpColor(ColorDanger, ColorWarn, dist / Math.Max(1, warn2));

            var brush = new SolidColorBrush(col);
            TxtDist.Foreground = brush;
            ProxBar.Background = brush;

            // Proximity bar fills as you approach (0 = far, full = at point)
            double fraction = warn1 > 0 ? Math.Clamp(1.0 - dist / warn1, 0, 1) : 0;
            double containerWidth = ProxContainer.ActualWidth;
            ProxBar.Width = containerWidth * fraction;
        }

        private static Color LerpColor(Color a, Color b, double t)
        {
            t = Math.Clamp(t, 0, 1);
            return Color.FromRgb(
                (byte)(a.R + (b.R - a.R) * t),
                (byte)(a.G + (b.G - a.G) * t),
                (byte)(a.B + (b.B - a.B) * t));
        }

        private void Window_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left) DragMove();
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Hide();
        }

        private void ResizeThumb_DragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
        {
            Width  = Math.Max(MinWidth,  Width  + e.HorizontalChange);
            Height = Math.Max(MinHeight, Height + e.VerticalChange);
        }
    }
}
