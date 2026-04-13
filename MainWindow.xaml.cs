using rF2SMMonitor;
using rF2SMMonitor.rFactor2Data;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Runtime.InteropServices;
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
        private double warn2Distance = 50.0;
        private double shiftBeeperPercentage = 0.97;
        private bool shiftBeepPlayed = false;
        private double expandedHeight = 540.0;

        private volatile bool isBrakeBeepEnabled = true;
        private volatile bool isShiftBeepEnabled = true;
        private string brakePreset = "Chime";
        private string shiftPreset = "Double";

        public MainWindow()
        {
            InitializeComponent();
            ListPoints.ItemsSource = BrakingPoints;
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
                            }

                            if (isBrakeBeepEnabled) CheckBeeperLogic(currentDistance);
                            if (isShiftBeepEnabled) CheckShiftLogic(myTel.mEngineRPM, myTel.mEngineMaxRPM);

                            Dispatcher.Invoke(() => {
                                TxtDistance.Text = $"{currentDistance:F1} m";
                                var nextPoint = BrakingPoints.Where(p => p.Distance > currentDistance).OrderBy(p => p.Distance).FirstOrDefault();
                                TxtNextBrake.Text = nextPoint != null ? $"{(nextPoint.Distance - currentDistance):F1} m" : "--- m";
                                BarGas.Value = myTel.mUnfilteredThrottle;
                                BarBrake.Value = myTel.mUnfilteredBrake;
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

        private void CheckBeeperLogic(double dist)
        {
            foreach (var bp in BrakingPoints)
            {
                double point = bp.Distance;
                if (!beepStatus.ContainsKey(point)) beepStatus[point] = new bool[3];

                if (dist >= point - warn1Distance && dist < point - (warn1Distance - 10) && !beepStatus[point][0])
                { string p = brakePreset; Task.Run(() => PlayBrakeBeep(p, 0)); beepStatus[point][0] = true; }

                if (dist >= point - warn2Distance && dist < point - (warn2Distance - 10) && !beepStatus[point][1])
                { string p = brakePreset; Task.Run(() => PlayBrakeBeep(p, 1)); beepStatus[point][1] = true; }

                if (dist >= point - 5 && dist < point + 5 && !beepStatus[point][2])
                { string p = brakePreset; Task.Run(() => PlayBrakeBeep(p, 2)); beepStatus[point][2] = true; }
            }
        }

        private void PlayBrakeBeep(string preset, int stage)
        {
            switch (preset)
            {
                case "Classic":
                    // Simple single beeps at rising pitch
                    if (stage == 0) { PlayTone(600, 100, 0.6); }
                    else if (stage == 1) { PlayTone(800, 100, 0.6); Thread.Sleep(120); PlayTone(800, 100, 0.6); }
                    else { PlayTone(1000, 100, 0.65); Thread.Sleep(120); PlayTone(1000, 100, 0.65); Thread.Sleep(120); PlayTone(1000, 100, 0.65); }
                    break;
                case "Radar":
                    // Short sharp pings
                    if (stage == 0) { PlayTone(1400, 50, 0.4); }
                    else if (stage == 1) { PlayTone(1400, 50, 0.45); Thread.Sleep(80); PlayTone(1400, 50, 0.45); }
                    else { PlayTone(1600, 50, 0.5); Thread.Sleep(70); PlayTone(1600, 50, 0.5); Thread.Sleep(70); PlayTone(1600, 50, 0.5); }
                    break;
                case "Alert":
                    // Low urgent tones
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
            { string p = shiftPreset; Task.Run(() => PlayShiftBeep(p)); shiftBeepPlayed = true; }
            else if (currentRpm < shiftPoint - 200) { shiftBeepPlayed = false; }
        }

        private void PlayShiftBeep(string preset)
        {
            switch (preset)
            {
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
        }

        private void Window_MouseDown(object sender, MouseButtonEventArgs e) { if (e.ChangedButton == MouseButton.Left) DragMove(); }
        private void ResizeThumb_DragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e) { this.Width = Math.Max(this.MinWidth, this.Width + e.HorizontalChange); this.Height = Math.Max(this.MinHeight, this.Height + e.VerticalChange); }
        private void BtnClose_Click(object sender, RoutedEventArgs e) { SaveLocalPoints(); Environment.Exit(0); }

        private void BtnToggleSettings_Click(object sender, RoutedEventArgs e)
        {
            if (SettingsContainer.Visibility == Visibility.Visible)
            {
                expandedHeight = this.Height;
                SettingsContainer.Visibility = Visibility.Collapsed;
                BtnToggleSettings.Content = "▶ SHOW SETTINGS";
                this.MinHeight = 150; this.Height = 150;
            }
            else
            {
                SettingsContainer.Visibility = Visibility.Visible;
                BtnToggleSettings.Content = "▼ HIDE SETTINGS";
                this.Height = expandedHeight; this.MinHeight = 300;
            }
        }
    }
}
