namespace SoundRecorder.Models;

public class AppSettings
{
    public string SavePath { get; set; }
    public bool AutoStartRecording { get; set; }
    public bool CloseAfterSave { get; set; }
    public int SampleRateHz { get; set; } = 44100;
    public int Mp3BitrateKbps { get; set; } = 128;
}

public class AppData
{
    public List<string> Genres { get; set; } = [];
    public List<string> Singers { get; set; } = [];
    public List<string> Albums { get; set; } = [];
}

