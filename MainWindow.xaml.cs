using rF2SMMonitor;
using rF2SMMonitor.rFactor2Data;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Runtime.InteropServices;
using System.Speech.Synthesis;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Media;
using Microsoft.Win32;

namespace LMUWeaver
{
    public class BrakingPoint
    {
        public double Distance { get; set; }
        public string Description { get; set; } = "";
    }

    public partial class MainWindow : Window
    {
        public ObservableCollection<BrakingPoint> BrakingPoints { get; set; } = new ObservableCollection<BrakingPoint>();

        private Dictionary<double, bool[]> beepStatus = new Dictionary<double, bool[]>();
        private int lastLapNumber = -1;
        private string currentTrack = "";
        private double currentDistance = 0;

        private double warn1Distance = 100.0;
        private double warn2Distance = 35.0;
        private double shiftBeeperPercentage = 0.97;
        private bool shiftBeepPlayed = false;
        private DateTime lastBrakeBeepTime = DateTime.MinValue;
        private readonly Queue<(double throttle, double brake)> trailData = new();
        private const int TrailLength = 300;
        private BrakeOverlay? _overlay;
        private double expandedHeight = 480.0;

        private volatile bool isBrakeBeepEnabled = true;
        private volatile bool isShiftBeepEnabled = true;
        private string brakePreset = "Earcon";
        private string shiftPreset = "Bell";
        private double currentSpeed = 0;
        private int currentGear = 0;
        private double currentRpmPct = 0;
        private double lastEntrySpeed = 0;

        // ── Pace Notes ────────────────────────────────────────────────────────
        public ObservableCollection<PaceNote> PaceNotes { get; } = new();
        private PaceNoteOverlay? _noteOverlay;
        private readonly HashSet<double> _notesFiredThisLap = new();
        private CancellationTokenSource? _noteHideCts;
        private double  _noteDisplaySeconds = 3.0;
        private bool    _noteTtsEnabled     = true;
        private SpeechSynthesizer? _tts;
        // editor state
        private PaceNote? _editingNote   = null;  // null = adding new
        private double    _editorDist    = 0;

        // ── Themes ──────────────────────────────────────────────────────────
        private sealed class ThemeConfig
        {
            public required string Name              { get; init; }
            public required Func<System.Windows.Media.Brush> MakeBg { get; init; }
            public required System.Windows.Media.Color BorderColor     { get; init; }
            public required System.Windows.Media.Color AccentColor     { get; init; }
            public required System.Windows.Media.Color WarnColor       { get; init; }
            public required System.Windows.Media.Color TextColor       { get; init; }
            public required System.Windows.Media.Color SubTextColor    { get; init; }
            public required System.Windows.Media.Color TrailGasColor   { get; init; }
            public required System.Windows.Media.Color TrailBrakeColor { get; init; }
            public required System.Windows.Media.Color TrailBgColor    { get; init; }
            public required System.Windows.Media.Color RpmLow          { get; init; }
            public required System.Windows.Media.Color RpmMid          { get; init; }
            public required System.Windows.Media.Color RpmHigh         { get; init; }
            public required System.Windows.Media.Color RpmMarkerColor  { get; init; }
            public required double RpmBarHeight  { get; init; }
            public required double TrailHeight   { get; init; }
            public required double BrakeFontSize { get; init; }
            public required double GearFontSize  { get; init; }
        }
        private ThemeConfig[] _allThemes = null!;
        private ThemeConfig   _theme     = null!;

        private static System.Windows.Media.Color C(byte a, byte r, byte g, byte b)
            => System.Windows.Media.Color.FromArgb(a, r, g, b);

        public MainWindow()
        {
            InitializeComponent();
            ListPoints.ItemsSource = BrakingPoints;
            ListNotes.ItemsSource  = PaceNotes;
            InitThemes();
            ApplyTheme(_allThemes[0]);
            Task.Run(() => ReadTelemetryLoop());
        }

        private void ReadTelemetryLoop()
        {
            string telMap = "$rFactor2SMMP_Telemetry$";
            string scoMap = "$rFactor2SMMP_Scoring$";
            MemoryMappedFile? telMmf = null;
            MemoryMappedFile? scoMmf = null;

            while (true)
            {
                if (telMmf == null || scoMmf == null)
                {
                    try
                    {
                        telMmf = MemoryMappedFile.OpenExisting(telMap);
                        scoMmf = MemoryMappedFile.OpenExisting(scoMap);
                    }
                    catch { Thread.Sleep(2000); continue; }
                }

                try
                {
                    using (var telAcc = telMmf.CreateViewAccessor())
                    using (var scoAcc = scoMmf.CreateViewAccessor())
                    {
                        unsafe
                        {
                            byte* tPtr = null; byte* sPtr = null;
                            telAcc.SafeMemoryMappedViewHandle.AcquirePointer(ref tPtr);
                            scoAcc.SafeMemoryMappedViewHandle.AcquirePointer(ref sPtr);

                            rF2Telemetry telData = (rF2Telemetry)Marshal.PtrToStructure((IntPtr)tPtr, typeof(rF2Telemetry))!;
                            rF2Scoring scoData = (rF2Scoring)Marshal.PtrToStructure((IntPtr)sPtr, typeof(rF2Scoring))!;

                            var mySco = scoData.mVehicles.FirstOrDefault(v => v.mIsPlayer == 1);
                            var myTel = telData.mVehicles.FirstOrDefault(v => v.mID == mySco.mID);

                            currentDistance = mySco.mLapDist;
                            string track = Encoding.Default.GetString(scoData.mScoringInfo.mTrackName).Split('\0')[0];

                            if (track != currentTrack && !string.IsNullOrEmpty(track))
                            {
                                currentTrack = track;
                                Dispatcher.Invoke(() => { TxtTrack.Text = track.ToUpper(); LoadLocalPoints(); });
                            }

                            if (mySco.mTotalLaps != lastLapNumber)
                            {
                                lastLapNumber = mySco.mTotalLaps;
                                ResetBeepStatus();
                                _notesFiredThisLap.Clear();
                            }

                            if (isBrakeBeepEnabled) CheckBeeperLogic(currentDistance);
                            if (isShiftBeepEnabled) CheckShiftLogic(myTel.mEngineRPM, myTel.mEngineMaxRPM);
                            if (PaceNotes.Count > 0) CheckPaceNotes(currentDistance);

                            double throttle = myTel.mUnfilteredThrottle;
                            double brake    = myTel.mUnfilteredBrake;
                            double spd      = Math.Sqrt(myTel.mLocalVel.x * myTel.mLocalVel.x + myTel.mLocalVel.z * myTel.mLocalVel.z) * 3.6;
                            int    gearNow  = myTel.mGear;
                            double rpmPct   = myTel.mEngineMaxRPM > 0 ? myTel.mEngineRPM / myTel.mEngineMaxRPM : 0;
                            currentSpeed    = spd;
                            currentGear     = gearNow;
                            currentRpmPct   = rpmPct;
                            Dispatcher.Invoke(() => {
                                TxtDistance.Text = $"{currentDistance:F1} m";
                                var nextPoint = BrakingPoints.Where(p => p.Distance > currentDistance).OrderBy(p => p.Distance).FirstOrDefault();
                                double? distToNext = nextPoint != null ? nextPoint.Distance - currentDistance : (double?)null;
                                TxtNextBrake.Text = distToNext != null ? $"{distToNext.Value:F1} m" : "--- m";
                                // Gear display
                                TxtGear.Text = gearNow == -1 ? "R" : gearNow == 0 ? "N" : gearNow.ToString();
                                TxtGear.Foreground = new System.Windows.Media.SolidColorBrush(gearNow == -1
                                    ? System.Windows.Media.Color.FromRgb(255, 60, 60)
                                    : gearNow == 0
                                        ? System.Windows.Media.Color.FromRgb(255, 200, 0)
                                        : _theme.AccentColor);
                                // Speed
                                TxtSpeed.Text = $"{spd:F0} km/h";
                                // Entry speed: capture at warn1 trigger, show while in zone
                                if (distToNext != null && distToNext.Value <= warn1Distance)
                                    TxtEntrySpeed.Text = $"{lastEntrySpeed:F0} km/h";
                                else
                                    TxtEntrySpeed.Text = "—";
                                // Trail
                                trailData.Enqueue((throttle, brake));
                                while (trailData.Count > TrailLength) trailData.Dequeue();
                                RedrawTrail();
                                // RPM bar
                                RedrawRpmBar(rpmPct);
                                _overlay?.Update(distToNext, warn1Distance, warn2Distance);
                            });

                            telAcc.SafeMemoryMappedViewHandle.ReleasePointer();
                            scoAcc.SafeMemoryMappedViewHandle.ReleasePointer();
                        }
                    }
                }
                catch { telMmf = null; scoMmf = null; }
                Thread.Sleep(16);
            }
        }

        private void RedrawTrail()
        {
            double w = TrailCanvas.ActualWidth;
            double h = TrailCanvas.ActualHeight;
            if (w <= 0 || h <= 0 || trailData.Count < 2) return;

            var data = trailData.ToArray();
            int n = data.Length;
            double step = w / (TrailLength - 1);

            var tPoints = new System.Windows.Media.PointCollection(n);
            var bPoints = new System.Windows.Media.PointCollection(n);
            int offset = TrailLength - n;
            for (int i = 0; i < n; i++)
            {
                double x = (offset + i) * step;
                tPoints.Add(new System.Windows.Point(x, h - data[i].throttle * h));
                bPoints.Add(new System.Windows.Point(x, h - data[i].brake * h));
            }
            TrailThrottle.Points = tPoints;
            TrailBrake.Points = bPoints;
        }

        private void RedrawRpmBar(double rpmPct)
        {
            if (_theme == null) return;
            double w = RpmCanvas.ActualWidth;
            if (w <= 0) return;
            double fillWidth = w * Math.Clamp(rpmPct, 0, 1);
            RpmFill.Width = fillWidth;
            System.Windows.Media.Color col;
            if (rpmPct < 0.7)
                col = _theme.RpmLow;
            else if (rpmPct < shiftBeeperPercentage - 0.03)
                col = LerpRpmColor(rpmPct, 0.7, shiftBeeperPercentage - 0.03, _theme.RpmLow, _theme.RpmMid);
            else
                col = LerpRpmColor(rpmPct, shiftBeeperPercentage - 0.03, 1.0, _theme.RpmMid, _theme.RpmHigh);
            RpmFill.Background = new System.Windows.Media.SolidColorBrush(col);
            System.Windows.Controls.Canvas.SetLeft(RpmMarker, w * shiftBeeperPercentage - 1);
        }

        private static System.Windows.Media.Color LerpRpmColor(double v, double lo, double hi,
            System.Windows.Media.Color a, System.Windows.Media.Color b)
        {
            double t = Math.Clamp((v - lo) / (hi - lo), 0, 1);
            return System.Windows.Media.Color.FromRgb(
                (byte)(a.R + (b.R - a.R) * t),
                (byte)(a.G + (b.G - a.G) * t),
                (byte)(a.B + (b.B - a.B) * t));
        }

        private void CheckBeeperLogic(double dist)
        {
            foreach (var bp in BrakingPoints)
            {
                double point = bp.Distance;
                if (!beepStatus.ContainsKey(point)) beepStatus[point] = new bool[3];

                if (dist >= point - warn1Distance && dist < point - (warn1Distance - 10) && !beepStatus[point][0])
                { string p = brakePreset; lastBrakeBeepTime = DateTime.Now; lastEntrySpeed = currentSpeed; Task.Run(() => PlayBrakeBeep(p, 0)); beepStatus[point][0] = true; }

                if (dist >= point - warn2Distance && dist < point - (warn2Distance - 10) && !beepStatus[point][1])
                { string p = brakePreset; lastBrakeBeepTime = DateTime.Now; Task.Run(() => PlayBrakeBeep(p, 1)); beepStatus[point][1] = true; }

                if (dist >= point - 5 && dist < point + 5 && !beepStatus[point][2])
                { string p = brakePreset; lastBrakeBeepTime = DateTime.Now; Task.Run(() => PlayBrakeBeep(p, 2)); beepStatus[point][2] = true; }
            }
        }

        private void PlayBrakeBeep(string preset, int stage)
        {
            switch (preset)
            {
                case "Earcon":
                    // Brain-optimized melodic earcons — each stage has a unique musical gesture:
                    // Stage 0: single E5 — "heads up"
                    // Stage 1: C5→G5 perfect fifth rising — "prepare, tension building"
                    // Stage 2: G5→E5→C5 descending major triad — brain reads as "go down / slow down"
                    if (stage == 0) { PlayTone(659, 150, 0.5); }
                    else if (stage == 1) { PlayTone(523, 110, 0.55); Thread.Sleep(125); PlayTone(784, 130, 0.6); }
                    else { PlayTone(784, 90, 0.65); Thread.Sleep(105); PlayTone(659, 90, 0.65); Thread.Sleep(105); PlayTone(523, 180, 0.65); }
                    break;
                case "GT":
                    // F1-style countable pips — 1 pip / 2 pips / 3 pips, zero ambiguity
                    if (stage == 0) { PlayTone(700, 85, 0.55); }
                    else if (stage == 1) { PlayTone(850, 75, 0.6); Thread.Sleep(90); PlayTone(850, 75, 0.6); }
                    else { PlayTone(1000, 65, 0.65); Thread.Sleep(75); PlayTone(1000, 65, 0.65); Thread.Sleep(75); PlayTone(1000, 65, 0.65); }
                    break;
                case "Classic":
                    if (stage == 0) { PlayTone(600, 100, 0.6); }
                    else if (stage == 1) { PlayTone(800, 100, 0.6); Thread.Sleep(120); PlayTone(800, 100, 0.6); }
                    else { PlayTone(1000, 100, 0.65); Thread.Sleep(120); PlayTone(1000, 100, 0.65); Thread.Sleep(120); PlayTone(1000, 100, 0.65); }
                    break;
                case "Radar":
                    if (stage == 0) { PlayTone(1400, 50, 0.4); }
                    else if (stage == 1) { PlayTone(1400, 50, 0.45); Thread.Sleep(80); PlayTone(1400, 50, 0.45); }
                    else { PlayTone(1600, 50, 0.5); Thread.Sleep(70); PlayTone(1600, 50, 0.5); Thread.Sleep(70); PlayTone(1600, 50, 0.5); }
                    break;
                case "Alert":
                    if (stage == 0) { PlayTone(350, 200, 0.55); }
                    else if (stage == 1) { PlayTone(450, 150, 0.6); Thread.Sleep(60); PlayTone(550, 150, 0.6); }
                    else { PlayTone(700, 350, 0.7); }
                    break;
                default: // Chime
                    if (stage == 0) { PlayTone(520, 120, 0.55); }
                    else if (stage == 1) { PlayTone(660, 120, 0.6); Thread.Sleep(140); PlayTone(880, 120, 0.6); }
                    else { PlayTone(880, 100, 0.65); Thread.Sleep(110); PlayTone(1100, 100, 0.65); Thread.Sleep(110); PlayTone(1320, 180, 0.65); }
                    break;
            }
        }

        private void CheckShiftLogic(double currentRpm, double maxRpm)
        {
            if (maxRpm <= 0) return;
            double shiftPoint = maxRpm * shiftBeeperPercentage;
            if (currentRpm >= shiftPoint && !shiftBeepPlayed)
            {
                shiftBeepPlayed = true;
                if ((DateTime.Now - lastBrakeBeepTime).TotalMilliseconds > 600)
                { string p = shiftPreset; Task.Run(() => PlayShiftBeep(p)); }
            }
            else if (currentRpm < shiftPoint - 200) { shiftBeepPlayed = false; }
        }

        private void PlayShiftBeep(string preset)
        {
            switch (preset)
            {
                case "Bell":
                    // Inharmonic overtones create a bell character instantly distinguishable from any brake sound
                    PlayBell(850, 300, 0.5);
                    break;
                case "Ding":
                    // Bright triangle-like strike — high, short, unmistakable
                    PlayBell(2200, 180, 0.4);
                    break;
                case "Single":
                    PlayTone(1600, 80, 0.5);
                    break;
                case "Triple":
                    PlayTone(1200, 50, 0.4); Thread.Sleep(65); PlayTone(1500, 50, 0.4); Thread.Sleep(65); PlayTone(1800, 50, 0.4);
                    break;
                case "Buzz":
                    PlayTone(180, 100, 0.6);
                    break;
                default: // Double
                    PlayTone(1400, 60, 0.45); Thread.Sleep(70); PlayTone(1800, 60, 0.45);
                    break;
            }
        }

        // Bell with inharmonic overtones + exponential decay — sounds nothing like a pure sine
        private void PlayBell(double frequency, int durationMs, double volume)
        {
            int sampleRate = 44100;
            int samples = (int)((sampleRate * durationMs) / 1000.0);
            int fadeIn = Math.Min(300, samples / 8);
            short[] waveData = new short[samples];
            for (int i = 0; i < samples; i++)
            {
                double t = (double)i / sampleRate;
                // Fundamental + inharmonic partials (ratios from struck metal acoustics)
                double wave = Math.Sin(2 * Math.PI * frequency       * t) * 0.55
                            + Math.Sin(2 * Math.PI * frequency * 2.756 * t) * 0.25
                            + Math.Sin(2 * Math.PI * frequency * 5.40  * t) * 0.13
                            + Math.Sin(2 * Math.PI * frequency * 8.93  * t) * 0.07;
                // Exponential decay — fast initial drop then long tail
                double env = Math.Exp(-5.5 * t / (durationMs / 1000.0));
                if (i < fadeIn) env *= (double)i / fadeIn;
                waveData[i] = (short)(wave * short.MaxValue * volume * env);
            }
            byte[] buffer = new byte[44 + waveData.Length * 2];
            using (MemoryStream mStream = new MemoryStream(buffer))
            using (BinaryWriter writer = new BinaryWriter(mStream))
            {
                writer.Write("RIFF".ToCharArray()); writer.Write(36 + waveData.Length * 2);
                writer.Write("WAVE".ToCharArray()); writer.Write("fmt ".ToCharArray());
                writer.Write(16); writer.Write((short)1); writer.Write((short)1);
                writer.Write(sampleRate); writer.Write(sampleRate * 2); writer.Write((short)2);
                writer.Write((short)16); writer.Write("data".ToCharArray()); writer.Write(waveData.Length * 2);
                foreach (var s in waveData) writer.Write(s);
                mStream.Position = 0;
                using (SoundPlayer player = new SoundPlayer(mStream)) { player.PlaySync(); }
            }
        }

        private void PlayTone(double frequency, int durationMs, double volume)
        {
            int sampleRate = 44100;
            int samples = (int)((sampleRate * durationMs) / 1000.0);
            int fadeIn = Math.Min(800, samples / 4);
            int fadeOut = Math.Min(1200, samples / 3);
            short[] waveData = new short[samples];
            for (int i = 0; i < samples; i++)
            {
                double t = frequency * ((double)i / sampleRate) * 2.0 * Math.PI;
                double env = 1.0;
                if (i < fadeIn) env = (double)i / fadeIn;
                else if (samples - i < fadeOut) env = (double)(samples - i) / fadeOut;
                waveData[i] = (short)(Math.Sin(t) * short.MaxValue * volume * env);
            }
            byte[] buffer = new byte[44 + waveData.Length * 2];
            using (MemoryStream mStream = new MemoryStream(buffer))
            using (BinaryWriter writer = new BinaryWriter(mStream))
            {
                writer.Write("RIFF".ToCharArray()); writer.Write(36 + waveData.Length * 2);
                writer.Write("WAVE".ToCharArray()); writer.Write("fmt ".ToCharArray());
                writer.Write(16); writer.Write((short)1); writer.Write((short)1);
                writer.Write(sampleRate); writer.Write(sampleRate * 2); writer.Write((short)2);
                writer.Write((short)16); writer.Write("data".ToCharArray()); writer.Write(waveData.Length * 2);
                foreach (var s in waveData) writer.Write(s);
                mStream.Position = 0;
                using (SoundPlayer player = new SoundPlayer(mStream)) { player.PlaySync(); }
            }
        }

        private void BtnImportFile_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog();
            openFileDialog.Filter = "JSON Dateien (*.json)|*.json";
            openFileDialog.InitialDirectory = AppDomain.CurrentDomain.BaseDirectory;

            if (openFileDialog.ShowDialog() == true)
            {
                try
                {
                    string json = File.ReadAllText(openFileDialog.FileName);
                    var importedPoints = JsonSerializer.Deserialize<List<BrakingPoint>>(json);

                    if (importedPoints != null)
                    {
                        BrakingPoints.Clear();
                        foreach (var p in importedPoints) BrakingPoints.Add(p);
                        SortAndRefreshList();
                        SaveLocalPoints();
                        MessageBox.Show("Braking points imported successfully!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error importing file: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void ToggleBrake_Changed(object sender, RoutedEventArgs e) { if (ToggleBrake != null) isBrakeBeepEnabled = ToggleBrake.IsChecked ?? false; }
        private void ToggleShift_Changed(object sender, RoutedEventArgs e) { if (ToggleShift != null) isShiftBeepEnabled = ToggleShift.IsChecked ?? false; }
        private void SliderWarn1_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { if (TxtWarn1 != null) { warn1Distance = e.NewValue; TxtWarn1.Text = $"{warn1Distance:F0}"; ResetBeepStatus(); } }
        private void SliderWarn2_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { if (TxtWarn2 != null) { warn2Distance = e.NewValue; TxtWarn2.Text = $"{warn2Distance:F0}"; ResetBeepStatus(); } }
        private void SliderShift_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { if (TxtShift != null) { shiftBeeperPercentage = e.NewValue / 100.0; TxtShift.Text = $"{e.NewValue:F1}%"; shiftBeepPlayed = false; } }
        private void SliderBgOpacity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { if (BackgroundLayer != null && TxtBgOpacity != null) { BackgroundLayer.Opacity = e.NewValue / 100.0; TxtBgOpacity.Text = $"{e.NewValue:F0}%"; } }
        private void VtTrack_Click(object sender, RoutedEventArgs e)    => PanelTrack.Visibility      = VtTrack.IsChecked   == true ? Visibility.Visible : Visibility.Collapsed;
        private void VtGear_Click(object sender, RoutedEventArgs e)     => PanelGearSpeed.Visibility  = VtGear.IsChecked    == true ? Visibility.Visible : Visibility.Collapsed;
        private void VtRpm_Click(object sender, RoutedEventArgs e)      => RpmCanvas.Visibility       = VtRpm.IsChecked     == true ? Visibility.Visible : Visibility.Collapsed;
        private void VtBspd_Click(object sender, RoutedEventArgs e)     => PanelEntrySpeed.Visibility = VtBspd.IsChecked    == true ? Visibility.Visible : Visibility.Collapsed;
        private void VtNext_Click(object sender, RoutedEventArgs e)     => PanelNextBrake.Visibility = VtNext.IsChecked    == true ? Visibility.Visible : Visibility.Collapsed;
        private void VtDist_Click(object sender, RoutedEventArgs e)    => PanelLapDist.Visibility   = VtDist.IsChecked    == true ? Visibility.Visible : Visibility.Collapsed;
        private void VtTrail_Click(object sender, RoutedEventArgs e)   => TrailCanvas.Visibility    = VtTrail.IsChecked   == true ? Visibility.Visible : Visibility.Collapsed;
        private void VtOverlay_Click(object sender, RoutedEventArgs e)
        {
            if (VtOverlay.IsChecked == true)
            {
                if (_overlay == null) { _overlay = new BrakeOverlay(); _overlay.Closed += (s, _) => { _overlay = null; VtOverlay.IsChecked = false; }; }
                _overlay.Show();
            }
            else { _overlay?.Hide(); }
        }

        private void ComboBrakePreset_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (ComboBrakePreset.SelectedItem is ComboBoxItem item) brakePreset = item.Content.ToString() ?? "Chime"; }
        private void ComboShiftPreset_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (ComboShiftPreset.SelectedItem is ComboBoxItem item) shiftPreset = item.Content.ToString() ?? "Double"; }
        private void ResetBeepStatus() => beepStatus.Clear();

        private void BtnAddPoint_Click(object sender, RoutedEventArgs e)
        {
            double rounded = Math.Round(currentDistance, 1);
            if (!BrakingPoints.Any(b => b.Distance == rounded))
            {
                BrakingPoints.Add(new BrakingPoint { Distance = rounded, Description = "Custom Point" });
                SortAndRefreshList(); SaveLocalPoints();
            }
        }

        private void BtnDeletePoint_Click(object sender, RoutedEventArgs e) { if (sender is Button btn && btn.Tag is BrakingPoint bp) { BrakingPoints.Remove(bp); SaveLocalPoints(); } }
        private void BtnMinus_Click(object sender, RoutedEventArgs e) { if (sender is Button btn && btn.Tag is BrakingPoint bp) { bp.Distance = Math.Round(bp.Distance - 1.0, 1); SortAndRefreshList(); SaveLocalPoints(); } }
        private void BtnPlus_Click(object sender, RoutedEventArgs e) { if (sender is Button btn && btn.Tag is BrakingPoint bp) { bp.Distance = Math.Round(bp.Distance + 1.0, 1); SortAndRefreshList(); SaveLocalPoints(); } }

        private void SortAndRefreshList()
        {
            var sorted = BrakingPoints.OrderBy(x => x.Distance).ToList();
            BrakingPoints.Clear();
            foreach (var item in sorted) BrakingPoints.Add(item);
            ResetBeepStatus();
        }

        private void BtnExport_Click(object sender, RoutedEventArgs e) => SaveLocalPoints();

        private void SaveLocalPoints()
        {
            if (string.IsNullOrEmpty(currentTrack)) return;
            string json = JsonSerializer.Serialize(BrakingPoints, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText($"{currentTrack}.json", json);
            SaveNotes();
        }

        private void LoadLocalPoints()
        {
            string path = $"{currentTrack}.json";
            if (File.Exists(path))
            {
                try
                {
                    var loaded = JsonSerializer.Deserialize<List<BrakingPoint>>(File.ReadAllText(path));
                    if (loaded != null) { BrakingPoints.Clear(); foreach (var p in loaded.OrderBy(x => x.Distance)) BrakingPoints.Add(p); ResetBeepStatus(); }
                }
                catch { }
            }
            LoadNotes();
        }

        // ── Pace Notes persistence ────────────────────────────────────────────

        private void SaveNotes()
        {
            if (string.IsNullOrEmpty(currentTrack)) return;
            File.WriteAllText($"{currentTrack}_notes.json",
                JsonSerializer.Serialize(PaceNotes.ToList(), new JsonSerializerOptions { WriteIndented = true }));
        }

        private void LoadNotes()
        {
            string path = $"{currentTrack}_notes.json";
            PaceNotes.Clear();
            _notesFiredThisLap.Clear();
            if (!File.Exists(path)) return;
            try
            {
                var loaded = JsonSerializer.Deserialize<List<PaceNote>>(File.ReadAllText(path));
                if (loaded != null)
                    foreach (var n in loaded.OrderBy(x => x.Distance)) PaceNotes.Add(n);
            }
            catch { }
        }

        // ── Pace Notes — symbol helpers ───────────────────────────────────────

        private static readonly Dictionary<string, string> SymbolGlyphs = new()
        {
            ["L1"] = "← 1",  ["L2"] = "← 2",  ["L3"] = "← 3",
            ["L4"] = "← 4",  ["L5"] = "← 5",  ["L6"] = "← 6",
            ["R1"] = "→ 1",  ["R2"] = "→ 2",  ["R3"] = "→ 3",
            ["R4"] = "→ 4",  ["R5"] = "→ 5",  ["R6"] = "→ 6",
            ["HAIRPIN L"] = "↩ HAIRPIN", ["HAIRPIN R"] = "↪ HAIRPIN",
            ["CREST"]   = "⌒ CREST",  ["JUMP"]    = "⤴ JUMP",
            ["CAUTION"] = "⚠ CAUTION",["FLAT"]    = "═ FLAT",
            ["CHICANE"] = "⇄ CHICANE",["NARROW"]  = ">< NARROW",
            ["BRIDGE"]  = "│ BRIDGE", ["NOTE"]    = "📝 NOTE",
        };

        private static string GetGlyph(string code) =>
            SymbolGlyphs.TryGetValue(code, out var g) ? g : code;

        // ── Pace Notes — trigger ─────────────────────────────────────────────

        private void CheckPaceNotes(double dist)
        {
            // ToArray snapshot — called from background thread, collection may change on UI thread
            PaceNote[] snapshot;
            try { snapshot = PaceNotes.ToArray(); } catch { return; }

            foreach (var note in snapshot)
            {
                if (_notesFiredThisLap.Contains(note.Distance)) continue;
                // Trigger when within 5 m before the marked point
                if (dist >= note.Distance - 5 && dist < note.Distance + 80)
                {
                    _notesFiredThisLap.Add(note.Distance);
                    var captured = note;
                    Dispatcher.BeginInvoke(new Action(() => TriggerNoteDisplay(captured)));
                }
            }
        }

        private async void TriggerNoteDisplay(PaceNote note)
        {
            // Cancel any previous auto-hide timer
            _noteHideCts?.Cancel();
            _noteHideCts = new CancellationTokenSource();
            var token = _noteHideCts.Token;

            _noteOverlay ??= new PaceNoteOverlay();
            _noteOverlay.ShowNote(GetGlyph(note.Symbol), note.Text);

            // Speak on background thread
            if (_noteTtsEnabled && !string.IsNullOrWhiteSpace(note.Text))
            {
                string spokenText = $"{note.Symbol.ToLower().Replace("_", " ")} {note.Text}";
                _ = Task.Run(() => SpeakText(spokenText));
            }

            try
            {
                await Task.Delay((int)(_noteDisplaySeconds * 1000), token);
                _noteOverlay?.HideNote();
            }
            catch (TaskCanceledException) { /* another note took over */ }
        }

        private void SpeakText(string text)
        {
            try
            {
                _tts ??= new SpeechSynthesizer();
                _tts.SpeakAsync(text);
            }
            catch { }
        }

        // ── Pace Notes — sort helper ──────────────────────────────────────────

        private void SortNotes()
        {
            var sorted = PaceNotes.OrderBy(n => n.Distance).ToList();
            PaceNotes.Clear();
            foreach (var n in sorted) PaceNotes.Add(n);
            _notesFiredThisLap.Clear();
        }

        private void Window_MouseDown(object sender, MouseButtonEventArgs e) { if (e.ChangedButton == MouseButton.Left) DragMove(); }
        private void ResizeThumb_DragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e) { this.Width = Math.Max(this.MinWidth, this.Width + e.HorizontalChange); this.Height = Math.Max(this.MinHeight, this.Height + e.VerticalChange); }
        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            SaveLocalPoints();
            _overlay?.Close();
            _noteOverlay?.Close();
            try { _tts?.Dispose(); } catch { }
            Environment.Exit(0);
        }

        private void BtnToggleSettings_Click(object sender, RoutedEventArgs e)
        {
            if (SettingsContainer.Visibility == Visibility.Visible)
            {
                expandedHeight = this.Height;
                SettingsContainer.Visibility = Visibility.Collapsed;
                BtnToggleSettings.Content = "▶ SETTINGS";
                this.MinHeight = 100; this.Height = 100;
            }
            else
            {
                SettingsContainer.Visibility = Visibility.Visible;
                BtnToggleSettings.Content = "▼ SETTINGS";
                this.Height = expandedHeight; this.MinHeight = 160;
            }
        }

        private void BtnToggleSound_Click(object sender, RoutedEventArgs e)
        {
            if (SoundContainer.Visibility == Visibility.Visible)
            {
                SoundContainer.Visibility = Visibility.Collapsed;
                BtnToggleSound.Content = "▶ SOUND";
            }
            else
            {
                SoundContainer.Visibility = Visibility.Visible;
                BtnToggleSound.Content = "▼ SOUND";
            }
        }

        // ── Notes UI handlers ─────────────────────────────────────────────────

        private void BtnToggleNotes_Click(object sender, RoutedEventArgs e)
        {
            bool open = NotesContainer.Visibility == Visibility.Visible;
            NotesContainer.Visibility = open ? Visibility.Collapsed : Visibility.Visible;
            BtnToggleNotes.Content    = open ? "▶ NOTES" : "▼ NOTES";
        }

        private void BtnAddNote_Click(object sender, RoutedEventArgs e)
        {
            _editingNote  = null;
            _editorDist   = Math.Round(currentDistance, 1);
            TxtNoteText.Text = "";
            ComboNoteSymbol.SelectedIndex = 8; // default R3
            TxtEditorDist.Text = $"{_editorDist:F1} m";
            NoteEditor.Visibility = Visibility.Visible;
            ListNotes.SelectedItem = null;
        }

        private void ListNotes_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ListNotes.SelectedItem is not PaceNote note) return;
            _editingNote = note;
            _editorDist  = note.Distance;
            TxtNoteText.Text = note.Text;
            // Select matching ComboBox item
            foreach (ComboBoxItem item in ComboNoteSymbol.Items)
                if (item.Content?.ToString() == note.Symbol) { ComboNoteSymbol.SelectedItem = item; break; }
            TxtEditorDist.Text = $"{_editorDist:F1} m";
            NoteEditor.Visibility = Visibility.Visible;
        }

        private void BtnNoteDistMinus_Click(object sender, RoutedEventArgs e)
        {
            _editorDist = Math.Round(_editorDist - 1.0, 1);
            TxtEditorDist.Text = $"{_editorDist:F1} m";
        }

        private void BtnNoteDistPlus_Click(object sender, RoutedEventArgs e)
        {
            _editorDist = Math.Round(_editorDist + 1.0, 1);
            TxtEditorDist.Text = $"{_editorDist:F1} m";
        }

        private void BtnSaveNote_Click(object sender, RoutedEventArgs e)
        {
            string symbol = ComboNoteSymbol.SelectedItem is ComboBoxItem ci
                ? ci.Content?.ToString() ?? "NOTE"
                : "NOTE";
            string text = TxtNoteText.Text.Trim();

            if (_editingNote == null)
            {
                PaceNotes.Add(new PaceNote { Distance = _editorDist, Symbol = symbol, Text = text });
            }
            else
            {
                int idx = PaceNotes.IndexOf(_editingNote);
                if (idx >= 0)
                {
                    PaceNotes.RemoveAt(idx);
                    _editingNote.Distance = _editorDist;
                    _editingNote.Symbol   = symbol;
                    _editingNote.Text     = text;
                    PaceNotes.Insert(idx, _editingNote);
                }
            }
            SortNotes();
            SaveNotes();
            NoteEditor.Visibility = Visibility.Collapsed;
            ListNotes.SelectedItem = null;
        }

        private void BtnCancelNote_Click(object sender, RoutedEventArgs e)
        {
            NoteEditor.Visibility = Visibility.Collapsed;
            ListNotes.SelectedItem = null;
        }

        private void BtnDeleteNote_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is PaceNote note)
            {
                PaceNotes.Remove(note);
                NoteEditor.Visibility = Visibility.Collapsed;
                SaveNotes();
            }
        }

        private void SliderNoteDuration_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (TxtNoteDuration != null)
            {
                _noteDisplaySeconds    = e.NewValue;
                TxtNoteDuration.Text   = $"{_noteDisplaySeconds:F1} s";
            }
        }

        private void ToggleNoteTts_Changed(object sender, RoutedEventArgs e)
            => _noteTtsEnabled = ToggleNoteTts.IsChecked ?? false;

        private void BtnTestNote_Click(object sender, RoutedEventArgs e)
        {
            // Preview the currently edited note, or first note in list
            var preview = _editingNote ?? PaceNotes.FirstOrDefault();
            if (preview == null)
            {
                // Show a demo
                _noteOverlay ??= new PaceNoteOverlay();
                _noteOverlay.ShowNote("← 3", "medium left tightens");
                return;
            }
            _noteOverlay ??= new PaceNoteOverlay();
            _noteOverlay.ShowNote(GetGlyph(preview.Symbol), preview.Text);
            if (_noteTtsEnabled && !string.IsNullOrWhiteSpace(preview.Text))
                _ = Task.Run(() => SpeakText($"{preview.Symbol} {preview.Text}"));
        }

        private void InitThemes()
        {
            static System.Windows.Media.DrawingBrush MakeCarbon()
            {
                var dg = new System.Windows.Media.DrawingGroup();
                dg.Children.Add(new System.Windows.Media.GeometryDrawing(
                    new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x08, 0x08, 0x08)),
                    null, new System.Windows.Media.RectangleGeometry(new Rect(0, 0, 8, 8))));
                var gg = new System.Windows.Media.GeometryGroup();
                gg.Children.Add(new System.Windows.Media.RectangleGeometry(new Rect(0, 0, 4, 4)));
                gg.Children.Add(new System.Windows.Media.RectangleGeometry(new Rect(4, 4, 4, 4)));
                dg.Children.Add(new System.Windows.Media.GeometryDrawing(
                    new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x15, 0x15, 0x15)),
                    null, gg));
                return new System.Windows.Media.DrawingBrush(dg)
                {
                    TileMode = System.Windows.Media.TileMode.Tile,
                    Viewport = new Rect(0, 0, 8, 8),
                    ViewportUnits = System.Windows.Media.BrushMappingMode.Absolute
                };
            }

            _allThemes = new[]
            {
                // ── 1. Carbon (default) ─────────────────────────────────────
                new ThemeConfig {
                    Name = "Carbon",
                    MakeBg         = MakeCarbon,
                    BorderColor    = C(0x33,0xFF,0xFF,0xFF),
                    AccentColor    = C(0xFF,0x00,0xFF,0xCC),
                    WarnColor      = C(0xFF,0xFF,0x8C,0x00),
                    TextColor      = C(0xFF,0xDD,0xDD,0xDD),
                    SubTextColor   = C(0xFF,0x88,0x88,0x88),
                    TrailGasColor  = C(0xCC,0x00,0xD0,0x00),
                    TrailBrakeColor= C(0xCC,0xD0,0x00,0x00),
                    TrailBgColor   = C(0x0C,0xFF,0xFF,0xFF),
                    RpmLow         = C(0xFF,0x00,0xA0,0x50),
                    RpmMid         = C(0xFF,0xFF,0xA0,0x00),
                    RpmHigh        = C(0xFF,0xFF,0x1E,0x00),
                    RpmMarkerColor = C(0x88,0xFF,0xFF,0xFF),
                    RpmBarHeight=7, TrailHeight=50, BrakeFontSize=30, GearFontSize=32 },

                // ── 2. Neon Drift ────────────────────────────────────────────
                new ThemeConfig {
                    Name = "Neon Drift",
                    MakeBg         = () => new System.Windows.Media.SolidColorBrush(C(0xFF,0x05,0x05,0x10)),
                    BorderColor    = C(0x99,0xFF,0x00,0xFF),
                    AccentColor    = C(0xFF,0xFF,0x00,0xFF),
                    WarnColor      = C(0xFF,0x00,0xFF,0xFF),
                    TextColor      = C(0xFF,0xEE,0xEE,0xFF),
                    SubTextColor   = C(0xFF,0x88,0x88,0x99),
                    TrailGasColor  = C(0xCC,0x00,0xFF,0xFF),
                    TrailBrakeColor= C(0xCC,0xFF,0x00,0xFF),
                    TrailBgColor   = C(0x0A,0x00,0x00,0x40),
                    RpmLow         = C(0xFF,0x00,0x80,0xFF),
                    RpmMid         = C(0xFF,0x88,0x00,0xFF),
                    RpmHigh        = C(0xFF,0xFF,0x00,0xFF),
                    RpmMarkerColor = C(0xFF,0xFF,0xFF,0x00),
                    RpmBarHeight=12, TrailHeight=50, BrakeFontSize=28, GearFontSize=36 },

                // ── 3. Anime Girl ♡ ──────────────────────────────────────────
                new ThemeConfig {
                    Name = "Anime Girl ♡",
                    MakeBg = () => {
                        var lb = new System.Windows.Media.LinearGradientBrush();
                        lb.StartPoint = new System.Windows.Point(0, 0);
                        lb.EndPoint   = new System.Windows.Point(1, 1);
                        lb.GradientStops.Add(new System.Windows.Media.GradientStop(C(0xFF,0x1E,0x06,0x14), 0));
                        lb.GradientStops.Add(new System.Windows.Media.GradientStop(C(0xFF,0x0A,0x02,0x0A), 1));
                        return lb; },
                    BorderColor    = C(0x99,0xFF,0x69,0xB4),
                    AccentColor    = C(0xFF,0xFF,0x69,0xB4),
                    WarnColor      = C(0xFF,0xFF,0xD7,0x00),
                    TextColor      = C(0xFF,0xFF,0xE4,0xF0),
                    SubTextColor   = C(0xFF,0xFF,0x99,0xCC),
                    TrailGasColor  = C(0xCC,0xFF,0x69,0xB4),
                    TrailBrakeColor= C(0xCC,0xFF,0x14,0x93),
                    TrailBgColor   = C(0x0A,0xFF,0x69,0xB4),
                    RpmLow         = C(0xFF,0xFF,0x69,0xB4),
                    RpmMid         = C(0xFF,0xFF,0x9F,0xC6),
                    RpmHigh        = C(0xFF,0xFF,0x14,0x93),
                    RpmMarkerColor = C(0xFF,0xFF,0xD7,0x00),
                    RpmBarHeight=14, TrailHeight=35, BrakeFontSize=26, GearFontSize=30 },

                // ── 4. Ferrari ───────────────────────────────────────────────
                new ThemeConfig {
                    Name = "Ferrari",
                    MakeBg         = () => new System.Windows.Media.SolidColorBrush(C(0xFF,0x10,0x00,0x00)),
                    BorderColor    = C(0x88,0xFF,0x20,0x20),
                    AccentColor    = C(0xFF,0xFF,0x30,0x30),
                    WarnColor      = C(0xFF,0xFF,0xD7,0x00),
                    TextColor      = C(0xFF,0xFF,0xFF,0xFF),
                    SubTextColor   = C(0xFF,0xAA,0xAA,0xAA),
                    TrailGasColor  = C(0xCC,0x00,0xE0,0x60),
                    TrailBrakeColor= C(0xCC,0xFF,0x20,0x20),
                    TrailBgColor   = C(0x0C,0xFF,0x20,0x20),
                    RpmLow         = C(0xFF,0x00,0xCC,0x40),
                    RpmMid         = C(0xFF,0xFF,0x88,0x00),
                    RpmHigh        = C(0xFF,0xFF,0x20,0x20),
                    RpmMarkerColor = C(0xFF,0xFF,0xD7,0x00),
                    RpmBarHeight=7, TrailHeight=55, BrakeFontSize=32, GearFontSize=36 },

                // ── 5. Night Sky ─────────────────────────────────────────────
                new ThemeConfig {
                    Name = "Night Sky",
                    MakeBg         = () => new System.Windows.Media.SolidColorBrush(C(0xFF,0x05,0x08,0x14)),
                    BorderColor    = C(0x66,0x44,0x88,0xFF),
                    AccentColor    = C(0xFF,0x44,0x88,0xFF),
                    WarnColor      = C(0xFF,0x88,0xDD,0xFF),
                    TextColor      = C(0xFF,0xCC,0xDD,0xEE),
                    SubTextColor   = C(0xFF,0x55,0x66,0x88),
                    TrailGasColor  = C(0xCC,0x44,0xAA,0xFF),
                    TrailBrakeColor= C(0xCC,0xFF,0x44,0x44),
                    TrailBgColor   = C(0x0C,0x44,0x88,0xFF),
                    RpmLow         = C(0xFF,0x22,0x55,0xAA),
                    RpmMid         = C(0xFF,0x44,0x88,0xFF),
                    RpmHigh        = C(0xFF,0x88,0xCC,0xFF),
                    RpmMarkerColor = C(0x88,0xFF,0xFF,0xFF),
                    RpmBarHeight=5, TrailHeight=45, BrakeFontSize=28, GearFontSize=32 },
            };

            _theme = _allThemes[0];
        }

        private void ApplyTheme(ThemeConfig t)
        {
            _theme = t;
            BackgroundLayer.Background  = t.MakeBg();
            BackgroundLayer.BorderBrush = new System.Windows.Media.SolidColorBrush(t.BorderColor);
            // Text
            TxtTrack.Foreground      = new System.Windows.Media.SolidColorBrush(t.SubTextColor);
            TxtGear.Foreground       = new System.Windows.Media.SolidColorBrush(t.AccentColor);
            TxtGear.FontSize         = t.GearFontSize;
            TxtSpeed.Foreground      = new System.Windows.Media.SolidColorBrush(t.TextColor);
            TxtNextBrake.Foreground  = new System.Windows.Media.SolidColorBrush(t.WarnColor);
            TxtNextBrake.FontSize    = t.BrakeFontSize;
            TxtDistance.Foreground   = new System.Windows.Media.SolidColorBrush(t.AccentColor);
            TxtEntrySpeed.Foreground = new System.Windows.Media.SolidColorBrush(t.SubTextColor);
            // Trail
            TrailThrottle.Stroke   = new System.Windows.Media.SolidColorBrush(t.TrailGasColor);
            TrailBrake.Stroke      = new System.Windows.Media.SolidColorBrush(t.TrailBrakeColor);
            TrailCanvas.Background = new System.Windows.Media.SolidColorBrush(t.TrailBgColor);
            TrailCanvas.Height     = t.TrailHeight;
            // RPM bar
            RpmCanvas.Height       = t.RpmBarHeight;
            RpmBg.Height           = t.RpmBarHeight;
            RpmFill.Height         = t.RpmBarHeight;
            RpmMarker.Height       = t.RpmBarHeight;
            RpmMarker.Background   = new System.Windows.Media.SolidColorBrush(t.RpmMarkerColor);
            RedrawRpmBar(currentRpmPct);
        }

        private void ComboTheme_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            int idx = ComboTheme.SelectedIndex;
            if (_allThemes != null && idx >= 0 && idx < _allThemes.Length)
                ApplyTheme(_allThemes[idx]);
        }

        // ── UI Scale ─────────────────────────────────────────────────────────
        private double _uiScale = 1.0;

        private void BtnScalePlus_Click(object sender, RoutedEventArgs e)  => SetScale(_uiScale + 0.1);
        private void BtnScaleMinus_Click(object sender, RoutedEventArgs e) => SetScale(_uiScale - 0.1);

        private void SetScale(double newScale)
        {
            double old = _uiScale;
            _uiScale = Math.Clamp(Math.Round(newScale * 10) / 10.0, 0.5, 2.0);
            UiScale.ScaleX = _uiScale;
            UiScale.ScaleY = _uiScale;
            double ratio = _uiScale / old;
            Width          = Math.Max(MinWidth,  Width  * ratio);
            Height         = Math.Max(MinHeight, Height * ratio);
            expandedHeight *= ratio;
            TxtScale.Text  = $"{(int)Math.Round(_uiScale * 100)}%";
        }

        private void KoFi_Click(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
            e.Handled = true;
        }
    }
}
