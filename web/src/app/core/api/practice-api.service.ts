import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { catchError, map, timeout } from 'rxjs/operators';

import { ApiClient } from './api-client';

/**
 * /practice 页的后端访问层(2026-09-15 第十七轮)。
 *
 * 为什么单独一个服务而不是组件里直接调 ApiClient:
 *   1. 端点路径只写一遍 —— 后端改路径时前端只改这里;
 *   2. 组件里彻底不出现 URL 字符串,组件只管交互;
 *   3. 返回类型集中声明,和 C# 那头的 DTO 一一对照,改名时两边一起改。
 *
 * 与前端的对应关系:
 *   MaterialNodeDto  ↔ MaterialNode(material-tree 组件)
 *   RecordingDto     ↔ Recording(recorder.service)
 */

// ---------- 素材树 ----------

/** 后端返回的素材节点(与 C# MaterialNodeDto 对齐)。 */
export interface MaterialNodeDto {
  id: string;
  name: string;
  folder: boolean;
  sortOrder: number;
  expanded: boolean;
  content: string | null;
  /** ★ 第四十三轮:标记色(null/none/orange/red/green) —— 属于素材本身,可按颜色筛选。 */
  markColor: string | null;
  /** ★ 2026-09-25:练习类别(仅根级有意义;null = 未分类)。 */
  categoryId: string | null;
  children: MaterialNodeDto[];
}

/** 提交给后端的节点(前端 MaterialNode 直接映射过来)。 */
export interface MaterialNodeIn {
  id: string | null;
  name: string;
  folder: boolean;
  content: string | null;
  sortOrder: number;
  expanded: boolean;
  /** ★ 第四十三轮:标记色随整树保存一起提交。 */
  markColor?: string | null;
  /** ★ 2026-09-25:练习类别随整树保存一起提交(仅根节点会被采纳)。 */
  categoryId?: string | null;
  children: MaterialNodeIn[];
}

/** ★ 票124:回收站清单项(顶层已删节点) —— 与后端 C# DeletedMaterialDto 逐字段对齐。 */
export interface DeletedMaterialDto {
  id: string;
  name: string;
  folder: boolean;
  parentId: string | null;
  /** 祖先名链(不含自身,根项为 null);分隔符 " / "。 */
  path: string | null;
  /** 该项所属根的类别(自身是根则取自身)。 */
  categoryId: string | null;
  categoryName: string | null;
  markColor: string | null;
  /** 删除时刻代理 = 行 UpdatedAt(零迁移)。 */
  deletedAt: string;
  /** 全部后代数(删/未删都算)。 */
  descendantTotal: number;
  /** 其中已删数。 */
  descendantDeleted: number;
  /** 子树内文件所挂未删录音数。 */
  recordingCount: number;
}

/**
 * ★ 票124:恢复响应 —— 必含**完整树包**(规格 §6 契约 C1)。
 * 只回计数会让「版本已对齐、payload 缺恢复项」的下一次整树保存再删掉它。
 */
export interface MaterialRestoreResult {
  /** 本次实际翻转的行数(含祖先与后代)。 */
  restored: number;
  treeVersion: string;
  nodes: MaterialNodeDto[];
}

// ---------- 练习类别(★ 2026-09-25 Forrest) ----------

export interface PracticeCategoryDto {
  id: string;
  name: string;
  sortOrder: number;
  /** ★ 票111 段3(A06):类别行读时 xmin,删除时 If-Match 回传。 */
  version?: number | null;
  /** 链接的数据源: null=普通分类, "techstack"=Tech Stack 实时视图 */
  linkedSource?: string | null;
}

// ---------- 录音 ----------

export interface WordScoreDto {
  word: string;
  accuracy: number;
  errorType: string;
  /** ★ 票122:词级时间偏移(后端落库读出);旧记录无此两键 → undefined = 无偏移。 */
  offsetMs?: number;
  durationMs?: number;
}

/**
 * 评分结果。
 * ⚠️ 可空字段 = 后端拿不到 → 界面必须显示 "—",绝不用 0 冒充。
 */
export interface RecordingScoreDto {
  pronScore: number | null;
  accuracyScore: number | null;
  fluencyScore: number | null;
  completenessScore: number | null;
  prosodyScore: number | null;
  recognized: string;
  words: WordScoreDto[];
  referenceText: string;
  assessedAt: string;
  /**
   * ★ 第三十四轮:评分计费口径 —— 送评音频时长(秒)。
   * ⚠️ Azure 发音评估按音频时长计费,不是 token。
   *   服务端从 WAV 头精确算出,落库后可重现。null = 未知。
   */
  billedSeconds?: number | null;
  /** 送评的音频字节数(核对用)。 */
  billedBytes?: number | null;
  /** ★ 票108 A10:评分行读时 xmin —— 重评分保存时回传钉版,过期得 409。 */
  version?: number | null;
}

export interface RecordingDto {
  id: string;
  materialId: string;
  durationSeconds: number;
  sizeBytes: number;
  contentType: string;
  createdAt: string;
  score: RecordingScoreDto | null;
  /** ★ 票111 段3(A11):录音行自身读时 xmin(区别于 score.version),删除时 If-Match 回传。 */
  version?: number | null;
  /** ★ 票258 S1:服务端跟读标识(缺省 = 历史行/非跟读,按 false 处理)。 */
  isShadow?: boolean;
  /** ★ 票259 S2:人声轨存在 + 双轨对齐参数（NULL = 非跟读行或旧行）。 */
  hasVoiceTrack?: boolean;
  takeRate?: number | null;
  laneShiftSec?: number | null;
}

/** 票259 S2：人声轨上传结果（行 + 文件三元组 + 对齐参数）。 */
export interface VoiceTrackDto {
  recordingId: string;
  contentType: string;
  sizeBytes: number;
  takeRate?: number | null;
  laneShiftSec?: number | null;
}

// ---------- LLM 设置(2026-09-18 新增;M2.3 起收敛到 AiGateway:/api/ai/*) ----------

/**
 * LLM 配置状态(AiGateway 返回)。⚠️ 同 SpeechSettingStatusDto 的原则:
 * 只有掩码,没有明文 key —— 任何接口都不下发 key 本身。
 *
 * configured = 用户在设置页保存过凭据;updatedAt = 最近一次保存时间。
 * (M2.3 前 Assessment 侧的 hasKey/source 字段已随收敛移除,统一为 configured)
 */
export interface AiSettingStatusDto {
  configured: boolean;
  protocol: string | null;
  displayName: string | null;
  baseUrl: string | null;
  model: string | null;
  endpoint: string | null;
  apiVersion: string | null;
  maskedKey: string | null;
  updatedAt: string | null;
}

/** 一个 provider 预设(静态数据,无密钥)。 */
export interface AiProviderPresetDto {
  /** 协议类型:openai-compatible / azure-openai / anthropic。决定后端用哪个客户端。 */
  protocol: string;
  /** 显示名,如 "DeepSeek" / "Qwen (DashScope)"。 */
  name: string;
  defaultBaseUrl: string;
  models: string[];
}

/** 提交给后端的 LLM 配置(保存与测试共用同一形状)。 */
export interface AiCredentialIn {
  protocol: string;
  apiKey: string;
  baseUrl: string | null;
  model: string;
  endpoint: string | null;
  apiVersion: string | null;
  /** 仅保存时带,用于界面回显"配置的是哪一家"。 */
  displayName?: string | null;
}

/**
 * 备考数据源配置(AiGateway 用户级,2026-10-21)。
 * ⚠️ 搜索密钥同 LLM 口径:只回掩码,永不下发明文。
 */
export interface PrepSourceSettingsDto {
  questionBankEnabled: boolean;
  companyBackgroundEnabled: boolean;
  webExperiencesEnabled: boolean;
  glassdoorEnabled: boolean;
  /** tavily | serper | null(未配置) */
  searchProvider: string | null;
  hasSearchKey: boolean;
  maskedSearchKey: string | null;
  updatedAt: string | null;
}

/** 截图视觉 OCR 结果(AiGateway,2026-10-21):status ok 时 text 为识别全文。 */
export interface ImageTextDto {
  status: 'ok' | 'notConfigured' | 'unsupported' | 'failed' | string;
  text: string | null;
}

/**
 * 共享 OCR 服务结果(平台能力,票59):本地 PaddleOCR,图片不出本地。
 * text = 服务处理层组装的全文(逐行、阅读序);droppedLines = 被置信过滤
 * 剔除的行数(不静默);失败/不可达由 Observable 错误通道如实上抛。
 */
export interface SharedOcrDto {
  status: string;
  engine?: string;
  model?: string;
  text: string | null;
  droppedLines?: number;
  elapsedMs?: number;
}

// ---------- Azure 语音设置 ----------

export interface SpeechSettingStatusDto {
  hasKey: boolean;
  region: string | null;
  maskedKey: string | null;
  /** 'database' | 'configuration' | 'none' —— 让界面能说清 key 是从哪来的。 */
  source: string;
  endpoint: string | null;
}

/**
 * 本机存储目录状态(与后端 StorageSettingDto 一一对应)。
 *
 * status 的四种取值:
 *   · Default            = 没自定义,用部署默认目录
 *   · CustomActive       = 自定义目录已生效,录音就写在那
 *   · CustomPendingMount = 已保存,但容器里还看不到(需要改挂载后重启)
 *   · CustomUnusable     = 路径不可用(权限等原因)
 */
export interface StorageSettingDto {
  /** 用户保存的本机目录;null = 未设置。 */
  desiredPath: string | null;
  /** 容器内实际写盘的根目录。 */
  effectiveRoot: string;
  /** 部署时挂载进容器的本机目录(compose 注入),用于界面回显。 */
  hostDirectory: string | null;
  status: 'Default' | 'CustomActive' | 'CustomPendingMount' | 'CustomUnusable';
  writable: boolean;
  /** 不可用时的人话原因;可用时为 null。 */
  message: string | null;
  fileCount: number;
  totalBytes: number;
  /** 目录结构示例(让用户一眼看懂文件会落在哪)。 */
  layoutExample: string;
  /** 后端是否跑在容器里(容器里换目录必须改挂载)。 */
  inContainer: boolean;
  /** 是否可以迁移已有录音到新目录。 */
  canMigrate: boolean;
  /** ★ 票108 A13:设置行读时版本 —— 保存时回传钉版,过期得 409。 */
  version?: number | null;
}

/** 迁移结果。 */
export interface StorageMigrationDto {
  moved: number;
  skipped: number;
  bytes: number;
  targetRoot: string;
}

@Injectable({ providedIn: 'root' })
export class PracticeApi {
  private readonly api = inject(ApiClient);

  private static readonly BASE = '/api/assessment';

  // ---------- 素材树 ----------

  /**
   * ★ 票111 段2:最近一次读树/保存回传的树摘要(不透明串,前端只存不解析)。
   *   整树保存时回传给服务端前置比对;树已被其他页签改动 → 409。
   */
  private treeVersion: string | null = null;

  /** 取整棵素材树(包对象 { treeVersion, nodes },摘要在此留存供保存回传)。 */
  getMaterials(): Observable<MaterialNodeDto[]> {
    return this.api.get<{ treeVersion: string; nodes: MaterialNodeDto[] }>(`${PracticeApi.BASE}/materials`)
      .pipe(map((r) => {
        const nodes = PracticeApi.treePackageNodes(r);
        this.treeVersion = r.treeVersion ?? null;
        return nodes;
      }));
  }

  /**
   * ★ 票134:整树包形态门 —— 响应必须是包对象 { treeVersion, nodes[] }。
   *   旧后端返裸数组时 r.nodes 为 undefined，旧代码 `?? []` 把它静默吞成空树
   *   （票111 事故：200 空树与真空态无法区分，空态提示与错误态混为一谈）。
   *   形态不符必须抛错，让调用方的 error 路径置 treeLoadFailed（错误态 + 重试），
   *   而不是空态。401/500/502 不走这里（ApiClient 在 map 之前已抛错，错误态路径本来独立）。
   *   注意：treeVersion 留存只在形态通过后写回，垃圾响应不污染留存摘要。
   */
  static treePackageNodes(r: unknown): MaterialNodeDto[] {
    const nodes = (r as { nodes?: unknown } | null | undefined)?.nodes;
    if (!Array.isArray(nodes)) {
      throw new Error('getMaterials: unexpected response shape (expected { treeVersion, nodes[] })');
    }
    return nodes as MaterialNodeDto[];
  }

  /**
   * 整树覆盖保存(新建/改名/改正文/排序/删除 全走这一个)。
   *
   * ★ 第四十轮:force=true 仅在**用户显式删除**时传,用于越过服务端
   *   "防误删熔断"(一次删掉 >80% 且 ≥5 个时会被拦)。
   * ★ 票111 段2:带上读时树摘要;成功后用响应里的新摘要续接;409 时
   *   响应体附有当前摘要 —— 先刷新本地留存,组件侧再回读整树对齐。
   */
  saveMaterials(nodes: MaterialNodeIn[], force = false): Observable<{ saved: number }> {
    return this.api.put<{ saved: number; treeVersion?: string }>(`${PracticeApi.BASE}/materials`,
      { nodes, force, treeVersion: this.treeVersion })
      .pipe(
        map((r) => {
          if (r.treeVersion) this.treeVersion = r.treeVersion;
          return { saved: r.saved };
        }),
        catchError((e: { original?: { error?: { treeVersion?: string } } }) => {
          const cur = e?.original?.error?.treeVersion;
          if (typeof cur === 'string') this.treeVersion = cur;
          throw e;
        })
      );
  }

  // ---------- 素材回收站(★ 票124) ----------

  /**
   * 我的回收站清单(顶层已删节点)。
   *
   * 事故背景:整树覆盖保存会把未提交的存量节点一律软删,而应用内原本没有
   * 任何列出/恢复已删素材的入口(用户只能求助并靠一条经批准的 UPDATE 才恢复)。
   */
  getMaterialTrash(): Observable<DeletedMaterialDto[]> {
    return this.api.get<DeletedMaterialDto[]>(`${PracticeApi.BASE}/materials/trash`);
  }

  /**
   * 恢复某个已删素材(含已删祖先链 + 全部已删后代)。
   *
   * ★ 规格 §6 契约 C1:**在 service 层即完成整树包采纳** —— 响应里的
   *   treeVersion 写回本服务的留存摘要,nodes 经与 getMaterials 同一 map 路径
   *   返回给调用方。任何调用方都不可能「只拿计数、忘了树」——
   *   只采纳版本号不采纳节点,会让「版本已对齐、payload 缺恢复项」的
   *   下一次整树保存畅通无阻地把它再删掉(碎纸机)。
   */
  restoreMaterial(id: string): Observable<MaterialRestoreResult> {
    return this.api.post<{ restored: number; treeVersion: string; nodes: MaterialNodeDto[] }>(
      `${PracticeApi.BASE}/materials/${id}/restore`, {})
      .pipe(map((r) => this.adoptTreePackage(r)));
  }

  /**
   * 恢复我的全部已删素材(整树误删事故的一键兜底)。
   * 无已删行时 restored=0 且照常回当前树包(幂等,不报错)。
   */
  restoreAllMaterials(): Observable<MaterialRestoreResult> {
    return this.api.post<{ restored: number; treeVersion: string; nodes: MaterialNodeDto[] }>(
      `${PracticeApi.BASE}/materials/trash/restore-all`, {})
      .pipe(map((r) => this.adoptTreePackage(r)));
  }

  /** 采纳恢复响应的整树包(契约 C1):版本写回留存,nodes 走与 getMaterials 同一路径。 */
  private adoptTreePackage(r: { restored: number; treeVersion: string; nodes: MaterialNodeDto[] }): MaterialRestoreResult {
    this.treeVersion = r.treeVersion ?? null;
    return { restored: r.restored ?? 0, treeVersion: this.treeVersion ?? '', nodes: r.nodes ?? [] };
  }

  // ---------- 练习类别(★ 2026-09-25 Forrest) ----------

  /** 我的类别列表(升序)。首次调用服务端会播种默认类别。 */
  getCategories(): Observable<PracticeCategoryDto[]> {
    return this.api.get<PracticeCategoryDto[]>(`${PracticeApi.BASE}/categories`);
  }

  createCategory(name: string): Observable<PracticeCategoryDto> {
    return this.api.post<PracticeCategoryDto>(`${PracticeApi.BASE}/categories`, { name });
  }

  renameCategory(id: string, name: string): Observable<{ saved: boolean }> {
    return this.api.put<{ saved: boolean }>(`${PracticeApi.BASE}/categories/${id}`, { name });
  }

  /** 按提交顺序整体重排(管理弹窗拖拽后调用)。 */
  reorderCategories(orderedIds: string[]): Observable<{ saved: boolean }> {
    return this.api.put<{ saved: boolean }>(`${PracticeApi.BASE}/categories/reorder`, { orderedIds });
  }

  /** 删除类别;其下素材由服务端自动归入"未分类"。 */
  deleteCategory(id: string, version?: number | null): Observable<{ deleted: boolean }> {
    return this.api.delete<{ deleted: boolean }>(`${PracticeApi.BASE}/categories/${id}`, ApiClient.ifMatch(version));
  }

  /** 设置/取消类别的数据源链接(如 "techstack")。null = 取消链接。 */
  setCategoryLink(id: string, linkedSource: string | null): Observable<{ saved: boolean }> {
    return this.api.put<{ saved: boolean }>(`${PracticeApi.BASE}/categories/${id}/link`, { linkedSource });
  }

  /**
   * ★ 第四十三轮(Forrest):单独设置某素材的标记色 —— 点一下颜色立即落库。
   *   不走整树 PUT:标记是单字段属性,一个 PATCH 就够,又快又安全。
   *   color: 'none' | 'orange' | 'red' | 'green'(none = 清除)。
   */
  setMaterialMark(materialId: string, color: string): Observable<{ saved: boolean }> {
    return this.api.patch<{ saved: boolean }>(
      `${PracticeApi.BASE}/materials/${materialId}/mark`, { color });
  }

  // ---------- 录音 ----------

  listRecordings(materialId: string): Observable<RecordingDto[]> {
    return this.api.get<RecordingDto[]>(
      `${PracticeApi.BASE}/materials/${materialId}/recordings`);
  }

  /**
   * ★ 第三十九轮:列出当前用户的**全部录音**(不按素材过滤)。
   * 数据安全兜底 —— 素材被删/ID 变动时,录音也不会在前端"消失"。
   */
  listAllRecordings(): Observable<RecordingDto[]> {
    return this.api.get<RecordingDto[]>(`${PracticeApi.BASE}/recordings`);
  }

  /**
   * 上传一次录音。
   * durationSeconds 走查询参数之外的表单字段,与后端 [FromForm] 对齐。
   */
  uploadRecording(materialId: string, blob: Blob, durationSeconds: number,
    fileName: string, contentType: string, isShadow?: boolean): Observable<RecordingDto> {
    const fd = new FormData();
    fd.append('file', blob, fileName);
    fd.append('durationSeconds', String(durationSeconds));
    fd.append('contentType', contentType);
    fd.append('language', 'en-US');
    // ★ 票258 S1:跟读混音上传传 isShadow=true;普通上传不传该字段(后端缺省 false),
    //   普通上传的请求体一字不变。
    if (isShadow !== undefined) fd.append('isShadow', String(isShadow));
    // 必须走 postForm —— FormData 交给普通 post 会被当成 JSON 序列化,后端收不到文件
    return this.api.postForm<RecordingDto>(
      `${PracticeApi.BASE}/materials/${materialId}/recordings`, fd);
  }

  /**
   * 回放地址 —— ⚠️ 第二十六轮起**不再直接给 <audio src> 用**。
   *
   * 原因:该端点带 [Authorize],而 <audio src> 由浏览器自行发请求,
   * **不会带 Authorization 头** → 一直 401。
   * 保留此方法仅供"已有 token 的媒体源"等特殊场景/调试;
   * 正常回放请用 fetchRecordingAudio() 拿 blob。
   */
  audioUrl(recordingId: string): string {
    return `${PracticeApi.BASE}/recordings/${recordingId}/audio`;
  }

  /**
   * 取录音音频流(带鉴权)。
   *
   * ★ 第二十六轮(401 修复):走 ApiClient → HttpClient →
   *   authInterceptor 自动附 `Authorization: Bearer <token>`,
   *   401 时还会触发全局刷新链路。拿到 blob 后由调用方 createObjectURL。
   */
  fetchRecordingAudio(recordingId: string): Observable<Blob> {
    return this.api.getBlob(`${PracticeApi.BASE}/recordings/${recordingId}/audio`);
  }

  /**
   * 票259 S2：上传/覆盖一条录音的人声轨（跟读跨设备：人声跟人走）。
   * 对齐参数缺省不传 = 保留服务端既有值（重传不带参数时不抹对齐）。
   */
  uploadVoiceTrack(recordingId: string, blob: Blob, fileName: string,
    contentType: string, takeRate?: number, laneShiftSec?: number): Observable<VoiceTrackDto> {
    const fd = new FormData();
    fd.append('file', blob, fileName);
    if (takeRate !== undefined) fd.append('takeRate', String(takeRate));
    if (laneShiftSec !== undefined) fd.append('laneShiftSec', String(laneShiftSec));
    return this.api.postForm<VoiceTrackDto>(
      `${PracticeApi.BASE}/recordings/${recordingId}/voice-track`, fd);
  }

  /**
   * 票259 S2：取一条录音的人声轨（另一设备打开时取回人声）。
   * 无人声轨时后端回 404，调用方回落 S1 降级态，绝不抛到界面当 500。
   */
  fetchVoiceTrack(recordingId: string): Observable<Blob> {
    return this.api.getBlob(`${PracticeApi.BASE}/recordings/${recordingId}/voice-track`);
  }

  // ---------- 票119 词典(点词查词典) ----------

  /** 查词:命中任一层回 schema v2 词卡;全层未命中后端回 404(调用方据状态码分简卡)。 */
  lookupDictionaryWord(word: string): Observable<DictionaryCardDto> {
    return this.api.get<DictionaryCardDto>(
      `${PracticeApi.BASE}/dictionary/${encodeURIComponent(word)}`);
  }

  /** 发音链单点解析(票254 §4):只供卡内播放入口;链耗尽回 {available:false}。 */
  pronounceDictionaryWord(
    word: string, accent: 'us' | 'uk',
    opts: { excludeKeys?: string[]; humanOnly?: boolean } = {},
  ): Observable<DictionaryPronounceDto> {
    const params = [`accent=${accent}`];
    if (opts.excludeKeys?.length)
      params.push(`excludeKeys=${encodeURIComponent(opts.excludeKeys.join(','))}`);
    if (opts.humanOnly) params.push('humanOnly=true');
    return this.api.get<DictionaryPronounceDto>(
      `${PracticeApi.BASE}/dictionary/${encodeURIComponent(word)}/pronounce?${params.join('&')}`);
  }

  /** 词典音频流:走 ApiClient 附鉴权头拿 Blob(同 fetchRecordingAudio 口径),调用方转 ObjectURL 播放。 */
  fetchDictionaryAudio(key: string, word?: string, accent?: 'us' | 'uk'): Observable<Blob> {
    // 票254 §1:word/accent 仅供合成键 serve-time 物化校验；真人键后端忽略两参数。
    const params = [];
    if (word !== undefined) params.push(`word=${encodeURIComponent(word)}`);
    if (accent !== undefined) params.push(`accent=${accent}`);
    const qs = params.length ? `?${params.join('&')}` : '';
    return this.api.getBlob(`${PracticeApi.BASE}/dictionary/audio/${encodeURIComponent(key)}${qs}`);
  }

  deleteRecording(recordingId: string, version?: number | null): Observable<void> {
    return this.api.delete<void>(`${PracticeApi.BASE}/recordings/${recordingId}`, ApiClient.ifMatch(version));
  }

  /** 读已缓存的评分(不调 Azure)。命中就不必重新花额度。 */
  getRecordingScore(recordingId: string): Observable<RecordingScoreDto> {
    return this.api.get<RecordingScoreDto>(
      `${PracticeApi.BASE}/recordings/${recordingId}/score`);
  }

  /**
   * 把评分结果存进后端(需求第 4 条:避免重复评分)。
   *
   * 存的是"已经算出来的分数",不是触发一次新评分 —— 所以后端不调 Azure,
   * 只做落库。下次打开这个素材时直接读缓存,不再花额度。
   */
  saveScore(recordingId: string, score: {
    pronScore: number | null; accuracyScore: number | null;
    fluencyScore: number | null; completenessScore: number | null;
    prosodyScore: number | null; recognized: string; referenceText: string;
    words: { word: string; accuracy: number; errorType: string }[];
    // ★ 第三十四轮:计费口径一并入库 —— 否则刷新后"本地已存评分"
    //   那条路径显示不出当时花了多少,信息残缺。
    billedSeconds?: number | null;
    billedBytes?: number | null;
    /** ★ 票108 A10:被替换评分行的读时版本(首次评分不传)。 */
    version?: number | null;
  }): Observable<RecordingScoreDto> {
    return this.api.post<RecordingScoreDto>(
      `${PracticeApi.BASE}/recordings/${recordingId}/score`, {
        pronScore: score.pronScore,
        accuracyScore: score.accuracyScore,
        fluencyScore: score.fluencyScore,
        completenessScore: score.completenessScore,
        prosodyScore: score.prosodyScore,
        recognizedText: score.recognized,
        referenceText: score.referenceText,
        words: score.words,
        billedSeconds: score.billedSeconds ?? null,
        billedBytes: score.billedBytes ?? null,
        version: score.version ?? null
      });
  }

  // ---------- Azure 语音设置 ----------

  getSpeechSettings(): Observable<SpeechSettingStatusDto> {
    return this.api.get<SpeechSettingStatusDto>(`${PracticeApi.BASE}/speech/settings`);
  }

  saveSpeechSettings(key: string, region: string, endpoint?: string):
    Observable<SpeechSettingStatusDto> {
    return this.api.put<SpeechSettingStatusDto>(
      `${PracticeApi.BASE}/speech/settings`, { key, region, endpoint });
  }

  // ---------- 本机录音存储目录(★ 2026-09-27 Forrest:"我的账户"里可自选) ----------

  /**
   * 当前存储目录状态。
   *
   * ⚠️ 这里的 `status` 是**服务端实际落盘位置**的判定结果 ——
   *    不是前端自己猜的。界面显示"已生效"就必须真的生效。
   */
  getStorageSettings(): Observable<StorageSettingDto> {
    return this.api.get<StorageSettingDto>(`${PracticeApi.BASE}/storage/settings`);
  }

  /**
   * 保存自选目录。路径当前不可用(容器里还没挂载)也会被保存,
   * 只是状态标成 CustomPendingMount,并把需要的挂载配置回显给界面。
   * 传空串 = 恢复部署默认目录。
   */
  saveStorageSettings(rootPath: string | null, version?: number | null): Observable<StorageSettingDto> {
    return this.api.put<StorageSettingDto>(
      `${PracticeApi.BASE}/storage/settings`, { rootPath, version: version ?? null });
  }

  /** 把已有录音搬到自选目录(相对路径不变,数据库记录不受影响)。 */
  migrateStorage(): Observable<StorageMigrationDto> {
    return this.api.post<StorageMigrationDto>(`${PracticeApi.BASE}/storage/migrate`);
  }

  /**
   * 测试一把**尚未保存**的候选凭据(先测后存)。
   *
   * 2026-09-16(Forrest 第 1 条):测请求体里这把**候选** key,不落库。
   * 失败时后端返回 502(凭据错)或 400(没填全)。
   */
  testSpeechCredential(key: string, region: string): Observable<{ ok: boolean; message: string }> {
    return this.api.post<{ ok: boolean; message: string }>(
      `${PracticeApi.BASE}/speech/test-credential`, { key, region });
  }

  // ---------- LLM 设置(M2.3:凭据统一存 AiGateway,走 /api/ai/*) ----------
  // 与 speech/* 严格同构:先测后存、key 只入不出。
  // ⚠️ 2026-09-24 M2.3 收敛:此前这组方法打 /api/assessment/ai/*(Assessment 自己
  //    的凭据栈),而 Jobs 的 Cover Letter 生成读的是 AiGateway —— 存的 key 永远
  //    不生效(脑裂)。现在唯一入口是 AiGateway:凭据一份、补全一份。

  /** 查 LLM 配置状态。只返回掩码,永远不下发明文 key。 */
  getAiSettings(): Observable<AiSettingStatusDto> {
    return this.api.get<AiSettingStatusDto>('/api/ai/settings');
  }

  /** 列出内置 provider 预设与常用模型(供下拉)。无密钥,可安全下发。 */
  listAiProviders(): Observable<AiProviderPresetDto[]> {
    return this.api.get<AiProviderPresetDto[]>('/api/ai/providers');
  }

  /**
   * 测试一把**尚未保存**的候选凭据(不落库)。
   * 失败时后端返回 502(凭据错)或 400(没填全),绝不用 401/403。
   */
  testAiCredential(body: AiCredentialIn):
    Observable<{ ok: boolean; message: string; sample?: string }> {
    return this.api.post<{ ok: boolean; message: string; sample?: string }>(
      '/api/ai/test-credential', body);
  }

  /** 保存 LLM 配置。后端会在保存前真测一次,不通不入库。 */
  saveAiSettings(body: AiCredentialIn): Observable<AiSettingStatusDto> {
    return this.api.put<AiSettingStatusDto>('/api/ai/settings', body);
  }

  /** 删除 LLM 配置(回到未配置状态)。 */
  deleteAiSettings(): Observable<void> {
    return this.api.delete<void>('/api/ai/settings');
  }

  // ---------- 备考数据源(2026-10-21:AiGateway 用户级配置) ----------

  /** 读备考数据源配置(开关 + 搜索服务;密钥只回掩码)。 */
  getPrepSourceSettings(): Observable<PrepSourceSettingsDto> {
    return this.api.get<PrepSourceSettingsDto>('/api/ai/prep-sources/settings');
  }

  /** 保存备考数据源配置。searchApiKey 为空 = 不改原密钥。 */
  savePrepSourceSettings(body: {
    questionBankEnabled: boolean;
    companyBackgroundEnabled: boolean;
    webExperiencesEnabled: boolean;
    glassdoorEnabled: boolean;
    searchProvider: string | null;
    searchApiKey: string | null;
  }): Observable<PrepSourceSettingsDto> {
    return this.api.put<PrepSourceSettingsDto>('/api/ai/prep-sources/settings', body);
  }

  // ---------- 截图视觉 OCR(2026-10-21:AiGateway 视觉通道) ----------

  /**
   * 把截图经用户配置的视觉模型转成文字。status 如实返回:
   * ok / notConfigured / unsupported / failed —— 失败时 text 为 null,不编造。
   */
  extractImageText(imageBase64: string, mimeType: string): Observable<ImageTextDto> {
    return this.api.post<ImageTextDto>('/api/ai/vision/extract-text', { imageBase64, mimeType });
  }

  // ---------- 共享 OCR 服务(2026-10-21:平台能力,票59) ----------

  /**
   * 共享 OCR 服务(经网关 /api/ocr → 本地 PaddleOCR):截图识别的主通道,
   * 图片不出本地。timeoutMs 由调用方以可配置常量传入(不写死在此);
   * 超时/不可达走错误通道,调用方据此回落视觉路径。baseUrl 可注入
   * (测试/降级模拟指向其它地址),默认走网关前缀。
   */
  extractImageTextShared(imageBase64: string, mimeType: string, timeoutMs: number,
                         baseUrl = '/api/ocr'): Observable<SharedOcrDto> {
    return this.api.post<SharedOcrDto>(`${baseUrl}/v1/ocr`, { imageBase64, mimeType })
      .pipe(timeout(timeoutMs));
  }

  // ---------- 示范朗读(TTS) ----------

  /**
   * Azure 神经语音合成。
   *
   * ★ 第三十三轮(Forrest):返回体从 Blob 改为
   *   `{ blob, fromCache }` —— 让调用方能如实告知用户
   *   「这是本地缓存的音频,未消耗额度」还是「本次调用了 Azure,消耗了额度」。
   *   fromCache 直接取自后端 `X-Tts-Cache: hit|miss` 响应头,
   *   不靠前端自己猜(前端无法判断服务端有没有缓存)。
   *
   * 未配 key 时后端返回 503,调用方据此**如实**回退浏览器语音。
   */
  synthesize(text: string, voice?: string, speed?: number,
             force = false, language?: string): Observable<TtsSynthesisResult> {
    return this.api
      .postBlobWithHeaders(`${PracticeApi.BASE}/tts`, { text, voice, speed, force, language })
      .pipe(map((res) => {
        const num = (k: string): number | null => {
          const v = res.headers.get(k);
          if (v === null || v.trim() === '') return null;
          const n = Number(v);
          return Number.isFinite(n) ? n : null;
        };
        return {
          blob: res.body as Blob,
          fromCache: (res.headers.get('X-Tts-Cache') ?? '').toLowerCase() === 'hit',
          // ★ 第三十四轮:精确计费字符数,由服务端下发(不是前端估算)
          billedChars: num('X-Tts-Chars'),
          fullChars: num('X-Tts-Chars-Full'),
          voice: res.headers.get('X-Tts-Voice') ?? '',
          audioBytes: num('X-Tts-Bytes')
        };
      }));
  }
}

/** 示范朗读合成结果:音频字节 + 是否来自本地缓存(诚实来源标记)。 */
/** 票119 词典卡 DTO(后端 schema v2,GET /dictionary/{word})。 */
export interface DictionaryAudioOptionDto {
  /** 票254 起 kind 含带标注合成档;无可用音频时后端省略此字段(available=false)。 */
  kind?: 'oxford' | 'commons' | 'synthetic' | null;
  key?: string | null;
  available: boolean;
}

export interface DictionarySenseDto {
  defEn: string;
  glossZh?: string | null;
  example?: { en: string; zh?: string | null } | null;
}

export interface DictionaryPosBlockDto {
  pos: string;
  label: string;
  senses: DictionarySenseDto[];
  foldedCount: number;
  zhLines: string[];
}

export interface DictionaryCardDto {
  query: string;
  lemma: string;
  matchedVia: string;
  tier: 'oxford' | 'open';
  phonetics: { uk?: string | null; us?: string | null };
  /** 票254 P3:所点词形本形音标（仅词形卡有；词元卡为 null）。 */
  formPhonetics?: { uk?: string | null; us?: string | null } | null;
  posBlocks: DictionaryPosBlockDto[];
  /** 票128:逐词形音频可用性(additive;旧后端无此字段,前端按缺失处理——无钮)。 */
  forms: { label: string; text: string; audio?: { us: boolean; uk: boolean } }[];
  audio: { us: DictionaryAudioOptionDto; uk: DictionaryAudioOptionDto };
  attribution: { source: string; audioSource?: string | null };
}

export interface DictionaryPronounceDto {
  /** 票254 §4:链耗尽 → available=false 且无 kind/key(端点回 200);kind 含带标注合成档。 */
  kind?: 'oxford' | 'commons' | 'synthetic' | null;
  key?: string | null;
  available: boolean;
}

export interface TtsSynthesisResult {
  blob: Blob;
  /**
   * true = 服务端本地缓存命中,本次**未消耗** Azure 额度。
   * false = 本次真调了 Azure 合成,**消耗了**额度。
   */
  fromCache: boolean;
  /**
   * ★ 第三十四轮:本次实际计费字符数。
   *
   * ⚠️ 叫"字符"而不是"token" —— Azure 语音 TTS **不以 token 计费**,
   *   真实计费单位是合成字符数(按每 1M 字符计价)。
   *   命中缓存时为 0。服务端精确计算,前端不估算。
   */
  billedChars: number | null;
  /** 这段文本的完整字符数(即使命中缓存也有值,便于对比"省了多少")。 */
  fullChars: number | null;
  /** 实际使用的音色(服务端默认也会如实回报)。 */
  voice: string;
  /** 音频字节数(便于核对,非计费单位)。 */
  audioBytes: number | null;
}
