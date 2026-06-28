using SmartEdgeHMI.Common;

namespace SmartEdgeHMI.State;

/// <summary>应用设置服务: 管理配置的加载、运行时访问和持久化</summary>
public interface ISettingsService
{
    /// <summary>当前运行时设置快照</summary>
    AppSettings Current { get; }

    /// <summary>加载持久化配置(启动时调用)</summary>
    void LoadSettings();

    /// <summary>异步保存当前配置</summary>
    Task SaveAsync(CancellationToken token);
}
