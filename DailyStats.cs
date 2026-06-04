using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace KeyboardCounter;

/// <summary>
/// 每日输入统计数据处理类
/// </summary>
public class DailyStats
{
    private readonly string _filePath;
    private Dictionary<string, DailyData> _data = new();

    public DailyStats(string filePath)
    {
        _filePath = filePath;
        Load();
    }

    /// <summary>
    /// 获取日期 Key（格式：yyyy-MM-dd）
    /// </summary>
    private static string GetDateKey(DateTime date) => date.ToString("yyyy-MM-dd");

    /// <summary>
    /// 从文件加载所有数据
    /// </summary>
    private void Load()
    {
        if (!File.Exists(_filePath))
            return;

        try
        {
            var json = File.ReadAllText(_filePath);
            var loaded = JsonSerializer.Deserialize<Dictionary<string, DailyData>>(json);
            if (loaded != null)
                _data = loaded;
        }
        catch
        {
            // 文件损坏时使用空数据
            _data = new Dictionary<string, DailyData>();
        }
    }

    /// <summary>
    /// 保存所有数据到文件
    /// </summary>
    private void SaveToFile()
    {
        try
        {
            var json = JsonSerializer.Serialize(_data, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            File.WriteAllText(_filePath, json);
        }
        catch
        {
            // 忽略写入错误
        }
    }

    /// <summary>
    /// 获取指定日期的数据
    /// </summary>
    public (int total, int space, int enter) Get(DateTime date)
    {
        var key = GetDateKey(date);
        if (_data.TryGetValue(key, out var data))
            return (data.Total, data.Space, data.Enter);
        return (0, 0, 0);
    }

    /// <summary>
    /// 保存指定日期的数据
    /// </summary>
    public void Save(DateTime date, int total, int space, int enter)
    {
        var key = GetDateKey(date);
        _data[key] = new DailyData { Total = total, Space = space, Enter = enter };
        SaveToFile();
    }

    /// <summary>
    /// 清零指定日期的数据
    /// </summary>
    public void Reset(DateTime date)
    {
        var key = GetDateKey(date);
        _data[key] = new DailyData { Total = 0, Space = 0, Enter = 0 };
        SaveToFile();
    }
}

/// <summary>
/// 每日数据结构
/// </summary>
public class DailyData
{
    public int Total { get; set; }
    public int Space { get; set; }
    public int Enter { get; set; }
}
