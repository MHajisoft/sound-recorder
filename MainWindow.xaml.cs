using NAudio.Wave;
using NAudio.Lame;
using Newtonsoft.Json;
using System.Configuration;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using File = System.IO.File;

namespace AudioRecorder
{
    // Converts a numeric level (0-100) to a Brush: Green (low), Yellow (mid), Red (high)
    public class LevelToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            try
            {
                double v = 0;
                if (value is double d) v = d;
                else if (value is float f) v = f;
                else if (value is int i) v = i;
                // Default thresholds: <33 green, <66 yellow, else red
                if (v < 60) return Brushes.Green;
                if (v < 85) return Brushes.Yellow;
                return Brushes.Red;
            }
            catch
            {
                return Brushes.Green;
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    public partial class MainWindow : Window
    {
        private WaveInEvent waveIn;
        private LameMP3FileWriter mp3Writer;
        private string outputFilePath;
        private List<string> categories = new List<string>();
        private List<string> singers = new List<string>();
        private readonly string dataFilePath = "appData.json";
        private  string savePath;
        private  bool autoStartRecording;
        private  bool closeAfterSave;

        public MainWindow()
        {
            InitializeComponent();
            LoadAppSettings();
            LoadAudioSources();
            LoadStoredData();
            UpdateFileName();

            if (autoStartRecording)
            {
                StartRecording();
            }
        }

        private void LoadAppSettings()
        {
            // Read save path, auto-start, and close-after-save settings from appsettings.json
            savePath = ConfigurationManager.AppSettings["SavePath"];
            if (string.IsNullOrEmpty(savePath))
            {
                savePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "Recordings");
            }
            // Create the save path directory if it doesn't exist
            Directory.CreateDirectory(savePath);

            autoStartRecording = bool.TryParse(ConfigurationManager.AppSettings["AutoStartRecording"], out bool autoStartResult) && autoStartResult;
            closeAfterSave = bool.TryParse(ConfigurationManager.AppSettings["CloseAfterSave"], out bool closeResult) && closeResult;
        }

        private void LoadAudioSources()
        {
            for (int i = 0; i < WaveIn.DeviceCount; i++)
            {
                var capabilities = WaveIn.GetCapabilities(i);
                SourceComboBox.Items.Add(capabilities.ProductName);
            }
            if (SourceComboBox.Items.Count > 0)
                SourceComboBox.SelectedIndex = 0;
            else
            {
                MessageBox.Show("هیچ دستگاه ورودی صوتی یافت نشد.", "خطا", MessageBoxButton.OK, MessageBoxImage.Error);
                Close();
            }
        }

        private void LoadStoredData()
        {
            if (File.Exists(dataFilePath))
            {
                var json = File.ReadAllText(dataFilePath);
                var data = JsonConvert.DeserializeObject<Dictionary<string, List<string>>>(json);
                if (data != null)
                {
                    categories = data.ContainsKey("Categories") ? data["Categories"] : new List<string>();
                    singers = data.ContainsKey("Singers") ? data["Singers"] : new List<string>();
                }
            }
            CategoryComboBox.ItemsSource = categories;
            SingerComboBox.ItemsSource = singers;
        }

        private void SaveStoredData()
        {
            var data = new Dictionary<string, List<string>>
            {
                { "Categories", categories },
                { "Singers", singers }
            };
            File.WriteAllText(dataFilePath, JsonConvert.SerializeObject(data, Formatting.Indented));
        }

        private void UpdateFileName()
        {
            var singer = string.IsNullOrWhiteSpace(SingerComboBox.Text) ? "خواننده ناشناس" : SingerComboBox.Text.Trim();
            var persianCalendar = new PersianCalendar();
            var now = DateTime.Now;
            var persianDate = $"{persianCalendar.GetYear(now)}-{persianCalendar.GetMonth(now):D2}-{persianCalendar.GetDayOfMonth(now):D2}";
            FileNameTextBox.Text = $"{singer}-{persianDate}";
        }

        private void CategoryComboBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            var category = CategoryComboBox.Text.Trim();
            if (!string.IsNullOrEmpty(category) && !categories.Contains(category))
            {
                categories.Add(category);
                SaveStoredData();
                CategoryComboBox.ItemsSource = null;
                CategoryComboBox.ItemsSource = categories;
                CategoryComboBox.Text = category;
            }
        }

        private void SingerComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateFileName();
        }

        private void SingerComboBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            var singer = SingerComboBox.Text.Trim();
            if (!string.IsNullOrEmpty(singer) && !singers.Contains(singer))
            {
                singers.Add(singer);
                SaveStoredData();
                SingerComboBox.ItemsSource = null;
                SingerComboBox.ItemsSource = singers;
                SingerComboBox.Text = singer;
            }
            UpdateFileName();
        }

        private void StartButton_Click(object sender, RoutedEventArgs e)
        {
            StartRecording();
        }

        private void StartRecording()
        {
            outputFilePath = Path.Combine(savePath, $"{FileNameTextBox.Text}.mp3");

            try
            {
                waveIn = new WaveInEvent
                {
                    DeviceNumber = SourceComboBox.SelectedIndex,
                    WaveFormat = new WaveFormat(44100, 16, 2) // 44.1kHz, 16-bit, stereo
                };

                mp3Writer = new LameMP3FileWriter(outputFilePath, waveIn.WaveFormat, LAMEPreset.STANDARD);
                waveIn.DataAvailable += (s, args) =>
                {
                    // Write audio data to MP3 file
                    mp3Writer.Write(args.Buffer, 0, args.BytesRecorded);

                    // Calculate peak amplitude for visualization
                    float max = 0;
                    for (int i = 0; i < args.BytesRecorded; i += 2)
                    {
                        short sample = BitConverter.ToInt16(args.Buffer, i);
                        float amplitude = Math.Abs(sample / 32768f);
                        if (amplitude > max) max = amplitude;
                    }
                    // Update ProgressBars on UI thread
                    Dispatcher.Invoke(() =>
                    {
                        var level = max * 100;
                        if (level < 0) level = 0;
                        if (level > 100) level = 100;
                        AudioLevelMeter.Value = level;
                        CenterRangeMeter.Value = level;
                    });
                };

                waveIn.StartRecording();
                StartButton.IsEnabled = false;
                StopButton.IsEnabled = true;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"خطا در شروع ضبط: {ex.Message}", "خطا", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                waveIn?.StopRecording();
                waveIn?.Dispose();
                waveIn?.Dispose();
                mp3Writer?.Close();
                mp3Writer?.Dispose();

                // Reset audio level meters
                AudioLevelMeter.Value = 0;
                CenterRangeMeter.Value = 0;

                // Add MP3 tags
                using (var file = TagLib.File.Create(outputFilePath))
                {
                    file.Tag.Title = FileNameTextBox.Text;
                    file.Tag.Genres = new[] { string.IsNullOrWhiteSpace(CategoryComboBox.Text) ? "دسته‌بندی پیش‌فرض" : CategoryComboBox.Text };
                    file.Tag.Performers = new[] { string.IsNullOrWhiteSpace(SingerComboBox.Text) ? "خواننده ناشناس" : SingerComboBox.Text };
                    file.Save();
                }

                MessageBox.Show("ضبط با موفقیت ذخیره شد!", "موفقیت", MessageBoxButton.OK, MessageBoxImage.Information);
                StartButton.IsEnabled = true;
                StopButton.IsEnabled = false;

                // Close the app if configured
                if (closeAfterSave)
                {
                    Application.Current.Shutdown();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"خطا در توقف ضبط: {ex.Message}", "خطا", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}