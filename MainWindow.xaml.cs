using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using NAudio.Lame;
using NAudio.Wave;
using Newtonsoft.Json;
using File = System.IO.File;

namespace SoundRecorder;

public partial class MainWindow
{
    private WaveInEvent _waveIn;
    private LameMP3FileWriter _mp3Writer;
    private string _outputFilePath;
    private List<string> _categories = [];
    private List<string> _singers = [];
    private const string DataFilePath = "appData.json";
    private string _savePath;
    private bool _autoStartRecording;
    private bool _closeAfterSave;
    private bool _isRecording; // Track recording state

    // Audio configuration (read from appsettings.json)
    private int _audioSampleRateHz = 44100; // default 44.1 kHz if not configured
    private int _audioBitsPerSample = 32; // default 32-bit as requested
    private int _audioChannels = 2; // default stereo
    private int _mp3BitrateKbps = 128; // default 128 kbps MP3

    public MainWindow()
    {
        InitializeComponent();
        LoadAppSettings();
        LoadAudioSources();
        LoadStoredData();
        UpdateFileName();

        if (_autoStartRecording)
            StartRecording();
    }

    private void LoadAppSettings()
    {
        var defaultSavePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "Recordings");

        try
        {
            string[] candidates =
            [
                "appsettings.json",
                Path.Combine(AppContext.BaseDirectory, "appsettings.json")
            ];

            var json = (from path in candidates where !string.IsNullOrWhiteSpace(path) && File.Exists(path) select File.ReadAllText(path)).FirstOrDefault();

            if (!string.IsNullOrWhiteSpace(json))
            {
                var map = JsonConvert.DeserializeObject<Dictionary<string, object>>(json) ?? new Dictionary<string, object>();

                _savePath = map.TryGetValue("SavePath", out var savePathVal) && !string.IsNullOrWhiteSpace(savePathVal?.ToString()) ? savePathVal!.ToString() : defaultSavePath;
                _autoStartRecording = map.TryGetValue("AutoStartRecording", out var autoVal) && (autoVal is bool b ? b : bool.TryParse(autoVal?.ToString(), out var b2) && b2);
                _closeAfterSave = map.TryGetValue("CloseAfterSave", out var closeVal) && (closeVal is bool b3 ? b3 : bool.TryParse(closeVal?.ToString(), out var b4) && b4);

                // Audio settings (optional)
                int ParseInt(object val, int defaultVal)
                {
                    if (val == null) return defaultVal;
                    var s = val.ToString();
                    if (int.TryParse(s, out var n)) return n;
                    var digits = new string(s!.Where(char.IsDigit).ToArray());
                    return int.TryParse(digits, out var n2) ? n2 : defaultVal;
                }

                if (map.TryGetValue("SampleRateHz", out var srVal))
                {
                    var sr = ParseInt(srVal, _audioSampleRateHz);
                    if (sr > 0) _audioSampleRateHz = sr;
                }

                if (map.TryGetValue("BitsPerSample", out var bpsVal))
                {
                    var bps = ParseInt(bpsVal, _audioBitsPerSample);
                    _audioBitsPerSample = bps == 32 ? 32 : 16; // limit to 16 or 32
                }

                if (map.TryGetValue("Channels", out var chVal))
                {
                    var ch = ParseInt(chVal, _audioChannels);
                    _audioChannels = ch < 1 ? 1 : (ch > 2 ? 2 : ch); // clamp 1..2
                }

                if (map.TryGetValue("Mp3BitrateKbps", out var brVal))
                {
                    var br = ParseInt(brVal, _mp3BitrateKbps);
                    if (br > 0) _mp3BitrateKbps = br;
                }
            }
            else
            {
                _savePath = defaultSavePath;
                _autoStartRecording = false;
                _closeAfterSave = false;
            }
        }
        catch
        {
            _savePath = defaultSavePath;
            _autoStartRecording = false;
            _closeAfterSave = false;
        }

        // Create the save path directory if it doesn't exist
        Directory.CreateDirectory(_savePath!);
    }

    private void LoadAudioSources()
    {
        for (var i = 0; i < WaveIn.DeviceCount; i++)
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
        if (File.Exists(DataFilePath))
        {
            var json = File.ReadAllText(DataFilePath);
            var data = JsonConvert.DeserializeObject<Dictionary<string, List<string>>>(json);
            if (data != null)
            {
                _categories = data.TryGetValue("Categories", out var categories) ? categories : [];
                _singers = data.TryGetValue("Singers", out var singers) ? singers : [];
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
        File.WriteAllText(DataFilePath, JsonConvert.SerializeObject(data, Formatting.Indented));
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

        var started = false;
        Exception? lastError = null;

        // Try with configured bit depth first, then fallback to 16-bit if needed
        foreach (var bits in new[] { _audioBitsPerSample, 16 })
        {
            if (started) break;
            if (bits != _audioBitsPerSample && _audioBitsPerSample == 16) break; // avoid duplicate try

            try
            {
                _waveIn = new WaveInEvent
                {
                    DeviceNumber = SourceComboBox.SelectedIndex,
                    WaveFormat = new WaveFormat(_audioSampleRateHz, bits, _audioChannels)
                };

                // Use configured MP3 bitrate (kbps)
                _mp3Writer = new LameMP3FileWriter(_outputFilePath, _waveIn.WaveFormat, _mp3BitrateKbps);

                _waveIn.DataAvailable += OnDataAvailable;
                _waveIn.RecordingStopped += OnRecordingStopped;

                _waveIn.StartRecording();
                _isRecording = true;

                StartButton.IsEnabled = false;
                StopButton.IsEnabled = true;
                started = true;
            }
            catch (Exception ex)
            {
                lastError = ex;
                // Cleanup any partially created resources before retrying
                try
                {
                    _mp3Writer?.Dispose();
                }
                catch
                {
                    // ignored
                }

                try
                {
                    _waveIn?.Dispose();
                }
                catch
                {
                    // ignored
                }

                _mp3Writer = null;
                _waveIn = null;
            }
        }

        if (!started)
        {
            MessageBox.Show($"خطا در شروع ضبط: {lastError?.Message}", "خطا", MessageBoxButton.OK, MessageBoxImage.Error);
            _isRecording = false;
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs args)
    {
        try
        {
            // Write audio data to MP3 file
            _mp3Writer?.Write(args.Buffer, 0, args.BytesRecorded);

            // Calculate peak amplitude for visualization (supports 16-bit or 32-bit PCM)
            float max = 0;
            var bytesPerSample = _waveIn?.WaveFormat.BitsPerSample == 32 ? 4 : 2;
            for (var i = 0; i <= args.BytesRecorded - bytesPerSample; i += bytesPerSample)
            {
                float amplitude;
                if (bytesPerSample == 4)
                {
                    var sample32 = BitConverter.ToInt32(args.Buffer, i);
                    amplitude = Math.Abs(sample32 / 2147483648f); // normalize int32
                }
                else
                {
                    var sample16 = BitConverter.ToInt16(args.Buffer, i);
                    amplitude = Math.Abs(sample16 / 32768f);
                }

                if (amplitude > max) max = amplitude;
            }

            // Update ProgressBars on UI thread
            Dispatcher.BeginInvoke(() =>
            {
                var level = max * 100;
                if (level < 0) level = 0;
                if (level > 100) level = 100;
                AudioLevelMeter.Value = level;
                CenterRangeMeter.Value = level;
            });
        }
        catch
        {
            // Swallow exceptions during shutdown race; finalization happens in RecordingStopped
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        // Unsubscribe first to avoid re-entrancy
        if (_waveIn != null)
        {
            _waveIn.DataAvailable -= OnDataAvailable;
            _waveIn.RecordingStopped -= OnRecordingStopped;
        }

        // Dispose resources safely
        try
        {
            _waveIn?.Dispose();
        }
        catch
        {
            // ignored
        }

        _waveIn = null;

        try
        {
            _mp3Writer?.Flush();
        }
        catch
        {
            // ignored
        }

        try
        {
            _mp3Writer?.Dispose();
        }
        catch
        {
            // ignored
        }

        _mp3Writer = null;

        _isRecording = false;

        // Reset audio level meters
        Dispatcher.BeginInvoke(() =>
        {
            AudioLevelMeter.Value = 0;
            CenterRangeMeter.Value = 0;
        });

        // If there was a capture error, inform the user
        if (e.Exception != null)
        {
            Dispatcher.BeginInvoke(() =>
            {
                MessageBox.Show($"خطا در ضبط صوت: {e.Exception.Message}", "خطا", MessageBoxButton.OK, MessageBoxImage.Error);
                StartButton.IsEnabled = true;
                StopButton.IsEnabled = false;
            });
            return;
        }

        // Add MP3 tags after file is closed
        try
        {
            using (var file = TagLib.File.Create(_outputFilePath))
            {
                file.Tag.Title = FileNameTextBox.Text;
                file.Tag.Genres = [string.IsNullOrWhiteSpace(CategoryComboBox.Text) ? "دسته‌بندی پیش‌فرض" : CategoryComboBox.Text];
                file.Tag.Performers = [string.IsNullOrWhiteSpace(SingerComboBox.Text) ? "خواننده ناشناس" : SingerComboBox.Text];
                file.Save();
            }

            Dispatcher.BeginInvoke(() =>
            {
                StartButton.IsEnabled = true;
                StopButton.IsEnabled = false;

                if (_closeAfterSave)
                {
                    Application.Current.Shutdown();
                }
            });
        }
        catch (Exception tagEx)
        {
            Dispatcher.BeginInvoke(() =>
            {
                MessageBox.Show($"ذخیره برچسب‌ها با خطا مواجه شد: {tagEx.Message}", "خطا", MessageBoxButton.OK, MessageBoxImage.Error);
                StartButton.IsEnabled = true;
                StopButton.IsEnabled = false;
            });
        }
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_waveIn != null && _isRecording)
            {
                _waveIn.StopRecording();
            }
            else
            {
                StartButton.IsEnabled = true;
            }

            StopButton.IsEnabled = false;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"خطا در توقف ضبط: {ex.Message}", "خطا", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}