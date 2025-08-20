using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shell;
using System.Windows.Threading;
using NAudio.Lame;
using NAudio.Wave;
using Newtonsoft.Json;
using SoundRecorder.Properties;
using File = System.IO.File;

namespace SoundRecorder;

public partial class MainWindow
{
    private WaveInEvent _waveIn;
    private LameMP3FileWriter _mp3Writer;
    private string _outputFilePath;
    private List<string> _genres = [];
    private List<string> _singers = [];
    private const string DataFilePath = "appData.json";
    private string _savePath;
    private bool _autoStartRecording;
    private bool _closeAfterSave;
    private bool _isRecording; // Track recording state
    private DispatcherTimer _recordTimer;
    private TimeSpan _elapsed;

    // Audio configuration (read from appsettings.json)
    private int _audioSampleRateHz = 44100; // default 44.1 kHz if not configured
    private int _mp3BitrateKbps = 128; // default 128 kbps MP3

    public MainWindow()
    {
        InitializeComponent();

        // Configure WindowChrome programmatically to keep resizing on a borderless window
        var chrome = new WindowChrome
        {
            CaptionHeight = 0,
            ResizeBorderThickness = new Thickness(6),
            GlassFrameThickness = new Thickness(0),
            UseAeroCaptionButtons = false
        };
        WindowChrome.SetWindowChrome(this, chrome);

        LoadAppSettings();
        LoadAudioSources();
        LoadStoredData();
        UpdateFileName();

        // Initialize recording timer and set initial display
        _recordTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _recordTimer.Tick += (s, e) =>
        {
            _elapsed = _elapsed.Add(TimeSpan.FromSeconds(1));
            RecordingTimeText.Text = _elapsed.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
        };
        _elapsed = TimeSpan.Zero;
        RecordingTimeText.Text = SoundRecorder.Properties.Resources.RecordingTime_Initial;

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
            MessageBox.Show(SoundRecorder.Properties.Resources.Error_NoInputDevices, SoundRecorder.Properties.Resources.Error_Title, MessageBoxButton.OK, MessageBoxImage.Error);
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
                if (data.TryGetValue("Genres", out var genres))
                    _genres = genres;
                else
                    _genres = [];

                _singers = data.TryGetValue("Singers", out var singers) ? singers : [];
            }
        }

        GenreComboBox.ItemsSource = _genres;
        SingerComboBox.ItemsSource = _singers;
    }

    private void SaveStoredData()
    {
        var data = new Dictionary<string, List<string>>
        {
            { "Genres", _genres },
            { "Singers", _singers }
        };
        File.WriteAllText(DataFilePath, JsonConvert.SerializeObject(data, Formatting.Indented));
    }

    // Helpers for filename handling
    private static string SanitizeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return SoundRecorder.Properties.Resources.Unknown_Singer;
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (var ch in name)
        {
            sb.Append(invalid.Contains(ch) ? '-' : ch);
        }
        var cleaned = sb.ToString().Trim();
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, "\\s+", " ");
        cleaned = cleaned.Trim(' ', '.');
        if (string.IsNullOrWhiteSpace(cleaned)) cleaned = SoundRecorder.Properties.Resources.Unknown_Singer;
        return cleaned;
    }

    private static string GetUniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path);
        var fileName = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        int counter = 1;
        string candidate;
        do
        {
            candidate = Path.Combine(dir!, $"{fileName} ({counter}){ext}");
            counter++;
        } while (File.Exists(candidate));
        return candidate;
    }

    private void UpdateFileName()
    {
        var singer = string.IsNullOrWhiteSpace(SingerComboBox.Text) ? SoundRecorder.Properties.Resources.Unknown_Singer : SingerComboBox.Text.Trim();
        var persianCalendar = new PersianCalendar();
        var now = DateTime.Now;
        var persianDate = $"{persianCalendar.GetYear(now)}-{persianCalendar.GetMonth(now):D2}-{persianCalendar.GetDayOfMonth(now):D2}";
        FileNameTextBox.Text = $"{singer}-{persianDate}";
    }

    private void GenreComboBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        // Do not mutate the stored list on each key press to avoid incremental entries (h, he, hel, ...)
        // Final values are added when recording is finished and saved.
    }

    private void SingerComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateFileName();
    }

    private void SingerComboBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        // Do not mutate the stored list on each key press to avoid incremental entries.
        // Only update the suggested filename as the user types.
        UpdateFileName();
    }

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        StartRecording();
    }

    private void StartRecording()
    {
        var baseName = SanitizeFileName(string.IsNullOrWhiteSpace(FileNameTextBox.Text) ? SoundRecorder.Properties.Resources.Unknown_Singer : FileNameTextBox.Text.Trim());
        var initialPath = Path.Combine(_savePath, $"{baseName}.mp3");
        _outputFilePath = GetUniquePath(initialPath);

        var started = false;
        Exception? lastError = null;

        try
        {
            _waveIn = new WaveInEvent
            {
                DeviceNumber = SourceComboBox.SelectedIndex,
                WaveFormat = new WaveFormat(_audioSampleRateHz, 16, 2)
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

            // Start the on-screen HH:MM:SS counter
            _elapsed = TimeSpan.Zero;
            RecordingTimeText.Text = SoundRecorder.Properties.Resources.RecordingTime_Initial;
            _recordTimer.Stop();
            _recordTimer.Start();
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

        if (!started)
        {
            MessageBox.Show(string.Format(SoundRecorder.Properties.Resources.Error_StartRecording_Format, lastError?.Message), SoundRecorder.Properties.Resources.Error_Title, MessageBoxButton.OK, MessageBoxImage.Error);
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

        // Stop the HH:MM:SS counter and keep the last shown value (freeze)
        Dispatcher.BeginInvoke(() => _recordTimer?.Stop());

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
                MessageBox.Show(string.Format(SoundRecorder.Properties.Resources.Error_Recording_Format, e.Exception.Message), SoundRecorder.Properties.Resources.Error_Title, MessageBoxButton.OK, MessageBoxImage.Error);
                StartButton.IsEnabled = true;
                StopButton.IsEnabled = false;
            });
            return;
        }

        // Determine desired filename from UI and rename if needed
        string uiTitle = "";
        string uiGenre = "";
        string uiSinger = "";
        Dispatcher.Invoke(new Action(() =>
        {
            uiTitle = FileNameTextBox.Text;
            uiGenre = GenreComboBox.Text;
            uiSinger = SingerComboBox.Text;
        }));

        try
        {
            var desiredBase = SanitizeFileName(string.IsNullOrWhiteSpace(uiTitle) ? SoundRecorder.Properties.Resources.Unknown_File : uiTitle.Trim());
            var desiredPathInitial = Path.Combine(_savePath, $"{desiredBase}.mp3");

            // If path differs, attempt rename
            if (!string.Equals(_outputFilePath, desiredPathInitial, StringComparison.OrdinalIgnoreCase))
            {
                var desiredPath = GetUniquePath(desiredPathInitial);
                try
                {
                    if (File.Exists(_outputFilePath))
                    {
                        File.Move(_outputFilePath, desiredPath);
                        _outputFilePath = desiredPath;
                    }
                }
                catch (Exception moveEx)
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        MessageBox.Show(string.Format(SoundRecorder.Properties.Resources.Warning_RenameAfterRecord_Format, moveEx.Message), SoundRecorder.Properties.Resources.Error_Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                    });
                }
            }
        }
        catch
        {
            // Ignore rename errors here; tagging still attempted
        }

        // Add MP3 tags after file is closed/renamed
        try
        {
            using (var file = TagLib.File.Create(_outputFilePath))
            {
                file.Tag.Title = uiTitle;
                file.Tag.Genres = [string.IsNullOrWhiteSpace(uiGenre) ? SoundRecorder.Properties.Resources.Default_Genre : uiGenre];
                file.Tag.Performers = [string.IsNullOrWhiteSpace(uiSinger) ? SoundRecorder.Properties.Resources.Unknown_Singer : uiSinger];
                file.Save();
            }

            // Add final values to lists once recording finished
            if (!string.IsNullOrWhiteSpace(uiGenre) && !_genres.Contains(uiGenre))
            {
                _genres.Add(uiGenre);
            }
            if (!string.IsNullOrWhiteSpace(uiSinger) && !_singers.Contains(uiSinger))
            {
                _singers.Add(uiSinger);
            }

            SaveStoredData();

            Dispatcher.BeginInvoke(() =>
            {
                // Refresh dropdowns to reflect newly added entries after final save
                GenreComboBox.ItemsSource = null;
                GenreComboBox.ItemsSource = _genres;
                SingerComboBox.ItemsSource = null;
                SingerComboBox.ItemsSource = _singers;

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
                MessageBox.Show(string.Format(SoundRecorder.Properties.Resources.Error_SaveTags_Format, tagEx.Message), SoundRecorder.Properties.Resources.Error_Title, MessageBoxButton.OK, MessageBoxImage.Error);
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
            MessageBox.Show(string.Format(SoundRecorder.Properties.Resources.Error_StopRecording_Format, ex.Message), SoundRecorder.Properties.Resources.Error_Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // --- Custom Title Bar Handlers ---
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // With ResizeMode=NoResize, disable double-click maximize/restore. Only allow dragging.
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try { DragMove(); } catch { /* ignore */ }
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        // If recording is active, stop and save before closing
        if (_isRecording && _waveIn != null)
        {
            _closeAfterSave = true; // trigger shutdown after RecordingStopped completes save/tagging
            try
            {
                _waveIn.StopRecording();
            }
            catch
            {
                // If stopping fails for any reason, fallback to closing
                Application.Current.Shutdown();
            }
            return; // wait for RecordingStopped to finish then close
        }

        Close();
    }
}