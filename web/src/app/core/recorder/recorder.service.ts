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

  /** 已从后端载入过的素材 id —— 避免同一素材反复拉取。 */
  private loadedMaterials = new Set<string>();

  // 2026-09-15 第八轮:已删除 backendAvailable 字段。
  // 以前它用来"失败一次就不再试后端,直接走模拟",现在没有模拟了,
  // 所以每次评分都真实请求,失败如实报错,不再有静默降级状态。

  /** 全部录音(按素材 id 可筛)。 */
  readonly recordings = signal<Recording[]>([]);

  /** 是否正在录。 */
  readonly recording = signal(false);

  /** 录音已进行的秒数(用于计时显示)。 */
  readonly elapsed = signal(0);

  /** 当前录音所属素材 id。 */
  private targetMaterialId: string | null = null;

  private mediaRecorder: MediaRecorder | null = null;
  private chunks: Blob[] = [];
  private stream: MediaStream | null = null;
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
    this.clearTimer();
  }

  cancel(): void {
    this.chunks = [];
    if (this.mediaRecorder && this.mediaRecorder.state !== 'inactive') {
      this.mediaRecorder.onstop = null;
      this.mediaRecorder.stop();
    }
    this.recording.set(false);
    this.clearTimer();
    this.releaseStream();
  }

  private finalize(): void {
    const blob = new Blob(this.chunks, {
      type: this.mediaRecorder?.mimeType || 'audio/webm'
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
    const blob = rec.blob;
    const ext = blob.type.includes('ogg') ? 'ogg' : blob.type.includes('mp4') ? 'm4a' : 'webm';

    // 非 GUID 素材(本地种子)无法上传 —— 如实标记,不发注定 404 的请求。
    if (!RecorderService.isGuid(materialId)) {
      this.patch(rec.id, {
        error: '该素材尚未保存到服务端,录音只保留在本次会话。请先点"保存修改"把素材树存到后端。'
      });
      return;
    }

    const localId = rec.id;
    this.practiceApi.uploadRecording(materialId, blob, rec.duration,
      `take-${Date.now()}.${ext}`, blob.type || 'audio/webm')
      .subscribe({
        next: (dto) => {
          // 用后端 id 替换本地临时 id:后续评分/删除都走后端 id
          this.recordings.update((list) => list.map((r) =>
            r.id === localId
              ? { ...r, id: dto.id, uploaded: true, createdAt: new Date(dto.createdAt).getTime(), url: '' }
              : r));
          this.persistError.set('');
        },
        error: (e) => {
          const msg = String((e as { message?: string } | null)?.message ?? e ?? '');
          this.patch(localId, { error: '录音未保存到服务端:' + msg.slice(0, 140) });
          this.persistError.set(msg.slice(0, 200));
        }
      });
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
      simulated: false
    };
  }

  /** 某条录音的播放地址(走后端流;本地未上传的用 objectURL)。 */
  audioSrc(rec: Recording): string {
    if (rec.url) return rec.url;
    return this.practiceApi.audioUrl(rec.id);
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
    // 已落库的录音先问后端要缓存分数 —— 命中就免掉一次 Azure 调用(省钱省时间)。
    try {
      const cached = await firstValueFrom(this.practiceApi.getRecordingScore(id));
      this.patch(id, { grading: false, score: RecorderService.scoreFromDto(cached), error: null });
      return;
    } catch {
      // 404(还没评分过)或后端不可用 → 继续走真实评分。
      // 这里静默是对的:缓存未命中是正常路径,不该弹错。
    }

    this.patch(id, { grading: true, error: null });

    // 唯一路径:真实后端。没有模拟分支。
    try {
      const samples = await this.decodeToPcm(rec.blob);
      if (!samples) {
        // 解不出 PCM 就直说。以前这里会偷偷造分,现在不做了。
        this.patch(id, {
          grading: false,
          error: '无法解析该录音格式(需 webm/opus 或 wav)。请重新录制。'
        });
        return;
      }

      const res = await firstValueFrom(
        this.api.post<{
          pronScore: number | null; accuracyScore: number | null;
          fluencyScore: number | null; completenessScore: number | null;
          prosodyScore: number | null; recognized: string;
          words: { word: string; accuracy: number; errorType: string }[];
        }>('/api/assessment/pronunciation/assess', {
          samples: samples.data,
          sampleRate: samples.rate,
          referenceText,
          language: 'en-US'
        })
      );

      const score: RecordingScore = {
        pronScore: Math.round(res.pronScore ?? res.accuracyScore ?? 0),
        accuracyScore: Math.round(res.accuracyScore ?? 0),
        fluencyScore: res.fluencyScore,
        completenessScore: res.completenessScore,
        prosodyScore: res.prosodyScore,
        recognized: res.recognized ?? '',
        words: res.words ?? [],
        simulated: false
      };
      this.patch(id, { grading: false, score });

      // 把评分结果落库 → 需求第 4 条"下次不必重复评分"。
      // 落库失败**不影响本次结果展示**(分数已在界面上),
      // 但如实记录原因,免得用户下次发现又要重评却不知为何。
      // ⚠️ 只在 uploaded 时才发(否则又撞 404)。
      if (rec.uploaded) {
        this.practiceApi.saveScore(id, {
          pronScore: score.pronScore,
          accuracyScore: score.accuracyScore,
          fluencyScore: score.fluencyScore,
          completenessScore: score.completenessScore,
          prosodyScore: score.prosodyScore,
          recognized: score.recognized,
          words: score.words,
          referenceText
        }).subscribe({
          error: (e) => this.persistError.set(
            '评分已生成但未能存入历史(下次会重新评分):' +
            String((e as { message?: string } | null)?.message ?? e ?? '').slice(0, 160))
        });
      }
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
    try {
      const Ctor = window.AudioContext
        || (window as unknown as { webkitAudioContext?: typeof AudioContext }).webkitAudioContext;
      if (!Ctor) return null;

      const ctx = new Ctor();
      const buf = await blob.arrayBuffer();
      const audio = await ctx.decodeAudioData(buf);

      // 取单声道(多声道就取第一轨 —— 语音评测不需要立体声)
      const ch = audio.getChannelData(0);
      const data = Array.from(ch);
      const rate = audio.sampleRate;

      await ctx.close();
      return { data, rate };
    } catch {
      return null;
    }
  }

  private patch(id: string, part: Partial<Recording>): void {
    this.recordings.update((list) =>
      list.map((r) => (r.id === id ? { ...r, ...part } : r))
    );
  }

  private pickMime(): string {
    const cands = [
      'audio/webm;codecs=opus',
      'audio/webm',
      'audio/ogg;codecs=opus',
      'audio/mp4'
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

  private releaseStream(): void {
    this.stream?.getTracks().forEach((t) => t.stop());
    this.stream = null;
  }
}
