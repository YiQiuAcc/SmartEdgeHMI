using System.Globalization;
using System.Windows;
using Serilog;

namespace SmartEdgeHMI.Core.Services;

/// <summary>多语言服务: 运行时替换 WPF 资源字典, 无需重启应用</summary>
/// <remarks>单例, 资源字典注册在 <see cref="Application.Current"/> 的全局资源上, 因此只在 UI 线程读写</remarks>
public class LocalizationService
{
    private const string StringsPath = "/Resources/Strings.{0}.xaml";

    public static LocalizationService Instance { get; } = new();

    public CultureInfo CurrentCulture { get; private set; } = new("zh-CN");

    public event Action? LanguageChanged;

    private readonly List<ResourceDictionary> _loadedDictionaries = [];

    /// <summary>切换运行时语言并加载对应资源字典</summary>
    /// <remarks>
    /// 必须在 UI 线程调用。只更新调用线程的 Culture 与 UI Culture, 已运行的后台线程沿用旧 Culture,
    /// 因此后台线程上的数字/日期格式化不会随语言切换而变化。
    /// </remarks>
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
