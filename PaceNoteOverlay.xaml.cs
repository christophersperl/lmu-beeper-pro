using System.Windows;

namespace LMUWeaver
{
    public partial class PaceNoteOverlay : Window
    {
        public PaceNoteOverlay()
        {
            InitializeComponent();
            Visibility = Visibility.Collapsed;
        }

        /// <summary>Show the note centred on the primary screen work-area.</summary>
        public void ShowNote(string symbolGlyph, string text)
        {
            TxtSymbol.Text = symbolGlyph;
            TxtNote.Text   = string.IsNullOrWhiteSpace(text) ? "" : text;

            Visibility = Visibility.Visible;
            UpdateLayout();

            var wa = SystemParameters.WorkArea;
            Left = wa.Left + (wa.Width  - ActualWidth)  / 2.0;
            Top  = wa.Top  + (wa.Height - ActualHeight) / 2.0;
        }

        public void HideNote() => Visibility = Visibility.Collapsed;
    }
}
