using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace SmartEdgeHMI.Shared;

/// <summary>Watchdog 心跳管道名: 按被测程序所在目录派生, 使不同安装实例与开发构建各自使用独立管道, 避免互相串台</summary>
public static class WatchdogPipeName
{
    /// <summary>管道名前缀</summary>
    public const string Prefix = "SmartEdgeHMI_Watchdog_Pipe_";

    /// <summary>按目录计算管道名</summary>
    public static string For(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new ArgumentNullException(nameof(directory));
        }

        // 用 TrimEndingDirectorySeparator 而不是 TrimEnd('\\'), 避免把 "C:\" 削成 "C:"; 小写因为 Windows 路径大小写不敏感
        string key = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)).ToLowerInvariant();
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        string suffix = Convert.ToHexString(hash)[..16].ToLowerInvariant();
        return Prefix + suffix;
    }
}
