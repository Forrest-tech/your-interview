import { Component, OnDestroy, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute } from '@angular/router';
import { Subscription } from 'rxjs';

import {
  InterviewsApiService, MailboxConnectionDto, MailboxOAuthSettingsDto, MailboxPendingDto,
  MailboxImportDto, RefreshMailboxResult
} from '../../core/api/interviews-api.service';
import { InterviewEntry } from '../../core/models/api.models';
import { I18nService } from '../../core/i18n/i18n.service';
import { MatIconModule } from '@angular/material/icon';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatSelectModule } from '@angular/material/select';
import { MatTooltipModule } from '@angular/material/tooltip';

/**
 * 票 240：邮箱配置页 —— 与 LLM 配置页（/settings/llm）平行，挂在设置中心左导航。
 *
 * 本页只管“连接”：连接状态、连接/断开/重连、全部刷新、待确认队列、导入审计。
 * 匹配规则绝不在此页 —— 规则随条目走，只在条目详情页的邮箱匹配区维护（方案 §2.3）。
 * 只读声明：本功能只读邮件，不发信、不删改、不标已读（后端 scope 即只读）。
 * 票 245：页首新增 OAuth 凭据卡（部署级，每 provider 一区；Secret 写-only 永不回显）。
 */
@Component({
  selector: 'app-mailbox-setting',
  standalone: true,
  imports: [
    CommonModule, FormsModule,
    MatIconModule, MatButtonModule,
    MatFormFieldModule, MatSelectModule, MatTooltipModule
  ],
  templateUrl: './mailbox-setting.component.html',
  styleUrl: './mailbox-setting.component.scss'
})
export class MailboxSettingComponent implements OnDestroy {
  readonly i18n = inject(I18nService);
  private readonly api = inject(InterviewsApiService);
  private readonly route = inject(ActivatedRoute);

  readonly connections = signal<MailboxConnectionDto[]>([]);
  readonly pending = signal<MailboxPendingDto[]>([]);
  readonly imports = signal<MailboxImportDto[]>([]);
  readonly entries = signal<InterviewEntry[]>([]);
  readonly notice = signal<string | null>(null);
  readonly error = signal<string | null>(null);
  readonly refreshing = signal(false);
  readonly refreshResult = signal<RefreshMailboxResult | null>(null);
  readonly busy = signal(false);
  /** 票 245：OAuth 凭据卡行（Secret 永不下发，只给状态位）。 */
  readonly oauthSettings = signal<MailboxOAuthSettingsDto[]>([]);
  /** 票 245：OAuth 凭据卡保存/清除进行中的 provider（按钮防重）。 */
  readonly oauthSaving = signal<string | null>(null);
  /** 票 245：每 provider 的编辑态（Secret 留空 = 沿用已存密文）。 */
  readonly oauthForms = signal<Record<string, { clientId: string; secret: string; redirectUri: string }>>({});
  /** 歧义待确认项当前展开的条目选择（pendingId → 选中的 entryId）。 */
  readonly pendingChoice = signal<Record<string, string>>({});

  private refreshSub: Subscription | null = null;

  t(key: string): string {
    return this.i18n.t(key);
  }

  ngOnInit(): void {
    // OAuth 回调跳回：?mailbox=connected&account=… / ?mailbox=error。
    const q = this.route.snapshot.queryParamMap;
    if (q.get('mailbox') === 'connected') {
      const account = q.get('account');
      this.notice.set(account
        ? this.t('mailbox.connectedAs').replace('{account}', account)
        : this.t('mailbox.connected'));
    } else if (q.get('mailbox') === 'error') {
      this.error.set(this.t('mailbox.connectFailed'));
    }
    this.loadAll();
    this.api.getEntries({ page: 1, pageSize: 100 }).subscribe({
      next: (p) => this.entries.set(p.items ?? []),
      error: () => { /* 条目列表仅供歧义选择，失败不阻塞本页 */ }
    });
  }

  ngOnDestroy(): void {
    this.refreshSub?.unsubscribe();
  }

  loadAll(): void {
    this.api.getMailboxConnections().subscribe({
      next: (list) => this.connections.set(list),
      error: (err) => this.error.set(errMsg(err))
    });
    this.loadOAuthSettings();
    this.api.getMailboxPending().subscribe({
      next: (list) => this.pending.set(list),
      error: () => { /* 待确认队列失败不阻塞连接卡 */ }
    });
    this.api.getMailboxImports().subscribe({
      next: (list) => this.imports.set(list.slice(0, 20)),
      error: () => { /* 审计列表失败不阻塞连接卡 */ }
    });
  }

  /** 票 245：OAuth 凭据卡读（表单以服务端值为准初始化，Secret 永不回填）。 */
  loadOAuthSettings(): void {
    this.api.getMailboxOAuthSettings().subscribe({
      next: (list) => {
        this.oauthSettings.set(list);
        const forms: Record<string, { clientId: string; secret: string; redirectUri: string }> = {};
        for (const s of list) {
          forms[s.provider] = { clientId: s.clientId, secret: '', redirectUri: s.redirectUri };
        }
        this.oauthForms.set(forms);
      },
      error: (err) => this.error.set(errMsg(err))
    });
  }

  oauthForm(provider: string): { clientId: string; secret: string; redirectUri: string } {
    return this.oauthForms()[provider] ?? { clientId: '', secret: '', redirectUri: '' };
  }

  setOAuthField(provider: string, field: 'clientId' | 'secret' | 'redirectUri', value: string): void {
    const cur = this.oauthForm(provider);
    this.oauthForms.set({ ...this.oauthForms(), [provider]: { ...cur, [field]: value } });
  }

  /** 票 245：保存单 provider 凭据（Secret 留空 = 沿用；首次保存无 Secret 时后端会拒）。 */
  saveOAuth(provider: string): void {
    const form = this.oauthForm(provider);
    const saved = this.oauthSettings().find((s) => s.provider === provider);
    if (!form.clientId.trim() || !form.redirectUri.trim()
      || (!saved?.secretSaved && !form.secret.trim())) {
      this.error.set(this.t('mailbox.oauthRequired'));
      return;
    }
    this.error.set(null);
    this.oauthSaving.set(provider);
    this.api.saveMailboxOAuthSettings({
      provider,
      clientId: form.clientId.trim(),
      clientSecret: form.secret.trim() ? form.secret : null,
      redirectUri: form.redirectUri.trim()
    }).subscribe({
      next: () => {
        this.oauthSaving.set(null);
        this.notice.set(this.t('mailbox.oauthSaved'));
        this.loadOAuthSettings();
      },
      error: (err) => { this.oauthSaving.set(null); this.error.set(errMsg(err)); }
    });
  }

  /** 票 245：清除单 provider 凭据（删 DB 行，恢复环境变量兜底）。 */
  clearOAuth(provider: string): void {
    this.error.set(null);
    this.oauthSaving.set(provider);
    this.api.clearMailboxOAuthSettings(provider).subscribe({
      next: () => {
        this.oauthSaving.set(null);
        this.notice.set(this.t('mailbox.oauthCleared'));
        this.loadOAuthSettings();
      },
      error: (err) => { this.oauthSaving.set(null); this.error.set(errMsg(err)); }
    });
  }

  /** 连接：后端返回授权 URL，前端直接跳转（OAuth 授权码流程）。 */
  connect(provider: string): void {
    this.error.set(null);
    this.busy.set(true);
    this.api.connectMailbox(provider).subscribe({
      next: (r) => { window.location.href = r.authUrl; },
      error: (err) => { this.error.set(errMsg(err)); this.busy.set(false); }
    });
  }

  disconnect(id: string): void {
    this.error.set(null);
    this.api.disconnectMailbox(id).subscribe({
      next: () => { this.notice.set(this.t('mailbox.disconnected')); this.loadAll(); },
      error: (err) => this.error.set(errMsg(err))
    });
  }

  /** 全部刷新（手动刷新；取消 = 退订请求，后端按已落库部分保留、cursor 不推进）。 */
  refreshAll(): void {
    this.error.set(null);
    this.notice.set(null);
    this.refreshResult.set(null);
    this.refreshing.set(true);
    this.refreshSub?.unsubscribe();
    this.refreshSub = this.api.refreshMailbox().subscribe({
      next: (r) => {
        this.refreshing.set(false);
        this.refreshResult.set(r);
        if (r.needsReconnect) this.error.set(this.t('mailbox.needsReconnect'));
        if (r.rateLimited) this.error.set(this.t('mailbox.rateLimited'));
        if (r.failed > 0 && r.failedReasons.length > 0) this.error.set(r.failedReasons.join('；'));
        this.loadAll();
      },
      error: (err) => { this.refreshing.set(false); this.error.set(errMsg(err)); }
    });
  }

  cancelRefresh(): void {
    this.refreshSub?.unsubscribe();
    this.refreshSub = null;
    this.refreshing.set(false);
    this.notice.set(this.t('mailbox.refreshCancelled'));
    this.loadAll();
  }

  confirmPending(item: MailboxPendingDto): void {
    const entryId = item.entryId ?? this.pendingChoice()[item.id] ?? null;
    if (!entryId) {
      this.error.set(this.t('mailbox.pickEntryFirst'));
      return;
    }
    this.error.set(null);
    this.api.confirmMailboxPending(item.id, entryId).subscribe({
      next: () => { this.notice.set(this.t('mailbox.confirmed')); this.loadAll(); },
      error: (err) => this.error.set(errMsg(err))
    });
  }

  ignorePending(item: MailboxPendingDto): void {
    this.error.set(null);
    this.api.ignoreMailboxPending(item.id).subscribe({
      next: () => { this.notice.set(this.t('mailbox.ignored')); this.loadAll(); },
      error: (err) => this.error.set(errMsg(err))
    });
  }

  setPendingChoice(pendingId: string, entryId: string): void {
    this.pendingChoice.set({ ...this.pendingChoice(), [pendingId]: entryId });
  }

  entryLabel(e: InterviewEntry): string {
    return `${e.companyName} · ${e.role}${e.interviewDate ? ` · ${e.interviewDate}` : ''}`;
  }

  activeConnection(): MailboxConnectionDto | undefined {
    return this.connections().find((c) => c.status === 'Active');
  }

  /** 刷新结果条：新增 N 封 · 待确认 M 封 · 跳过重复 K 封（失败另行提示）。 */
  resultLine(r: RefreshMailboxResult): string {
    return this.t('mailbox.resultLine')
      .replace('{added}', String(r.added))
      .replace('{pending}', String(r.pending))
      .replace('{skipped}', String(r.skippedDuplicates))
      .replace('{failed}', String(r.failed));
  }
}

/** 后端 ProblemDetails / 网络错误的可读消息（绝不回显 token 类字段——后端本就不下发）。 */
function errMsg(err: unknown): string {
  const e = err as { error?: { detail?: string; title?: string }; message?: string };
  return e?.error?.detail || e?.error?.title || e?.message || '请求失败，请稍后重试。';
}
