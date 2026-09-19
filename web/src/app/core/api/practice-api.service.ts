import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { map } from 'rxjs/operators';

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
  children: MaterialNodeIn[];
}

// ---------- 录音 ----------

export interface WordScoreDto {
  word: string;
  accuracy: number;
  errorType: string;
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
}

export interface RecordingDto {
  id: string;
  materialId: string;
  durationSeconds: number;
  sizeBytes: number;
  contentType: string;
  createdAt: string;
  score: RecordingScoreDto | null;
}

// ---------- LLM 设置(面试前准备包,2026-09-18) ----------

/**
 * LLM 配置状态。⚠️ 与 SpeechSettingStatusDto 同理:
 * 只有掩码,没有明文 key —— 任何接口都不下发 key 本身。
 *
 * source: 'database' | 'none'
 *   'database' = 用户在本页保存过(界面显示"已配置")
 *   'none'     = 没保存过。配置文件里的 key 只作服务端兜底,不影响界面判定 ——
 *                否则会出现"我从没填过却显示已配置"。
 */
export interface AiSettingStatusDto {
  hasKey: boolean;
  protocol: string | null;
  displayName: string | null;
  baseUrl: string | null;
  model: string | null;
  endpoint: string | null;
  apiVersion: string | null;
  maskedKey: string | null;
  source: string;
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

// ---------- Azure 语音设置 ----------

export interface SpeechSettingStatusDto {
  hasKey: boolean;
  region: string | null;
  maskedKey: string | null;
  /** 'database' | 'configuration' | 'none' —— 让界面能说清 key 是从哪来的。 */
  source: string;
  endpoint: string | null;
}

@Injectable({ providedIn: 'root' })
export class PracticeApi {
  private readonly api = inject(ApiClient);

  private static readonly BASE = '/api/assessment';

  // ---------- 素材树 ----------

  /** 取整棵素材树。 */
  getMaterials(): Observable<MaterialNodeDto[]> {
    return this.api.get<MaterialNodeDto[]>(`${PracticeApi.BASE}/materials`);
  }

  /**
   * 整树覆盖保存(新建/改名/改正文/排序/删除 全走这一个)。
   *
   * ★ 第四十轮:force=true 仅在**用户显式删除**时传,用于越过服务端
   *   "防误删熔断"(一次删掉 >80% 且 ≥5 个时会被拦)。
   */
  saveMaterials(nodes: MaterialNodeIn[], force = false): Observable<{ saved: number }> {
    return this.api.put<{ saved: number }>(`${PracticeApi.BASE}/materials`, { nodes, force });
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
    fileName: string, contentType: string): Observable<RecordingDto> {
    const fd = new FormData();
    fd.append('file', blob, fileName);
    fd.append('durationSeconds', String(durationSeconds));
    fd.append('contentType', contentType);
    fd.append('language', 'en-US');
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

  deleteRecording(recordingId: string): Observable<void> {
    return this.api.delete<void>(`${PracticeApi.BASE}/recordings/${recordingId}`);
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
        billedBytes: score.billedBytes ?? null
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

  /** 真连通性测试 —— 拿当前 key 去 Azure 走一次。失败会返回 401/403。 */
  testSpeech(): Observable<{ ok: boolean; message: string }> {
    return this.api.post<{ ok: boolean; message: string }>(`${PracticeApi.BASE}/speech/test`);
  }

  /**
   * 测试一把**尚未保存**的候选凭据(先测后存)。
   *
   * 2026-09-16(Forrest 第 1 条):与 testSpeech() 的区别在于 ——
   *   · testSpeech()          → 测服务端**已保存**的 key
   *   · testSpeechCredential() → 测请求体里这把**候选** key,不落库
   * 失败时后端返回 502(凭据错)或 400(没填全)。
   */
  testSpeechCredential(key: string, region: string): Observable<{ ok: boolean; message: string }> {
    return this.api.post<{ ok: boolean; message: string }>(
      `${PracticeApi.BASE}/speech/test-credential`, { key, region });
  }

  // ---------- LLM 设置(面试前准备包,2026-09-18) ----------
  // 与 speech/* 严格同构:先测后存、key 只入不出。

  /** 查 LLM 配置状态。只返回掩码,永远不下发明文 key。 */
  getAiSettings(): Observable<AiSettingStatusDto> {
    return this.api.get<AiSettingStatusDto>(`${PracticeApi.BASE}/ai/settings`);
  }

  /** 列出内置 provider 预设与常用模型(供下拉)。无密钥,可安全下发。 */
  listAiProviders(): Observable<AiProviderPresetDto[]> {
    return this.api.get<AiProviderPresetDto[]>(`${PracticeApi.BASE}/ai/providers`);
  }

  /**
   * 测试一把**尚未保存**的候选凭据(不落库)。
   * 失败时后端返回 502(凭据错)或 400(没填全),绝不用 401/403。
   */
  testAiCredential(body: AiCredentialIn):
    Observable<{ ok: boolean; message: string; sampleOutput?: string }> {
    return this.api.post<{ ok: boolean; message: string; sampleOutput?: string }>(
      `${PracticeApi.BASE}/ai/test-credential`, body);
  }

  /** 保存 LLM 配置。后端会在保存前真测一次,不通不入库。 */
  saveAiSettings(body: AiCredentialIn): Observable<AiSettingStatusDto> {
    return this.api.put<AiSettingStatusDto>(`${PracticeApi.BASE}/ai/settings`, body);
  }

  /** 删除 LLM 配置(回到未配置状态)。 */
  deleteAiSettings(): Observable<void> {
    return this.api.delete<void>(`${PracticeApi.BASE}/ai/settings`);
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
