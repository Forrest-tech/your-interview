import { Component, computed, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';

import { PracticeApi, AiProviderPresetDto, PrepSourceSettingsDto } from '../../core/api/practice-api.service';
import { serverFieldErrorSummary } from '../../core/api/validation-errors.helpers';
import { I18nService } from '../../core/i18n/i18n.service';
import { MatIconModule } from '@angular/material/icon';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatTooltipModule } from '@angular/material/tooltip';

/**
 * LLM 设置页（票 244：自旧 AI 语音设置页拆分，LLM 凭据卡 + 备考数据源卡）。
 *
 * ⚠️ 诚实边界(必须保留):
 *   1. LLM Key **绝不下发浏览器** —— 前端把 key 提交给后端保管，
 *      后端只回传状态与掩码。任何接口都不返回明文 key。
 *   2. 先测试、通过了才允许保存（与语音卡同门禁）。
 */

/** 一个 TTS 引擎选项(与 /practice 的引擎菜单同源)。
 *  nameKey/noteKey 是 i18n 字典键 —— 模板里用 t() 取当前语言文案。 */

@Component({
  selector: 'app-llm-setting',
  standalone: true,
  imports: [
    CommonModule, FormsModule,
    MatIconModule, MatButtonModule,
    MatFormFieldModule, MatInputModule, MatSelectModule, MatCheckboxModule, MatTooltipModule
  ],
  templateUrl: './llm-setting.component.html',
  styleUrl: './llm-setting.component.scss'
})
export class LlmSettingComponent {
  /** i18n —— 页面文案随顶栏语言切换(2026-09-16 Forrest 第 6 条)。 */
  readonly i18n = inject(I18nService);

  /** 模板用:直接暴露 t() 给模板调用。 */
  t(key: string): string {
    return this.i18n.t(key);
  }
  private readonly practiceApi = inject(PracticeApi);

  constructor() {
    // 拉一次 LLM 配置状态与预设列表
    this.loadAiStatus();

    // 备考数据源配置
    this.loadPrepSources();
  }

  // ==================== LLM 设置(面试前准备包) ====================
  // 与 Voice 页严格同构:先测后存、key 只入不出、掩码回填。
  // 刻意复用同一套模式(而非另创一套交互)—— 两页行为一致,学一次就够。

  /** 后端内置的 provider 预设(含 DeepSeek / Qwen 等),进入页面时拉一次。 */
  readonly aiPresets = signal<AiProviderPresetDto[]>([]);

  /** 当前选中的预设下标。-1 = 未选(首次加载前)。 */
  readonly aiPresetIndex = signal(-1);

  /** LLM API Key 输入框内容。⚠️ 只提交给后端,不落 localStorage。 */
  readonly aiKey = signal('');

  /** 模型名。选预设时自动填第一个,用户可改。 */
  readonly aiModel = signal('');

  /** 协议地址(openai-compatible / anthropic 用)。 */
  readonly aiBaseUrl = signal('');

  /** Azure OpenAI 的端点与版本(仅该协议用)。 */
  readonly aiEndpoint = signal('');
  readonly aiApiVersion = signal('');

  /** 后端(AiGateway)当前 LLM 配置状态。null = 还没查过。
   *  M2.3:字段随收敛改为 configured/updatedAt(原 Assessment 是 hasKey/source)。 */
  readonly aiRemoteStatus = signal<{
    configured: boolean; protocol: string | null; displayName: string | null;
    baseUrl: string | null; model: string | null; maskedKey: string | null; updatedAt: string | null;
  } | null>(null);

  /** 测试态:null=未测,true=通过,false=失败。 */
  readonly aiTested = signal<boolean | null>(null);
  readonly aiTesting = signal(false);
  readonly aiBusy = signal(false);

  /** 测试返回的模型原样输出 —— 让用户直观看到"真的通了",而不是只信一句提示。 */
  readonly aiSampleOutput = signal('');

  /** 保存后的短暂反馈提示。 */
  readonly savedHint = signal('');
  // 提示的严重级别:ok=绿色成功,err=红色失败。
  // ⚠️ 2026-09-16:以前只有 savedHint 一个字符串且样式写死绿色，
  //    导致"保存失败:...Npgsql...28P01..."也渲染成绿色"成功"样式 —— 极具误导性。
  readonly savedHintKind = signal<'ok' | 'err'>('ok');

  /** 输入框里是否是掩码(掩码不可再提交,否则会把掩码写进库覆盖真 key)。 */
  private readonly aiKeyIsMasked = computed(() => this.aiKey().includes('•'));

  /** 输入框是否为一个可提交的真 key。 */
  readonly aiKeyUsable = computed(
    () => this.aiKey().trim().length > 0 && !this.aiKeyIsMasked());

  /** 当前选中预设。 */
  readonly aiCurrentPreset = computed(() => {
    const i = this.aiPresetIndex();
    const list = this.aiPresets();
    return i >= 0 && i < list.length ? list[i] : null;
  });

  /** 是否需要 BaseUrl 输入框(openai-compatible 与 anthropic 需要)。 */
  readonly aiNeedsBaseUrl = computed(() => {
    const p = this.aiCurrentPreset();
    return p ? p.protocol !== 'azure-openai' : false;
  });

  /** 是否是 Azure OpenAI(需要 Endpoint + ApiVersion)。 */
  readonly aiIsAzure = computed(() => this.aiCurrentPreset()?.protocol === 'azure-openai');

  /** 硬门禁:测试通过才允许保存(与 Azure 半页同规则)。 */
  readonly aiCanSave = computed(
    () => this.aiKeyUsable() && this.aiTested() === true && !this.aiBusy());

  readonly aiRemoteMasked = computed(() => this.aiRemoteStatus()?.maskedKey ?? '');

  /** 已配置厂商的展示名(优先用后端存的 displayName)。 */
  readonly aiRemoteName = computed(
    () => this.aiRemoteStatus()?.displayName ?? this.aiRemoteStatus()?.protocol ?? '');

  /**
   * 选择预设:自动填 BaseUrl 与第一个模型名,并作废之前的测试态
   * (换了厂商,之前测通过的那把凭据不再代表当前选择)。
   */
  onAiPresetChange(index: number): void {
    this.aiPresetIndex.set(index);
    const p = this.aiPresets()[index];
    if (!p) return;
    this.aiBaseUrl.set(p.defaultBaseUrl ?? '');
    this.aiModel.set(p.models.length > 0 ? p.models[0] : '');
    this.aiTested.set(null);
    this.aiSampleOutput.set('');
  }

  /** 改动任一连接参数都要作废测试态 —— 否则测了 A 改成 B 还能存。 */
  onAiKeyInput(v: string): void {
    this.aiKey.set(v);
    this.aiTested.set(null);
    this.aiSampleOutput.set('');
  }

  onAiModelInput(v: string): void {
    this.aiModel.set(v);
    this.aiTested.set(null);
  }

  onAiBaseUrlInput(v: string): void {
    this.aiBaseUrl.set(v);
    this.aiTested.set(null);
  }

  /** 测试当前输入框里的候选凭据(不落库)。 */
  testAiKey(): void {
    if (!this.aiKeyUsable() || this.aiTesting() || this.aiBusy()) return;
    const p = this.aiCurrentPreset();
    if (!p) { this.flash(this.t('ai.presetFirst'), 'err'); return; }

    this.aiTesting.set(true);
    this.aiTested.set(null);
    this.aiSampleOutput.set('');

    this.practiceApi.testAiCredential({
      protocol: p.protocol,
      apiKey: this.aiKey().trim(),
      baseUrl: this.aiBaseUrl().trim() || null,
      model: this.aiModel().trim(),
      endpoint: this.aiEndpoint().trim() || null,
      apiVersion: this.aiApiVersion().trim() || null
    }).subscribe({
      next: (r) => {
        this.aiTested.set(true);
        this.aiTesting.set(false);
        this.aiSampleOutput.set(r.sample ?? '');
        this.flash(r.message || this.t('ai.testOk'));
      },
      error: (e) => {
        this.aiTested.set(false);
        this.aiTesting.set(false);
        this.flash(this.t('ai.testFail') + this.errText(e), 'err');
      }
    });
  }

  /** 保存 LLM 配置 —— 仅测试通过后允许(与 Azure 半页同门禁)。 */
  saveAiKey(): void {
    if (!this.aiCanSave()) {
      if (!this.aiKeyUsable()) this.flash(this.t('setting.keyHint'), 'err');
      else if (this.aiTested() !== true) this.flash(this.t('setting.testFirst'), 'err');
      return;
    }
    const p = this.aiCurrentPreset();
    if (!p) return;

    this.aiBusy.set(true);
    this.practiceApi.saveAiSettings({
      protocol: p.protocol,
      apiKey: this.aiKey().trim(),
      baseUrl: this.aiBaseUrl().trim() || null,
      model: this.aiModel().trim(),
      endpoint: this.aiEndpoint().trim() || null,
      apiVersion: this.aiApiVersion().trim() || null,
      displayName: p.name
    }).subscribe({
      next: (status) => {
        this.aiRemoteStatus.set(status);
        // 保存后输入框回填服务端掩码(与 Azure 半页一致):
        // 用户一眼确认"生效的是哪一把",同时掩码不可再提交。
        this.aiKey.set(status.maskedKey ?? '');
        this.aiTested.set(null);
        this.aiSampleOutput.set('');
        this.flash(this.t('ai.savedOk'));
        this.aiBusy.set(false);
      },
      error: (e) => {
        this.flash(this.t('setting.saveFail') + this.errText(e), 'err');
        this.aiBusy.set(false);
      }
    });
  }

  /** 从后端拉 LLM 配置状态与预设列表(进入页面时调)。 */
  loadAiStatus(): void {
    this.practiceApi.listAiProviders().subscribe({
      next: (list) => {
        this.aiPresets.set(list);
        // 默认选中第一个(DeepSeek)—— 用户最常用,少一次点击。
        if (list.length > 0 && this.aiPresetIndex() < 0) {
          this.onAiPresetChange(0);
        }
      },
      error: () => { /* 后端没起:预设下拉为空,页面仍可用 */ }
    });

    this.practiceApi.getAiSettings().subscribe({
      next: (st) => {
        this.aiRemoteStatus.set(st);
        if (st.configured) {
          // 回填非敏感字段,让用户看到"当前配的是哪家哪个模型"。
          if (st.model) this.aiModel.set(st.model);
          if (st.baseUrl) this.aiBaseUrl.set(st.baseUrl);
          if (st.endpoint) this.aiEndpoint.set(st.endpoint);
          if (st.apiVersion) this.aiApiVersion.set(st.apiVersion);
          const idx = this.aiPresets().findIndex(p =>
            p.protocol === st.protocol && (p.name === st.displayName || !st.displayName));
          if (idx >= 0) this.aiPresetIndex.set(idx);
          // 输入框显示掩码(不是明文 key,也不是空 —— 让用户知道已配置)
          this.aiKey.set(st.maskedKey ?? '');
        }
      },
      error: () => { /* 同上 */ }
    });
  }

  // ==================== 备考数据源(2026-10-21) ====================
  // 备考报告生成时按公司 + 岗位自动匹配的数据源开关,存 AiGateway(用户级,
  // 跟随账号)。搜索密钥同 LLM 口径:只入不出、只回掩码;输入框留空 = 不改。

  readonly psQb = signal(true);
  readonly psCompany = signal(true);
  readonly psWeb = signal(false);
  readonly psGlassdoor = signal(false);
  readonly psProvider = signal<string>('tavily');
  /** 搜索密钥输入框。⚠️ 只用于提交,不落 localStorage;回填的是掩码,提交时按"不改"处理。 */
  readonly psKey = signal('');
  readonly psStatus = signal<PrepSourceSettingsDto | null>(null);
  readonly psBusy = signal(false);
  readonly psHint = signal('');
  readonly psHintKind = signal<'ok' | 'err'>('ok');

  loadPrepSources(): void {
    this.practiceApi.getPrepSourceSettings().subscribe({
      next: (st) => {
        this.psStatus.set(st);
        this.psQb.set(st.questionBankEnabled);
        this.psCompany.set(st.companyBackgroundEnabled);
        this.psWeb.set(st.webExperiencesEnabled);
        this.psGlassdoor.set(st.glassdoorEnabled);
        if (st.searchProvider) this.psProvider.set(st.searchProvider);
        this.psKey.set(st.maskedSearchKey ?? '');
      },
      error: () => { /* 后端没起:用默认口径,页面仍可用 */ }
    });
  }

  savePrepSources(): void {
    if (this.psBusy()) return;
    this.psBusy.set(true);
    const key = this.psKey().trim();
    this.practiceApi.savePrepSourceSettings({
      questionBankEnabled: this.psQb(),
      companyBackgroundEnabled: this.psCompany(),
      webExperiencesEnabled: this.psWeb(),
      glassdoorEnabled: this.psGlassdoor(),
      searchProvider: this.psProvider() || null,
      // 空或掩码(含 •) = 不改原密钥(后端同口径再判一次)
      searchApiKey: key && !key.includes('•') ? key : null
    }).subscribe({
      next: (st) => {
        this.psStatus.set(st);
        this.psKey.set(st.maskedSearchKey ?? '');
        this.psBusy.set(false);
        this.psHintKind.set('ok');
        this.psHint.set(this.t('prepSrc.saved'));
        setTimeout(() => this.psHint.set(''), 2600);
      },
      error: (e) => {
        this.psBusy.set(false);
        this.psHintKind.set('err');
        this.psHint.set(this.t('setting.saveFail') + this.errText(e));
        setTimeout(() => this.psHint.set(''), 3500);
      }
    });
  }

  private errText(e: unknown): string {
    // 票109:字段级校验错误(errors{})优先透出具体文案 —— 只读 e.message
    // 时 flash 只剩通用英文标题,看不到「…长度不能超过 N 个字符」。
    return (serverFieldErrorSummary(e)
      ?? String((e as { message?: string })?.message ?? e ?? '未知错误')).slice(0, 200);
  }

  // kind 参数化:成功走绿色,失败走红色。所有错误分支必须传 'err'。
  private flash(msg: string, kind: 'ok' | 'err' = 'ok'): void {
    this.savedHintKind.set(kind);
    this.savedHint.set(msg);
    setTimeout(() => this.savedHint.set(''), 2600);
  }
}

