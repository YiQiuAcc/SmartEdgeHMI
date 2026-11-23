using SmartEdgeHMI.Common;

namespace SmartEdgeHMI.Core.Services;

/// <summary>应用设置服务: 负责配置的加载、运行时访问与持久化</summary>
public interface ISettingsService
{
    /// <summary>当前运行时设置实例(非副本), 调用方修改字段后需显式调用 <see cref="SaveAsync"/></summary>
    AppSettings Current { get; }

    /// <summary>加载持久化配置; 文件缺失时按 appsettings.json 的默认值初始化并持久化</summary>
    void LoadSettings();

    /// <summary>异步保存当前配置, 采用临时文件与覆盖的原子写入</summary>
    Task SaveAsync(CancellationToken token);
}
