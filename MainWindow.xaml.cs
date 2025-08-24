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
using File = System.IO.File;

namespace SoundRecorder;

public partial class MainWindow
{
    private WaveInEvent _waveIn;
    private LameMP3FileWriter _mp3Writer;
    private string _outputFilePath;
    private List<string> _genres = [];
    private List<string> _singers = [];
    private List<string> _albums = [];
    private const string DataFilePath = "appData.json";
    private string _savePath;
    private bool _autoStartRecording;
    private bool _closeAfterSave;
    private bool _isRecording; // Track recording state
    private readonly DispatcherTimer _recordTimer;
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
                _albums = data.TryGetValue("Albums", out var albums) ? albums : [];
            }
        }

        GenreComboBox.ItemsSource = _genres;
        SingerComboBox.ItemsSource = _singers;
        AlbumComboBox.ItemsSource = _albums;
    }

    private void SaveStoredData()
    {
        var data = new Dictionary<string, List<string>>
        {
            { "Genres", _genres },
            { "Singers", _singers },
            { "Albums", _albums }
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
        var counter = 1;
        string candidate;
        do
        {
            candidate = Path.Combine(dir!, $"{fileName} ({counter}){ext}");
            counter++;
        } while (File.Exists(candidate));

        return candidate;
    }

    private static string SanitizePathSegmentForFolder(string name, string defaultVal)
    {
        if (string.IsNullOrWhiteSpace(name)) return defaultVal;
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (var ch in name)
        {
            sb.Append(invalid.Contains(ch) ? '-' : ch);
        }

        var cleaned = sb.ToString().Trim();
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, "\\s+", " ");
        cleaned = cleaned.Trim(' ', '.');
        if (string.IsNullOrWhiteSpace(cleaned)) cleaned = defaultVal;
        return cleaned;
    }


    private void UpdateFileName()
    {
        var singer = string.IsNullOrWhiteSpace(SingerComboBox.Text) ? SoundRecorder.Properties.Resources.Unknown_Singer : SingerComboBox.Text.Trim();
        var persianCalendar = new PersianCalendar();
        var now = DateTime.Now;
        var persianDate = $"{persianCalendar.GetYear(now)}-{persianCalendar.GetMonth(now):D2}-{persianCalendar.GetDayOfMonth(now):D2}";
        FileNameTextBox.Text = $"{singer}-{persianDate}";
    }

    private void SetRecordingUi(bool recording)
    {
        Dispatcher.BeginInvoke(() =>
        {
            StartButton.Visibility = recording ? Visibility.Collapsed : Visibility.Visible;
            StopButton.Visibility = recording ? Visibility.Visible : Visibility.Collapsed;
        });
    }

    private void SingerComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateFileName();
    }

    private void SingerComboBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateFileName();
    }

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        StartRecording();
    }

    private void CleanupRecording(bool deleteFile = true)
    {
        // First unsubscribe events to prevent save logic
        if (_waveIn != null)
        {
            _waveIn.DataAvailable -= OnDataAvailable;
            _waveIn.RecordingStopped -= OnRecordingStopped;
        }

        // Now stop recording if active
        if (_waveIn != null && _isRecording)
        {
            try
            {
                _waveIn.StopRecording();
            }
            catch
            {
                /* ignore stop errors */
            }
        }

        // Dispose and nullify writer first
        if (_mp3Writer != null)
        {
            try
            {
                _mp3Writer.Flush();
                _mp3Writer.Dispose();
            }
            catch
            {
                /* ignore disposal errors */
            }

            _mp3Writer = null;
        }

        // Dispose and nullify wave in
        if (_waveIn != null)
        {
            try
            {
                _waveIn.Dispose();
            }
            catch
            {
                /* ignore disposal errors */
            }

            _waveIn = null;
        }

        // Force GC collection to release file handles
        GC.Collect();
        GC.WaitForPendingFinalizers();

        // Delete file if requested
        if (deleteFile && !string.IsNullOrEmpty(_outputFilePath))
        {
            for (int attempts = 0; attempts < 3; attempts++)
            {
                try
                {
                    if (File.Exists(_outputFilePath))
                    {
                        File.Delete(_outputFilePath);
                    }

                    break;
                }
                catch
                {
                    if (attempts < 2)
                    {
                        Thread.Sleep(100); // Wait before retry
                    }
                }
            }
        }

        _isRecording = false;
    }

    private void StartRecording()
    {
        // Cleanup previous recording and delete any existing temp file
        CleanupRecording(true);

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

            _mp3Writer = new LameMP3FileWriter(_outputFilePath, _waveIn.WaveFormat, _mp3BitrateKbps);

            _waveIn.DataAvailable += OnDataAvailable;
            _waveIn.RecordingStopped += OnRecordingStopped;

            _waveIn.StartRecording();
            _isRecording = true;

            SetRecordingUi(true);
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
            CleanupRecording(true);
        }

        if (!started)
        {
            MessageBox.Show(string.Format(SoundRecorder.Properties.Resources.Error_Recording_Format, lastError?.Message), SoundRecorder.Properties.Resources.Error_Title, MessageBoxButton.OK, MessageBoxImage.Error);
            SetRecordingUi(false);
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs args)
    {
        try
        {
            // Write audio data to MP3 file
            _mp3Writer?.Write(args.Buffer, 0, args.BytesRecorded);

            // Calculate per-channel peak amplitude for visualization (supports 16-bit or 32-bit PCM)
            float leftMax = 0f, rightMax = 0f;
            var channels = _waveIn?.WaveFormat.Channels ?? 1;
            var bytesPerSample = _waveIn?.WaveFormat.BitsPerSample == 32 ? 4 : 2;
            var blockAlign = bytesPerSample * channels;

            for (var i = 0; i <= args.BytesRecorded - blockAlign; i += blockAlign)
            {
                // Channel 0 (Left)
                float amp0;
                if (bytesPerSample == 4)
                {
                    var sample32 = BitConverter.ToInt32(args.Buffer, i + 0 * bytesPerSample);
                    amp0 = Math.Abs(sample32 / 2147483648f);
                }
                else
                {
                    var sample16 = BitConverter.ToInt16(args.Buffer, i + 0 * bytesPerSample);
                    amp0 = Math.Abs(sample16 / 32768f);
                }

                if (amp0 > leftMax) leftMax = amp0;

                // Channel 1 (Right) if present
                if (channels > 1)
                {
                    float amp1;
                    if (bytesPerSample == 4)
                    {
                        var sample32b = BitConverter.ToInt32(args.Buffer, i + 1 * bytesPerSample);
                        amp1 = Math.Abs(sample32b / 2147483648f);
                    }
                    else
                    {
                        var sample16b = BitConverter.ToInt16(args.Buffer, i + 1 * bytesPerSample);
                        amp1 = Math.Abs(sample16b / 32768f);
                    }

                    if (amp1 > rightMax) rightMax = amp1;
                }
            }

            if (channels == 1)
            {
                rightMax = leftMax;
            }

            // Update ProgressBars on UI thread
            Dispatcher.BeginInvoke(() =>
            {
                var leftLevel = leftMax * 100.0;
                if (leftLevel < 0) leftLevel = 0;
                if (leftLevel > 100) leftLevel = 100;

                var rightLevel = rightMax * 100.0;
                if (rightLevel < 0) rightLevel = 0;
                if (rightLevel > 100) rightLevel = 100;

                // Assign per-channel: LeftLevelMeter shows left channel; RightLevelMeter shows right channel
                LeftLevelMeter.Value = leftLevel;
                RightLevelMeter.Value = rightLevel;
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
            RightLevelMeter.Value = 0;
            LeftLevelMeter.Value = 0;
        });

        // If there was a capture error, inform the user
        if (e.Exception != null)
        {
            Dispatcher.BeginInvoke(() =>
            {
                MessageBox.Show(string.Format(SoundRecorder.Properties.Resources.Error_Recording_Format, e.Exception.Message), SoundRecorder.Properties.Resources.Error_Title, MessageBoxButton.OK, MessageBoxImage.Error);
                StartButton.Visibility = Visibility.Visible;
                StopButton.Visibility = Visibility.Collapsed;
            });
            return;
        }

        // Determine desired filename from UI and rename if needed
        var uiTitle = "";
        var uiGenre = "";
        var uiSinger = "";
        var uiAlbum = "";
        Dispatcher.Invoke(new Action(() =>
        {
            uiTitle = FileNameTextBox.Text;
            uiGenre = GenreComboBox.Text;
            uiSinger = SingerComboBox.Text;
            uiAlbum = AlbumComboBox.Text;
        }));

        try
        {
            var desiredBase = SanitizeFileName(string.IsNullOrWhiteSpace(uiTitle) ? SoundRecorder.Properties.Resources.Unknown_File : uiTitle.Trim());
            var albumFolder = SanitizePathSegmentForFolder(string.IsNullOrWhiteSpace(uiAlbum) ? SoundRecorder.Properties.Resources.Unknown_Album : uiAlbum.Trim(), SoundRecorder.Properties.Resources.Unknown_Album);
            var albumDir = Path.Combine(_savePath, albumFolder);
            Directory.CreateDirectory(albumDir);
            var desiredPathInitial = Path.Combine(albumDir, $"{desiredBase}.mp3");

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
                    Dispatcher.BeginInvoke(() => { MessageBox.Show(string.Format(SoundRecorder.Properties.Resources.Warning_RenameAfterRecord_Format, moveEx.Message), SoundRecorder.Properties.Resources.Error_Title, MessageBoxButton.OK, MessageBoxImage.Warning); });
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
                file.Tag.Album = string.IsNullOrWhiteSpace(uiAlbum) ? SoundRecorder.Properties.Resources.Unknown_Album : uiAlbum;

                // Set Year tag using Persian calendar
                var pc = new PersianCalendar();
                file.Tag.Year = (uint)pc.GetYear(DateTime.Now);

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

            if (!string.IsNullOrWhiteSpace(uiAlbum) && !_albums.Contains(uiAlbum))
            {
                _albums.Add(uiAlbum);
            }

            SaveStoredData();

            Dispatcher.BeginInvoke(() =>
            {
                // Refresh dropdowns to reflect newly added entries after final save
                GenreComboBox.ItemsSource = null;
                GenreComboBox.ItemsSource = _genres;
                SingerComboBox.ItemsSource = null;
                SingerComboBox.ItemsSource = _singers;
                AlbumComboBox.ItemsSource = null;
                AlbumComboBox.ItemsSource = _albums;

                StartButton.Visibility = Visibility.Visible;
                StopButton.Visibility = Visibility.Collapsed;

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
                StartButton.Visibility = Visibility.Visible;
                StopButton.Visibility = Visibility.Collapsed;
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

            // Immediately reflect UI state: show Start, hide Stop
            StartButton.Visibility = Visibility.Visible;
            StopButton.Visibility = Visibility.Collapsed;
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
            try
            {
                DragMove();
            }
            catch
            {
                /* ignore */
            }
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

    private bool _isSourceChanging = false;

    private void SourceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isSourceChanging) return;
        _isSourceChanging = true;

        try
        {
            if (_isRecording && _waveIn != null)
            {
                var wasRecording = _isRecording;
                
                // Reset UI state
                Dispatcher.BeginInvoke(() =>
                {
                    _recordTimer?.Stop();
                    _elapsed = TimeSpan.Zero;
                    RecordingTimeText.Text = SoundRecorder.Properties.Resources.RecordingTime_Initial;
                    RightLevelMeter.Value = 0;
                    LeftLevelMeter.Value = 0;
                }).Wait(); // Wait for UI updates to complete

                // Clean up previous recording
                CleanupRecording(true);

                // Start new recording
                if (wasRecording)
                {
                    StartRecording();
                }
            }
        }
        finally
        {
            _isSourceChanging = false;
        }
    }
}