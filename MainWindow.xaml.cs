using System.Configuration;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using NAudio.Lame;
using NAudio.Wave;
using Newtonsoft.Json;
using File = System.IO.File;

namespace SoundRecorder;

// Converts a numeric level (0-100) to a Brush: Green (low), Yellow (mid), Red (high)
public class LevelToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        try
        {
            var v = value switch
            {
                double d => d,
                float f => f,
                int i => i,
                _ => 0
            };
            return v switch
            {
                < 60 => Brushes.Green,
                < 85 => Brushes.Yellow,
                _ => Brushes.Red
            };
        }
        catch
        {
            return Brushes.Green;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public partial class MainWindow : Window
{
    private WaveInEvent _waveIn;
    private LameMP3FileWriter _mp3Writer;
    private string _outputFilePath;
    private List<string> _categories = [];
    private List<string> _singers = [];
    private readonly string _dataFilePath = "appData.json";
    private string _savePath;
    private bool _autoStartRecording;
    private bool _closeAfterSave;

    public MainWindow()
    {
        InitializeComponent();
        LoadAppSettings();
        LoadAudioSources();
        LoadStoredData();
        UpdateFileName();

        if (_autoStartRecording)
        {
            StartRecording();
        }
    }

    private void LoadAppSettings()
    {
        // Read save path, auto-start, and close-after-save settings from appsettings.json
        _savePath = ConfigurationManager.AppSettings["SavePath"];
        if (string.IsNullOrEmpty(_savePath))
        {
            _savePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "Recordings");
        }

        // Create the save path directory if it doesn't exist
        Directory.CreateDirectory(_savePath);

        _autoStartRecording = bool.TryParse(ConfigurationManager.AppSettings["AutoStartRecording"], out bool autoStartResult) && autoStartResult;
        _closeAfterSave = bool.TryParse(ConfigurationManager.AppSettings["CloseAfterSave"], out bool closeResult) && closeResult;
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
        if (File.Exists(_dataFilePath))
        {
            var json = File.ReadAllText(_dataFilePath);
            var data = JsonConvert.DeserializeObject<Dictionary<string, List<string>>>(json);
            if (data != null)
            {
                _categories = data.ContainsKey("Categories") ? data["Categories"] : [];
                _singers = data.ContainsKey("Singers") ? data["Singers"] : [];
            }
        }

        CategoryComboBox.ItemsSource = _categories;
        SingerComboBox.ItemsSource = _singers;
    }

    private void SaveStoredData()
    {
        var data = new Dictionary<string, List<string>>
        {
            { "Categories", _categories },
            { "Singers", _singers }
        };
        File.WriteAllText(_dataFilePath, JsonConvert.SerializeObject(data, Formatting.Indented));
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
        if (!string.IsNullOrEmpty(category) && !_categories.Contains(category))
        {
            _categories.Add(category);
            SaveStoredData();
            CategoryComboBox.ItemsSource = null;
            CategoryComboBox.ItemsSource = _categories;
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
        if (!string.IsNullOrEmpty(singer) && !_singers.Contains(singer))
        {
            _singers.Add(singer);
            SaveStoredData();
            SingerComboBox.ItemsSource = null;
            SingerComboBox.ItemsSource = _singers;
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
        _outputFilePath = Path.Combine(_savePath, $"{FileNameTextBox.Text}.mp3");

        try
        {
            _waveIn = new WaveInEvent
            {
                DeviceNumber = SourceComboBox.SelectedIndex,
                WaveFormat = new WaveFormat(44100, 16, 2) // 44.1kHz, 16-bit, stereo
            };

            _mp3Writer = new LameMP3FileWriter(_outputFilePath, _waveIn.WaveFormat, LAMEPreset.STANDARD);
            _waveIn.DataAvailable += (s, args) =>
            {
                // Write audio data to MP3 file
                _mp3Writer.Write(args.Buffer, 0, args.BytesRecorded);

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

            _waveIn.StartRecording();
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
            _waveIn?.StopRecording();
            _waveIn?.Dispose();
            _waveIn?.Dispose();
            _mp3Writer?.Close();
            _mp3Writer?.Dispose();

            // Reset audio level meters
            AudioLevelMeter.Value = 0;
            CenterRangeMeter.Value = 0;

            // Add MP3 tags
            using (var file = TagLib.File.Create(_outputFilePath))
            {
                file.Tag.Title = FileNameTextBox.Text;
                file.Tag.Genres = [string.IsNullOrWhiteSpace(CategoryComboBox.Text) ? "دسته‌بندی پیش‌فرض" : CategoryComboBox.Text];
                file.Tag.Performers = [string.IsNullOrWhiteSpace(SingerComboBox.Text) ? "خواننده ناشناس" : SingerComboBox.Text];
                file.Save();
            }

            MessageBox.Show("ضبط با موفقیت ذخیره شد!", "موفقیت", MessageBoxButton.OK, MessageBoxImage.Information);
            StartButton.IsEnabled = true;
            StopButton.IsEnabled = false;

            // Close the app if configured
            if (_closeAfterSave)
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