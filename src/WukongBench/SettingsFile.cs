using System.Text;
using System.Text.RegularExpressions;

namespace WukongBench;

public sealed class SettingsFile
{
    private readonly string _path;
    private readonly string _backupPath;
    private List<string> _lines = [];
    private Encoding _encoding = Encoding.UTF8;
    private string _newline = "\r\n";

    /// <summary>
    /// Был ли в исходном файле маркер кодировки (BOM). Игра пишет конфиг без него,
    /// и добавлять три невидимых байта нельзя: она сверяет первую строку с эталоном,
    /// BOM ломает сравнение, и настройки сбрасываются на дефолтные.
    /// </summary>
    private bool _hasBom;

    private readonly Action<string> _log;

    public const string MainSection = "/Script/GSGameSettings.GSGameUserSettings";
    public const string ScalabilitySection = "ScalabilityGroups";

    /// <summary>Путь к конфигу.</summary>
    public string Path => _path;

    /// <summary>Путь к резервной копии.</summary>
    public string BackupPath => _backupPath;

    public SettingsFile(string path, Action<string>? log = null)
    {
        _path = path;
        _backupPath = path + ".wukongbench.bak";
        _log = log ?? (_ => { });
    }

    public void Load()
    {
        if (!File.Exists(_path))
            throw new FileNotFoundException(
                $"Не найден конфиг: {_path}. Запустите бенчмарк вручную хотя бы раз.");

        // BOM проверяем по байтам файла. Через CurrentEncoding это не определить:
        // и UTF-8 с BOM, и UTF-8 без BOM для StreamReader выглядят одинаково.
        var bytes = File.ReadAllBytes(_path);
        _hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;

        using var reader = new StreamReader(_path, Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true);
        var text = reader.ReadToEnd();

        _encoding = reader.CurrentEncoding;
        _newline = text.Contains("\r\n") ? "\r\n" : "\n";
        _lines = text.Replace("\r\n", "\n").Split('\n').ToList();
    }

    public void Save()
    {
        // Для UTF-8 .NET по умолчанию ДОПИСЫВАЕТ BOM, чего в файле игры не было.
        // Поэтому для UTF-8 кодировку создаём сами, с нужным флагом.
        // Всё остальное (UTF-16) пишем как есть: там BOM обязателен для определения кодировки.
        var encoding = _encoding.CodePage == Encoding.UTF8.CodePage
            ? new UTF8Encoding(encoderShouldEmitUTF8Identifier: _hasBom)
            : _encoding;

        File.WriteAllText(_path, string.Join(_newline, _lines), encoding);
    }

    private int FindSection(string section)
    {
        var index = _lines.FindIndex(l => l.Trim() == $"[{section}]");
        if (index < 0)
            throw new InvalidOperationException($"В конфиге нет секции [{section}].");
        return index;
    }

    public string GetIni(string section, string key)
    {
        var start = FindSection(section);

        for (var i = start + 1; i < _lines.Count && !_lines[i].TrimStart().StartsWith('['); i++)
        {
            if (_lines[i].StartsWith(key + "=", StringComparison.Ordinal))
                return _lines[i][(_lines[i].IndexOf('=') + 1)..].Trim();
        }

        throw new KeyNotFoundException($"В секции [{section}] нет ключа {key}.");
    }

    public void SetIni(string section, string key, string value)
    {
        var start = FindSection(section);

        for (var i = start + 1; i < _lines.Count && !_lines[i].TrimStart().StartsWith('['); i++)
        {
            if (_lines[i].StartsWith(key + "=", StringComparison.Ordinal))
            {
                _lines[i] = $"{key}={value}";
                return;
            }
        }

        throw new KeyNotFoundException(
            $"В секции [{section}] нет ключа {key}. Новые ключи не добавляем — " +
            "игра их может проигнорировать, а конфиг испортить.");
    }

    private int FindUiLine() =>
        _lines.FindIndex(l => l.StartsWith("UISettingData=", StringComparison.Ordinal));

    private static Regex UiPattern(string key) =>
        new($@"\(\s*""{Regex.Escape(key)}""\s*,\s*""(?<value>[^""]*)""\s*\)",
            RegexOptions.CultureInvariant);

    public string GetUi(string key)
    {
        var index = FindUiLine();
        if (index < 0) throw new InvalidOperationException("В конфиге нет строки UISettingData.");

        var match = UiPattern(key).Match(_lines[index]);
        if (!match.Success) throw new KeyNotFoundException($"В UISettingData нет ключа {key}.");

        return match.Groups["value"].Value;
    }

    public void SetUi(string key, string value)
    {
        var index = FindUiLine();
        if (index < 0) throw new InvalidOperationException("В конфиге нет строки UISettingData.");

        var pattern = UiPattern(key);
        if (!pattern.IsMatch(_lines[index]))
            throw new KeyNotFoundException($"В UISettingData нет ключа {key}.");

        _lines[index] = pattern.Replace(_lines[index], $"(\"{key}\", \"{value}\")", count: 1);
    }

    public void SetIniBatch(string section, IReadOnlyDictionary<string, string> values)
    {
        foreach (var (key, value) in values)
            SetIni(section, key, value);

        Save();
    }

    public void Backup()
    {
        File.Copy(_path, _backupPath, overwrite: true);
        _log($"Бэкап конфига: {_backupPath}");
    }

    public void Restore()
    {
        if (!File.Exists(_backupPath)) return;
        File.Copy(_backupPath, _path, overwrite: true);
        File.Delete(_backupPath);
        _log("Исходный конфиг восстановлен.");
    }

    public bool RecoverAfterCrash()
    {
        if (!File.Exists(_backupPath)) return false;
        _log("Найден бэкап от незавершённого прошлого запуска — восстанавливаю ваш конфиг.");
        Restore();
        return true;
    }
}