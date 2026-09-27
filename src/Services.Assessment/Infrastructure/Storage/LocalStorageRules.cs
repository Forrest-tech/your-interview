namespace YourInterview.Services.Assessment.Infrastructure.Storage;

/// <summary>
/// 本机存储路径的**判定规则** —— 路径归一化、可写性探测、容器内挂载校验。
///
/// 为什么单独成一类(2026-09-27,Forrest):
///   录音"消失"这类事故,根因从来不是写文件失败,而是**写成功了但写到了
///   一个 Mac 上看不见的地方**(容器内部的 overlay 目录)。
///   所以"这个目录能不能用"必须是**一套共享的判定**,设置页与实际写盘
///   走同一份代码 —— 不能界面显示"已生效"、落盘却去了别处。
///
/// 判定结果只有两种:
///   · Usable  = 写进去的文件在本机(Mac)文件系统里能直接看到;
///   · 不可用  = 一律退回部署默认目录,并把原因如实告诉用户。
/// </summary>
public static class LocalStorageRules
{
    /// <summary>
    /// 本服务在共用 recordings 根目录下使用的子目录名。
    /// 与 docker-compose 里 `Storage__RootDirectory=/app/storage/assessment` 一致 ——
    /// 各服务互不串目录(将来 interviews 的视频放 ../recordings/interviews)。
    /// </summary>
    public const string ServiceFolder = "assessment";

    /// <summary>是否在容器里跑(Docker / k8s)。原生 dotnet run 时为 false。</summary>
    public static bool RunningInContainer { get; } =
        File.Exists("/.dockerenv")
        || Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true";

    /// <summary>
    /// 归一化路径:去首尾空白与成对引号、反斜杠转正斜杠、展开 `~`、去掉结尾斜杠。
    /// 用户手输的路径什么形状都有(`~/Documents/…`、粘贴时带引号、Windows 反斜杠),
    /// 不归一化就会在字符串比较时永远"不相等",界面就会一直提示未生效。
    /// </summary>
    public static string Normalize(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        var p = raw.Trim().Trim('"').Trim('\'').Trim();
        p = p.Replace('\\', '/');

        if (p == "~" || p.StartsWith("~/", StringComparison.Ordinal))
        {
            // ⚠️ 容器里 HOME 通常是 /root —— 展开后**不会**被判定为可用(不在挂载点下),
            //    所以这里不会造成"写进容器内部"的事故,只是让用户看到明确的提示。
            var home = Environment.GetEnvironmentVariable("HOME");
            if (string.IsNullOrWhiteSpace(home))
                home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            p = p == "~" ? home : Path.Combine(home, p[2..]);
        }

        // 折叠重复的斜杠(用户粘贴 "//Users//jaden" 也照样认)
        while (p.Contains("//", StringComparison.Ordinal)) p = p.Replace("//", "/");
        if (!p.StartsWith('/')) p = "/" + p;
        return p.Length > 1 ? p.TrimEnd('/') : p;
    }

    /// <summary>把"用户选的共用 recordings 根目录"转成**本服务**实际写盘的根目录。</summary>
    public static string AudioRootFor(string recordingsRoot) =>
        Path.Combine(Normalize(recordingsRoot), ServiceFolder);

    /// <summary>去掉服务子目录,还原成用户视角的 recordings 根目录(仅用于展示)。</summary>
    public static string TrimServiceFolder(string audioRoot)
    {
        var normalized = Normalize(audioRoot);
        var suffix = "/" + ServiceFolder;
        return normalized.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? normalized[..^suffix.Length]
            : normalized;
    }

    /// <summary>
    /// 综合判定:这个目录**能不能用来存录音**。
    /// 容器里额外要求"必须位于宿主机 bind mount 之下" —— 否则文件会落在容器内部,
    /// 写盘日志一切正常,但 Mac 上永远找不到(2026-09-27 事故的根因)。
    /// </summary>
    public static (bool Usable, string? Reason) Evaluate(string audioRoot)
    {
        if (string.IsNullOrWhiteSpace(audioRoot))
            return (false, "路径为空。");
        if (!audioRoot.StartsWith('/'))
            return (false, "请填写绝对路径(以 / 开头)。");

        // ⚠️ 顺序很重要:**先判挂载,再探测写入**。
        //    反过来的话,"探测"会在容器内部真的把目录建出来 ——
        //    留下一个 Mac 上永远看不到、还会让排查更混乱的空目录。
        if (RunningInContainer && !IsUnderHostMount(audioRoot))
            return (false,
                "这个目录在容器内部,MAC 上看不到 —— 需要先把本机目录挂载进容器(见页面提示)。");

        var (writable, error) = TryEnsureWritable(audioRoot);
        return writable ? (true, null) : (false, error ?? "目录不可写。");
    }

    /// <summary>
    /// 真去写一次探针文件 —— 只检查"字符串非空"毫无意义,和 2026-09-16 语音密钥
    /// "填错也显示保存成功"是同一类骗人。目录不存在就建,建不了/写不了都如实报原因。
    /// </summary>
    public static (bool Ok, string? Error) TryEnsureWritable(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, ".write-probe.tmp");
            File.WriteAllText(probe, "probe");
            File.Delete(probe);
            return (true, null);
        }
        catch (UnauthorizedAccessException)
        {
            return (false, "没有写入权限,请换一个目录。");
        }
        catch (IOException ex)
        {
            return (false, "目录创建/写入失败:" + ex.Message);
        }
        catch (Exception ex)
        {
            return (false, "目录不可用:" + ex.Message);
        }
    }

    /// <summary>
    /// 该路径是否位于**宿主机挂载进来的**目录之下(读 /proc/self/mountinfo)。
    /// 只在容器内调用有意义;原生运行时一律视为"本机真实目录"。
    /// </summary>
    public static bool IsUnderHostMount(string path)
    {
        var normalized = Normalize(path);
        foreach (var mount in HostMountPoints())
        {
            if (normalized == mount || normalized.StartsWith(mount + "/", StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    /// <summary>
    /// 容器里**真实的用户数据挂载点**(过滤掉 /proc /sys /dev /etc 这些 Docker 自己塞进来的)。
    /// mountinfo 第 5 列(0 基第 4 列)是挂载点,可能带 \040 这类转义。
    /// </summary>
    public static IReadOnlyList<string> HostMountPoints()
    {
        var result = new List<string>();
        try
        {
            const string file = "/proc/self/mountinfo";
            if (!File.Exists(file)) return result;

            foreach (var line in File.ReadLines(file))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 5) continue;

                var point = Unescape(parts[4]);
                if (point.Length <= 1) continue;                       // "/" 之类整盘挂载不算
                if (point.StartsWith("/proc", StringComparison.Ordinal)) continue;
                if (point.StartsWith("/sys", StringComparison.Ordinal)) continue;
                if (point.StartsWith("/dev", StringComparison.Ordinal)) continue;
                if (point.StartsWith("/etc", StringComparison.Ordinal)) continue;   // resolv.conf / hosts …
                if (point.StartsWith("/var/run", StringComparison.Ordinal)) continue; // docker.sock …

                result.Add(point);
            }
        }
        catch
        {
            // 读不到挂载信息 ≠ 目录可用:返回空表,调用方会判定为"容器内部路径",
            // 于是继续用部署默认目录 —— 保守但不会把录音写丢。
        }
        return result;
    }

    private static string Unescape(string s) =>
        s.Replace(@"\040", " ").Replace(@"\011", "\t").Replace(@"\012", "\n").Replace(@"\134", "\\");

    /// <summary>
    /// 统计目录下的文件数与总字节数(设置页展示用)。
    /// 目录不存在/没权限 → 如实给 0,不抛异常(展示信息不该让接口 500)。
    /// </summary>
    public static (long Files, long Bytes) Measure(string root)
    {
        long files = 0, bytes = 0;
        try
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return (0, 0);
            foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                try
                {
                    files++;
                    var info = new FileInfo(f);
                    bytes += info.Length;
                }
                catch { /* 单个文件读不到不中断统计 */ }
            }
        }
        catch { /* 目录整体读不到 → 0 */ }
        return (files, bytes);
    }
}
