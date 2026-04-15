using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace LMUWeaver
{
    /// <summary>
    /// A lightweight FrameworkElement that renders text with a solid stroke outline,
    /// producing the thick white-text-on-black-border look used for on-screen call-outs.
    /// </summary>
    public class OutlinedTextBlock : FrameworkElement
    {
        public static readonly DependencyProperty TextProperty =
            DependencyProperty.Register(nameof(Text), typeof(string), typeof(OutlinedTextBlock),
                new FrameworkPropertyMetadata("",
                    FrameworkPropertyMetadataOptions.AffectsRender |
                    FrameworkPropertyMetadataOptions.AffectsMeasure));

        public static readonly DependencyProperty FontSizeProperty =
            DependencyProperty.Register(nameof(FontSize), typeof(double), typeof(OutlinedTextBlock),
                new FrameworkPropertyMetadata(48.0,
                    FrameworkPropertyMetadataOptions.AffectsRender |
                    FrameworkPropertyMetadataOptions.AffectsMeasure));

        public static readonly DependencyProperty FillProperty =
            DependencyProperty.Register(nameof(Fill), typeof(Brush), typeof(OutlinedTextBlock),
                new FrameworkPropertyMetadata(Brushes.White,
                    FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty StrokeProperty =
            DependencyProperty.Register(nameof(Stroke), typeof(Brush), typeof(OutlinedTextBlock),
                new FrameworkPropertyMetadata(Brushes.Black,
                    FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty StrokeThicknessProperty =
            DependencyProperty.Register(nameof(StrokeThickness), typeof(double), typeof(OutlinedTextBlock),
                new FrameworkPropertyMetadata(5.0,
                    FrameworkPropertyMetadataOptions.AffectsRender));

        public string Text            { get => (string)GetValue(TextProperty);              set => SetValue(TextProperty, value); }
        public double FontSize        { get => (double)GetValue(FontSizeProperty);          set => SetValue(FontSizeProperty, value); }
        public Brush  Fill            { get => (Brush) GetValue(FillProperty);              set => SetValue(FillProperty, value); }
        public Brush  Stroke          { get => (Brush) GetValue(StrokeProperty);            set => SetValue(StrokeProperty, value); }
        public double StrokeThickness { get => (double)GetValue(StrokeThicknessProperty);   set => SetValue(StrokeThicknessProperty, value); }

        private FormattedText MakeFt() =>
            new(Text ?? "",
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI Black,Arial Black,Impact"),
                    FontStyles.Normal, FontWeights.Black, FontStretches.Normal),
                FontSize,
                Fill,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);

        protected override Size MeasureOverride(Size _)
        {
            if (string.IsNullOrEmpty(Text)) return Size.Empty;
            var ft  = MakeFt();
            double pad = StrokeThickness * 2;
            return new Size(ft.Width + pad, ft.Height + pad);
        }

        protected override void OnRender(DrawingContext dc)
        {
            if (string.IsNullOrEmpty(Text)) return;
            var ft  = MakeFt();
            var geo = ft.BuildGeometry(new Point(StrokeThickness, StrokeThickness));
            // Draw outline (stroke drawn twice as wide so it surrounds the fill)
            dc.DrawGeometry(null,
                new Pen(Stroke, StrokeThickness * 2) { LineJoin = PenLineJoin.Round },
                geo);
            // Draw fill on top
            dc.DrawGeometry(Fill, null, geo);
        }
    }
}
