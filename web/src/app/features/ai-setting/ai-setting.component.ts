import { Component, computed, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';

import { PracticeApi } from '../../core/api/practice-api.service';
import { I18nService } from '../../core/i18n/i18n.service';
import { MatIconModule } from '@angular/material/icon';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatTooltipModule } from '@angular/material/tooltip';

/**
 * AI / Azure 语音设置页。
 *
 * 2026-09-15 第六轮新增 —— 任务书第三节要求 /practice 的
 * 「TTS 引擎 → 前往配置 ↗」有真实落点,原路由不存在(点过去会 404),
 * 故补建此页。
 *
 * ⚠️ 诚实边界(必须保留):
 *   1. Azure Speech Key **绝不下发浏览器** —— 前端把 key 提交给后端保管,
 *      后端只回传 hasKey / region / 掩码。任何接口都不返回明文 key。
 *   2. 保存后会自动做一次**真连通性测试**(拿 key 去 Azure 走一次最小请求):
 *      失败就如实报 401/403 与原因,绝不显示假成功。
 *   3. key 生效优先级:数据库(本页保存的) > 环境变量 > appsettings。
 *      所以在本页保存后**无需重启服务**即刻可用。
 *
 * 2026-09-15 第十七轮:后端密钥托管接口已实现(GetSpeechSettingQuery /
 * SaveSpeechSettingCommand / PingAsync),本页从"待后端接入"切换为真实调用。
 */

/** 一个 TTS 引擎选项(与 /practice 的引擎菜单同源)。
 *  nameKey/noteKey 是 i18n 字典键 —— 模板里用 t() 取当前语言文案。 */
interface EngineOption {
  key: 'browser' | 'azure';
  nameKey: string;
  noteKey: string;
  icon: string;
}

const ENGINE_KEY = 'practice.ttsEngine';
const REGION_KEY = 'practice.azureRegion';

@Component({
  selector: 'app-ai-setting',
  standalone: true,
  imports: [
    CommonModule, FormsModule, RouterLink,
    MatIconModule, MatButtonModule,
    MatFormFieldModule, MatInputModule, MatSelectModule, MatTooltipModule
  ],
  templateUrl: './ai-setting.component.html',
  styleUrl: './ai-setting.component.scss'
})
export class AiSettingComponent {
  /** i18n —— 页面文案随顶栏语言切换(2026-09-16 Forrest 第 6 条)。 */
  readonly i18n = inject(I18nService);

  /** 模板用:直接暴露 t() 给模板调用。 */
  t(key: string): string {
    return this.i18n.t(key);
  }

  /**
   * 与 /practice 引擎菜单共用的两个选项。
   * ⚠️ name/note 改成 **i18n 的 key** —— 模板里再 t() 出当前语言的文案。
   *    2026-09-16 之前这里是写死的中文,导致网页切到英文后此页仍是中文。
   */
  readonly engines: EngineOption[] = [
    {
      key: 'browser',
      nameKey: 'setting.engineBrowserName',
      noteKey: 'setting.engineBrowserNote',
      icon: 'record_voice_over'
    },
    {
      key: 'azure',
      nameKey: 'setting.engineAzureName',
      noteKey: 'setting.engineAzureNote',
      icon: 'graphic_eq'
    }
  ];

  /** Azure 可用区域(常用几个,给下拉选,避免用户手打出错)。 */
  readonly regions = [
    'canadacentral', 'canadaeast', 'eastus', 'eastus2',
    'westus2', 'westeurope', 'southeastasia', 'japaneast'
  ];

  /** 当前选中的引擎偏好。 */
  readonly engine = signal<'browser' | 'azure'>('browser');

  /** Azure 区域。 */
  readonly region = signal('canadacentral');

  /** Azure Key 输入框内容。⚠️ 只用于提交给后端,不落 localStorage。 */
  readonly azureKey = signal('');

  /** 保存后的短暂反馈提示。 */
  readonly savedHint = signal('');
  // 提示的严重级别:ok=绿色成功,err=红色失败。
  // ⚠️ 2026-09-16:以前只有 savedHint 一个字符串且样式写死绿色,
  //    导致"保存失败:...Npgsql...28P01..."也渲染成绿色"成功"样式 —— 极具误导性。
  readonly savedHintKind = signal<'ok' | 'err'>('ok');

  /** 后端当前的配置状态(hasKey / region / 掩码 / 来源)。null = 还没查过。 */
  readonly remoteStatus = signal<{
    hasKey: boolean; region: string | null; maskedKey: string | null; source: string;
  } | null>(null);

  /**
   * 输入框里显示的是"服务端掩码"(保存后回填)而不是用户手打的真 key ——
   * 此时它不可作为凭据提交。
   *
   * 2026-09-16(Forrest 第 1 条):保存后输入框回填 `Delt••••••••OVCi`。
   * ⚠️ 该串不能被当成 key 再次提交(会把掩码写进库、覆盖真 key),
   *    所以这里识别出"值是掩码"→ 直接判定为"不是有效输入"。
   *    判据:含有掩码点号 • 的串一定是掩码。
   */
  private readonly isMaskedValue = computed(() => this.azureKey().includes('•'));

  /** 输入框是否为一个"可提交的真 key"(排除掩码回填)。 */
  readonly keyInputUsable = computed(
    () => this.keyFilled() && !this.isMaskedValue());

  /**
   * 保存前的"先测试"状态:null=未测,true=通过,false=失败。
   * 2026-09-16(Forrest 第 1 条):必须**测试通过后才能保存**。
   */
  readonly tested = signal<boolean | null>(null);

  /** 正在测试连接。 */
  readonly testing = signal(false);

  /** 保存中 / 测试中的忙碌标记 —— 防止重复点击。 */
  readonly busy = signal(false);

  /** 只有"测试通过了"才允许保存 —— 这是 Forrest 要求的硬门禁。 */
  readonly canSave = computed(
    () => this.keyInputUsable() && this.tested() === true && !this.busy());

  /** 服务端返回的掩码 key(用于展示"已配置的是哪一把")。 */
  readonly remoteMasked = computed(() => this.remoteStatus()?.maskedKey ?? '');

  private readonly practiceApi = inject(PracticeApi);

  readonly keyFilled = computed(() => this.azureKey().trim().length > 0);

  readonly keyMasked = computed(() => {
    const k = this.azureKey().trim();
    if (!k) return '';
    if (k.length <= 8) return '••••••••';
    return k.slice(0, 4) + '••••••••' + k.slice(-4);
  });

  constructor() {
    // 恢复已有的引擎偏好与区域(与 /practice 共享同一组 localStorage 键)
    try {
      const e = localStorage.getItem(ENGINE_KEY);
      if (e === 'browser' || e === 'azure') this.engine.set(e);
      const r = localStorage.getItem(REGION_KEY);
      if (r) this.region.set(r);
    } catch {
      // 隐私模式下读不到就用默认值,不阻断页面
    }

    // 拉一次服务端的真实状态 —— 刷新后仍能看到"已配置的 key 是哪一把"
    this.loadRemoteStatus();
  }

  /** 切换引擎偏好 —— 立即落盘,与 /practice 保持同步。 */
  setEngine(e: 'browser' | 'azure'): void {
    this.engine.set(e);
    try {
      localStorage.setItem(ENGINE_KEY, e);
    } catch { /* 存储不可用:仅本次会话生效 */ }
    this.flash(this.t('setting.prefSaved'));
  }

  /** 区域变更同样落盘。 */
  onRegionChange(r: string): void {
    this.region.set(r);
    try {
      localStorage.setItem(REGION_KEY, r);
    } catch { /* 同上 */ }
  }

  /**
   * Key 输入框变更。
   * ⚠️ 2026-09-16(Forrest 第 1 条):一旦改动 key,之前的"测试通过"状态立即作废 ——
   *    否则用户测了 A 通过、又把输入改成 B,保存按钮还会亮着,又把错 key 存进去。
   */
  onKeyInput(v: string): void {
    this.azureKey.set(v);
    this.tested.set(null);
  }

  /**
   * 测试当前输入框里的**候选凭据**(不落库)。
   *
   * 2026-09-16(Forrest 第 1 条):这是新的强制流程的第一步 ——
   * 用户填 key → 测试 → 通过才解锁保存按钮。
   * 测试直接拿输入框里的值去 Azure 验证,与库里旧 key 无关。
   */
  testKey(): void {
    // ⚠️ 用 keyInputUsable 而非 keyFilled —— 保存后输入框里是掩码,
    //    不能拿掩码去测(白跑一次 Azure 并报错)。
    if (!this.keyInputUsable() || this.testing() || this.busy()) return;
    this.testing.set(true);
    this.tested.set(null);

    this.practiceApi.testSpeechCredential(this.azureKey().trim(), this.region())
      .subscribe({
        next: (r) => {
          this.tested.set(true);
          this.testing.set(false);
          this.flash(r.message || this.t('setting.testOk'));
        },
        error: (e) => {
          this.tested.set(false);
          this.testing.set(false);
          this.flash(this.t('setting.testFail') + this.errText(e), 'err');
        }
      });
  }

  /**
   * 保存 Azure 密钥 —— 仅在**测试通过后**才允许。
   *
   * 2026-09-16(Forrest 第 1 条)重写:
   *   旧流程是"先存再测",结果是错的 key 也进了库。
   *   新流程:testKey() → canSave() 为真才点得动保存 → 落库。
   *   保存成功后无需再测(已经测过了),直接标记为已配置。
   *
   * ⚠️ 后端仍会独立做校验(key 不能为空 / 太短),前端门禁不是唯一防线。
   */
  saveKey(): void {
    // 硬门禁:未测试通过绝不允许保存(Forrest 要求)
    if (!this.canSave()) {
      if (!this.keyInputUsable()) this.flash(this.t('setting.keyHint'), 'err');
      else if (this.tested() !== true) this.flash(this.t('setting.testFirst'), 'err');
      return;
    }
    this.busy.set(true);

    this.practiceApi.saveSpeechSettings(this.azureKey().trim(), this.region())
      .subscribe({
        next: (status) => {
          this.remoteStatus.set(status);
          this.markConfigured();
          // 2026-09-16(Forrest 第 1 条):保存成功后输入框显示**服务端回传的掩码 key**
          //   (如 Delt••••••••OVCi),而不是清空。用户一眼就能确认"生效的是哪一把"。
          //   ⚠️ 仅显示掩码,明文 key 绝不回传、绝不留在页面。
          this.azureKey.set(status.maskedKey ?? '');
          // 掩码不是可提交的凭据 → 立即作废测试态,防止"拿掩码当 key 再存一次"(会把掩码写进库)。
          this.tested.set(null);
          this.flash(this.t('setting.savedOk'));
          this.busy.set(false);
        },
        error: (e) => {
          // 保存本身失败:绝不写声明标记,绝不假装成功
          this.flash(this.t('setting.saveFail') + this.errText(e), 'err');
          this.busy.set(false);
        }
      });
  }

  /**
   * 从后端拉当前配置状态(进入页面时调)。
   * 这样刷新页面也能看到"服务端已配置哪把 key",而不是只依赖本地标记。
   */
  loadRemoteStatus(): void {
    this.practiceApi.getSpeechSettings().subscribe({
      next: (st) => {
        this.remoteStatus.set(st);
        if (st.region) this.region.set(st.region);
        if (st.hasKey) this.markConfigured();
      },
      error: () => { /* 后端没起:页面仍可用,只是拿不到远端状态 */ }
    });
  }

  /** 只有**服务端确认**有 key 时才写本地标记 —— 它是给 /practice 的快速门禁用的。 */
  private markConfigured(): void {
    try {
      localStorage.setItem('azure_speech_configured', '1');
    } catch { /* 存储不可用:仅本次会话 */ }
  }

  private errText(e: unknown): string {
    return String((e as { message?: string })?.message ?? e ?? '未知错误').slice(0, 200);
  }

  // kind 参数化:成功走绿色,失败走红色。所有错误分支必须传 'err'。
  private flash(msg: string, kind: 'ok' | 'err' = 'ok'): void {
    this.savedHintKind.set(kind);
    this.savedHint.set(msg);
    setTimeout(() => this.savedHint.set(''), 2600);
  }
}
