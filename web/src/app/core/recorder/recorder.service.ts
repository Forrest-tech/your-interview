import { inject, Injectable, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';

import { ApiClient } from '../api/api-client';
import { PracticeApi, RecordingDto, RecordingScoreDto } from '../api/practice-api.service';

/** 一条录音。阶段一存内存;阶段二换成向后端提交并落库。 */
export interface Recording {
  id: string;
  /** 所属素材文件 id —— 录音挂在素材上,换素材就换录音列表。 */
  materialId: string;
  createdAt: number;
  /** 时长(秒)。 */
  duration: number;
  /** 音频 Blob(浏览器内存态)。 */
  blob: Blob;
  /** objectURL,用于 <audio> 播放与回放。 */
  url: string;
  /** AI 评分结果(未评分为 null)。 */
  score: RecordingScore | null;
  /** 评分状态。 */
  grading: boolean;
  /**
   * 是否已在服务端存在(拿到了真实 GUID id)。
   * ⚠️ 2026-09-16(真机 404 根因):本地 take 的 id 是 `r_xxx` 临时串,
   *    而后端路由是 `recordings/{id:guid}/score` —— 拿临时 id 去打就是 **404**。
   *    所以必须有一个明确的"已落库"标志,而不是靠 `id.startsWith('r_')` 猜。
   */
  uploaded: boolean;
  /** 评分失败原因。 */
  error: string | null;
}

/** Azure 发音评估结果(与后端返回结构对齐)。 */
export interface RecordingScore {
  pronScore: number;
  accuracyScore: number;
  /**
   * 流利度。
   * ⚠️ Free F0 层不返回该项 —— 为 null 时界面必须显示"—",
   * 不能拿 0 冒充(与后端 AzureSpeechClient 的诚实约定一致)。
   */
  fluencyScore: number | null;
  completenessScore: number | null;
  prosodyScore: number | null;
  /** 识别出的文本。 */
  recognized: string;
  /** 逐词明细。 */
  words: { word: string; accuracy: number; errorType: string }[];
  /** 是否在模拟态(阶段一,未接后端)。界面需明示。 */
  simulated: boolean;
  /**
   * ★ 第三十四轮:本次评分的**真实计费量** —— 送评音频时长(秒)。
   *
   * ⚠️ Azure 发音评估按音频时长计费,不是 token。
   *   这个值由服务端从 WAV 头精确算出(字节率 × data 长度)。
   *   落库后即使刷新、重新打开也能显示。null = 未知,不显示数字。
   */
  billedSeconds?: number | null;
  /** 送评的音频字节数(核对用,非计费单位)。 */
  billedBytes?: number | null;
}

/**
 * 录音仓库(阶段一:内存)。
 *
 * 需求(Forrest 2026-09-15):录音要先能保存、能删除;可对某条录音按需做
 * Azure Speech 发音评分。所以录音是一等实体 —— 挂素材、有时长、有评分状态。
 *
 * 阶段二接后端时的迁移点都集中在 submitForGrading():把 Blob 转 16k PCM WAV
 * 上传给服务端,由服务端持 key 调 Azure(key 绝不进浏览器)。
 */
@Injectable({ providedIn: 'root' })
export class RecorderService {
  private readonly api = inject(ApiClient);
  private readonly practiceApi = inject(PracticeApi);

  /**
   * 是否正在从后端载入历史录音(2026-09-15 第十七轮)。
   * 载入中不显示"暂无录音",否则用户会以为记录丢了。
   */
  readonly loading = signal(false);

  /** 载入/上传失败的真实原因(不吞错)。 */
  readonly persistError = signal('');

  /**
   * ★ 第二十七轮:等待补传的录音 id 列表。
   * 场景:录音时素材还没保存到服务端(本地种子 id)→ 无法上传;
   *       等素材树保存拿到真 GUID 后,由 flushPendingUploads() 自动补传。
   * 目的:彻底杜绝"录音只存内存、刷新即丢"。
   */
  readonly pendingUploads = signal<string[]>([]);

  /** 已从后端载入过的素材 id —— 避免同一素材反复拉取。 */
  private loadedMaterials = new Set<string>();

  // 2026-09-15 第八轮:已删除 backendAvailable 字段。
  // 以前它用来"失败一次就不再试后端,直接走模拟",现在没有模拟了,
  // 所以每次评分都真实请求,失败如实报错,不再有静默降级状态。

  /** 全部录音(按素材 id 可筛)。 */
  readonly recordings = signal<Recording[]>([]);

  /** 是否正在录。 */
  readonly recording = signal(false);

  /**
   * ★ 第三十轮:录音中的实时音量波形(0~1 归一化的一小段柱状数据)。
   *   由 AnalyserNode 定时采样得到,供录音条画出「正在说话」的实时动效。
   *   录音结束/取消后清空,避免残留。
   */
  readonly liveWave = signal<number[]>([]);

  /** 录音已进行的秒数(用于计时显示)。 */
  readonly elapsed = signal(0);

  /** 当前录音所属素材 id。 */
  private targetMaterialId: string | null = null;

  private mediaRecorder: MediaRecorder | null = null;
  private chunks: Blob[] = [];
  private stream: MediaStream | null = null;

  /** 第三十轮:实时波形采样(AnalyserNode + 定时器),录音结束后必须释放。 */
  private analyserCtx: AudioContext | null = null;
  private analyser: AnalyserNode | null = null;
  private waveTimer: ReturnType<typeof setInterval> | null = null;
  private startedAt = 0;
  private timer: ReturnType<typeof setInterval> | null = null;

  /** 本环境是否支持录音。 */
  get supported(): boolean {
    return typeof navigator !== 'undefined'
      && !!navigator.mediaDevices
      && typeof MediaRecorder !== 'undefined';
  }

  forMaterial(materialId: string | null): Recording[] {
    if (!materialId) return [];
    return this.recordings().filter((r) => r.materialId === materialId);
  }

  // ---------- 录制 ----------
  async start(materialId: string): Promise<string | null> {
    if (this.recording()) return null;
    if (!this.supported) return '浏览器不支持录音,请改用 Chrome / Edge / Safari。';
    try {
      this.stream = await navigator.mediaDevices.getUserMedia({ audio: true });
    } catch {
      return '无法访问麦克风。请检查浏览器权限设置。';
    }

    this.targetMaterialId = materialId;
    this.chunks = [];
    // 优先 audio/webm,失败退到浏览器默认
    const mime = this.pickMime();
    try {
      this.mediaRecorder = mime
        ? new MediaRecorder(this.stream, { mimeType: mime })
        : new MediaRecorder(this.stream);
    } catch {
      this.mediaRecorder = new MediaRecorder(this.stream);
    }

    this.mediaRecorder.ondataavailable = (e) => {
      if (e.data && e.data.size > 0) this.chunks.push(e.data);
    };
    this.mediaRecorder.onstop = () => this.finalize();

    this.mediaRecorder.start();
    this.startLiveWave();          // ★ 第三十轮:启动实时波形采样
    this.recording.set(true);
    this.startedAt = Date.now();
    this.elapsed.set(0);
    this.timer = setInterval(() => {
      this.elapsed.set(Math.floor((Date.now() - this.startedAt) / 1000));
    }, 250);
    return null;
  }

  stop(): void {
    if (!this.recording()) return;
    if (this.mediaRecorder && this.mediaRecorder.state !== 'inactive') {
      this.mediaRecorder.stop();
    }
    this.recording.set(false);
    this.stopLiveWave();           // ★ 第三十轮:停止实时波形采样
    this.clearTimer();
  }

  cancel(): void {
    this.chunks = [];
    if (this.mediaRecorder && this.mediaRecorder.state !== 'inactive') {
      this.mediaRecorder.onstop = null;
      this.mediaRecorder.stop();
    }
    this.recording.set(false);
    this.stopLiveWave();           // ★ 第三十轮:停止实时波形采样
    this.clearTimer();
    this.releaseStream();
  }

  private finalize(): void {
    const blob = new Blob(this.chunks, {
      // ★ 第二十八轮:Safari 录的是 mp4 —— 兜底不能再写死 webm,
      //   否则 blob.type 与真实字节不符,后续上传/解码都按错类型处理。
      type: this.mediaRecorder?.mimeType || this.chunks[0]?.type || 'audio/webm'
    });
    this.chunks = [];
    this.releaseStream();
    this.mediaRecorder = null;

    if (blob.size === 0 || !this.targetMaterialId) return;

    const materialId = this.targetMaterialId;
    const duration = Math.max(1, Math.round((Date.now() - this.startedAt) / 1000));

    const localId = 'r_' + Date.now().toString(36) + '_' + Math.random().toString(36).slice(2, 7);
    const rec: Recording = {
      id: localId,
      materialId,
      createdAt: Date.now(),
      duration,
      blob,
      url: URL.createObjectURL(blob),
      score: null,
      grading: false,
      uploaded: false,
      error: null
    };

    // ⚠️ 2026-09-16(Forrest 本轮)：**录完不再自动进列表**。
    //    改为持在 pendingTake —— 界面上出现"提交"按钮,
    //    用户点提交后才真正进下方列表(并上传后端)。
    //    这样"录音完 -> 无任何反应"的问题彻底消失:
    //    录完立刻有明确的下一步(提交按钮)。
    this.pendingTake.set(rec);
  }

  /**
   * 待提交的录音(录完但用户还没点"提交")。
   * null = 没有待提交的录音。
   */
  readonly pendingTake = signal<Recording | null>(null);

  /**
   * 提交待提交的录音 —— 进列表 + 上传后端。
   * 由界面的"提交"按钮调用(Forrest 本轮需求)。
   */
  submitPending(): void {
    const rec = this.pendingTake();
    if (!rec) return;
    this.pendingTake.set(null);

    // 进列表(本地先落一条,让用户立刻能回放/评分)
    this.recordings.update((list) => [rec, ...list]);

    const materialId = rec.materialId;

    // ★ 第二十七轮(Forrest 报"录音后数据全丢"的根治点):
    //   非 GUID 素材(本地种子 'f_intro_edu' 等)无法上传 —— 后端路由要求 GUID。
    //   旧实现只 patch 一条 error 就 return → 录音**永不上传** → 只存内存 → 刷新即丢。
    //   现在:① 明确告知用户数据【尚未保存,刷新会丢】;② 把这条录音挂进
    //   "待重传队列",等素材树拿到真 GUID 后自动补传(见 flushPendingUploads)。
    if (!RecorderService.isGuid(materialId)) {
      this.patch(rec.id, {
        error: '⚠️ 该素材还没保存到服务端,录音暂未入库 —— 刷新页面会丢失。'
          + '请点侧栏「保存修改」后再重试,或重录一次。'
      });
      // 入队等素材拿到 GUID 后自动补传
      this.pendingUploads.set([...this.pendingUploads(), rec.id]);
      return;
    }

    this.upload(rec.id);
  }

  /**
   * 上传一条已在列表里的录音(按当前 id 与 materialId)。
   *
   * ★ 第二十七轮抽出:submitPending 与 flushPendingUploads 共用同一段上传逻辑,
   *   避免两处实现分叉(第七轮教训:同一危险模式只修一处 = 没修完)。
   */
  upload(id: string): void {
    const rec = this.recordings().find((r) => r.id === id);
    if (!rec || rec.uploaded || !RecorderService.isGuid(rec.materialId)) return;

    const blob = rec.blob;
    if (!blob || !blob.size) {
      this.patch(id, { error: '录音数据已不在内存,无法上传。请重录一次。' });
      return;
    }
    // ⚠️ 第二十八轮:扩展名与 mime 必须从 blob 的**真实类型**推导。
    //   Safari 录的是 mp4 字节;若这里谎报 webm,后端 GuessExtension
    //   会把文件存成 .webm 而内容是 mp4 → 回放/解码时按错类型处理。
    const mime = blob.type || 'audio/webm';
    const ext = mime.includes('ogg') ? 'ogg'
      : mime.includes('mp4') || mime.includes('m4a') ? 'm4a'
      : mime.includes('wav') ? 'wav'
      : 'webm';
    const localId = rec.id;

    this.practiceApi.uploadRecording(rec.materialId, blob, rec.duration,
      `take-${Date.now()}.${ext}`, mime)
      .subscribe({
        next: (dto) => {
          // 用后端 id 替换本地临时 id:后续评分/删除都走后端 id
          this.recordings.update((list) => list.map((r) =>
            r.id === localId
              ? { ...r, id: dto.id, uploaded: true, error: null,
                  createdAt: new Date(dto.createdAt).getTime(), url: '' }
              : r));
          this.persistError.set('');
          // 上传成功后若这条还在补传队列里 → 移除
          if (this.pendingUploads().includes(localId)) {
            this.pendingUploads.set(this.pendingUploads().filter((x) => x !== localId));
          }
          // ★ 第二十七轮:这条录音若已有评分但当时没能写库,现在用真 GUID 补写
          this.flushPendingScore(localId, dto.id);
        },
        error: (e) => {
          const msg = String((e as { message?: string } | null)?.message ?? e ?? '');
          this.patch(localId, { error: '录音未保存到服务端:' + msg.slice(0, 140) });
          this.persistError.set(msg.slice(0, 200));
        }
      });
  }

  /**
   * ★ 第二十七轮:补传"因素材没有 GUID 而积压"的录音。
   *
   * 调用时机:素材树保存成功、前端拿到真 GUID 之后(组件里 refreshTreeFromServer
   * 完成时调用)。把每条积压录音按它所属素材重新解析 id 再上传。
   *
   * 为什么需要:录音上传要求 materialId 是 GUID;而素材可能是本地种子,
   *   保存到服务端后 id 会变成 GUID。旧实现遇到种子 id 直接放弃 →
   *   录音永远进不了库 → 刷新即丢(Forrest 报的严重 bug)。
   */
  flushPendingUploads(resolveMaterialId: (oldId: string) => string | null): void {
    const queue = this.pendingUploads();
    if (!queue.length) return;

    const stillPending: string[] = [];
    for (const recId of queue) {
      const rec = this.recordings().find((r) => r.id === recId);
      if (!rec) continue;                       // 已被删除,丢弃
      const mapped = resolveMaterialId(rec.materialId);
      if (!RecorderService.isGuid(mapped)) {
        stillPending.push(recId);               // 还没拿到 GUID,继续等
        continue;
      }
      // 用新 GUID 就地改掉 materialId,然后走正常上传
      this.patch(recId, { materialId: mapped! });
      this.upload(recId);
    }
    this.pendingUploads.set(stillPending);
  }

  /** 丢弃待提交的录音(用户不想提交)。 */
  discardPending(): void {
    const rec = this.pendingTake();
    if (!rec) return;
    try { URL.revokeObjectURL(rec.url); } catch { /* 忽略 */ }
    this.pendingTake.set(null);
  }

  /**
   * 从后端载入某素材的历史录音(2026-09-15 第十七轮)。
   *
   * 为什么按素材懒加载而不是一次全部拉:素材可能很多,录音更可能成百上千,
   * 全量拉会让首屏变慢且浪费流量。用户切到哪个素材就拉哪个。
   */
  loadForMaterial(materialId: string | null): void {
    if (!materialId || this.loadedMaterials.has(materialId)) return;

    // ⚠️ 2026-09-16:后端路由是 materials/{materialId:guid}/recordings,
    //    只接受 GUID。而前端在"后端不可用"时会回退到本地种子树,
    //    其 id 是 'f_intro_edu' 这类非 GUID 串 → 直接撞路由 → 404
    //    (Forrest 报的"点播放录音 404"就是这条)。
    //    这里做防御:非 GUID 一律不发请求(本地种子素材本来也没有服务端录音)。
    if (!RecorderService.isGuid(materialId)) return;

    this.loadedMaterials.add(materialId);
    this.loading.set(true);

    this.practiceApi.listRecordings(materialId).subscribe({
      next: (dtos) => {
        // 服务端是唯一真相:把该素材的本地条目替换为服务端记录。
        // 只保留仍在"上传中"的本地条目(它们还没有后端 id)。
        const uploading = this.recordings().filter((r) =>
          r.materialId === materialId && !r.uploaded);
        const fromServer = dtos.map((d) => RecorderService.fromDto(d));
        this.recordings.update((list) => [
          ...uploading,
          ...fromServer,
          ...list.filter((r) => r.materialId !== materialId)
        ]);
        this.loading.set(false);
      },
      error: (e) => {
        // 拉不到就说清:历史录音在服务端,后端没起时看不到
        this.persistError.set(String((e as { message?: string } | null)?.message ?? e ?? '').slice(0, 200));
        this.loading.set(false);
      }
    });
  }

  /**
   * 强制重新拉取某素材的录音列表(忽略"已加载"缓存)。
   *
   * ⚠️ 2026-09-16(Forrest 本轮):Retry 按钮要"刷新下面的内容"。
   *   旧实现用 loadedMaterials 集合做一次性加载,再次调用会被直接 return
   *   → 列表永远不刷新。此方法先把缓存标记去掉再调 loadForMaterial。
   */
  reloadForMaterial(materialId: string | null): void {
    if (!materialId) return;
    this.loadedMaterials.delete(materialId);
    this.loadForMaterial(materialId);
  }

  /**
   * ★ 第三十九轮 数据安全兜底:拉取当前用户的**全部录音**,
   * 把“本地未持有、且能按 materialId 对上当前树”的录音补回内存。
   *
   * 为何需要:loadForMaterial 按素材懒加载 + loadedMaterials 缓存。
   * 一旦某素材在缓存建立后才产生录音(或多标签页/多设备写入),
   * 或者素材 id 与录音的 materialId 一时对不上,录音就不显示 ——
   * 用户看到的是“我的录音没了”。此方法把服务端真相拉回来补齐。
   */
  syncAllRecordings(knownMaterialIds?: Set<string>): void {
    this.practiceApi.listAllRecordings().subscribe({
      next: (dtos) => {
        const fromServer = dtos
          .filter((d) => !knownMaterialIds || knownMaterialIds.has(d.materialId))
          .map((d) => RecorderService.fromDto(d));
        const serverIds = new Set(fromServer.map((r) => r.id));
        // 以服务端为真相:保留仍在“上传中”的本地条目,其余换成服务端集合
        const uploading = this.recordings().filter((r) => !r.uploaded);
        const merged = [
          ...uploading,
          ...fromServer,
          // 保留本地已持有但服务端未返回的(极端情况下回读失败,不得凭空丢弃内存态)
          ...this.recordings().filter((r) => r.uploaded && !serverIds.has(r.id))
        ];
        this.recordings.set(merged);
        // 把所有已同步的素材标记为已加载,避免重复请求
        for (const r of fromServer) this.loadedMaterials.add(r.materialId);
      },
      error: () => { /* 兼底性同步失败不影响主流程,不弹错 */ }
    });
  }

  /** 是否是后端可接受的素材 id(GUID)。非 GUID = 本地种子,没有服务端录音。 */
  static isGuid(v: string | null | undefined): boolean {
    return !!v && /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(v);
  }

  /** 后端录音 → 前端模型。评分直接用后端缓存的,不重打 Azure。 */
  private static fromDto(d: RecordingDto): Recording {
    const sc = d.score;
    return {
      id: d.id,
      materialId: d.materialId,
      createdAt: new Date(d.createdAt).getTime(),
      duration: d.durationSeconds,
      blob: new Blob([], { type: d.contentType }),
      // 音频不回浏览器内存 —— 播放直接指向后端流地址,省内存也省一次下载
      url: '',
      score: sc ? RecorderService.scoreFromDto(sc) : null,
      grading: false,
      // 能从后端列出来的录音,必然已在服务端存在
      uploaded: true,
      error: null
    };
  }

  private static scoreFromDto(sc: RecordingScoreDto): RecordingScore {
    return {
      pronScore: sc.pronScore ?? sc.accuracyScore ?? 0,
      accuracyScore: sc.accuracyScore ?? 0,
      fluencyScore: sc.fluencyScore,
      completenessScore: sc.completenessScore,
      prosodyScore: sc.prosodyScore,
      recognized: sc.recognized ?? '',
      words: sc.words ?? [],
      simulated: false,
      // ★ 第三十四轮:把库里的计费口径带上来 —— 刷新后也能显示花了多少
      billedSeconds: sc.billedSeconds ?? null,
      billedBytes: sc.billedBytes ?? null
    };
  }

  /**
   * 某条录音的播放地址。
   *
   * ⚠️ 第二十六轮:后端录音端点是 [Authorize] 保护的,直接把它喂给
   *   <audio src> 会 **401**(浏览器不带 Authorization 头)。
   *   所以这里**只返回本地 Object URL**;已上传的录音请走
   *   PracticeApi.fetchRecordingAudio() 由 HttpClient 带 token 拉 blob。
   *   (该方法保留是为了给调用方一个明确信号:没有本地 url 时应走鉴权拉流。)
   */
  audioSrc(rec: Recording): string {
    return rec.url ?? '';
  }

  /**
   * 删除一条录音。
   *
   * 后端真删(音频文件 + 元数据 + 评分),本地同步移除。
   * 已在服务端存在的录音若删除失败,**必须把它放回列表** ——
   * 否则界面看起来删掉了、刷新又回来,用户会以为是 bug。
   */
  remove(id: string): void {
    const rec = this.recordings().find((r) => r.id === id);
    if (rec) this.revoke(rec);

    // 还没上传成功的本地条目 → 仅本地删(上传成功后会有真实 GUID 与 uploaded=true)
    if (!rec || !rec.uploaded) {
      this.recordings.update((list) => list.filter((r) => r.id !== id));
      return;
    }

    const snapshot = this.recordings();
    this.recordings.update((list) => list.filter((r) => r.id !== id));
    this.practiceApi.deleteRecording(id).subscribe({
      error: (e) => {
        // 删失败 → 放回原位置,并如实报错
        this.recordings.set(snapshot);
        this.persistError.set(String((e as { message?: string } | null)?.message ?? e ?? '').slice(0, 200));
      }
    });
  }

  /**
   * 素材被删时清掉它的录音。
   * 服务端有 ON DELETE CASCADE(素材删 → 录音删),所以这里只清理本地内存。
   */
  removeByMaterial(materialId: string): void {
    for (const r of this.recordings()) {
      if (r.materialId === materialId) this.revoke(r);
    }
    this.recordings.update((list) => list.filter((r) => r.materialId !== materialId));
    this.loadedMaterials.delete(materialId);
  }

  /** 回收 objectURL(仅本地临时的才有,服务端录音 url 为空)。 */
  private revoke(rec: Recording): void {
    if (rec.url) URL.revokeObjectURL(rec.url);
  }

  /**
   * 清掉某条录音的评分结果。
   * 对应参考站的 "Reset before scoring again" —— 重录前先擦掉旧分数,
   * 防止屏幕上残留上一次的结果让人误读。
   */
  clearScore(id: string): void {
    this.patch(id, { score: null, error: null, grading: false });
  }

  // ---------- AI 评分:真实 Azure 发音评估(无任何模拟回退) ----------
  /**
   * 提交评分。
   *
   * 真实链路(后端 /api/assessment/pronunciation/assess):
   *   1. 浏览器录的是 webm/opus,Azure 只吃 PCM WAV 16k 单声道。
   *   2. 用 Web Audio API 的 decodeAudioData 把它解成 Float32 PCM —— 这是浏览器原生能力,
   *      服务端就不需要 ffmpeg。重采样到 16k 与装 WAV 头都由服务端 PcmWav 做。
   *   3. 只把样本数组上传,key 始终留在服务端。
   *
   * ⚠️ 2026-09-15 第八轮:已**彻底删除**模拟评分回退。
   *    以前后端不可达时会造一份假分数(simulated=true)充场面 —— 现已移除。
   *    拿不到真实结果就如实报错(errors 展示),绝不编造数字。
   *
   * ⚠️ 诚实约束:Free F0 层不返回 Fluency/Prosody,所以这两项一律 null,不用 0 充数。
   */
  async grade(id: string, referenceText: string): Promise<void> {
    const rec = this.recordings().find((r) => r.id === id);
    if (!rec || rec.grading) return;

    // ⚠️ 2026-09-16(真机 404 根因修复):
    //    后端路由是 `recordings/{id:guid}/score` —— 拿本地临时 id(`r_xxx`)
    //    或未上传成功的录音去请求,会直接撞上路由约束 → **404**。
    //    以前靠 `id.startsWith('r_')` 猜,不可靠(上传失败时 id 仍是临时串,
    //    但字节已经进了列表)。现在看明确的 `uploaded` 标志。
    if (!rec.uploaded) {
      this.patch(id, {
        grading: false,
        error: '这条录音还没保存到服务端(或保存失败),无法评分。'
          + '请等上传完成后再点,或重录一次。'
      });
      return;
    }

    // 需求第 4 条:"评分信息入库,避免重复评分"。
    //
    // ⚠️ 第二十七轮(Forrest 报"点击 run ai scoring 还是报错"的真凶):
    //    旧实现在这里**发了一个 GET /recordings/{id}/score 探测缓存**。
    //    而该端点在后端是这样写的 —— 没有评分记录时返回 404:
    //      if (score is null) return Result.Failure(Error.NotFound("评分"));
    //    于是**每一次对未评分录音点评分,控制台必留一条红色 404**,
    //    看起来就像"功能坏了"(其实只是"还没评过")。
    //
    //    修法:**不再探测**。理由:
    //      · 录音列表本来就已经带了 score 字段(列表接口返回的 DTO 里有),
    //        有分就是有分,直接读本地即可,根本不需要为此再发一次请求;
    //      · 评分结果一旦产生,会由 saveScore 写库并在列表刷新时带回来;
    //      · 消除这个探测请求 = 彻底消除那条红色 404。
    if (rec.score) return;                 // 本地已有分数(列表带回来的)→ 不再重复评分

    this.patch(id, { grading: true, error: null });

    // 唯一路径:真实后端。没有模拟分支。
    try {
      this.decodeError.set('');

      // ★★ 第二十九轮:评分用的音频来源必须健壮 ★★
      //   刚录完的录音:blob 在内存里,直接解码,零请求。
      //   刷新后/历史录音:fromDto() 只给了空占位 blob —— 此时
      //   **带鉴权**从后端把音频流拉回来再解码(与回放同一套机制,
      //   走 authInterceptor,不会像 <audio src> 那样 401)。
      let source: Blob | null = rec.blob && rec.blob.size > 0 ? rec.blob : null;
      if (!source && RecorderService.isGuid(id)) {
        try {
          source = await firstValueFrom(this.practiceApi.fetchRecordingAudio(id));
        } catch (e) {
          this.patch(id, {
            grading: false,
            error: '取回录音音频失败,无法评分:'
              + String((e as { message?: string } | null)?.message ?? e ?? '').slice(0, 120)
          });
          return;
        }
      }
      if (!source || source.size === 0) {
        this.patch(id, {
          grading: false,
          error: '这条录音没有可用的音频数据,无法评分。请重新录制。'
        });
        return;
      }

      const samples = await this.decodeToPcm(source);
      if (!samples) {
        // 解不出 PCM 就直说。以前这里会偷偷造分,现在不做了。
        // ★ 第二十八轮:文案不再笼统归罪于"格式" —— 改为回传**真实原因**
        //   (Safari 的 decodeAudioData 兼容问题与录制格式无关,
        //    笼统说"需 webm/opus 或 wav"会把人往错误方向引)。
        const why = this.decodeError()
          || '无法解析该录音格式。请重新录制,或改用 Chrome 打开本页。';
        this.patch(id, { grading: false, error: why });
        return;
      }

      const res = await firstValueFrom(
        this.api.post<{
          pronScore: number | null; accuracyScore: number | null;
          fluencyScore: number | null; completenessScore: number | null;
          prosodyScore: number | null; recognized: string;
          words: { word: string; accuracy: number; errorType: string }[];
          // ★ 第三十四轮:服务端精确算出的送评音频时长(秒)/字节数。
          //   Azure 发音评估按音频时长计费 —— 这是真实计费口径,
          //   不是 token。可能为 null(旧后端) → 界面不显示数字。
          billedSeconds?: number | null;
          billedBytes?: number | null;
        }>('/api/assessment/pronunciation/assess', {
          samples: samples.data,
          sampleRate: samples.rate,
          referenceText,
          language: 'en-US'
        })
      );

      // ★ 第三十四轮:把计费口径也带上 —— 它跟着分数一起走,
      //   落库后在"读本地已存评分"路径也能显示当时花了多少。
      const score: RecordingScore = {
        pronScore: Math.round(res.pronScore ?? res.accuracyScore ?? 0),
        accuracyScore: Math.round(res.accuracyScore ?? 0),
        fluencyScore: res.fluencyScore,
        completenessScore: res.completenessScore,
        prosodyScore: res.prosodyScore,
        recognized: res.recognized ?? '',
        words: res.words ?? [],
        simulated: false,
        billedSeconds: res.billedSeconds ?? null,
        billedBytes: res.billedBytes ?? null
      };
      this.patch(id, { grading: false, score });

      // 把评分结果落库 → 需求第 4 条"下次不必重复评分"。
      // 落库失败**不影响本次结果展示**(分数已在界面上),
      // 但如实记录原因,免得用户下次发现又要重评却不知为何。
      //
      // ★ 第二十七轮(Forrest 报"数据都丢失了,必须都存数据库"):
      //   旧实现只在 `rec.uploaded` 时才落库 —— 若录音还没上传成功
      //   (例如素材当时没有 GUID),**评分结果也一并被丢掉**,
      //   刷新后连分数都没了。这是"数据丢失"的第二条链路。
      //   现在:评分一产生就尝试落库;失败则把这条录音放进
      //   "待补存评分"队列,等录音上传成功后再补写。
      this.persistScore(id, rec, score, referenceText);
    } catch (e) {
      // 后端没起 / 未配 key / Azure 报错 —— 全部如实告知,不造分。
      const raw = String((e as { message?: string })?.message ?? e ?? '');
      let msg = 'AI 评分失败:无法连接评测服务,请确认后端已启动并已配置 Azure Speech 密钥。';
      if (/401|403|Unauthorized|Forbidden/i.test(raw)) {
        msg = 'Azure 密钥无效或区域不匹配(401/403)。请前往 AI 语音设置核对。';
      } else if (/key/i.test(raw)) {
        msg = '服务端尚未配置 Azure Speech 密钥。请前往 AI 语音设置配置。';
      } else if (raw) {
        msg = `AI 评分失败:${raw.slice(0, 160)}`;
      }
      this.patch(id, { grading: false, error: msg });
    }
  }

  /**
   * 把浏览器录音解成 Float32 PCM。
   * decodeAudioData 能直接吃 webm/opus —— 不用自己写解码器。
   * 失败返回 null(例如采样率不支持的容器),由调用方降级。
   */
  private async decodeToPcm(
    blob: Blob
  ): Promise<{ data: number[]; rate: number } | null> {
    const Ctor = window.AudioContext
      || (window as unknown as { webkitAudioContext?: typeof AudioContext }).webkitAudioContext;
    if (!Ctor) return null;

    // ★★ 第二十九轮(Forrest 真机:评分出来却是 Recognition 0%、无逐词分数)★★
    //   真凶:fromDto() 把【来自服务端的录音】的 blob 设成 new Blob([]) (空),
    //   而 grade() 曾经直接拿 rec.blob 去解码。
    //   空 blob 在部分浏览器不抛错、返回 0 长度 AudioBuffer →
    //   getChannelData(0) 得到空数组 → 后端即使拒收也先浪费一次往返;
    //   在另一些浏览器则直接解码失败,被吞成"格式无法解析"。
    //   这里显式挡掉空 blob,让调用方去走"从后端拉流"的正路。
    if (!blob || blob.size === 0) {
      this.decodeError.set(
        '这条录音的音频不在浏览器内存里(通常是刷新页面后加载的历史录音)。'
        + '正在从服务端取回音频…');
      return null;
    }

    const ctx = new Ctor();
    try {
      const buf = await blob.arrayBuffer();

      // ============================================================
      // ★★ 第二十八轮(Forrest 真机:点 Run AI Scoring 报
      //    "无法解析该录音格式(需 webm/opus 或 wav)。请重新录制。")★★
      //
      // 真根因:旧写法 `await ctx.decodeAudioData(buf)` 用的是
      //   **Promise 形式**,而 Safari / 旧 WebKit **不支持 Promise 形式**,
      //   只支持**回调形式**。于是 await 立刻拿到 undefined,
      //   紧接着 `audio.getChannelData(0)` 抛 TypeError,
      //   被外层 catch 吞掉 → 返回 null → 界面报"格式无法解析"。
      //
      //   ⚠️ 这与你实际录的是什么格式【完全无关】——
      //   webm/opus 也好,mp4 也好,在 Safari 上统统解不开。
      //
      // 修法:两种调用形式都兼容 ——
      //   优先走 Promise(Chrome/Firefox/新版 Safari);
      //   若返回值不是 Promise(Safari 旧式),回退到回调 + 手写 Promise。
      // ============================================================
      const audio = await RecorderService.decodeCompat(ctx, buf);

      // 取单声道(多声道就取第一轨 —— 语音评测不需要立体声)
      const ch = audio.getChannelData(0);
      const data = Array.from(ch);
      const rate = audio.sampleRate;

      return { data, rate };
    } catch (e) {
      // 不静默:把真实原因留给调用方诊断(仍返回 null 以保持既有契约)
      this.decodeError.set(RecorderService.describeDecodeError(e));
      return null;
    } finally {
      try { await ctx.close(); } catch { /* 已关闭/不支持 close,忽略 */ }
    }
  }

  /**
   * ★ 第二十八轮:兼容 Safari 的 decodeAudioData 调用封装。
   *
   * 背景:规范说 decodeAudioData 返回 Promise,但 Safari 长期只实现
   * **回调形式**,调用后返回 undefined。旧代码 await 一个 undefined,
   * 拿到 undefined 再取声道 → TypeError → 被吞成"格式不支持"。
   *
   * 这里两种形式都试,任一成功即可。失败时抛错,由上层如实上报。
   */
  private static decodeCompat(ctx: BaseAudioContext, buf: ArrayBuffer): Promise<AudioBuffer> {
    return new Promise<AudioBuffer>((resolve, reject) => {
      let settled = false;
      const ok = (b: AudioBuffer) => { if (!settled) { settled = true; resolve(b); } };
      const bad = (e: unknown) => { if (!settled) { settled = true; reject(e); } };

      try {
        // 形式一:Promise(Chrome / Firefox / 新版 Safari)
        const maybe = ctx.decodeAudioData(buf, ok, bad) as unknown;
        if (maybe && typeof (maybe as Promise<AudioBuffer>).then === 'function') {
          (maybe as Promise<AudioBuffer>).then(ok, bad);
        }
        // 形式二:回调(Safari 旧式)—— 上面已把 ok/bad 传进去了。
        //   若某种实现既不返回 Promise 也不回调,则由下面的超时兜底。
        else if (maybe instanceof AudioBuffer) {
          ok(maybe);
        }
      } catch (e) {
        bad(e);
      }

      // 兜底:3 秒没有任何结果 → 明确报"解码超时",而不是无限挂起
      setTimeout(() => bad(new Error('decodeAudioData 超时(3s)未返回结果')), 3000);
    });
  }

  /** 把解码异常翻译成用户能看懂、且能指向真因的中文说明。 */
  private static describeDecodeError(e: unknown): string {
    const m = String((e as { message?: string } | null)?.message ?? e ?? '');
    if (/timeout|超时/i.test(m)) return '音频解码超时:浏览器未能解析该录音。请重录一次。';
    if (/EncodingError|Unable to decode|decode/i.test(m)) {
      return '浏览器无法解码该录音(EncodingError)。'
        + '常见于 Safari 录制的音轨格式不被 Web Audio 支持 —— 请重录,或改用 Chrome 打开本页。';
    }
    return '音频解码失败:' + m.slice(0, 120);
  }

  /**
   * ★ 第二十七轮:把评分结果写进后端。
   *
   * 与旧实现的区别:**不再要求 rec.uploaded**。
   *   录音已上传 → 直接写;
   *   录音尚未上传 → 记入 pendingScores,等 flushPendingUploads 上传成功后补写。
   *
   * 为什么必须这样:否则"素材还没 GUID 时录的音",分数评出来了却存不进库,
   * 刷新后连结果都看不到 —— 正是 Forrest 报的"数据都丢失了"。
   */
  private persistScore(id: string, rec: Recording, score: RecordingScore,
    referenceText: string): void {
    if (!rec.uploaded || !RecorderService.isGuid(id)) {
      // 还不能写库 → 入队等录音上传成功后补写
      this.pendingScores.set([...this.pendingScores(), { id, referenceText }]);
      return;
    }

    this.practiceApi.saveScore(id, {
      pronScore: score.pronScore,
      accuracyScore: score.accuracyScore,
      fluencyScore: score.fluencyScore,
      completenessScore: score.completenessScore,
      prosodyScore: score.prosodyScore,
      recognized: score.recognized,
      words: score.words,
      referenceText,
      billedSeconds: score.billedSeconds ?? null,
      billedBytes: score.billedBytes ?? null
    }).subscribe({
      next: () => {
        // 落库成功 → 从待补队列移除
        this.pendingScores.set(this.pendingScores().filter((x) => x.id !== id));
      },
      error: (e) => this.persistError.set(
        '评分已生成但未能存入历史(下次会重新评分):' +
        String((e as { message?: string } | null)?.message ?? e ?? '').slice(0, 160))
    });
  }

  /** ★ 第二十八轮:上一次解码失败的真实原因(不静默)。 */
  readonly decodeError = signal('');

  /** ★ 第二十七轮:等待补写库的评分(录音上传成功后统一补写)。 */
  readonly pendingScores = signal<{ id: string; referenceText: string }[]>([]);

  /**
   * ★ 第二十七轮:录音上传成功(拿到真 GUID)后,把当时评出的分数补写进库。
   * localId = 上传前的临时 id;serverId = 后端返回的 GUID。
   */
  private flushPendingScore(localId: string, serverId: string): void {
    const item = this.pendingScores().find((x) => x.id === localId);
    if (!item) return;
    const rec = this.recordings().find((r) => r.id === serverId);
    if (!rec?.score) {
      this.pendingScores.set(this.pendingScores().filter((x) => x.id !== localId));
      return;
    }
    const sc = rec.score;
    this.practiceApi.saveScore(serverId, {
      pronScore: sc.pronScore,
      accuracyScore: sc.accuracyScore,
      fluencyScore: sc.fluencyScore,
      completenessScore: sc.completenessScore,
      prosodyScore: sc.prosodyScore,
      recognized: sc.recognized,
      words: sc.words,
      referenceText: item.referenceText,
      billedSeconds: sc.billedSeconds ?? null,
      billedBytes: sc.billedBytes ?? null
    }).subscribe({
      next: () => this.pendingScores.set(this.pendingScores().filter((x) => x.id !== localId)),
      error: (e) => this.persistError.set(
        '评分未能存入历史:' + String((e as { message?: string } | null)?.message ?? e ?? '').slice(0, 160))
    });
  }

  private patch(id: string, part: Partial<Recording>): void {
    this.recordings.update((list) =>
      list.map((r) => (r.id === id ? { ...r, ...part } : r))
    );
  }

  /**
   * 挑选录音容器/编码。
   *
   * ★ 第二十八轮:Safari 不支持 webm —— 旧候选表把 audio/mp4 放在最后,
   *   虽然最终能轮到它,但**顺序上先问 webm 是纯浪费**,且更关键的是:
   *   Safari 对 `audio/mp4` 的 isTypeSupported 常返回 true 而具体 codec 需细化。
   *   这里把 mp4/aac 明确补齐,并保留 webm/opus 优先(Chrome 下体积更小)。
   *
   * 不管录成什么格式,后续评分都靠 Web Audio 解码 ——
   * 而 Web Audio 的解码能力各浏览器不同(见 decodeCompat 的说明)。
   */
  private pickMime(): string {
    const cands = [
      // Chrome / Firefox 首选:webm+opus,语音场景体积小
      'audio/webm;codecs=opus',
      'audio/webm',
      // Safari / iOS:mp4 容器 + AAC
      'audio/mp4;codecs=mp4a.40.2',
      'audio/mp4',
      // 少数实现只认 ogg
      'audio/ogg;codecs=opus',
      'audio/ogg'
    ];
    for (const c of cands) {
      try {
        if (MediaRecorder.isTypeSupported(c)) return c;
      } catch {
        /* 某些环境没有 isTypeSupported */
      }
    }
    return '';
  }

  private clearTimer(): void {
    if (this.timer) {
      clearInterval(this.timer);
      this.timer = null;
    }
  }

  // ============================================================
  // ★ 第三十轮:录音中的实时波形
  //   直接从已有的 MediaStream 挂 AnalyserNode,不额外申请麦克风。
  //   每 60ms 采样一次,取时域数据的 RMS 归一化后推入 liveWave,
  //   前端据此画出「正在说话」的跳动柱条。
  //   ⚠️ 必须在 stop/cancel/ngOnDestroy 释放,否则 AudioContext 泄漏。
  // ============================================================
  private startLiveWave(): void {
    this.stopLiveWave();
    if (!this.stream) return;
    const Ctor = window.AudioContext
      || (window as unknown as { webkitAudioContext?: typeof AudioContext }).webkitAudioContext;
    if (!Ctor) return;
    try {
      this.analyserCtx = new Ctor();
      const src = this.analyserCtx.createMediaStreamSource(this.stream);
      this.analyser = this.analyserCtx.createAnalyser();
      // 32 个柱位;FFT 取小一点,够画音量强度即可
      this.analyser.fftSize = 64;
      this.analyser.smoothingTimeConstant = 0.6;
      src.connect(this.analyser);
      // 注意:不 connect 到 destination —— 否则会把麦克风回放出来(啸叫)。

      const buf = new Uint8Array(this.analyser.frequencyBinCount);
      const BARS = 28;
      this.liveWave.set(new Array(BARS).fill(0));

      this.waveTimer = setInterval(() => {
        if (!this.analyser) return;
        this.analyser.getByteFrequencyData(buf);
        // 把频域分箱压缩成 BARS 根柱子,取每段平均后归一化到 0~1
        const out: number[] = [];
        const step = Math.max(1, Math.floor(buf.length / BARS));
        for (let i = 0; i < BARS; i++) {
          let sum = 0;
          for (let j = 0; j < step; j++) sum += buf[i * step + j] ?? 0;
          const avg = sum / step / 255;
          // 轻微放大低音量段,让小声说话也能看到起伏
          out.push(Math.min(1, Math.pow(avg, 0.75) * 1.25));
        }
        this.liveWave.set(out);
      }, 60);
    } catch {
      // 波形纯粹是视觉增强:失败绝不能影响录音主流程。
      this.stopLiveWave();
    }
  }

  private stopLiveWave(): void {
    if (this.waveTimer) {
      clearInterval(this.waveTimer);
      this.waveTimer = null;
    }
    if (this.analyser) {
      try { this.analyser.disconnect(); } catch { /* 已断开 */ }
      this.analyser = null;
    }
    if (this.analyserCtx && this.analyserCtx.state !== 'closed') {
      void this.analyserCtx.close().catch(() => undefined);
    }
    this.analyserCtx = null;
    this.liveWave.set([]);
  }

  private releaseStream(): void {
    // 第三十轮:释放麦克风前先停掉波形采样,避免 AnalyserNode 持有已关闭的流。
    this.stopLiveWave();
    this.stream?.getTracks().forEach((t) => t.stop());
    this.stream = null;
  }
}
