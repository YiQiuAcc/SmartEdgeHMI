using System.Globalization;
using System.Windows;
using Serilog;

namespace SmartEdgeHMI.Infrastructure;

public class LocalizationService
{
    private const string StringsPath = "/Resources/Strings.{0}.xaml";

    public static LocalizationService Instance { get; } = new();

    public CultureInfo CurrentCulture { get; private set; } = new("zh-CN");

    public event Action? LanguageChanged;

    private readonly List<ResourceDictionary> _loadedDictionaries = [];

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
