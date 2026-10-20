using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using TarkovTacticalMap.Core.Models;

namespace TarkovTacticalMap.App.Services;

/// <summary>
/// 本地设置持久化：%LocalAppData%/TarkovTacticalMap/settings.json
/// 保存上次端口、玩家名、上次房主 IP、默认颜色/大小等。
/// </summary>
public sealed class AppSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string PlayerName { get; set; } = Environment.UserName;

    public string LastHostIp { get; set; } = "127.0.0.1";

    public int LastPort { get; set; } = 27015;

    public string DefaultColor { get; set; } = MarkerDefaults.DefaultColor;

    public float DefaultPointSize { get; set; } = MarkerDefaults.PointSize;

    /// <summary>一级网格密度（每边最多格数）。</summary>
    public int GridDensity { get; set; } = MapInfo.DefaultGridDensity;

    public string? LastImagePath { get; set; }

    /// <summary>设置文件路径。</summary>
    public static string DirectoryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TarkovTacticalMap");

    public static string FilePath => Path.Combine(DirectoryPath, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new AppSettings();
            var json = File.ReadAllText(FilePath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
            if (settings is null) return new AppSettings();
            settings.Normalize();
            return settings;
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save()
    {
        try
        {
            Normalize();
            Directory.CreateDirectory(DirectoryPath);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch
        {
            // 设置保存失败不影响使用
        }
    }

    private void Normalize()
    {
        PlayerName = string.IsNullOrWhiteSpace(PlayerName) ? Environment.UserName : PlayerName.Trim();
        if (PlayerName.Length > 24) PlayerName = PlayerName[..24];
        LastHostIp = string.IsNullOrWhiteSpace(LastHostIp) ? "127.0.0.1" : LastHostIp.Trim();
        if (LastPort is < 1 or > 65535) LastPort = 27015;
        if (!Core.Utils.ColorHex.IsValid(DefaultColor)) DefaultColor = MarkerDefaults.DefaultColor;
        DefaultPointSize = Math.Clamp(DefaultPointSize, 0.002f, 0.2f);
        GridDensity = Math.Clamp(GridDensity, 2, MapInfo.MaxGrid);
    }
}
