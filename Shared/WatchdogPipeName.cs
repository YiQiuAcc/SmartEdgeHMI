using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace SmartEdgeHMI.Shared;

/// <summary>Watchdog 心跳管道名: 按被测程序所在目录派生, 使不同安装实例与开发构建各用独立管道, 避免互相影响</summary>
/// <remarks>
/// 两侧必须传入同一个目录: HMI 侧用 <c>AppContext.BaseDirectory</c>, Watchdog 侧用 HMI 可执行文件所在目录,
/// 任一方的取值方式改变都会导致管道名不匹配, 后缀取 SHA256 前 16 个十六进制字符。
/// </remarks>
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
        // 用 TrimEndingDirectorySeparator 而非 TrimEnd('\\'), 避免把 "C:\" 截断为 "C:"; 小写是因为 Windows 路径大小写不敏感
        string key = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)).ToLowerInvariant();
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        string suffix = Convert.ToHexString(hash)[..16].ToLowerInvariant();
        return Prefix + suffix;
    }
}
