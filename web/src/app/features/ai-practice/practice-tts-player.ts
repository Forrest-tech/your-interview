import { computed, signal } from '@angular/core';
import { Howl, Howler } from 'howler';
import { getCachedAudio, getCachedAudioByMaterial, putCachedAudio, ttsKey } from './tts-audio-cache';
import type { AiPracticeComponent } from './ai-practice.component';

export class PracticeTtsPlayer {
  constructor(private readonly host: AiPracticeComponent) {}


  // ---------- 示范朗读 ----------
  readonly speaking = signal(false);

  // ══ ★ 2026-09-20(Forrest):**全用 Howler** ══
  //   音源由 Howler 在创建时给定;换素材 = unload() 旧的、按新音频新建。
  //   时长 duration()、位置 seek()、播放/暂停 play()/pause()、
  //   结束 on('end')、倍速 rate() —— 全部是 Howler 自己的 API 与事件。
  private ttsHowl: Howl | null = null;
  /** 当前播放位置(秒) —— 值来自 howl.seek()。 */
  readonly speakPos = signal(0);
  /** 音频真实总时长(秒) —— 来自 howl.duration(),倍速不影响它。 */
  readonly speakDur = signal(0);
  /**
   * ★ 2026-09-20(Forrest):进度刷新 —— 每 200ms 读一次 howl.seek()。
   * 值完全来自 Howler(不是我估算);Howler 的 seek 事件只在程序主动
   * 调 seek() 时触发,播放中不触发,所以这里按方案二用定时器搬运真实值。
   */
  private posTimer: ReturnType<typeof setInterval> | null = null;
  /** 当前播放器装载的音频指纹键(文本+音色+语言)。 */
  private ttsPlayingKey: string | null = null;
  /**
   * 这一段音频是否已经自然播完(用于"再点一次从头播")。
   * Howler 播完后 seek() 未必自动归零,这里显式记一笔,避免重播时
   * 从结尾处开始、看起来像"点了没反应"。
   */
  private ttsEnded = false;
  readonly rate = signal(1);
  readonly rates = [0.5, 0.75, 1, 1.25, 1.5, 2];
  // ★ 2026-09-20(Forrest):utterance / ttsAudio 已删除 ——
  //   两者都是 Web Speech / HTMLAudioElement 时代的产物,
  //   现在统一由 this.ttsHowl(Howl 实例)承载。

  /** Azure TTS 的 objectURL —— 必须回收,否则每次合成都会攒住一份 MP3。 */
  ttsUrl: string | null = null;

  /**
   * 本次示范朗读实际用的引擎说明(如 "Azure 神经语音(服务端合成)"、
   * "Azure 语音未配置,已回退浏览器语音"）。
   * ⚠️ 这是诚实标记:回退时必须显示,不能让用户误以为听到了 Azure 人声。
   */
  readonly ttsNote = signal('');

  /**
   * ★ 第三十三轮(Forrest):示范朗读的**额度消耗**提示。
   *
   * 三种取值,必须在界面上说清楚(不猜、不含糊):
   *   · 'fresh'  = 本次真调了 Azure 合成 → **消耗了额度**
   *   · 'cache'  = 服务端本地缓存命中 → **未消耗额度**
   *   · ''       = 无提示(未播 / 回退浏览器语音 / 非 Azure 引擎)
   *
   * 数据来源:后端 `X-Tts-Cache: hit|miss` 响应头。
   * ⚠️ 前端**无法自行判断**服务端有没有缓存 ——
   *    所以绝不能根据"这段文本我播过没"在本地猜,
   *    猜错就是骗用户(比如另一个标签页/另一台设备已经合成过)。
   */
  readonly ttsCost = signal<'' | 'fresh' | 'cache' | 'local'>('');

  /**
   * ★ 第三十四轮(Forrest:"给我准确的消耗了多少"):本次示范朗读的精确计费量。
   *
   * ⚠️ 名字里不用 token —— Azure 语音 TTS **根本不以 token 计费**,
   *   真实计费单位是**合成字符数**(神经语音按每 1M 字符计价)。
   *   这里存的 billedChars 就是"本次送给 Azure 的字符数",
   *   由服务端精确算出(UTF-16 代码单元数,与 Azure 口径一致)。
   *   命中缓存时为 0。
   *   null = 服务端未下发该头(旧后端)→ 界面不显示数字,不编造。
   */
  readonly ttsBilledChars = signal<number | null>(null);

  /** 这段文本的完整字符数(命中缓存时用来对比"省了多少")。 */
  readonly ttsFullChars = signal<number | null>(null);

  /** 本次实际使用的音色(服务端默认也如实回报)。 */
  readonly ttsVoice = signal('');

  /** 本次返回的音频字节数(核对用,非计费单位)。 */
  readonly ttsAudioBytes = signal<number | null>(null);

  /**
   * ★ 2026-09-24(Forrest):正文语言与朗读语言不一致的**预检**。
   *
   * 真实案例:正文是中文,朗读语言却选了 English (US) → Azure 用英文音色
   * 合成中文,拿到手的是坏音频 → 播放条报"Audio failed to load"。
   * 与其等失败后让用户猜,不如在播放前就发现并给出**一键修正**(fixLang)。
   *
   * 判定用 Unicode 书写系统启发式(行业标准做法,Chrome/VS Code 同类):
   *   · 正文含 CJK 统一表意文字 → 期望 zh-CN;
   *   · 正文完全不含 CJK → 中文音色念英文必然跑调 → 期望 en-US。
   * 只在"明确矛盾"时警告,不做更细的法/英区分(heuristic 不可靠)。
   * 返回:'' = 无矛盾; 'to-zh' = 应切到中文; 'to-en' = 应切回英文。
   */
  readonly langMismatch = computed<'' | 'to-zh' | 'to-en'>(() => {
    const text = (this.host.saved() || '').trim();
    if (!text) return '';
    const hasCjk = /[\u3400-\u9FFF\uF900-\uFAFF]/.test(text);
    if (hasCjk && this.host.practiceLang() !== 'zh-CN') return 'to-zh';
    if (!hasCjk && this.host.practiceLang() === 'zh-CN') return 'to-en';
    return '';
  });

  /** 当前朗读语言的展示名(用于提示文案里的 {lang} 占位)。 */
  readonly mismatchLangLabel = computed(() => {
    const opt = this.host.langOptions.find((o) => o.code === this.host.practiceLang());
    return opt ? opt.label : this.host.practiceLang();
  });

  /** 提示条上的"一键修正":切到正确语言并立即重播。 */
  fixLang(): void {
    const target: 'zh-CN' | 'en-US' = this.langMismatch() === 'to-zh' ? 'zh-CN' : 'en-US';
    this.host.setPracticeLang(target);
    this.ttsNote.set('');
    this.speak();
  }

  /** 程序化展开顶栏的朗读设置浮层。 */
  openTtsSettings(): void {
    this.host.engineTrigger?.openMenu();
  }

  /**
   * ★ 2026-09-20(Forrest):刷新/新进页面时,若本地已有该文本的音频,
   * 就地建一个 Howler 实例(不播放)让它的 on('load') 报出真实时长 ——
   * 这样进度条一开始就显示正确总时长,而不是等用户点播放。
   * 本地没有则什么都不做(时长显示 --,符合选项 1)。
   */
  /** ★ 2026-09-20:Azure 就绪后,若当前素材还没装载本地音频时长,补跑一次。 */
  retryCachedDuration(): void {
    if (this.ttsHowl) return;             // 已经装载过了
    const id = this.host.selectedId();
    if (!id) return;
    const node = this.host.tree.findFile(this.host.tree.nodes(), id);
    if (node) void this.showCachedDuration(id, node.content ?? '');
  }

  async showCachedDuration(materialId: string, text: string): Promise<void> {
    // ⚠️ 不要在这里判 azureReady:读**本地已缓存**的音频根本不需要 Azure,
    //    而且 azureReady 是异步校准的,早于它就绪会被误挡(2026-09-20 真因)。
    const clean = (text || '').trim();
    // 优先按文本指纹查;正文还没回填时,退回到按素材 id 查(Forrest 2026-09-20)。
    const key = clean ? ttsKey(clean, '', this.host.practiceLang()) : '';
    const cached = key ? await getCachedAudio(key) : await getCachedAudioByMaterial(materialId);
    if (!cached) return;
    // 期间用户可能已切走 —— 只在仍是同一素材时装载。
    if (this.host.selectedId() !== materialId) return;
    // 若正文已回填且与缓存那段不一致,说明文本改过,这份时长不再作数。
    const nowText = (this.host.saved() || '').trim();
    if (nowText && cached.text && nowText !== cached.text) return;

    const url = URL.createObjectURL(cached.blob);
    if (this.ttsUrl) URL.revokeObjectURL(this.ttsUrl);
    this.ttsUrl = url;
    this.ttsHowl?.unload();
    const h = new Howl({
      src: [url],
      format: this.howlFormat(cached.blob.type),   // blob URL 无扩展名 → 必须显式给
      // ★ 2026-09-20(Forrest 选项 1):预载时长用 Howler 的 Web Audio 模式 ——
      //   html5:true 是流式的,onload 时浏览器还没解析出媒体时长(duration()=0);
      //   html5:false 会完整解码,onload 时 duration() 立即可用。
      //   这是 Howler 原生行为,不做任何自写重试。
      // ★ 2026-09-20(Forrest 报"倍速声音扭曲"):改用 html5 —— 只有浏览器
      //   原生 <audio> 的 preservesPitch 能在变速时**保住音高**。
      //   (Web Audio 的 playbackRate 会连音高一起拉,人声就变味了。)
      html5: true,
      rate: this.rate(),
      onload: () => {
        this.speakDur.set(Math.round(h.duration()));   // Howler 的真实时长(此刻已可用)
        this.speakPos.set(0);
        this.host.cdr.detectChanges();
      },
      onplay: () => {
        this.speaking.set(true);
        this.startPosTimer();
        this.host.cdr.detectChanges();
      },
      onpause: () => {
        this.speaking.set(false);
        this.stopPosTimer();
        this.speakPos.set(h.seek() || 0);
        this.host.cdr.detectChanges();
      },
      onend: () => {
        this.speaking.set(false);
        this.stopPosTimer();
        this.speakPos.set(0);
        this.host.cdr.detectChanges();
      },
      onplayerror: () => {
        h.once('unlock', () => h.play());
      },
    });
    this.ttsHowl = h;
    this.keepPitch(h);
    // 预载的就是这份 → 点播放时文本指纹若与之一致就直接播,不再请求。
    this.ttsPlayingKey = cached.key;
  }

  // ---------- 示范朗读 ----------
  speak(): void {
    // ══ ★ 2026-09-20(Forrest):只用 Howler 播 Azure 真实音频,无任何兜底 ══
    //     播放 → play() · 暂停 → pause() · 播完再点 → 从头 play()

    // ── 门禁:没配 Azure Key 就**不能播**,并把原因直接摊给用户看 ──
    //    诚实红线的一半是不伪装,另一半是不能让人对着灰按钮发愣 ——
    //    所以这里顺手把右上角的配置浮层展开。
    if (!this.host.grading.azureReady()) {
      this.ttsNote.set(this.host.t('practice.azurePlayBlocked'));
      this.host.toast(this.host.t('practice.azurePlayBlocked'));
      this.openTtsSettings();
      this.host.cdr.detectChanges();
      return;
    }

    const h = this.ttsHowl;
    if (h && this.speaking()) {
      h.pause();                 // Howler 的 onpause 会把 speaking 置 false
      return;
    }
    const text = this.host.editing() ? this.host.draft() : this.host.saved();
    const clean = (text || '').trim();
    if (!clean) return;

    // ★ Forrest 逻辑:文本没改 → 直接播本地/已装载的那份。
    const key = ttsKey(clean, '', this.host.practiceLang());
    if (h && h.state() === 'loaded') {
      if (this.ttsPlayingKey === key) {
        // 上一次是自然播完的 → 先归零,否则会从结尾处开始播
        if (this.ttsEnded) {
          h.seek(0);
          this.speakPos.set(0);
          this.ttsEnded = false;
        }
        this.unlockAudio();      // ★ 先唤醒 AudioContext,再 play
        h.play();                // 文本未变:续播/重播当前这份
        return;
      }
      // 文本变了 → 下面重新合成
    }

    this.synthesizeAndPlay(clean, key);
  }

  /**
   * ★ 2026-09-20(Forrest):按 Forrest 的逻辑取音频并播放 ——
   *   1. 先查**本地缓存**(IndexedDB):有就直接播,一个请求都不发;
   *   2. 本地没有 → 调 Azure 合成,拿到后**存本地**再播;
   *   3. 文本/语言变了 → key 变 → 自然走"重新合成"。
   */
  private async synthesizeAndPlay(text: string, key: string, autoplay = true): Promise<void> {
    this.ttsNote.set('');
    this.ttsCost.set('');
    this.ttsBilledChars.set(null);
    this.ttsFullChars.set(null);
    this.ttsVoice.set('');
    this.ttsAudioBytes.set(null);

    // ── 1. 本地缓存优先 ──
    const cached = await getCachedAudio(key);
    if (cached) {
      this.ttsCost.set('local');      // 如实标记:这次用的是本地那份
      this.ttsVoice.set(cached.voice);
      const url = URL.createObjectURL(cached.blob);
      if (this.ttsUrl) URL.revokeObjectURL(this.ttsUrl);
      this.ttsUrl = url;
      this.ttsPlayingKey = key;
      this.createHowl(url, autoplay, this.howlFormat(cached.blob.type));
      return;
    }

    // ── 2. 本地没有 → Azure 合成(speed 恒为 1.0;倍速是播放行为) ──
    this.host.practiceApi.synthesize(text, undefined, 1, false, this.host.practiceLang()).subscribe({
      next: (res) => {
        this.ttsCost.set(res.fromCache ? 'cache' : 'fresh');
        this.ttsBilledChars.set(res.billedChars);
        this.ttsFullChars.set(res.fullChars);
        this.ttsVoice.set(res.voice);
        this.ttsAudioBytes.set(res.audioBytes);

        // ── 3. 存本地,下次(含刷新)直接用,不再请求 ──
        // ★ 2026-09-20(Forrest 真因):Angular responseType:'blob' 拿到的
        //   Blob 常常**没有 MIME type**;<audio> 能嗅探播放,但 Howler 解码
        //   时拿不到时长(duration()=0)→ 刷新后显示 "--s"。
        //   这里显式补上类型,本地取回的 Blob 才有类型可用。
        const typedBlob = res.blob.type
          ? res.blob
          : new Blob([res.blob], { type: 'audio/mpeg' });
        void putCachedAudio({
          key,
          blob: typedBlob,
          voice: res.voice,
          lang: this.host.practiceLang(),
          savedAt: Date.now(),
          materialId: this.host.selectedId() ?? undefined,
          text,
        });

        const url = URL.createObjectURL(res.blob);
        if (this.ttsUrl) URL.revokeObjectURL(this.ttsUrl);
        this.ttsUrl = url;
        this.ttsPlayingKey = key;
        this.createHowl(url, autoplay, this.howlFormat(typedBlob.type));
      },
      error: (e) => {
        const status = (e as { status?: number } | null)?.status;
        // ★ 2026-09-23(Forrest):三种失败原因此前写死中文 → 接语言设置。
        const note =
          status === 503 ? this.host.t('practice.ttsNotConfigured')
            : status === 401 || status === 403 || status === 502
              ? this.host.t('practice.ttsKeyInvalid')
              : this.host.t('practice.ttsSynthFailed');
        this.ttsNote.set(note);
        // ★ 2026-09-20(Forrest 报"没反应"):失败必须**弹出来**,
        //   只靠播放条那一行小字容易看不到。
        this.host.toast(note + (status ? ' · ' + this.host.t('common.httpStatus').replace('{n}', String(status)) : ''));
        this.speaking.set(false);
        this.ttsBilledChars.set(null);
        this.ttsFullChars.set(null);
        this.ttsVoice.set('');
        this.ttsAudioBytes.set(null);
      }
    });
  }

  /**
   * ★ 2026-09-20(Forrest):按音源**新建** Howl 并播放。
   * Howler 用法:src 创建时给定;换音频就 unload 旧的、重建。
   * 时长/位置/结束/暂停全部由 Howler 自己的事件回调驱动。
   */
  /**
   * ★ 2026-09-20(Forrest 第三十六轮):按音源**新建** Howl。
   *
   * · autoplay = true  → 装载完立刻播放(用户点了播放键)
   * · autoplay = false → 只装载、取真实时长(编辑保存后重建 / 进页面预载)
   *
   * ⚠️ 关键坑(0s/0s 的真因):html5:true 是**流式**模式,onload 时浏览器
   *    还没解析出媒体元数据 → duration() 返回 0 → 时间文本变成 "0s / 0s"。
   *    这里统一 html5:false(Web Audio 全解码):onload 时 duration() 就是
   *    真实秒数。数值全部取自 Howler 自身,不做任何自写估算。
   */
  /**
   * ★ 2026-09-20(Forrest 报"点了没反应"的**真因**):
   * Howler 靠 src 的**扩展名**猜音频格式。我们给它的是 `blob:http://.../uuid`,
   * 没有扩展名 → Howler 判定"无可用解码器",直接抛
   * `No codec support for selected audio sources.` → onloaderror
   * → 旧代码没有 onloaderror 处理 → **点了完全没反应**。
   *
   * 修法:显式传 `format`。按 Blob 的真实 MIME 推导(后端换成 wav/ogg 也不会错),
   * Angular 的 blob 没有 MIME 时退回 mp3(Azure 的默认输出格式)。
   */
  /**
   * ★ 2026-09-20(Forrest 报"蓝色按钮点了没声音"):浏览器的自动播放策略会把
   *   Howler 的 Web Audio 上下文停在 suspended。此时 play() 既不报错也不出声,
   *   speaking 永远不会变 true —— 用户看到的就是"点了没反应"。
   *   点击本身就是合法的用户手势,这里主动把上下文唤醒。
   */
  private unlockAudio(): void {
    try {
      const ctx = (Howler as unknown as { ctx?: AudioContext }).ctx;
      if (ctx && ctx.state !== 'running') void ctx.resume();
    } catch { /* 拿不到上下文就算了,不影响主流程 */ }
  }

  private howlFormat(mime: string): string[] {
    const t = (mime || '').toLowerCase();
    if (t.includes('mpeg') || t.includes('mp3')) return ['mp3'];
    if (t.includes('wav') || t.includes('wave')) return ['wav'];
    if (t.includes('ogg') || t.includes('opus')) return ['oga', 'ogg'];
    if (t.includes('m4a') || t.includes('mp4') || t.includes('aac')) return ['m4a', 'aac'];
    if (t.includes('flac')) return ['flac'];
    if (t.includes('webm')) return ['webm'];
    return ['mp3'];   // 没有 MIME → 按 Azure 默认输出兜底
  }

  /**
   * ★ 2026-09-20(Forrest 报"选了倍速声音扭曲"):
   * Howler 的 `html5:false` 走 Web Audio 的 `playbackRate`,那是**硬拉采样率**,
   * 速度变的同时音高也跟着变 → 人声变成"花栗鼠"。
   *
   * `html5:true` 走浏览器原生 <audio>,它的 `preservesPitch`(Chrome/Safari/
   * Firefox 都有,默认就是 true)会在变速时**保住音高** —— 只是说得快一点,
   * 嗓音还是原来那个人。这才是"倍速只改变播放速度"。
   *
   * 注意:html5 模式下时长同样可靠 —— Howler 在 canplaythrough 里把
   * `_duration` 设成媒体时长(见 howler.js 的 _loadListener),所以
   * duration() 照样拿得到,不会再退回 "0s / 0s"(之前那个 0s 的真因
   * 是 blob URL 没扩展名导致解码失败,已经由 format 修掉)。
   */
  private keepPitch(h: Howl): void {
    try {
      type PitchNode = HTMLAudioElement & {
        preservesPitch?: boolean;
        mozPreservesPitch?: boolean;
        webkitPreservesPitch?: boolean;
      };
      const node = (h as unknown as { _sounds?: { _node?: PitchNode }[] })
        ._sounds?.[0]?._node;
      if (!node) return;
      node.preservesPitch = true;
      node.mozPreservesPitch = true;
      node.webkitPreservesPitch = true;
    } catch { /* 老浏览器没有这个属性就不勉强,退化成普通变速 */ }
  }

  private createHowl(url: string, autoplay = true, format?: string[]): void {
    this.ttsHowl?.unload();          // Howler 原生:释放旧实例
    this.ttsHowl = null;
    this.stopPosTimer();
    this.speaking.set(false);
    this.speakPos.set(0);
    this.speakDur.set(0);

    const h = new Howl({
      src: [url],                    // ★ 音源在创建时给定(Howler 就是这样用)
      // ★ 必须显式给 format:blob: URL 没有扩展名,Howler 猜不出解码器
      //   → 直接 onloaderror("No codec support") → 这就是"点了没反应"的真因。
      format: format ?? ['mp3'],
      // ★ 2026-09-20(Forrest 报"倍速声音扭曲"):改用 html5 —— 只有浏览器
      //   原生 <audio> 的 preservesPitch 能在变速时**保住音高**。
      //   (Web Audio 的 playbackRate 会连音高一起拉,人声就变味了。)
      html5: true,                  // ★ 必须 false —— 否则拿不到真实时长
      rate: this.rate(),             // 当前倍速(只改播放速度,不动总时长)
      onload: () => {
        // ★ 总时长只在 load 回调里读,且只认 Howler 的返回值
        this.speakDur.set(Math.round(h.duration()));
        this.speakPos.set(0);
        this.host.cdr.detectChanges();
      },
      onplay: () => {
        this.speaking.set(true);
        this.startPosTimer();
        this.host.cdr.detectChanges();
      },
      onpause: () => {
        // 暂停 = 就地冻住:保留 seek() 的真实位置,下次 play() 从该处续播
        this.speaking.set(false);
        this.stopPosTimer();
        this.speakPos.set(h.seek() || 0);
        this.host.cdr.detectChanges();
      },
      onend: () => {
        // ★ 播完必须彻底复位 —— 否则按钮永远停在 ■、进度条卡在 100%
        this.speaking.set(false);
        this.stopPosTimer();
        this.speakPos.set(0);
        this.ttsEnded = true;
        // ★ 必须显式detectChanges:Howler 回调跑在 Angular 之外,
        //   不推一次变更检测,播放键不会立刻刷回 ▶。
        this.host.cdr.detectChanges();
      },
      onseek: () => {
        this.speakPos.set(h.seek() || 0);
        this.host.cdr.detectChanges();
      },
      // ★ 2026-09-20(Forrest 报"点了没反应"):装载/播放失败也必须出声,
      //   否则用户面对一个毫无反馈的按钮(真因排查时的最后一块拼图)。
      onloaderror: (_id: number, err?: unknown) => {
        console.warn('[tts] loaderror:', err);       // 保留一条线索,便于远程排障
        this.speaking.set(false);
        this.ttsNote.set(this.host.t('practice.noteLoadFail'));
        this.host.toast(this.host.t('practice.audioLoadFailToast'));
        this.host.cdr.detectChanges();
      },
      onplayerror: (_id: number, err?: unknown) => {
        console.warn('[tts] playerror:', err);
        this.speaking.set(false);
        this.ttsNote.set(this.host.t('practice.notePlayFail'));
        this.host.toast(this.host.t('practice.audioPlayFailToast'));
        this.host.cdr.detectChanges();
        // 浏览器自动播放策略:解锁后重播(Howler 自带 unlock)
        h.once('unlock', () => h.play());
      }
    });

    this.ttsHowl = h;
    this.keepPitch(h);               // ★ 变速不变声(见 keepPitch 的说明)
    this.ttsEnded = false;
    if (autoplay) {
      this.unlockAudio();                 // ★ 先唤醒 AudioContext,再 play
      h.play();
      // ★ 看门狗:1.2s 后仍没真正出声 → 明确说出来,绝不让人对着静音发愣
      setTimeout(() => {
        if (this.ttsHowl === h && h.state() === 'loaded' && !h.playing()) {
          this.ttsNote.set(this.host.t('practice.noteNotStarted'));
          this.host.toast(this.host.t('practice.audioBlockedToast'));
          this.host.cdr.detectChanges();
        }
      }, 1200);
    }
    this.host.cdr.detectChanges();
  }

  /**
   * ★ 2026-09-20(Forrest 方案 2):每 200ms 读一次 howl.seek() 搬进视图。
   * 只做"搬运真实值",不做任何估算;停止/暂停/结束即停表。
   */
  private startPosTimer(): void {
    this.stopPosTimer();
    this.posTimer = setInterval(() => {
      const h = this.ttsHowl;
      if (!h || !h.playing()) { this.stopPosTimer(); return; }
      this.speakPos.set(h.seek() || 0);
      // 时长只在尚未拿到时补一次;已拿到就以 onload 的值为准,保持整数秒
      const d = h.duration();
      if (d && isFinite(d) && d > 0 && !(this.speakDur() > 0)) {
        this.speakDur.set(Math.round(d));
      }
      this.host.cdr.detectChanges();
    }, 200);
  }

  stopPosTimer(): void {
    if (this.posTimer !== null) {
      clearInterval(this.posTimer);
      this.posTimer = null;
    }
  }

  // ---------- Sample Reading 播放进度 ----------

  /** 示范朗读进度百分比 —— 数据源与时间文本完全一致(Howler 真实值)。 */
  speakProgress(): number {
    const total = this.speakDur();
    if (!(total > 0)) return 0;              // 时长未知 → 0
    const raw = Math.min(this.speakPos(), total);
    return Math.min(100, Math.max(0, (raw / total) * 100));
  }

  /**
   * ★ 2026-09-20(Forrest):停止播放 —— Howler 原生 stop() 归零。
   * 实例保留,下次换素材/重播时再按需重建(unload + new Howl)。
   */
  stopSpeak(): void {
    this.ttsHowl?.stop();       // Howler 原生 stop:回到开头
    this.stopPosTimer();
    this.speaking.set(false);
    this.speakPos.set(0);
  }

  /**
   * ★ 2026-09-20(Forrest 第三十六轮):彻底销毁当前音频。
   *
   * 用于"文本已变"的场景(编辑保存 / 切题):旧音频必须 stop + unload,
   * 否则 Howler 还持有上一次的资源,新音频的 onload 时长会被旧的干扰。
   * 这里只销毁,不重新合成 —— 重新合成由 rebuildTts() 负责。
   */
  destroyTtsAudio(): void {
    if (this.ttsHowl) {
      this.ttsHowl.stop();
      this.ttsHowl.unload();      // Howler 原生:释放解码资源
      this.ttsHowl = null;
    }
    this.stopPosTimer();
    this.speaking.set(false);
    this.speakPos.set(0);
    this.speakDur.set(0);
    this.ttsPlayingKey = null;
    this.ttsEnded = false;
    if (this.ttsUrl) {
      URL.revokeObjectURL(this.ttsUrl);
      this.ttsUrl = null;
    }
    this.host.cdr.detectChanges();
  }

  /**
   * ★ 2026-09-20(Forrest 第三十六轮):文本变更后重建音频(不自动播放)。
   *
   * 编辑保存 → 立即调它。行为按 Forrest 要求:
   *   1. 正在播放就停掉,并销毁旧音频(stop + unload);
   *   2. 用**新文本**重新向 Azure 请求合成;
   *   3. 拿到音频后以 autoplay=false 装载 → 触发 onload →
   *      展示这份新文本的真实总时长,进度归零,等用户点播放。
   */
  rebuildTts(): void {
    const clean = ((this.host.editing() ? this.host.draft() : this.host.saved()) || '').trim();
    if (!clean) { this.destroyTtsAudio(); return; }
    // 没配 Key 就不发请求 —— 发也一定失败,白白让用户等
    if (!this.host.grading.azureReady()) { this.destroyTtsAudio(); return; }
    const key = ttsKey(clean, '', this.host.practiceLang());
    void this.synthesizeAndPlay(clean, key, /* autoplay */ false);
  }

  /**
   * ★ 2026-09-20(Forrest):切换倍速。
   *
   * 直接调 Howler 原生 rate() —— 成熟控件已完成原地变速(位置保留、不重播)。
   * 音频本身永远是 1.0 合成的那一份,所以**总时长恒定不变**。
   */
  setRate(r: number): void {
    this.rate.set(r);
    this.ttsHowl?.rate(r);    // 原生原地变速:位置保留、总时长不变
    if (this.ttsHowl) this.keepPitch(this.ttsHowl);   // ★ 每次变速都重申一次"保音高"
  }

  speakTimeText(): string {
    // ★ 2026-09-20(Forrest 第三十六轮):**未配 Azure Key 就没有音频这回事**。
    //   此时一律显示 --:-- / --:--,绝不拿 0 或估算值冒充时长。
    if (!this.host.grading.azureReady()) return '--:-- / --:--';

    const total = this.speakDur();
    if (!(total > 0)) {
      // Azure 已配置、音频尚未装载 → 中性占位(0s / --s)
      return this.host.grading.fmtDuration(this.speakPos()) + ' / --s';
    }
    // ★ 总时长**固定不变**:倍速只改播放速度(duration() 仍是 1x 的音源长度)
    return this.host.grading.fmtDuration(this.speakPos()) + ' / ' + this.host.grading.fmtDuration(total);
  }
}
