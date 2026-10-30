import { computed, effect, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { Recording, RecordingScore, RecorderService } from '../../core/recorder/recorder.service';
import type { AiPracticeComponent } from './ai-practice.component';
import { SENTENCE_MIN_WORDS, sentenceWordCountOf } from './sentence-select.helpers';
import { computeWaveBarsFromSamples, estimateAvgF0 } from './waveform.helpers';


/**
 * 从 localStorage 恢复字号缩放百分比。
 * 在模块级声明(不是类方法)—— signal() 初始化时需要立刻调用,
 * 此时 this 还不可用。
 * 返回值一定落在 80–150 区间;读失败/值非法都回落 100。
 */
/** "Azure 已配置"标记位的 localStorage 键(与设置页共用)。 */
export const AZURE_READY_KEY = 'azure_speech_configured';

/**
 * 读"Azure 已配置"标记位。
 * ⚠️ 这不是密钥 —— 密钥永远不进浏览器。此标记只表示用户是否去设置页
 *    声明过已配置,用于前端提前引导。默认 false(未配置)。
 */
function readAzureReadyFlag(): boolean {
  try {
    return localStorage.getItem(AZURE_READY_KEY) === '1';
  } catch {
    return false;
  }
}

/** AI 评分配置 —— 用户可在页面内自己调。 */
export interface GradingConfig {
  /** 各维度权重(0-100),用于总分加权 */
  weights: { key: string; label: string; weight: number }[];
  /** 严格度 1-5,影响打分宽严 */
  strictness: number;
  /** 是否检查语法 */
  checkGrammar: boolean;
  /** 是否给改进建议 */
  advice: boolean;
  /** 评分语言 */
  lang: string;
}
export class PracticeGradingSession {
  constructor(private readonly host: AiPracticeComponent) {}


  /** 录音失败提示(麦克风权限等)。 */
  readonly recError = signal<string | null>(null);

  /** 当前素材的录音列表。 */
  readonly myRecordings = computed(() =>
    this.host.recorder.forMaterial(this.host.selectedId())
  );

  /**
   * ★ 第四十九轮(Forrest 报"新录音显示 Take #1"):Take 序号按时间先后。
   * 列表显示顺序是"最新在上"(后端 CreatedAt DESC),旧实现直接用显示序号
   * ($index+1)当 Take 号 → 每录一条新录音,旧的号全被顶下去。
   * 现在:按 createdAt 升序编号,最早 = #1,新录音永远是最大号;
   * 显示顺序保持不变,只有号码与时间轴对齐。
   */
  readonly takeNumbers = computed(() => {
    const sorted = [...this.myRecordings()].sort((a, b) => a.createdAt - b.createdAt);
    const map = new Map<string, number>();
    sorted.forEach((r, i) => map.set(r.id, i + 1));
    return map;
  });

  /** 某条录音的 Take 序号(时间先后,最早=1)。 */
  takeNo(id: string): number {
    return this.takeNumbers().get(id) ?? 1;
  }

  // ============================================================
  // Azure Speech 凭证校验 —— AI 评分前置门禁(任务书第六节)
  // ============================================================
  //
  // ⚠️ 诚实边界:前端**拿不到也存不到** Azure Key。
  //    Key 只存在于服务端 appsettings.Development.json(gitignored),
  //    浏览器永远不知道真实密钥。所以这里的 azureReady() 只能反映
  //    "用户是否已声明配置过" —— 靠一个本地标记位,不是密钥本身。
  //
  //    真正的密钥校验发生在**服务端**(PronunciationAssessor 调 Azure 失败
  //    会返回 ErrorType/错误信息)。所以本门禁的作用是"早发现早引导",
  //    而不是伪造一个前端安全边界。
  // ============================================================

  /**
   * Azure 是否就绪。
   *
   * ★ 2026-09-20(Forrest BUG1 真因修复):以**服务端**为准。
   *
   * 旧实现只读 localStorage['azure_speech_configured'] —— 该标记只有用户
   * 手动去 AI 设置页保存过才会写入。本机从未写过,于是 azureReady 恒为 false,
   * speak() 第一行就被门禁挡住 → 点播放毫无反应、时间永远 --:-- / --:--
   * (这正是 Forrest 报的"默认时间是空的")。
   *
   * 现在:localStorage 标记为 true 时立即就绪(快速路径);
   *       否则向 GET /speech/settings 查 hasKey,以服务端为准。
   */
  readonly azureReady = signal(readAzureReadyFlag());

  /**
   * ★ 2026-09-20(Forrest BUG1 修复):向服务端核对 Azure 是否真的配好。
   *   服务端持 key(不进浏览器),GET speech/settings 只回 hasKey 布尔值。
   *   本地标记已是 true 就不必再问;否则以服务端回答为准。
   */
  syncAzureReadyFromServer(): void {
    if (this.azureReady()) return;   // 本地已声明配置过 → 不必等网络
    this.host.practiceApi.getSpeechSettings().subscribe({
      next: (s) => {
        if (s?.hasKey) {
          this.azureReady.set(true);
          try { localStorage.setItem(AZURE_READY_KEY, '1'); } catch { /* 隐私模式忽略 */ }
          this.host.cdr.detectChanges();
          // ★ 2026-09-20(Forrest):若首次进入时该状态尚未就绪、导致
          //   本地音频时长没装载,这里补跑一次,保证与响应时序无关。
          this.host.tts.retryCachedDuration();
        }
      },
      // 服务端不可用时不误判为"未配置",保持原值,让用户点播放时拿到真实错误
      error: () => { /* noop */ }
    });
  }

  /**
   * 待提交的录音是否可以提交。
   * 2026-09-16(Forrest 本轮):提交按钮的行为定义 ——
   *   · 没有录音        → 置灰
   *   · 录好了(有待提交) → 激活
   * 这是界面上的硬门禁,不让用户点到注定失败的按钮。
   */
  readonly canSubmitTake = computed(() => {
    const pt = this.host.recorder.pendingTake();
    if (!pt) return false;
    // 素材不是 GUID(本地种子)→ 后端存不了,先如实置灰(保存素材树后再来)
    return RecorderService.isGuid(pt.materialId);
  });

  /**
   * 待提交录音是否正在上传。
   * 为什么需要这个:提交 → 列表落一条本地记录 → 上传异步完成。
   * 上传未回来之前,那条录音还不能评分(否则 404)。
   * 界面用这个标志告诉用户"还在上传",而不是让人对着一颗灰按钮发愣。
   */
  readonly uploadingPending = computed(() => {
    const list = this.host.recorder.recordings();
    return list.some((r) => !r.uploaded);
  });

  /** 当前素材下的录音序号(第几次录音)。 */
  readonly recOrder = computed(() => this.myRecordings().length);

  /**
   * ★ 第三十三轮(Forrest):AI 评分的**额度消耗**标记。
   *
   * 与示范朗读同理,必须告诉用户这次到底花没花 Azure 额度:
   *   · 'fresh' = 本次真调了 Azure 发音评估 → 消耗了额度
   *   · 'cache' = 该录音已有入库评分,直接读库 → 未消耗额度
   *   · ''      = 无提示
   *
   * ★ 第五十四轮:grade() 允许重复评分后,评分入口恒为 'fresh';
   *   'cache' 仅保留给"列表首次加载带回历史评分"的展示路径。
   */
  readonly scoreCost = signal<'' | 'fresh' | 'cache'>('');

  /**
   * ★ 第三十四轮(Forrest):本次 AI 评分的精确计费量。
   *
   * ⚠️ 评分这边同样不提 token —— Azure 发音评估按**音频时长**计费。
   *   billedSeconds 由服务端从 WAV 头精确算出(字节率 × data 长度),
   *   不是估算也不是从 Azure 请求返回物里猜的。
   *   null = 服务端/旧后端未提供 → 不显示数字。
   */
  readonly scoreBilledSeconds = signal<number | null>(null);

  /** 评分时送评的音频字节数(核对用)。 */
  readonly scoreBilledBytes = signal<number | null>(null);

  /**
   * ★ 第三十四轮:把评分音频秒数格式化成界面文案用的字符串。
   *
   * 拿不到就返回 '—'(与四项分数的 "拿不到显示 —" 口径一致),
   * **绝不编一个数**。保留 1 位小数(音频长度到 0.1 秒已足够精确)。
   */
  readonly scoreSecondsText = computed(() => {
    const s = this.scoreBilledSeconds();
    if (s === null || !Number.isFinite(s)) return '—';
    return s.toFixed(1);
  });

  // ---------- 发音音标 ----------
  /**
   * 取单词音标。
   * ⚠️ 诚实约束：Azure PA 的返回里**没有音标**（只有 AccuracyScore/ErrorType）。
   * 本地没有音标词典，所以无法给出真实音标 —— 返回 null，
   * 由界面显示"暂无音标"。**绝不编造 /ˈwɜːd/ 这种看起来像真的假音标。**
   * 后续若接发音词典 API（如 Free Dictionary API）再填这里。
   */
  phonetic(_word: string): string | null {
    return null;
  }

  /** 当前在逐词区点开的单词下标（null = 未点开）。 */
  readonly pickedWord = signal<number | null>(null);

  /** 点词展开音标与得分；再点同一个则收起。 */
  pickWord(i: number): void {
    this.pickedWord.set(this.pickedWord() === i ? null : i);
  }

  /** ★ #1:填充词检测(Yoodli 模式)。 */
  private static readonly FILLERS = new Set([
    'um', 'uh', 'uhm', 'umm', 'hmm', 'hm',
    'like', 'you know', 'i mean', 'basically', 'actually', 'literally',
    'stuff', 'things', 'well', 'so', 'right',
    '那个', '就是', '然后', '嗯', '啊', '这个', '其实'
  ]);

  isFiller(word: string): boolean {
    return PracticeGradingSession.FILLERS.has(word.toLowerCase().trim());
  }

  /** ★ #1:当前评分的填充词统计。 */
  readonly fillerStats = computed(() => {
    const sc = this.activeTake()?.score;
    if (!sc?.words?.length) return null;
    const fillers = sc.words.filter(w => this.isFiller(w.word));
    const minutes = (sc.billedSeconds ?? 60) / 60;
    return {
      count: fillers.length,
      perMinute: minutes > 0 ? +(fillers.length / minutes).toFixed(1) : 0,
      indices: new Set(sc.words.map((w, i) => this.isFiller(w.word) ? i : -1).filter(i => i >= 0))
    };
  });

  /** ★ #15:语速分析 (WPM + 评价)。票137：显示精度由整数改为一位小数
   *   （与参考图二 144.7 WPM 的形态对齐）；verdict 带（110/160）作用于
   *   一位小数后的值。wpm 为 toFixed(1) 字符串，模板直接填 {wpm} 占位。 */
  readonly speechPace = computed(() => {
    const sc = this.activeTake()?.score;
    if (!sc?.words?.length) return null;
    const wordCount = sc.words.length;
    const minutes = (sc.billedSeconds ?? 60) / 60;
    if (minutes <= 0) return null;
    const wpm = (wordCount / minutes).toFixed(1);
    const wpmNum = Number(wpm);
    let verdict: string;
    let level: 'slow' | 'good' | 'fast';
    if (wpmNum < 110) { verdict = this.host.t('practice.paceSlow'); level = 'slow'; }
    else if (wpmNum <= 160) { verdict = this.host.t('practice.paceGood'); level = 'good'; }
    else { verdict = this.host.t('practice.paceFast'); level = 'fast'; }
    return { wpm, verdict, level, wordCount };
  });

  /** 票132 R-F6:远端取源的在途序号——每次发起 +1,迟到结果按序号作废。 */
  private replayFetchSeq = 0;

  /** ★ #1:点击填充词 → 从该时间戳回放音频。 */
  replayAt(offsetMs: number): void {
    const rec = this.activeTake();
    const blob = (rec as any)?.blob as Blob | undefined;
    // 票132 R-F1/D3:本地有源判据与全仓统一为 size > 0(grade/togglePlay/
    // waveAudioSource 三处先例同此判据)。旧实现以 truthiness 为闸门——重载
    // take 经 fromDto 拿到的是 0 字节占位 blob(truthy),被当真源对空源
    // play() 必败(MEDIA_ERR_SRC_NOT_SUPPORTED),且只弹笼统 retryLater、
    // 全程不发请求(票122 已知发现)。
    if (blob && blob.size > 0) {
      this.playReplayBlob(blob, offsetMs);
      return;
    }
    // R-F2:本地无真源 → 仅当该条是已上传录音(id 为 GUID)才走远端支路;
    // 未上传临时条又恰无 blob 的异常态不发请求,沿现行无源文案收尾。
    if (!rec || !RecorderService.isGuid(rec.id)) {
      this.host.toast(this.host.t('rec.noAudio'));
      return;
    }
    // R-F3:点播当刻经既有鉴权通道懒拉取(fetchRecordingAudio,与
    // togglePlay 同口),不做加载时回填、不回写不缓存(D1/D5)。
    const takeId = rec.id;
    const seq = ++this.replayFetchSeq;
    this.host.practiceApi.fetchRecordingAudio(takeId).subscribe({
      next: (fetched) => {
        if (fetched && fetched.size > 0) {
          const url = URL.createObjectURL(fetched);
          // R-F6:在途期间用户切了 take 或连点新词 → 本次结果作废,
          // revoke 未播的 URL,不播不弹。
          if (!this.isReplayFetchCurrent(takeId, seq)) { URL.revokeObjectURL(url); return; }
          this.playReplayUrl(url, offsetMs);
        } else if (this.isReplayFetchCurrent(takeId, seq)) {
          // 服务端回空体按取流失败处理(R-F3),走 R-F5 其余失败支。
          this.host.toast(this.replayLoadFailText(''));
        }
      },
      error: (e) => {
        // R-F6:迟到的旧失败与用户已无关,静默作废。
        if (!this.isReplayFetchCurrent(takeId, seq)) return;
        // R-F5:失败分级照 togglePlay——404 = 音频文件已不存在,明示之;
        // 其余取流失败走 recLoadFail 组文。不许笼统弹 retryLater 冒充原因。
        const status = (e as { status?: number } | null)?.status;
        if (status === 404) {
          this.host.toast(this.host.t('practice.recGone'));
        } else {
          const raw = String((e as { message?: string } | null)?.message ?? e ?? '');
          this.host.toast(this.replayLoadFailText(raw));
        }
      },
    });
  }

  /** R-F6 守卫:发起后 activeTake 仍是同一条、且没有更新一次 replay 发起。 */
  private isReplayFetchCurrent(takeId: string, seq: number): boolean {
    return seq === this.replayFetchSeq && this.activeTake()?.id === takeId;
  }

  /** 以本地真源 blob 起播(票132 前 replayAt 的现行五步,逐字守恒)。 */
  private playReplayBlob(blob: Blob, offsetMs: number): void {
    this.playReplayUrl(URL.createObjectURL(blob), offsetMs);
  }

  /** 票132 本地/远端两支路共用的同一套播法:同 seek 公式、同失败文案。 */
  private playReplayUrl(url: string, offsetMs: number): void {
    const audio = new Audio(url);
    audio.currentTime = Math.max(0, offsetMs / 1000 - 0.5); // 提前 0.5s
    audio.play().catch(() => this.host.toast(this.host.t('practice.retryLater')));
    audio.onended = () => URL.revokeObjectURL(url);
  }

  /** 取流失败(非 404)的组文,与 togglePlay 的 recLoadFail 口径逐字一致。 */
  private replayLoadFailText(raw: string): string {
    return this.host.t('practice.recLoadFail') + (raw || this.host.t('practice.retryLater'));
  }

  /** ★ #3:Orai 模式 — 一次只给一个最关键的改进点。 */
  readonly oneFix = computed(() => {    const sc = this.activeTake()?.score;
    if (!sc) return null;
    const fs = this.fillerStats();
    // 优先级:填充词 > 流利度 > 准确度 > 发音 > 完整度
    if (fs && fs.perMinute > 3) {
      return {
        icon: 'graphic_eq',
        title: this.host.t('practice.oneFixFillersTitle'),
        detail: this.host.t('practice.oneFixFillersDetail')
          .replace('{count}', `${fs.count}`).replace('{per}', `${fs.perMinute}`),
      };
    }
    const dims = [
      { key: 'fluencyScore', label: this.host.t('dim.fluency'), val: sc.fluencyScore },
      { key: 'accuracyScore', label: this.host.t('dim.accuracy'), val: sc.accuracyScore },
      { key: 'pronScore', label: this.host.t('dim.pronunciation'), val: sc.pronScore },
      { key: 'completenessScore', label: this.host.t('dim.completeness'), val: sc.completenessScore },
    ].filter(d => d.val != null) as { key: string; label: string; val: number }[];
    if (!dims.length) return null;
    dims.sort((a, b) => a.val - b.val);
    const weakest = dims[0];
    if (weakest.val >= 85) return null; // 都不错,不打扰
    const fixes: Record<string, string> = {
      fluencyScore: this.host.t('practice.oneFixFluency'),
      accuracyScore: this.host.t('practice.oneFixAccuracy'),
      pronScore: this.host.t('practice.oneFixPron'),
      completenessScore: this.host.t('practice.oneFixCompleteness'),
    };
    return {
      icon: 'tips_and_updates',
      title: this.host.t('practice.oneFixPriority')
        .replace('{label}', weakest.label).replace('{score}', weakest.val.toFixed(0)),
      detail: fixes[weakest.key] ?? this.host.t('practice.oneFixFallback'),
    };
  });

  /** ★ #10:Interviewing.io 模式 — Hire/No-Hire verdict + 2-3 个待加强点。 */
  /** ★ #14:AI 追问 (像真面试官一样,基于回答生成一层追问)。 */
  /** 票136 R-136-1:追问按 take id 存（会话内，不进后端）；会话级信号改为按 activeTakeId 解析的计算值，模板与组件绑定保持原名。 */
  readonly followUpsByTake = signal<Map<string, string>>(new Map());
  readonly followUpQuestion = computed(() => {
    const id = this.activeTakeId();
    if (!id) return null;
    return this.followUpsByTake().get(id) ?? null;
  });
  /** 票136 R-136-8:加载态跟生成 Take 走（生成中 take id 集合）；模板进度条仅在加载中 take 的报告展开时显示。 */
  readonly followUpLoadingTakes = signal<Set<string>>(new Set());
  readonly followUpLoading = computed(() => {
    const id = this.activeTakeId();
    return id !== null && this.followUpLoadingTakes().has(id);
  });

  /** ★ 打通:AI Practice 低分 → 创建 Tech Stack 复习卡。 */
  async addToReview(): Promise<void> {
    const question = this.host.customQuestion() || this.host.currentText();
    if (!question.trim()) return;
    const take = this.activeTake();
    const transcript = take?.score?.recognized ?? '';
    if (!transcript.trim()) { this.host.toast(this.host.t('practice.noRecognizedText')); return; }
    try {
      await this.host.knowledgeApi.createItem({
        title: question.slice(0, 60),
        question: question,
        myAnswer: transcript.slice(0, 1000),
        topic: 'AI Practice',
        category: this.host.t('practice.reviewCategory'),
        mastery: 'ToDo'
      }).toPromise();
      this.host.toast(this.host.t('practice.addedToReview'));
    } catch {
      this.host.toast(this.host.t('practice.addToReviewFailed'));
    }
  }

  async generateFollowUp(): Promise<void> {
    const take = this.activeTake();
    if (!take) return;
    // 票136 R-136-7:生成绑生成时 Take（R-F6 同款守卫模式）：完成时写回捕获的 takeId，不是完成时的 activeTake。
    const takeId = take.id;
    if (this.followUpLoadingTakes().has(takeId)) return;
    const transcript = take?.score?.recognized ?? '';
    const question = this.host.currentText();
    if (!transcript.trim()) { this.host.toast(this.host.t('practice.noRecognizedText')); return; }

    this.followUpLoadingTakes.update(s => new Set(s).add(takeId));
    // 现行行为守恒（按 take 作用域）：开始时清本条（旧 set(null)），失败/空结果时本条保持空态 + toast；绝不碰其他 take 的条目。
    this.followUpsByTake.update(m => { const n = new Map(m); n.delete(takeId); return n; });
    try {
      const res = await this.host.aiApi.complete({
        purpose: 'ai-practice-followup',
        systemPrompt: this.followUpSystemPrompt(),
        prompt: `Original question: ${question}\n\nCandidate's answer:\n${transcript.slice(0, 2000)}`,
        maxTokens: 200,
        temperature: 0.7
      }).toPromise();
      const q = res?.text ?? res?.content ?? '';
      if (q.trim()) {
        this.followUpsByTake.update(m => { const n = new Map(m); n.set(takeId, q.trim()); return n; });
      } else {
        this.host.toast(this.host.t('practice.followUpFailed'));
      }
    } catch {
      this.host.toast(this.host.t('practice.followUpFailed'));
    } finally {
      this.followUpLoadingTakes.update(s => { const n = new Set(s); n.delete(takeId); return n; });
    }
  }

  /** 票136 R-136-10:语言钉死 practiceLang（前端一层，后端透传不动）；prompt 其余部分逐字保留。 */
  private followUpSystemPrompt(): string {
    const lang: Record<string, string> = { 'en-US': 'English', 'fr-FR': 'French', 'zh-CN': 'Chinese' };
    const language = lang[this.host.practiceLang()] ?? 'English';
    return `You are a senior technical interviewer. Based on the candidate's answer, ask ONE sharp follow-up question that digs deeper into a weak point, edge case, or design trade-off. Reply in ${language}. Output only the question, no preamble.`;
  }

  /** 在历史列表里点"查看报告"：把该条设为当前作品并弹到结果区。 */
  focusTake(id: string): void {
    this.activeTakeId.set(id);
    this.pickedWord.set(null);
  }

  // ---------- 评分配置(朗读语言 = 朗读与评分的唯一语言出口) ----------
  readonly config = signal<GradingConfig>({
    weights: [],
    strictness: 3,
    checkGrammar: true,
    advice: true,
    lang: 'en'
  });

  // ---------- 录音 ----------
  async startRecording(): Promise<void> {
    // 票 292：常规录音启动先取消进行中跟读校准（窗口内录音启动规则）。
    // ?. 容忍局部 mock host（既有 pausePendingPreview?.() 同款口径）；
    // 生产 host.shadow 恒存在，行为不受影响。
    this.host.shadow?.cancelMicCalibration();
    this.recError.set(null);
    // 票 269：新录音开始即清掉上一条的选区快照（上一条若未提交已被下行丢弃；
    // 若本条是一句话录音，成功开始后重新捕获，见下）。
    this.host.sentence?.clearRecordingSnapshot();
    const id = this.host.selectedId();
    if (!id) return;
    // ★ 第四十九轮:录音前停掉页面上所有在响的声音 ——
    //   示范朗读 / 历史录音回放 / 待提交试听。否则两路声音叠播,
    //   麦克风还会把正在放的内容录进去。
    this.host.tts.stopSpeak();
    this.stopPlayback();
    this.pausePendingPreview();

    // ★ 第四十九轮:上一条录完还没提交,用户又按了麦克风 ——
    //   明确丢弃并告知。旧实现是等新录音录完由 finalize() 把它无声顶掉,
    //   那条已经录好的音频就【凭空消失】了,用户却以为它还在。
    if (this.host.recorder.pendingTake()) {
      this.host.recorder.discardPending();
      this.host.toast(this.host.t('practice.prevTakeDiscarded'));
    }

    const err = await this.host.recorder.start(id);
    if (err) this.recError.set(err);
    else this.host.sentence?.captureForRecording();
  }

  stopRecording(): void {
    this.host.recorder.stop();
    // ⚠️ 2026-09-16(Forrest 本轮):**不再自动选中**刚录的这条。
    //    录音会停在 recorder.pendingTake(),界面出现"提交"按钮;
    //    用户点提交后它才进列表、才被选中。
    //    (旧行为是立刻选中 → 中间按钮变成"播放" → 用户以为没反应。)
  }

  // ---------- 待提交录音(Forrest 本轮:录完 → 提交 → 进列表) ----------

  /** 正在试听待提交的录音。 */
  /** 试听待提交录音的播放状态。⚠️ 必须是 public —— 模板里绑定了它
   *  ({{ previewingPending() ? 'pause' : 'play_arrow' }})。
   *  2026-09-16 教训:Angular 编译器对模板引用的 private 成员直接报 NG1,
   *  而 `tsc` 根本不查模板 → 本地 tsc 0 error 也拦不住。 */
  readonly previewingPending = signal(false);

  /** 试听/停止试听待提交的录音。 */
  previewPending(): void {
    const pt = this.host.recorder.pendingTake();
    if (!pt) return;
    // 票 292：常规待确认试听启动先取消进行中跟读校准（窗口内播放启动规则）。
    this.host.shadow.cancelMicCalibration();

    if (this.previewingPending()) {
      this.pausePendingPreview();
      return;
    }

    // 试听前停掉其他声音(示范朗读 / 历史回放),避免叠音
    this.host.tts.stopSpeak();
    this.stopPlayback();

    this.pendingAudio = new Audio(pt.url);
    // ⚠️ 用 addEventListener 而不是 `onended = …`:Zone.js 【不修补】属性式回调,
    //    那样写会让这个回调跑在 Angular Zone 之外,写 signal 也不触发刷新
    //    (第四十九轮"第二次录完没反应"就是同一类根因,详见 RecorderService.zone)。
    this.pendingAudio.addEventListener('ended', () => {
      this.previewingPending.set(false);
      this.pendingAudio = null;
    }, { once: true });
    void this.pendingAudio.play().catch(() => {
      this.previewingPending.set(false);
      this.pendingAudio = null;
    });
    this.previewingPending.set(true);
  }

  private pendingAudio: HTMLAudioElement | null = null;

  /** ★ 第四十九轮:停掉待提交录音的试听(统一的收口,防两路声音叠播)。 */
  pausePendingPreview(): void {
    if (this.pendingAudio) {
      this.pendingAudio.pause();
      this.pendingAudio = null;
    }
    this.previewingPending.set(false);
  }

  /** 提交待提交录音 → 进列表。 */
  submitPending(): void {
    const pt = this.host.recorder.pendingTake();
    if (!pt) return;
    // 停掉试听
    this.pausePendingPreview();

    // 票 269：待提交条带上录音时刻的选区快照（M-A）；快照缺失 = 非一句话 take，原样提交。
    // 快照在提交前打到 pendingTake 上，recorder.submitPending() → upload() 即随 FormData 落库，
    // 上传成功后的 id 替换是 recordings.update 的展开，字段自动保留，无需重键逻辑。
    const snap = this.host.sentence?.recordingSentenceText();
    if (snap) {
      // 纵深防御：快照若不足 10 词（正常路径不可能：录音键已门禁 + 录音中锁定），
      // 拒绝提交，绝不把不足选区的 take 当一句话 take 送上服务端；待提交条保留，用户可重录。
      if (sentenceWordCountOf(snap) < SENTENCE_MIN_WORDS) {
        this.host.toast(this.host.t('practice.sentence.needMoreWords')
          .replace('{m}', '' + (SENTENCE_MIN_WORDS - sentenceWordCountOf(snap))));
        return;
      }
      this.host.recorder.pendingTake.update((p) =>
        p ? { ...p, isSentence: true, sentenceText: snap } : p);
    }

    this.host.recorder.submitPending();
    this.host.sentence?.clearRecordingSnapshot();
    // 提交后把它设为当前作品 —— 用户下一步大概率就是回听/评分它。
    // ⚠️ 2026-09-16:这里是**本地临时 id**;上传成功后后端会换成正式 GUID,
    //    下面的 effect 会跟着把 activeTakeId 改过去,否则 activeTake() 会找不到人。
    this.activeTakeId.set(pt.id);
    // ★ 第五十轮(Forrest):提交成功不再弹提示条 ——
    //   录音已经出现在下方列表里,结果本身就是反馈;
    //   再弹一条带警示图标的"Submit"反而像出了问题(第五十轮截图反馈)。

    // 上传完成前先记住"待接替的本地 id"。
    this.pendingIdRemap = pt.id;
  }

  /**
   * 上传成功后,把当前作品 id 从本地临时 id 改后端 GUID。
   * 不做这一步的后果:列表里已经是 GUID,而 activeTakeId 还指向 r_xxx
   * → activeTake() 返回 null → 评分/回放全部"无反应"。
   *
   * ⚠️ 2026-09-16(真机报错,严重):这个方法里会写 activeTakeId,
   *    而它是在 effect 里被调用的 —— Angular 默认禁止在 effect 里写 signal,
   *    直接抛 NG0600 "Writing to signals is not allowed in a computed or an effect"。
   *    后果不只是这条报错:effect 整个炸掉,而 Submit / Retry 的可用状态
   *    依赖 recorder 状态流转 —— 于是按钮永远灰着、点了没反应。
   *
   *    修法:不在 effect 里直接写。改为把"待接替 id"记下来,
   *    用 queueMicrotask 推到 effect 之外再写 signal。
   *    (不用 allowSignalWrites:会引 Angular 18+ 才稳定的选项,
   *     且容易掩盖真实的循环依赖问题。)
   */
  private pendingIdRemap: string | null = null;

  syncPendingIdRemap(): void {
    const oldId = this.pendingIdRemap;
    if (!oldId) return;
    // 本地 id 已不存在 = 已被后端 GUID 替掉
    const stillLocal = this.host.recorder.recordings().some((r) => r.id === oldId);
    if (stillLocal) return;

    const candidate = this.host.recorder.recordings().find((r) => r.uploaded);
    if (!candidate) return;

    this.pendingIdRemap = null;

    // ⚠️ 关键:状态写入必须跳出 effect 的同步执行上下文。
    const newId = candidate.id;
    queueMicrotask(() => {
      if (this.activeTakeId() === oldId) this.activeTakeId.set(newId);
    });
  }

  /** 丢弃待提交录音。 */
  discardPending(): void {
    this.pausePendingPreview();
    this.host.recorder.discardPending();
    // 票 269：丢弃即丢快照 —— 下一条录音的选区身份须重新捕获，不得沿用旧快照。
    this.host.sentence?.clearRecordingSnapshot();
  }

  removeRecording(id: string): void {
    // ★ 2026-09-20(Forrest):删除录音必须先经确认弹窗,确认了才真删。
    // ★ 第五十轮(Forrest 截图反馈):标题已经问清"删除这条录音?",
    //   不再附加"删除后无法恢复"的说明行 —— 确认弹窗保持最简:
    //   一句问话 + 取消/确认(行业通行的轻量确认样式)。
    void this.host.tree.confirmDialog({
      title: this.host.t('dialog.deleteRecTitle'),
      confirmText: this.host.t('dialog.deleteConfirm'),
      danger: true
    }).then((ok) => {
      if (!ok) return;
      this.host.recorder.remove(id);
      if (this.activeTakeId() === id) this.activeTakeId.set(null);
      // 票136 R-136-5:删除连带清条目——追问不得比它的 Take 活得久。
      this.followUpsByTake.update(m => { const n = new Map(m); n.delete(id); return n; });
      // 票137 A-8：同理清波形缓存——删 Take 后其柱/Hz 不残留（新 Take 绝不会命中旧 id）。
      this.waveCache.delete(id);
    });
  }

  /**
   * 对某条录音做发音评分(普通 take 以当前素材文本为参考文本；
   * 票 269：一句话 take 以行上选区快照为参考文本，只评选区)。
   *
   * ★ 第五十轮(Forrest):胶囊上的「Run AI Scoring」已移除,这里是唯一评分入口
   *   (列表每行的 ✦ 按钮)。评分即把它设为当前作品 —— 报告区跟随被评的那条。
   *
   * ★ 第三十三/三十四轮(Forrest):如实标记本次评分花没花 Azure 额度与计费量
   *   判定依据是 grade() 的行为:
   *   ★ 第五十四轮起 grade() 允许重复评分 —— 每次点击都真调 Azure,故恒为 'fresh'。
   */
  async gradeRecording(id: string): Promise<void> {
    const t = this.myRecordings().find((r) => r.id === id);
    if (!t || t.grading) return;
    // 票 269 fail-closed（结构性要求）：isSentence 为真而选区缺失 → 拒绝评分，
    // 绝不回落到 currentText() 全文评分。静默评全文是本功能唯一不可接受的失效形态。
    if (t.isSentence === true && !t.sentenceText?.trim()) {
      this.host.toast(this.host.t('practice.sentence.missingSelection'));
      return;
    }
    this.stopPlayback();
    this.activeTakeId.set(id);

    // 每次都会真调 Azure(重复评分覆盖旧分数)→ 计费口径一律 'fresh'。
    this.scoreCost.set('fresh');
    this.scoreBilledSeconds.set(null);
    this.scoreBilledBytes.set(null);

    // 票 269：一句话 take 评行上选区快照（提交后改选区不影响它，§6 第 3 条）；
    // 普通 take 评当前全文（一字不动）。
    const referenceText = t.isSentence === true && t.sentenceText
      ? t.sentenceText : this.host.currentText();
    await this.host.recorder.grade(id, referenceText, this.host.practiceLang());

    // grade() 完成后回读计费口径:真调 → 新值;命中缓存 → 库里的旧值。
    const after = this.myRecordings().find((x) => x.id === id) ?? null;
    this.scoreBilledSeconds.set(after?.score?.billedSeconds ?? null);
    this.scoreBilledBytes.set(after?.score?.billedBytes ?? null);
  }

  // ---------- 当前作品(参考站的"录 → 回听 → 评分"流程) ----------

  /**
   * 当前作品 id。
   * 为什么要有"当前作品"这个概念:
   *   参考站是单题工作流 —— 一次只处理一条录音。
   *   没有这个概念,用户会对列表里随手一条录音点评分,
   *   很容易把上一次的分当成这一次的。
   */
  readonly activeTakeId = signal<string | null>(null);

  /** 当前作品(找不到则为 null)。 */
  readonly activeTake = computed(() => {
    const id = this.activeTakeId();
    if (!id) return null;
    return this.myRecordings().find((r) => r.id === id) ?? null;
  });

  /** 当前作品是否正在播放(波形"跳动"高亮用;回放入口在列表每行的播放键)。 */
  readonly activePlaying = computed(() => {
    const t = this.activeTake();
    return !!t && this.playingId() === t.id;
  });

  /** 停掉当前回放。 */
  stopPlayback(): void {
    this.audio.pause();
    this.playingId.set(null);
    this.playPos.set(0);
  }


  /**
   * 评分后的识别率 = 被正确识别的词占比。拿不到就返回 0。
   *
   * ⚠️ 2026-09-16(Forrest 本轮):只有**有词级数据且 errorType === 'None'**
   *    才计入"正确";无词级数据的词(errorType 为空串)不能算正确 ——
   *    否则会凭空空提高识别率,让报告看起来"假"。
   */
  recognitionPct(sc: RecordingScore): number {
    const scored = sc.words.filter((w) => !!w.errorType);
    if (!scored.length) return 0;
    const ok = scored.filter((w) => w.errorType === 'None').length;
    return Math.round((ok / scored.length) * 100);
  }

  /**
   * 五类错误计数(参考站右侧图例)。
   * ⚠️ 这五个 key 是 Azure PA 返回的 ErrorType 原值,不能改写成别的词:
   *   None / Mispronunciation / Omission / Insertion / UnexpectedBreak / MissingBreak
   */
  readonly errorCounts = computed(() => {
    const sc = this.activeTake()?.score;
    const empty = [
      { key: 'err.mispron', cls: 'e-mispron', n: 0 },
      { key: 'err.omission', cls: 'e-omission', n: 0 },
      { key: 'err.insertion', cls: 'e-insertion', n: 0 },
      { key: 'err.unexpectedBreak', cls: 'e-break', n: 0 },
      { key: 'err.missingBreak', cls: 'e-pause', n: 0 }
    ];
    if (!sc) return empty;

    const words = sc.words;
    const count = (t: string) => words.filter((w) => w.errorType === t).length;
    return [
      { key: 'err.mispron', cls: 'e-mispron', n: count('Mispronunciation') },
      { key: 'err.omission', cls: 'e-omission', n: count('Omission') },
      { key: 'err.insertion', cls: 'e-insertion', n: count('Insertion') },
      { key: 'err.unexpectedBreak', cls: 'e-break', n: count('UnexpectedBreak') },
      { key: 'err.missingBreak', cls: 'e-pause', n: count('MissingBreak') }
    ];
  });

  /** 逐词颜色:按 Azure 的 ErrorType 分类,而不是只看分数 —— 错误类型更有指导性。 */
  errorClass(errorType: string): string {
    switch (errorType) {
      case 'Mispronunciation': return 'e-mispron';
      case 'Omission': return 'e-omission';
      case 'Insertion': return 'e-insertion';
      case 'UnexpectedBreak': return 'e-break';
      case 'MissingBreak': return 'e-pause';
      // ⚠️ 2026-09-16:空串 = 该词无词级评估数据(Azure 未返回)。
      //    不能归为 e-ok(那会让它看起来"读对了"),用 e-na 中性色。
      case '': return 'e-na';
      default: return 'e-ok';
    }
  }

  /** 错误类型的中文名(用于 tooltip)。 */
  errLabel(errorType: string): string {
    switch (errorType) {
      case 'Mispronunciation': return this.host.t('err.mispron');
      case 'Omission': return this.host.t('err.omission');
      case 'Insertion': return this.host.t('err.insertion');
      case 'UnexpectedBreak': return this.host.t('err.unexpectedBreak');
      case 'MissingBreak': return this.host.t('err.missingBreak');
      case '': return this.host.t('err.noData');
      default: return this.host.t('err.ok');
    }
  }

  /**
   * 词是否带评分数据。
   * ⚠️ 2026-09-16(Forrest 本轮):没用它之前,无数据的词会渲染成 "0.0",
   *    看起来像"发音 0 分",其实是 Azure 没返回词级分 —— 这就是"假报告"的观感来源。
   */
  hasWordScore(w: { accuracy?: number; errorType?: string }): boolean {
    return !!w.errorType && typeof w.accuracy === 'number';
  }

  /** 时长格式化 —— ★ 2026-09-20(Forrest):秒一律取整,不显示小数。 */
  fmtDuration(sec: number): string {
    const t = Math.max(0, Math.floor(sec || 0));   // 抹掉小数(seek() 返回浮点)
    const m = Math.floor(t / 60);
    const s = t % 60;
    return m > 0
      ? m + ':' + String(s).padStart(2, '0')
      : s + 's';
  }

  fmtTime(ts: number): string {
    const d = new Date(ts);
    const p = (n: number) => String(n).padStart(2, '0');
    return p(d.getMonth() + 1) + '-' + p(d.getDate()) + ' '
      + p(d.getHours()) + ':' + p(d.getMinutes());
  }

  // ---------- 录音回放(自绘播放器) ----------

  /**
   * 当前正在播放的录音 id。
   * 用单例 <audio> 而不是每条录音一个元素 —— 播放器同时只应有一个在响,
   * 单例天然保证互斥,也便于切歌时统一收尾。
   */
  readonly audio = new Audio();
  readonly playingId = signal<string | null>(null);
  readonly playPos = signal(0);

  /**
   * ★ 第二十六轮:已上传录音的 blob → objectURL 缓存(按 recording id)。
   * 认证拉流拿到的 blob 不再重复下载;同时集中回收,防内存泄漏。
   */
  readonly audioBlobUrls = new Map<string, string>();

  /** 正在"拉流加载"中的录音 id(界面可显示 loading)。 */
  readonly audioLoadingId = signal<string | null>(null);

  /** 拉流失败的真实原因(如实展示,绝不静默)。 */
  readonly audioError = signal<string | null>(null);
  /** 逐词明细展开状态(按录音 id)。 */
  /** 评分报告面板是否展开(任务书第三节:可随时收起/展开)。默认展开。 */
  readonly reportOpen = signal(true);

  /**
   * 把某条录音设为"当前作品"。
   *
   * ⚠️ 2026-09-15 第十二轮修两个严重 bug(Forrest 反馈"点 Take #1 没反应,
   *    还会跟上面的 Submit AI Scoring / play 联动"):
   *
   *  bug A —— **事件冒泡**:本行内部 4 个按钮都有各自的 (click),但都没
   *    stopPropagation,点击会冒泡到本行的 (click)=selectTake,于是
   *    "点播放" = togglePlay + selectTake 同时执行。
   *
   *  bug B —— **toggle 语义错误**:原实现"再点已选中行就取消选中 + stopPlayback"。
   *    与 bug A 叠加 → 点已选中行的播放:先开播,紧接着 selectTake 判定
   *    "已是选中态"→ 清空 activeTakeId + stopPlayback() → **刚开的播放被掐断**,
   *    上方 Submit/play 也一起变灰。这就是"联动、不能使用"的真因。
   *
   * 修法:
   *  1. selectTake 改为**幂等选择**(点已选中项不动,不再 toggle 取消);
   *  2. 行内所有按钮/track 的 click 一律 $event.stopPropagation()
   *     (模板侧改),让按钮点击不再误触发选行。
   */
  selectTake(id: string): void {
    // 幂等:已经是当前作品就原样返回,不做任何副作用。
    // 取消选中改由「点其他行」或删除行来完成,不用重复点同一行。
    if (this.activeTakeId() === id) return;
    this.stopPlayback();
    this.activeTakeId.set(id);
  }

  /** 录音列表里的 View Report —— 同一个 take 再点一次就收起(任务书要求 toggle)。 */
  toggleTakeReport(id: string): void {
    if (this.activeTakeId() === id && this.reportOpen()) {
      this.reportOpen.set(false);
      return;
    }
    this.activeTakeId.set(id);
    this.reportOpen.set(true);
  }

  /** 当前展开报告的 take 是否为这条录音(驱动按钮文案)。 */
  isTakeReportOpen(id: string): boolean {
    return this.activeTakeId() === id && this.reportOpen();
  }

  isPlaying(id: string): boolean {
    return this.playingId() === id;
  }

  /**
   * 播放/暂停切换。切到另一条时先停掉当前这条。
   *
   * ★ 第二十六轮(401 修复):
   *   旧实现 `this.audio.src = api.audioUrl(id)` 把后端录音端点直接交给
   *   <audio src> —— 浏览器发这个请求**不带 Authorization 头**,
   *   而端点带 [Authorize] → 必然 401(Forrest 控制台看到的报错)。
   *
   *   新实现分两条路:
   *     a) 本页刚录、还没上传的 → 直接用本地 Object URL(零网络,零鉴权);
   *     b) 已上传的历史录音 → 先用 HttpClient(带 Bearer)拉回 blob,
   *        再 createObjectURL 交给 <audio>;blob 缓存在本组件 Map 里,
   *        同一条重复播放不再重复下载。
   *   拉取失败时如实提示,绝不静默假装能播。
   */
  togglePlay(r: Recording): void {
    // 票 292：常规回放启动先取消进行中跟读校准（窗口内播放启动规则；
    // 窗口内无在播音频，故此调用恒为启动）。
    this.host.shadow.cancelMicCalibration();
    // 播放期间先把示范朗读停掉,避免两种声音叠在一起
    this.host.tts.stopSpeak();
    // ★ 第四十九轮:待提交录音的试听也一并停掉 —— 全页只允许一路声音
    this.pausePendingPreview();

    if (this.playingId() === r.id) {
      this.audio.pause();
      this.playingId.set(null);
      return;
    }

    // 换目标:若正在放别的,先停
    if (this.playingId() !== null) this.audio.pause();

    // (a) 本地 Object URL:刚录完、还没上传完成的,直接播,不发请求。
    if (r.url) {
      this.startAudio(r, r.url);
      return;
    }

    // (b) 已上传:命中缓存就直接播。
    const cached = this.audioBlobUrls.get(r.id);
    if (cached) {
      this.startAudio(r, cached);
      return;
    }

    // (b-2) 走带鉴权的 HttpClient 拉 blob(拦截器自动附 Bearer + 401 刷新)。
    this.audioLoadingId.set(r.id);
    this.host.practiceApi.fetchRecordingAudio(r.id).subscribe({
      next: (blob) => {
        const url = URL.createObjectURL(blob);
        this.audioBlobUrls.set(r.id, url);
        this.audioLoadingId.set(null);
        this.startAudio(r, url);
      },
      error: (e) => {
        this.audioLoadingId.set(null);
        // 如实报错,绝不静默。401 已被拦截器处理;其余错误给出可读原因。
        const status = (e as { status?: number } | null)?.status;
        const raw = String((e as { message?: string } | null)?.message ?? e ?? '');
        // ★ 2026-09-20(Forrest 报 "take #1 点播报 数据不存在"):
        //   404 的真实含义不是"系统故障",而是**库里这条记录还在、音频文件没了**
        //   —— 服务端容器重建/清理时,容器内 storage/recordings 会被重置,
        //   而 PostgreSQL 里的录音行还在,于是列表还在、却取不到音频。
        const msg = status === 404
          ? this.host.t('practice.recGone')
          : this.host.t('practice.recLoadFail') + (raw || this.host.t('practice.retryLater'));
        this.audioError.set(msg);
        this.host.toast(msg);
      }
    });
  }

  /** 绑定 src 并起播(播放/暂停本地共用的收尾逻辑)。 */
  private startAudio(r: Recording, src: string): void {
    this.audio.src = src;
    this.audio.volume = 1;
    // ★ 与示范朗读同一口径:变速只改速度,不改嗓音
    (this.audio as HTMLAudioElement & { preservesPitch?: boolean }).preservesPitch = true;
    this.audio.currentTime = 0;
    this.playPos.set(0);
    void this.audio.play().then(
      () => this.playingId.set(r.id),
      // ★ 2026-09-20(Forrest 报"点了没声音"):浏览器拦截自动播放时
      //   play() 的 Promise 会 reject,旧代码只是把状态置回 null —— 用户
      //   看到的又是"点了没反应"。现在如实说一句。
      () => {
        this.playingId.set(null);
        this.host.toast(this.host.t('practice.audioBlockedToast'));
      }
    );
  }

  // ============================================================
  // ★ 第三十轮:静态波形(录完后)
  //   从当前作品/待提交录音的真实音频解出峰值柱,而不是画一条假的装饰波形。
  //   缓存:同一段音频只解码一次(waveCache),避免每次变更检测都重算。
  // ============================================================

  /** 当前是否有可供画波形的录音。 */
  readonly showWaveform = computed(() => {
    const t = this.activeTake();
    if (t) return true;
    return !!this.host.recorder.pendingTake();
  });

  /** 当前波形归属的录音 id(作品优先,否则待提交)。 */
  waveTargetId(): string | null {
    const t = this.activeTake();
    if (t) return t.id;
    const p = this.host.recorder.pendingTake();
    return p?.id ?? null;
  }

  /** 静态波形的柱高数组(0~100,已按最大值归一化)。 */
  readonly waveBars = signal<number[]>([]);

  /**
   * 票137 R-137-1：波形状态机 —— 节渲染与柱数据脱钩的根治。
   * idle = 无目标（防御分支，节隐藏）；loading = 解码中（只露节头 + 进度条）；
   * ready = 柱 + 指标行；failed = 节头 + 分级诚实文案（原因见 waveFailReason）。
   * 三种态下绝无裸绿条。由组件 effect 驱动；waveBars/waveCache/竞态守卫语义不动。
   */
  readonly waveStatus = signal<'idle' | 'loading' | 'ready' | 'failed'>('idle');

  /** 票137 R-137-5：失败原因种 —— gone = 远端 404（复用 recGone），其余 = unavailable。 */
  readonly waveFailReason = signal<'gone' | 'unavailable' | null>(null);

  /**
   * 票137 R-137-4：平均频率（整数 Hz）——与柱子同一次解码产出，见 computeWaveBars。
   * null = 无足够浊音帧，模板直接省略该段，绝不写 0 Hz。
   */
  readonly waveAvgHz = signal<number | null>(null);

  /** 解码结果缓存:录音 id → {柱高数组, 平均频率}（同一音频只解码一次）。 */
  readonly waveCache = new Map<string, { bars: number[]; avgHz: number | null }>();

  /**
   * 音频源（带失败分级）：优先内存里的 blob(刚录完),否则走带鉴权接口把已上传录音取回。
   * 与评分同一个取数策略(第二十九轮),避免「刷新后没有 blob」时波形画不出来。
   * 分级（票137 R-137-5，复用票132 口径）：远端 404 → gone；空体/网络失败/非 GUID 无源 → unavailable。
   * waveAudioSource 仍保留原 Blob|null 契约（委托本方法），供其它调用点沿用。
   */
  async waveAudioSourceDetailed(rec: Recording): Promise<
    { kind: 'blob'; blob: Blob } | { kind: 'gone' } | { kind: 'unavailable' }
  > {
    const b = (rec as { blob?: Blob }).blob;
    if (b && b.size > 0) return { kind: 'blob', blob: b };
    if (RecorderService.isGuid(rec.id)) {
      try {
        const fetched = await firstValueFrom(this.host.practiceApi.fetchRecordingAudio(rec.id));
        if (fetched && fetched.size > 0) return { kind: 'blob', blob: fetched };
        return { kind: 'unavailable' };
      } catch (e) {
        const status = (e as { status?: number } | null)?.status;
        return status === 404 ? { kind: 'gone' } : { kind: 'unavailable' };
      }
    }
    return { kind: 'unavailable' };
  }

  /**
   * 音频源:优先内存里的 blob(刚录完),否则走带鉴权接口把已上传录音取回。
   * 与评分同一个取数策略(第二十九轮),避免「刷新后没有 blob」时波形画不出来。
   */
  async waveAudioSource(rec: Recording): Promise<Blob | null> {
    const r = await this.waveAudioSourceDetailed(rec);
    return r.kind === 'blob' ? r.blob : null;
  }

  /**
   * 解码音频并算出波形柱高 + 平均频率（同一次解码产出，绝不为 Hz 单独再解一次）。
   * 柱形规则与此前逐字一致（56 根、峰值归一、下限 6，见 waveform.helpers）；
   * Hz 口径：自相关基频估计（约 50ms 分帧、RMS 门限、50~500 Hz、浊音帧均值）。
   * 无 AudioContext、解码失败、空声道、全零峰值 → null（与此前失败语义一致）。
   */
  async computeWaveBars(blob: Blob, bars = 56): Promise<{ bars: number[]; avgHz: number | null } | null> {
    const Ctor = window.AudioContext
      || (window as unknown as { webkitAudioContext?: typeof AudioContext }).webkitAudioContext;
    if (!Ctor) return null;
    const ctx = new Ctor();
    try {
      const buf = await blob.arrayBuffer();
      const audio = await this.decodeCompat(ctx, buf);
      const data = audio.getChannelData(0);
      if (!data.length) return null;
      const barsOut = computeWaveBarsFromSamples(data, bars);
      if (!barsOut) return null;
      return { bars: barsOut, avgHz: estimateAvgF0(data, audio.sampleRate) };
    } catch {
      return null;
    } finally {
      if (ctx.state !== 'closed') void ctx.close().catch(() => undefined);
    }
  }

  /** 与录音服务同款双形态解码(Safari 只支持回调式)。 */
  private decodeCompat(ctx: BaseAudioContext, buf: ArrayBuffer): Promise<AudioBuffer> {
    return new Promise<AudioBuffer>((resolve, reject) => {
      const anyCtx = ctx as unknown as {
        decodeAudioData: (
          b: ArrayBuffer,
          ok?: (x: AudioBuffer) => void,
          bad?: (e: unknown) => void
        ) => Promise<AudioBuffer> | void;
      };
      try {
        const ret = anyCtx.decodeAudioData(buf, resolve, reject);
        if (ret && typeof (ret as Promise<AudioBuffer>).then === 'function') {
          (ret as Promise<AudioBuffer>).then(resolve).catch(reject);
        }
      } catch (e) {
        reject(e);
      }
    });
  }

  /** 播放位置对应第几根柱子已播过(用于高亮)。 */
  wavePlayed(index: number): boolean {
    const bars = this.waveBars().length;
    if (!bars) return false;
    const t = this.activeTake();
    const p = this.host.recorder.pendingTake();
    const isActive = !!t && this.playingId() === t.id;
    const isPending = !!p && this.previewingPending();
    if (!isActive && !isPending) return false;
    return index / bars <= this.playPos() / Math.max(1, t?.duration ?? p?.duration ?? 1);
  }

  /** 播放进度百分比。 */
  progress(r: Recording): number {
    if (this.playingId() !== r.id || !r.duration) return 0;
    return Math.min(100, (this.playPos() / r.duration) * 100);
  }

  /** 当前播放位置的显示时间。 */
  playingTime(r: Recording): string {
    if (this.playingId() !== r.id) return '0:00';
    const s = Math.floor(this.playPos());
    return Math.floor(s / 60) + ':' + String(s % 60).padStart(2, '0');
  }

  /** 点击时间轴跳转。 */
  seek(r: Recording, ev: MouseEvent): void {
    const el = ev.currentTarget as HTMLElement;
    const rect = el.getBoundingClientRect();
    const ratio = Math.max(0, Math.min(1, (ev.clientX - rect.left) / rect.width));

    if (this.playingId() !== r.id) {
      // 未在播放:先起播再定位
      this.togglePlay(r);
    }
    const target = ratio * (r.duration || 0);
    this.audio.currentTime = target;
    this.playPos.set(target);
  }

  /**
   * ★ 第三十五轮(Forrest):逐词分值区**已取消折叠,改为常驻展开**。
   *
   * 它只是纯文本展示(与 Recognition 同性质),没有任何理由需要点一下展开。
   * 这里保留方法签名并恒返回 true —— 这样旧调用点不会被破坏
   * (但模板里已经不再用了);今后若有人重新引入折叠,也不至于拿到错的值。
   */
  isWordsOpen(_id: string): boolean {
    return true;
  }

  /**
   * ★ 第三十五轮:折叠已取消,故本方法不再被模板调用。
   * 保留为空实现,避免旧引用直接报错;行为上什么也不做。
   */
  toggleWords(_id: string): void {
    // 第三十五轮:无操作 —— 逐词分值已改为常驻显示。
  }

  /**
   * ★ 第二十九轮:确保某条录音的逐词明细处于展开状态(幂等)。
   *
   * 与 toggleWords 的区别:**只展开,不收起** —— 供"评分完成后自动展开"
   * 使用。用户在展开后手动收起,不应被下一次数据刷新重新弹开,
   * 所以只对"尚未展开过"的 id 生效(由调用方判断 has())。
   */
  /**
   * ★ 第三十五轮:折叠已取消,自动展开逻辑也一并作废。
   * 保留空实现,避免旧调用点报错。
   */
  private ensureWordsOpen(_id: string): void {
    // 第三十五轮:无操作 —— 逐词分值始终可见,无需再维护展开状态。
  }

  /** 总分的中文口语化评价 —— 比裸数字直观。 */
  verdict(v: number): string {
    if (v >= 90) return this.host.t('verdict.excellent');
    if (v >= 80) return this.host.t('verdict.good');
    if (v >= 70) return this.host.t('verdict.fair');
    return this.host.t('verdict.poor');
  }

  /** 逐词颜色的分档。 */
  wordClass(acc: number): string {
    if (acc >= 80) return 'w-good';
    if (acc >= 60) return 'w-mid';
    return 'w-bad';
  }

  scoreClass(v: number): string {
    // 四档:颜色梯度足够细,又不至于让用户分不清
    if (v >= 85) return 's-good';
    if (v >= 70) return 's-mid';
    if (v >= 55) return 's-warn';
    return 's-bad';
  }
}
