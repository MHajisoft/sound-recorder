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
using SoundRecorder.Models;
using SoundRecorder.Services;
using File = System.IO.File;

namespace SoundRecorder;

public partial class MainWindow
{
    private WaveInEvent _waveIn;
    private LameMP3FileWriter _mp3Writer;
    private string _outputFilePath;
    private const string DataFilePath = "appData.json";
    private bool _isRecording; // Track recording state
    private readonly DispatcherTimer _recordTimer;
    private TimeSpan _elapsed;
    private bool _isSourceChanging;

    private AppSettings _appSettings;
    private AppData _appData;

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

        if (_appSettings is { AutoStartRecording: true })
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
                _appSettings = JsonConvert.DeserializeObject<AppSettings>(json) ?? new AppSettings();
            }
            else
            {
                _appSettings = new AppSettings();
            }

            // Set defaults if missing
            if (string.IsNullOrWhiteSpace(_appSettings.SavePath))
                _appSettings.SavePath = defaultSavePath;
        }
        catch
        {
            _appSettings = new AppSettings { SavePath = defaultSavePath };
        }
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
            _appData = JsonConvert.DeserializeObject<AppData>(json) ?? new AppData();
        }
        else
        {
            _appData = new AppData();
        }

        GenreComboBox.ItemsSource = _appData.Genres;
        SingerComboBox.ItemsSource = _appData.Singers;
        AlbumComboBox.ItemsSource = _appData.Albums;
    }

    private void SaveStoredData()
    {
        File.WriteAllText(DataFilePath, JsonConvert.SerializeObject(_appData, Formatting.Indented));
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
        var singer = string.IsNullOrWhiteSpace(SingerComboBox.Text) ? Properties.Resources.Unknown_Singer : SingerComboBox.Text.Trim();
        FileNameTextBox.Text = $"{singer}-{DateService.GetPersianDate(DateTime.Now)}";
    }

    private void SetButtonStates(bool isRecording)
    {
        StartButton.Visibility = isRecording ? Visibility.Collapsed : Visibility.Visible;
        StopButton.Visibility = isRecording ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetRecordingUi(bool recording)
    {
        Dispatcher.BeginInvoke(() => SetButtonStates(recording));
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

        var baseName = SanitizeFileName(string.IsNullOrWhiteSpace(FileNameTextBox.Text) ? Properties.Resources.Unknown_Singer : FileNameTextBox.Text.Trim());
        var initialPath = Path.Combine(_appSettings.SavePath, $"{baseName}.mp3");
        _outputFilePath = GetUniquePath(initialPath);

        var started = false;
        Exception lastError = null;

        try
        {
            _waveIn = new WaveInEvent
            {
                DeviceNumber = SourceComboBox.SelectedIndex,
                WaveFormat = new WaveFormat(_appSettings.SampleRateHz, 16, 2)
            };

            _mp3Writer = new LameMP3FileWriter(_outputFilePath, _waveIn.WaveFormat, _appSettings.Mp3BitrateKbps);

            _waveIn.DataAvailable += OnDataAvailable;
            _waveIn.RecordingStopped += OnRecordingStopped;

            _waveIn.StartRecording();
            _isRecording = true;

            SetRecordingUi(true);
            started = true;

            // Start the on-screen HH:MM:SS counter
            _elapsed = TimeSpan.Zero;
            RecordingTimeText.Text = Properties.Resources.RecordingTime_Initial;
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
            MessageBox.Show(string.Format(Properties.Resources.Error_Recording_Format, lastError?.Message), Properties.Resources.Error_Title, MessageBoxButton.OK, MessageBoxImage.Error);
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

    private void OnRecordingStopped(object sender, StoppedEventArgs e)
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
                MessageBox.Show(string.Format(Properties.Resources.Error_Recording_Format, e.Exception.Message), Properties.Resources.Error_Title, MessageBoxButton.OK, MessageBoxImage.Error);
                SetButtonStates(false);
            });
            return;
        }

        // Determine desired filename from UI and rename if needed
        var uiTitle = string.Empty;
        var uiFileName = string.Empty;
        var uiGenre = string.Empty;
        var uiSinger = string.Empty;
        var uiAlbum = string.Empty;
        Dispatcher.Invoke(() =>
        {
            uiTitle = GenreComboBox.Text;
            uiFileName = FileNameTextBox.Text;
            uiGenre = GenreComboBox.Text;
            uiSinger = SingerComboBox.Text;
            uiAlbum = AlbumComboBox.Text;
        });

        try
        {
            var desiredBase = SanitizeFileName(string.IsNullOrWhiteSpace(uiFileName) ? Properties.Resources.Unknown_File : uiFileName.Trim());
            var albumFolder = SanitizePathSegmentForFolder(string.IsNullOrWhiteSpace(uiAlbum) ? Properties.Resources.Unknown_Album : uiAlbum.Trim(), Properties.Resources.Unknown_Album);
            var albumDir = Path.Combine(_appSettings.SavePath, albumFolder);
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
                file.Tag.Title = $"{uiTitle}-{DateService.GetPersianDate(DateTime.Now)}";
                file.Tag.Genres = [string.IsNullOrWhiteSpace(uiGenre) ? Properties.Resources.Default_Genre : uiGenre];
                file.Tag.Performers = [string.IsNullOrWhiteSpace(uiSinger) ? Properties.Resources.Unknown_Singer : uiSinger];
                file.Tag.Album = string.IsNullOrWhiteSpace(uiAlbum) ? Properties.Resources.Unknown_Album : uiAlbum;

                // Set Year tag using Persian calendar
                file.Tag.Year = DateService.GetPersianYear(DateTime.Now);

                file.Save();
            }

            // Add final values to lists once recording finished
            if (!string.IsNullOrWhiteSpace(uiGenre) && !_appData.Genres.Contains(uiGenre))
            {
                _appData.Genres.Add(uiGenre);
            }

            if (!string.IsNullOrWhiteSpace(uiSinger) && !_appData.Singers.Contains(uiSinger))
            {
                _appData.Singers.Add(uiSinger);
            }

            if (!string.IsNullOrWhiteSpace(uiAlbum) && !_appData.Albums.Contains(uiAlbum))
            {
                _appData.Albums.Add(uiAlbum);
            }

            SaveStoredData();

            Dispatcher.BeginInvoke(() =>
            {
                // Refresh dropdowns to reflect newly added entries after final save
                GenreComboBox.ItemsSource = null;
                GenreComboBox.ItemsSource = _appData.Genres;
                SingerComboBox.ItemsSource = null;
                SingerComboBox.ItemsSource = _appData.Singers;
                AlbumComboBox.ItemsSource = null;
                AlbumComboBox.ItemsSource = _appData.Albums;

                SetButtonStates(false);

                if (_appSettings.CloseAfterSave)
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
                SetButtonStates(false);
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

            // Immediately reflect UI state
            SetButtonStates(false);
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
            _appSettings.CloseAfterSave = true; // trigger shutdown after RecordingStopped completes save/tagging
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