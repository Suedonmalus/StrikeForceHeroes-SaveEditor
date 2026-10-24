using System.IO;
using System.Reflection;
using System.Security.Cryptography;

namespace SFHTrainer.Injection;

/// <summary>
/// 代理 DLL 以嵌入资源随 exe 一起分发；注入前释放到用户本地目录。
/// 目标：用户下载到的只有一个 SFHTrainer.exe。
/// </summary>
public static class AgentPayload
{
    const string ResourceName = "SFHTrainer.Resources.sfh_agent.dll";

    /// <summary>本次运行使用的 DLL 是否来自嵌入资源（false = 回退到程序同目录的外部 DLL）。</summary>
    public static bool IsEmbedded { get; private set; }

    /// <summary>释放（或定位）代理 DLL，返回可注入的绝对路径。</summary>
    public static string EnsureExtracted()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(ResourceName);
        if (stream is null)
        {
            // 兼容旧的分发方式：同目录放 sfh_agent.dll
            IsEmbedded = false;
            return Path.Combine(AppContext.BaseDirectory, "sfh_agent.dll");
        }

        IsEmbedded = true;
        byte[] bytes;
        using (var ms = new MemoryStream())
        {
            stream.CopyTo(ms);
            bytes = ms.ToArray();
        }

        var hash = Convert.ToHexString(SHA256.HashData(bytes))[..16].ToLowerInvariant();
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SFHTrainer", "agent");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"agent_{hash}.dll");

        var needWrite = true;
        try
        {
            if (File.Exists(path) && new FileInfo(path).Length == bytes.Length) needWrite = false;
        }
        catch { /* 读不到就当需要重写 */ }

        if (needWrite)
        {
            var tmp = path + ".tmp";
            File.WriteAllBytes(tmp, bytes);
            try { File.Move(tmp, path, overwrite: true); }
            catch
            {
                try { File.Delete(tmp); } catch { }
                if (!File.Exists(path)) throw;
            }
        }

        CleanupOldVersions(dir, Path.GetFileName(path));
        return path;
    }

    /// <summary>清理旧版本释放出来的 agent_*.dll（正在被游戏加载的删不掉，忽略）。</summary>
    static void CleanupOldVersions(string dir, string keepFileName)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir, "agent_*.dll"))
            {
                if (string.Equals(Path.GetFileName(file), keepFileName, StringComparison.OrdinalIgnoreCase)) continue;
                try { File.Delete(file); } catch { /* 被占用就留着，下次再删 */ }
            }
        }
        catch { /* 清理失败不影响主流程 */ }
    }
}
