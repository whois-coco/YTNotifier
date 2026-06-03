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
    private readonly string _categoriesPath;

    public AppSettings Settings { get; private set; } = new();
    public List<ChannelInfo> Channels { get; private set; } = new();
    public List<CategoryInfo> Categories { get; private set; } = new();

    private SettingsService()
    {
        _appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "YTNotifier");
        Directory.CreateDirectory(_appDataDir);
        _configPath      = Path.Combine(_appDataDir, "config.json");
        _channelsPath    = Path.Combine(_appDataDir, "channels.json");
        _categoriesPath  = Path.Combine(_appDataDir, "categories.json");
    }

    public string AppDataDir => _appDataDir;

    private void LoadCategories()
    {
        try
        {
            if (File.Exists(_categoriesPath))
            {
                var json = File.ReadAllText(_categoriesPath);
                Categories = JsonConvert.DeserializeObject<List<CategoryInfo>>(json) ?? new();
            }
        }
        catch { Categories = new(); }
    }

    public void SaveCategories()
    {
        try
        {
            var json = JsonConvert.SerializeObject(Categories, Formatting.Indented);
            File.WriteAllText(_categoriesPath, json);
        }
        catch { }
    }

    public CategoryInfo AddCategory(string name)
    {
        var cat = new CategoryInfo
        {
            CategoryId = Guid.NewGuid().ToString(),
            CategoryName = name,
            SortOrder = Categories.Count
        };
        Categories.Add(cat);
        SaveCategories();
        return cat;
    }

    public void RemoveCategory(string categoryId)
    {
        // カテゴリ削除時は所属チャンネルを未分類に
        foreach (var ch in Channels.Where(c => c.CategoryId == categoryId))
            ch.CategoryId = null;
        Categories.RemoveAll(c => c.CategoryId == categoryId);
        SaveCategories();
        SaveChannels();
    }

    public void RenameCategory(string categoryId, string newName)
    {
        var cat = Categories.FirstOrDefault(c => c.CategoryId == categoryId);
        if (cat == null) return;
        cat.CategoryName = newName;
        SaveCategories();
    }

    public void SetChannelCategory(string channelId, string? categoryId)
    {
        var ch = Channels.FirstOrDefault(c => c.ChannelId == channelId);
        if (ch == null) return;
        ch.CategoryId = categoryId;
        SaveChannels();
    }

    public void Load()
    {
        LoadSettings();
        LoadChannels();
        LoadCategories();
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
