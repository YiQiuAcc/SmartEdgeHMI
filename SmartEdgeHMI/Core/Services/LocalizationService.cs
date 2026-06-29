using System.Globalization;
using System.Windows;
using Serilog;

namespace SmartEdgeHMI.Core.Services;

/// <summary>多语言国际化服务: 运行时切换资源字典, 无需重启应用</summary>
public class LocalizationService
{
    private const string StringsPath = "/Resources/Strings.{0}.xaml";

    public static LocalizationService Instance { get; } = new();

    public CultureInfo CurrentCulture { get; private set; } = new("zh-CN");

    public event Action? LanguageChanged;

    private readonly List<ResourceDictionary> _loadedDictionaries = [];

    /// <summary>切换运行时语言并加载对应资源字典</summary>
    public void SetLanguage(string cultureName)
    {
        CurrentCulture = new CultureInfo(cultureName);
        Thread.CurrentThread.CurrentCulture = CurrentCulture;
        Thread.CurrentThread.CurrentUICulture = CurrentCulture;

        var appResources = Application.Current.Resources.MergedDictionaries;

        foreach (var dict in _loadedDictionaries)
            appResources.Remove(dict);
        _loadedDictionaries.Clear();

        string path = string.Format(StringsPath, cultureName);
        var stringsDict = new ResourceDictionary { Source = new Uri(path, UriKind.RelativeOrAbsolute) };
        appResources.Add(stringsDict);
        _loadedDictionaries.Add(stringsDict);

        LanguageChanged?.Invoke();
        Log.Information("语言已切换: {Culture}", cultureName);
    }
}
