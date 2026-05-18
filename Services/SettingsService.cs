using System.IO;
using Newtonsoft.Json;
using YTNotifier.Models;

namespace YTNotifier.Services;

public class SettingsService
{
    private static SettingsService? _instance;
    public static SettingsService Instance => _instance ??= new SettingsService();

    private readonly string _appDataDir;
    private readonly string _configPath;
    private readonly string _channelsPath;

    public AppSettings Settings { get; private set; } = new();
    public List<ChannelInfo> Channels { get; private set; } = new();

    private SettingsService()
    {
        _appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "YTNotifier");
        Directory.CreateDirectory(_appDataDir);
        _configPath = Path.Combine(_appDataDir, "config.json");
        _channelsPath = Path.Combine(_appDataDir, "channels.json");
    }

    public string AppDataDir => _appDataDir;

    public void Load()
    {
        LoadSettings();
        LoadChannels();
    }

    private void LoadSettings()
    {
        try
        {
            if (File.Exists(_configPath))
            {
                var json = File.ReadAllText(_configPath);
                Settings = JsonConvert.DeserializeObject<AppSettings>(json) ?? new AppSettings();
            }
        }
        catch { Settings = new AppSettings(); }
    }

    private void LoadChannels()
    {
        try
        {
            if (File.Exists(_channelsPath))
            {
                var json = File.ReadAllText(_channelsPath);
                Channels = JsonConvert.DeserializeObject<List<ChannelInfo>>(json) ?? new List<ChannelInfo>();
            }
        }
        catch { Channels = new List<ChannelInfo>(); }
    }

    public void SaveSettings()
    {
        try
        {
            var json = JsonConvert.SerializeObject(Settings, Formatting.Indented);
            File.WriteAllText(_configPath, json);
        }
        catch { }
    }

    public void SaveChannels()
    {
        try
        {
            var json = JsonConvert.SerializeObject(Channels, Formatting.Indented);
            File.WriteAllText(_channelsPath, json);
        }
        catch { }
    }

    public void AddChannel(ChannelInfo channel)
    {
        if (!Channels.Any(c => c.ChannelId == channel.ChannelId))
        {
            Channels.Add(channel);
            SaveChannels();
        }
    }

    public void RemoveChannel(string channelId)
    {
        var ch = Channels.FirstOrDefault(c => c.ChannelId == channelId);
        if (ch != null)
        {
            Channels.Remove(ch);
            SaveChannels();
        }
    }

    public void UpdateChannel(ChannelInfo channel)
    {
        var idx = Channels.FindIndex(c => c.ChannelId == channel.ChannelId);
        if (idx >= 0)
        {
            Channels[idx] = channel;
            SaveChannels();
        }
    }

    public void MoveChannelUp(string channelId)
    {
        var idx = Channels.FindIndex(c => c.ChannelId == channelId);
        if (idx <= 0) return;
        (Channels[idx], Channels[idx - 1]) = (Channels[idx - 1], Channels[idx]);
        SaveChannels();
    }

    public void MoveChannelDown(string channelId)
    {
        var idx = Channels.FindIndex(c => c.ChannelId == channelId);
        if (idx < 0 || idx >= Channels.Count - 1) return;
        (Channels[idx], Channels[idx + 1]) = (Channels[idx + 1], Channels[idx]);
        SaveChannels();
    }
}
