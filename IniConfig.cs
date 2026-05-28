using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace KeyboardCounter;

/// <summary>
/// INI 配置文件处理类
/// </summary>
public class IniConfig
{
    private readonly string _filePath;
    private readonly Dictionary<string, Dictionary<string, string>> _data = new();

    public IniConfig(string filePath)
    {
        _filePath = filePath;
        Load();
    }

    private void Load()
    {
        if (!File.Exists(_filePath))
        {
            return;
        }

        string? currentSection = null;
        foreach (var line in File.ReadAllLines(_filePath))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith(";") || trimmed.StartsWith("#"))
                continue;

            if (trimmed.StartsWith("[") && trimmed.EndsWith("]"))
            {
                currentSection = trimmed[1..^1];
                if (!_data.ContainsKey(currentSection))
                    _data[currentSection] = new Dictionary<string, string>();
            }
            else if (currentSection != null && trimmed.Contains('='))
            {
                var parts = trimmed.Split('=', 2);
                if (parts.Length == 2)
                {
                    _data[currentSection][parts[0].Trim()] = parts[1].Trim();
                }
            }
        }
    }

    public void Save()
    {
        var sb = new StringBuilder();
        foreach (var section in _data)
        {
            sb.AppendLine($"[{section.Key}]");
            foreach (var kv in section.Value)
            {
                sb.AppendLine($"{kv.Key}={kv.Value}");
            }
            sb.AppendLine();
        }
        File.WriteAllText(_filePath, sb.ToString());
    }

    public string GetString(string section, string key, string defaultValue = "")
    {
        if (_data.TryGetValue(section, out var sec) && sec.TryGetValue(key, out var value))
            return value;
        return defaultValue;
    }

    public void SetString(string section, string key, string value)
    {
        if (!_data.ContainsKey(section))
            _data[section] = new Dictionary<string, string>();
        _data[section][key] = value;
    }

    public bool GetBool(string section, string key, bool defaultValue = false)
    {
        var value = GetString(section, key, defaultValue.ToString().ToLower());
        return value.Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    public void SetBool(string section, string key, bool value)
    {
        SetString(section, key, value.ToString().ToLower());
    }
}
