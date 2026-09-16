import { Component, computed, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';

import { PracticeApi } from '../../core/api/practice-api.service';
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

/** 一个 TTS 引擎选项(与 /practice 的引擎菜单同源)。 */
interface EngineOption {
  key: 'browser' | 'azure';
  name: string;
  note: string;
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
  /** 与 /practice 引擎菜单共用的两个选项,文案保持一致。 */
  readonly engines: EngineOption[] = [
    {
      key: 'browser',
      name: '浏览器内置语音',
      note: '本地合成 · 免费 · 即时生成',
      icon: 'record_voice_over'
    },
    {
      key: 'azure',
      name: 'Azure 神经网络语音',
      note: '高质量自然人声 · 需消耗额度',
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

  /**
   * 后端密钥托管接口是否已实现。
   * 2026-09-15 第十七轮:后端接口已就绪 → true。
   */
  readonly backendReady = signal(true);

  /** 后端当前的配置状态(hasKey / region / 掩码 / 来源)。null = 还没查过。 */
  readonly remoteStatus = signal<{
    hasKey: boolean; region: string | null; maskedKey: string | null; source: string;
  } | null>(null);

  /** 保存中 / 测试中的忙碌标记 —— 防止重复点击。 */
  readonly busy = signal(false);

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
    this.flash('偏好已保存');
  }

  /** 区域变更同样落盘。 */
  onRegionChange(r: string): void {
    this.region.set(r);
    try {
      localStorage.setItem(REGION_KEY, r);
    } catch { /* 同上 */ }
  }

  /**
   * 保存 Azure 密钥。
   *
   * ⚠️ 后端尚未提供密钥托管接口,所以这里**不假装保存成功**。
   *    只提示"待后端接入",并把用户填的内容留在输入框里。
   *
   *    2026-09-15 第七轮补充:为了让 /practice 的"未配置"门禁可被解除,
   *    这里写入一个**声明级**标记位 azure_speech_configured=1 ——
   *    它不代表密钥真的可用,只代表"用户已声明配置过",
   *    使练习页不再拦住用户。真校验仍在服务端。
   */
  saveKey(): void {
    if (!this.keyFilled() || this.busy()) return;
    this.busy.set(true);

    this.practiceApi.saveSpeechSettings(this.azureKey().trim(), this.region())
      .subscribe({
        next: async (status) => {
          // 保存成功 → 立刻做一次真连通性测试。
          // 不测的话,用户填错区域也会看到"保存成功",到练习时才炸 —— 那是骗人。
          try {
            const test = await firstValueFrom(this.practiceApi.testSpeech());
            this.remoteStatus.set(status);
            this.markConfigured();
            this.azureKey.set('');            // 提交后清空输入框,密钥不在页面久留
            this.flash(test.message || '已保存并通过连通性测试');
          } catch (e) {
            // 存进去了但 key 不可用 —— 如实说明,不让用户误以为能用
            this.remoteStatus.set(status);
            this.flash('已保存到服务端,但连通性测试未通过:' + this.errText(e), 'err');
          }
          this.busy.set(false);
        },
        error: (e) => {
          // 保存本身失败:绝不写声明标记,绝不假装成功
          this.flash('保存失败:' + this.errText(e), 'err');
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
