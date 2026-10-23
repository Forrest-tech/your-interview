using Microsoft.Extensions.Configuration;

namespace YourInterview.BuildingBlocks.Storage;

/// <summary>
/// 文件存储 adapter(ARC-12,票39)——「落盘 + 库里存相对引用」的**单一实现**。
///
/// 收编前同一套设计被写了三遍(根目录解析各写各的):
///   · Assessment <c>LocalAudioStore</c>(默认根解析);
///   · Interviews <c>LocalInterviewAudioStore</c>(根解析);
///   · Analysis.Worker <c>InterviewAnalysisConsumer</c>(Interviews 根解析的第三份拷贝)。
/// 本文件是根目录解析、相对路径约定与原子落盘的**唯一实现处**;
/// 各 Store 只保留自己的接口与业务策略,做本 adapter 上的薄封装。
///
/// ## 两条根解析链是「参数不同、机制同一」(刻意保留,不强行合一)
///
/// 两服务部署口径不同(compose 注入的键/值不同),链**顺序与键名逐字保留**:
///   · Assessment 链:<see cref="ResolveAssessmentDefaultRoot(IConfiguration)"/>;
///   · Interviews 链:<see cref="ResolveInterviewsRoot(IConfiguration)"/>
///     (Interviews 服务与 Analysis.Worker 共用同一条,两边拼出的才是同一个文件)。
/// 改链 = 改这里一处,禁止再在服务里另写解析逻辑。
///
/// ## Jobs 不用本 adapter(口径差异,票面既定)
///
/// Jobs 的简历文件**不落盘**:字节直接存数据库(ResumeFileHandlers)。
/// 它没有根目录、没有相对路径,与本 adapter 的「落盘+库引用」模型不同构,
/// 故**刻意不收编**——日后 Jobs 若改落盘,应直接用本 adapter,而不是再写一份。
///
/// ## 相对路径约定:统一规范守卫(票94 安全加固,supersede 票39 的差异保留)
///
/// 票39 收编时两服务口径不同(Interviews 有守卫但以 ".." **子串**粗判、Assessment 无守卫),
/// 当时按行为不变原则原样锁定,并注明「补守卫属安全口径变更,须另行立票」。
/// 票94 即该立票的执行:两服务读/删路径统一走 <see cref="StorageRelativePaths.ResolveUnderRoot"/>
/// 的**规范判定**——相对路径经分隔符归一与 <see cref="Path.GetFullPath(string)"/> 规范化后,
/// 结果必须严格位于根目录之内,否则拒绝(null):
///   · Assessment 读/删(<see cref="StorageRelativePaths.CombineUnderRoot"/>)自票94 起同样带守卫;
///   · Interviews(<see cref="StorageRelativePaths.ResolveGuarded"/>)自票94 起不再用子串粗判:
///     `a..b` 这类仅文件名含 ".." 子串、规范化后仍在根内的合法名**放行**,真穿越仍拒绝。
/// Analysis.Worker 的事件路径信任口径不在此列(服务内部事件、信发送方,票94 盘点单列说明,不改)。
/// </summary>
public static class StorageRootResolver
{
    // ---------------- Assessment 链 ----------------

    /// <summary>
    /// Assessment 默认根目录。优先级:
    /// `Storage:RootDirectory` 配置 > `PRACTICE_STORAGE_DIR` 环境变量 > 仓库同级 recordings/assessment。
    /// 未显式配置时落到一个**可预测**的目录,而不是随机临时目录 —— 否则重启后找不到文件。
    /// </summary>
    public static string ResolveAssessmentDefaultRoot(IConfiguration config) =>
        ResolveAssessmentDefaultRoot(
            config["Storage:RootDirectory"],
            Environment.GetEnvironmentVariable("PRACTICE_STORAGE_DIR"),
            Directory.GetCurrentDirectory());

    /// <summary>Assessment 链纯逻辑核心(入参齐备版,供单测逐级锁链)。</summary>
    public static string ResolveAssessmentDefaultRoot(
        string? configured, string? envValue, string currentDirectory)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return configured;
        if (!string.IsNullOrWhiteSpace(envValue)) return envValue;

        // 兜底(2026-09-27 口径):录音**不能放进仓库里面**(防止误提交 GitHub),
        // 统一放「仓库的同级 recordings/assessment/」—— 仓库在 ~/dev/your-interview,
        // 录音在 ~/dev/recordings/assessment。再加一层 assessment 子目录,是为了让
        // ../recordings 下能按服务分目录。向上找 .git 定位仓库根;找不到才退回 cwd 下 storage/。
        var gitRoot = FindGitRoot(currentDirectory);
        return gitRoot is null
            ? Path.Combine(currentDirectory, "storage", "assessment")
            : Path.GetFullPath(Path.Combine(gitRoot, "..", "recordings", "assessment"));
    }

    /// <summary>
    /// 从 <paramref name="startDirectory"/> 起向上找第一个含 `.git` **目录**的祖先
    /// (含起点本身);找不到返回 null。Assessment 兜底链专用语义,逐字保留。
    /// </summary>
    public static string? FindGitRoot(string startDirectory)
    {
        var dir = new DirectoryInfo(startDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, ".git")))
            dir = dir.Parent;
        return dir?.FullName;
    }

    // ---------------- Interviews 链 ----------------

    /// <summary>
    /// Interviews 存储根目录 —— Interviews 服务与 Analysis.Worker **共用本实现**
    /// (两处必须一致,相对路径拼出来才是同一个文件):
    ///   `Storage:InterviewsRoot` 配置 > `INTERVIEW_STORAGE_DIR` 环境变量 >
    ///   `{Storage:RootDirectory}/interviews` > 仓库根/storage/interviews > cwd 兜底。
    /// docker-compose 里 interviews 与 analysis-worker 挂**同一个卷**
    /// 到 /app/storage/interviews —— 上传方写、分析方读,路径天然一致。
    /// </summary>
    public static string ResolveInterviewsRoot(IConfiguration config) =>
        ResolveInterviewsRoot(
            config["Storage:InterviewsRoot"],
            Environment.GetEnvironmentVariable("INTERVIEW_STORAGE_DIR"),
            config["Storage:RootDirectory"],
            AppContext.BaseDirectory,
            Directory.GetCurrentDirectory());

    /// <summary>Interviews 链纯逻辑核心(入参齐备版,供单测逐级锁链)。</summary>
    public static string ResolveInterviewsRoot(
        string? dedicated, string? envValue, string? sharedRootDirectory,
        string baseDirectory, string currentDirectory)
    {
        // 1. 专用配置/环境变量(docker-compose 里 interviews 与 worker 共卷,两边都设它)
        if (!string.IsNullOrWhiteSpace(dedicated)) return dedicated;
        if (!string.IsNullOrWhiteSpace(envValue)) return envValue;

        // 2. 沙盒/统一 Storage 根派生:{Storage:RootDirectory}/interviews ——
        //    和 Assessment 的 {root}/{user}/… 同一层级,一个根管所有录音。
        if (!string.IsNullOrWhiteSpace(sharedRootDirectory))
            return Path.Combine(sharedRootDirectory, "interviews");

        // 3. 本地裸跑(dotnet run):锚定到**仓库根**而不是进程 cwd ——
        //    interviews 与 analysis-worker 的 cwd 不同,按 cwd 会指向两个地方。
        var solutionRoot = FindSolutionRoot(baseDirectory);
        if (solutionRoot is not null) return Path.Combine(solutionRoot, "storage", "interviews");

        return Path.Combine(currentDirectory, "storage", "interviews");
    }

    /// <summary>
    /// 从 <paramref name="startDirectory"/> 起向上找仓库根。优先含 docker-compose.yml
    /// 的目录(仓库根,.sln 常在 src/ 子目录里,只认 sln 会锚错一层),找不到 compose
    /// 再退回 sln。最多向上 8 层,找不到返回 null —— 只作兜底,不影响显式配置。
    /// 语义逐字保留:同类标记多层命中时取**最高**一层(向上遍历时后写覆盖)。
    /// </summary>
    public static string? FindSolutionRoot(string startDirectory)
    {
        DirectoryInfo? withCompose = null, withSln = null;
        var dir = new DirectoryInfo(startDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            if (dir.GetFiles("docker-compose.yml").Length > 0) withCompose = dir;
            if (dir.GetFiles("*.sln").Length > 0) withSln = dir;
        }
        return (withCompose ?? withSln)?.FullName;
    }
}

/// <summary>
/// 相对路径约定:入库一律存**正斜杠相对路径**(跨平台可移植、整个 storage 目录可整体搬走),
/// 落盘/读盘时再转回当前 OS 的分隔符。读/删路径的解析自票94 起统一走规范守卫
/// <see cref="ResolveUnderRoot"/>(见 <see cref="StorageRootResolver"/> 类注释的票94 段)。
/// </summary>
public static class StorageRelativePaths
{
    /// <summary>OS 相对路径 → 入库形态(反斜杠转正斜杠)。</summary>
    public static string ToPortableRelative(string relativePath) =>
        relativePath.Replace('\\', '/');

    /// <summary>
    /// 规范守卫(票94,两服务共用的唯一判定):相对路径 → 根目录内的绝对路径,越界返回 null。
    ///
    /// 判定流程:
    ///   1. 空/空白/含 NUL → null;
    ///   2. **URL 编码穿越**先行拦截:反复解码(至多 3 轮,挡双重编码)后的形态若越界,
    ///      原始形态同样拒绝 —— `%2e%2e%2f`、`..%2f`、`%2e%2e%5c` 不得借编码绕过;
    ///      解码仅用于判定,返回的仍是原始形态组合出的路径(字面 `%` 合法名不受影响);
    ///   3. 分隔符归一(`/` 与 `\` 都按分隔符处理,挡 Windows 形态 `..\..\` 在任何 OS 上的穿越),
    ///      rooted / 盘符 / UNC 形态一律拒绝(绝对路径不许借根组合胜出);
    ///   4. <see cref="Path.GetFullPath(string)"/> 规范化后必须**严格**位于根内
    ///      (带尾分隔符的前缀判定 —— 同前缀兄弟目录 `root2/` 不算根内;等于根本身不算文件,拒绝)。
    /// 规范化后仍在根内的路径放行 —— 含 `a..b` 文件名、`a/./b`、`a/../b` 这类形态
    /// (与票39 前 Interviews 的 ".." 子串粗判的区别正在于此,票94 票面批准的行为变化)。
    /// </summary>
    public static string? ResolveUnderRoot(string root, string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(relativePath)) return null;
        if (relativePath.Contains('\0')) return null;

        // URL 编码形态:解码后的形态若越界,按穿越拒绝(只判定、不拿解码结果拼路径)
        var decoded = DecodeRepeatedly(relativePath);
        if (!string.Equals(decoded, relativePath, StringComparison.Ordinal)
            && !TryCanonicalUnderRoot(root, decoded, out _))
            return null;

        return TryCanonicalUnderRoot(root, relativePath, out var full) ? full : null;
    }

    /// <summary>去掉路径尾部的分隔符(文件系统根 "/" 本身保留)。</summary>
    private static string TrimTrailingSeparator(string path)
    {
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return trimmed.Length == 0 ? path[..1] : trimmed;
    }

    /// <summary>反复 URL 解码至稳定(至多 3 轮);解码失败按原样返回,不因此拒绝字面 `%` 文件名。</summary>
    private static string DecodeRepeatedly(string value)
    {
        var current = value;
        for (var i = 0; i < 3; i++)
        {
            string next;
            try { next = Uri.UnescapeDataString(current); }
            catch (UriFormatException) { return current; }
            if (string.Equals(next, current, StringComparison.Ordinal)) return current;
            current = next;
        }
        return current;
    }

    /// <summary>规范化 + 根内判定核心:成功时 <paramref name="fullPath"/> 为根内的规范绝对路径。</summary>
    private static bool TryCanonicalUnderRoot(string root, string relativePath, out string? fullPath)
    {
        fullPath = null;
        if (string.IsNullOrWhiteSpace(relativePath) || relativePath.Contains('\0')) return false;

        // `/` 与 `\` 都按分隔符:入库形态是正斜杠,但外部带入的路径可能是 Windows 形态
        var unified = relativePath.Replace('\\', '/');
        if (unified.StartsWith('/')) return false;                                   // rooted / UNC(//…)
        if (unified.Length >= 2 && char.IsLetter(unified[0]) && unified[1] == ':')   // 盘符 C:/…
            return false;
        if (Path.IsPathRooted(relativePath) || Path.IsPathFullyQualified(relativePath)) return false;

        string fullRoot, candidate;
        try
        {
            fullRoot = Path.GetFullPath(root);
            candidate = Path.GetFullPath(Path.Combine(
                fullRoot, unified.Replace('/', Path.DirectorySeparatorChar)));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        // 去尾分隔符再比:防 "./" 这类输入规范化出的 "/root/" 与 "/root" 字符串不等而绕过「等于根」判定
        fullRoot = TrimTrailingSeparator(fullRoot);
        candidate = TrimTrailingSeparator(candidate);

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(candidate, fullRoot, comparison)) return false;   // 根本身不是文件
        // 文件系统根("/"/盘根)在 Trim 后自带尾分隔符,不再叠加
        var rootWithSep = fullRoot.EndsWith(Path.DirectorySeparatorChar)
            ? fullRoot : fullRoot + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(rootWithSep, comparison)) return false;

        fullPath = candidate;
        return true;
    }

    /// <summary>
    /// Assessment 读/删路径原语 —— 票39 时为**无守卫**的 <see cref="Path.Combine(string, string)"/>;
    /// 票94 起改为与 <see cref="ResolveGuarded"/> 同一规范守卫(委托 <see cref="ResolveUnderRoot"/>),
    /// 越界返回 null,调用方(Assessment Store)须按「无此文件」处理。名称保留以兼容既有调用点。
    /// </summary>
    public static string? CombineUnderRoot(string root, string? relativePath) =>
        ResolveUnderRoot(root, relativePath);

    /// <summary>
    /// Interviews 读/删/存在性原语 —— 票39 时为 ".." **任意子串**粗判;
    /// 票94 起改为与 <see cref="CombineUnderRoot"/> 同一规范守卫(委托 <see cref="ResolveUnderRoot"/>):
    /// `a..b` 类合法名放行,`../`、`..\..\`、URL 编码穿越、rooted/绝对路径逃逸仍拒绝。空/空白返回 null。
    /// </summary>
    public static string? ResolveGuarded(string root, string? relativePath) =>
        ResolveUnderRoot(root, relativePath);
}

/// <summary>本地落盘机制:两 Store 共用的「写临时文件 + 原子改名」。</summary>
public static class LocalFileStorage
{
    /// <summary>
    /// 原子写:先写 <c>{absolutePath}.tmp</c> 再 <see cref="File.Move(string, string, bool)"/>
    /// 覆盖改名 —— 中途崩溃也不会留下「半截文件被当成完整文件」。目标目录不存在先建。
    /// 字节写逻辑由调用方回调提供(Assessment 直拷流;Interviews 边写边算 SHA-256),
    /// 回调内对流的读写口径与改前各自实现一致。
    /// </summary>
    public static async Task WriteAtomicallyAsync(
        string absolutePath, Func<Stream, Task> writeAsync, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(absolutePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var tmp = absolutePath + ".tmp";
        await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await writeAsync(fs);
        }
        File.Move(tmp, absolutePath, overwrite: true);
    }
}
