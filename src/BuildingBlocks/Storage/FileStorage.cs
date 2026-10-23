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
/// ## 相对路径约定的两处既有差异(保留,不静默统一)
///
///   · Interviews 读路径走 <see cref="StorageRelativePaths.ResolveGuarded"/>:
///     空/rooted/含 ".." 一律拒绝(StoragePath 会跨服务进事件,必须钉死在根内);
///   · Assessment 读/删路径走 <see cref="StorageRelativePaths.CombineUnderRoot"/>:
///     历史实现**没有**穿越守卫。本票按行为不变原则原样收编并以特征测试锁定;
///     要不要给 Assessment 补守卫是**安全口径变更**,须另行申报立票,不借重构静默改。
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
/// 落盘/读盘时再转回当前 OS 的分隔符。两个解析原语的守卫差异见 <see cref="StorageRootResolver"/>
/// 类注释 —— 是既有行为差异,不是疏漏,禁止静默统一。
/// </summary>
public static class StorageRelativePaths
{
    /// <summary>OS 相对路径 → 入库形态(反斜杠转正斜杠)。</summary>
    public static string ToPortableRelative(string relativePath) =>
        relativePath.Replace('\\', '/');

    /// <summary>
    /// 相对路径 → 根目录下的绝对路径(**无守卫**,Assessment 既有口径,特征测试锁定):
    /// 就是 <see cref="Path.Combine(string, string)"/> 加分隔符还原 —— rooted 输入会
    /// 绕过根、".." 会出根。本票不借重构改此安全口径(见类注释),调用方仅 Assessment Store。
    /// </summary>
    public static string CombineUnderRoot(string root, string relativePath) =>
        Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>
    /// 相对路径 → 根目录下的绝对路径(**带守卫**,Interviews 既有口径):
    /// 含路径穿越("..")或 rooted 的输入直接拒绝返回 null —— 这类路径会进事件、
    /// 跨服务传给 Worker,这里把它钉死在根目录内。空/空白同样返回 null。
    /// 注:".." 是**任意位置子串**判定(连 `a..b` 这类文件名也会被拒)—— 既有口径,
    /// 特征测试锁定,放宽须另行申报。
    /// </summary>
    public static string? ResolveGuarded(string root, string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return null;
        var normalized = relativePath.Replace('\\', '/');
        if (Path.IsPathRooted(normalized) || normalized.Contains("..")) return null;

        return Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar));
    }
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
