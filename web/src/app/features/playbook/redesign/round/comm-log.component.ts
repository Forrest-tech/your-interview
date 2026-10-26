import {
  Component, EventEmitter, Output, computed, effect, inject, input, signal
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatSnackBar } from '@angular/material/snack-bar';
import { InterviewDetail, InterviewRound } from '../../../../core/models/api.models';
import { PlaybookV2Api } from '../playbook-v2.api';
import { AssetMeta, CommItem } from '../v2.models';
import { MaterialsListComponent } from './materials-list.component';
import { I18nService } from '../../../../core/i18n/i18n.service';
import { derivePhase, formatLabel, makeV2Fmt, makeV2T, normalizeThreadSubject, roundDisplayNo, sortRoundsByDate } from '../v2.helpers';

/**
 * 沟通记录(2026-10-20 定稿,条目级时间线):
 * 与 Tracker 是同一份底层数据 —— 条目关联了投递时读写 Jobs 的 communications 表,
 * Tracker 抽屉里看到的就是这里这些;未关联投递的条目用条目级材料(无 RoundId)兜底。
 * 带新安排且未处理的记录会冒泡:页签红点 + 计数由父组件统计,本页给出处理动作:
 * 建轮次并关联 / 同步到已有轮次(改期)/ 先放着 / 标为已处理。
 * 注意例外:带问答评估的电话(如 Phone Screen)是轮次,不进这里。
 */
@Component({
  selector: 'app-comm-log',
  standalone: true,
  imports: [CommonModule, FormsModule, MatButtonModule, MatIconModule, MaterialsListComponent],
  template: `
    <div class="comm">
      @if (pendingItems().length) {
        <div class="pending-banner" data-testid="comm-pending-banner">
          {{ fmt('pb.v2.c.pendingBanner', { n: pendingItems().length }) }}
        </div>
      }

      @if (useJobs()) {
        @if (!items().length && !looseAssets().length) {
          <p class="empty">{{ t('pb.v2.c.empty') }}</p>
        }
        @for (g of groupedJobsItems(); track g.key) {
          @if (g.items.length > 1) {
            <div class="thread-head" data-testid="comm-thread-head">
              <span class="thread-subject">🧵 {{ g.subject }}</span>
              <span class="thread-count">{{ fmt('pb.v2.c.threadCount', { n: g.items.length }) }}</span>
            </div>
          }
          @for (c of g.items; track c.id) {
          <article class="citem" [class.pending]="isPending(c)" data-testid="comm-item">
            <div class="ci-head">
              <span class="ci-icon">{{ typeIcon(c.type) }}</span>
              <span class="ci-title">{{ c.subject || typeLabel(c.type) }}</span>
              @if (c.contactName) { <span class="ci-contact">{{ c.contactName }}</span> }
              <span class="ci-date">{{ (c.occurredAt ?? '').slice(0, 10) }}</span>
              @if (isPending(c)) { <span class="pend-tag">{{ t('pb.v2.c.pending') }}</span> }
              @else if (c.handlingStatus === 'Ignored') { <span class="done-tag">{{ t('pb.v2.c.parked') }}</span> }
              @else if (c.handlingStatus === 'Done') { <span class="done-tag">{{ t('pb.v2.c.done') }}</span> }
              <span class="spacer"></span>
              <button type="button" class="icon-btn danger" (click)="remove(c)" [title]="t('common.delete')">
                <mat-icon>delete_outline</mat-icon>
              </button>
            </div>
            @if (c.meta.summary) { <p class="ci-summary">{{ t('pb.v2.m.fSummary') }}{{ c.meta.summary }}</p> }
            <p class="ci-body">{{ preview(c.content) }}</p>
            @if (c.content.length > 240) {
              <button type="button" class="mini-btn" (click)="toggleFull(c.id)">
                {{ fullId() === c.id ? t('pb.v2.d.collapse') : t('pb.v2.c.viewFull') }}
              </button>
              @if (fullId() === c.id) { <pre class="ci-full">{{ c.content }}</pre> }
            }
            @if (c.meta.arrangement; as arr) {
              <div class="arr-box">
                <div class="arr-title">{{ t('pb.v2.c.arrTitle') }}
                  {{ arr.date ?? t('pb.v2.d.dateTbd') }} {{ arr.time ?? '' }}
                  {{ arr.stage ? '· ' + arr.stage : '' }}
                  {{ arr.interviewers ? '· ' + arr.interviewers : '' }}
                  {{ arr.format ? '· ' + formatLabel(t, arr.format) : '' }}
                </div>
                @if (isPending(c)) {
                  <div class="arr-actions">
                    <button mat-raised-button color="primary" (click)="createRoundFrom(c)">{{ t('pb.v2.c.createRound') }}</button>
                    @if (matchingRound(c); as mr) {
                      <button mat-stroked-button (click)="syncToRound(c, mr.id)">
                        {{ fmt('pb.v2.c.syncToRound', { n: mr.no }) }}
                      </button>
                    }
                    <button mat-stroked-button (click)="setStatus(c, 'Ignored')">{{ t('pb.v2.c.park') }}</button>
                    <button mat-button (click)="setStatus(c, 'Done')">{{ t('pb.v2.c.markDone') }}</button>
                  </div>
                }
              </div>
            }
            @if (!c.meta.arrangement) {
              <div class="move-row">
                <label>{{ t('pb.v2.c.moveToRound') }}
                  <select class="fld sm" [ngModel]="''" (ngModelChange)="moveToRound(c, $event)">
                    <option value="">{{ t('pb.v2.c.pickRound') }}</option>
                    @for (r of rounds(); track r.id) {
                      <option [value]="r.id">Round {{ roundNo(r) }} · {{ r.stage }}</option>
                    }
                  </select>
                </label>
              </div>
            }
          </article>
          }
        }
        @if (looseAssets().length) {
          <h4 class="loose-title">{{ t('pb.v2.c.looseTitle') }}</h4>
          <p class="hint">{{ t('pb.v2.c.looseHint') }}</p>
          <app-materials-list [entryId]="entry().id" [assets]="looseAssets()"
                              [rounds]="entry().rounds ?? []" (changed)="reload()" />
        }
      } @else {
        <p class="hint">{{ t('pb.v2.c.noAppHint') }}</p>
        <app-materials-list [entryId]="entry().id" [assets]="entryAssets()"
                            [rounds]="entry().rounds ?? []" (changed)="reload()" />
      }
    </div>
  `,
  styles: [`
    .comm { padding: 6px 2px; }
    .thread-head { display: flex; align-items: center; gap: 9px; margin: 16px 0 2px; }
    .thread-subject { font-size: 14px; font-weight: 600; }
    .thread-count { font-size: 12px; font-weight: 700; color: #475569; background: #e2e8f0;
                    border-radius: 999px; padding: 1px 9px; }
    .pending-banner { background: #fef2f2; border: 1px solid #fef2f2; color: #dc2626;
                        border-radius: 10px; padding: 10px 14px; font-size: 14px; margin-bottom: 12px; }
    .empty, .hint { font-size: 13px; color: var(--yi-text-muted, #475569); }
    .empty { font-size: 14px; }
    .citem { border: 1px solid var(--yi-border, #e2e8f0); border-radius: 10px; padding: 12px 14px; margin: 10px 0; }
    .citem.pending { border-color: #fef2f2; }
    .ci-head { display: flex; align-items: center; gap: 9px; flex-wrap: wrap; }
    .ci-icon { font-size: 15px; }
    .ci-title { font-size: 14px; font-weight: 600; }
    .ci-contact, .ci-date { font-size: 13px; color: var(--yi-text-muted, #475569); }
    .pend-tag { font-size: 12px; font-weight: 700; color: #dc2626; border: 1px solid #fef2f2;
                border-radius: 999px; padding: 1px 8px; }
    .done-tag { font-size: 12px; font-weight: 600; color: var(--yi-text-muted, #475569);
                border: 1px solid var(--yi-border, #e2e8f0); border-radius: 999px; padding: 1px 8px; }
    .spacer { flex: 1; }
    .icon-btn { border: none; background: transparent; cursor: pointer; color: inherit; display: inline-flex; }
    .icon-btn.danger { color: #dc2626; }
    .icon-btn mat-icon { font-size: 19px; width: 19px; height: 19px; }
    .ci-summary { font-size: 13px; color: var(--yi-text-muted, #475569); margin: 7px 0 0; }
    .ci-body { font-size: 14px; line-height: 1.6; margin: 6px 0 0; white-space: pre-wrap; }
    .ci-full { white-space: pre-wrap; font-size: 14px; line-height: 1.6; font-family: inherit;
               border-top: 1px dashed var(--yi-border, #e2e8f0); padding-top: 8px; }
    .mini-btn { border: 1px solid var(--yi-border, #e2e8f0); background: transparent; color: inherit;
                border-radius: 999px; padding: 4px 11px; font-size: 14px; font-weight: 500; cursor: pointer; margin-top: 6px; }
    .arr-box { border: 1px dashed #b06a00; border-radius: 10px; padding: 10px 12px; margin-top: 10px;
               background: color-mix(in srgb, #b06a00 6%, transparent); }
    .arr-title { font-size: 14px; font-weight: 600; }
    .arr-actions { display: flex; gap: 8px; margin-top: 9px; flex-wrap: wrap; }
    .loose-title { font-size: 16px; font-weight: 600; margin: 18px 0 4px; }
    .move-row { margin-top: 9px; font-size: 13px; color: var(--yi-text-muted, #475569); }
    .fld.sm { border: 1px solid var(--yi-border, #e2e8f0); border-radius: 6px; padding: 4px 7px;
              font-size: 14px; background: transparent; color: inherit; margin-left: 6px; }
  `]
})
export class CommLogComponent {
  readonly entry = input.required<InterviewDetail>();
  @Output() readonly changed = new EventEmitter<void>();
  @Output() readonly pendingCount = new EventEmitter<number>();

  private readonly api = inject(PlaybookV2Api);
  private readonly snack = inject(MatSnackBar);
  private readonly i18n = inject(I18nService);
  readonly t = makeV2T(this.i18n);
  readonly formatLabel = formatLabel;
  readonly fmt = makeV2Fmt(this.i18n);

  readonly items = signal<CommItem[]>([]);
  readonly fullId = signal<string | null>(null);

  readonly useJobs = computed(() => !!this.entry().jobApplicationId);
  // 票30(REQ-10):与详情页 stepper 同一日期序、同一展示编号
  readonly rounds = computed(() => sortRoundsByDate(this.entry().rounds ?? []));

  roundNo(r: InterviewRound): number {
    return roundDisplayNo(this.rounds(), r);
  }
  readonly entryAssets = computed(() =>
    (this.entry().assets ?? []).filter(a => !a.roundId));
  /** 已关联投递时:没有 Jobs 镜像行的条目级材料(communicationId 未回填的)。 */
  readonly looseAssets = computed(() =>
    this.entryAssets().filter(a => {
      try {
        const m = JSON.parse(a.metaJson ?? '{}') as AssetMeta;
        return !m.communicationId;
      } catch { return true; }
    }));
  readonly visibleJobsItems = computed(() =>
    this.items().filter(c => !c.meta.movedToRoundId));
  /**
   * 主题聚合展示(定稿第九节):按 threadKey(保存时落的归一化主题)分组,
   * 存量无 threadKey 的按主题现场归一化兜底;无主题的各自独立不成组。
   * 组内按发生时间正序(完整往来),组间按组内最新时间倒序;单条组不带组头,
   * 呈现与原平铺一致。聚合只影响展示,底层仍是单条沟通数据。
   */
  readonly groupedJobsItems = computed(() => {
    const map = new Map<string, CommItem[]>();
    for (const c of this.visibleJobsItems()) {
      const key = c.meta.threadKey
        ? 'th:' + c.meta.threadKey
        : c.subject
          ? 'th:' + normalizeThreadSubject(c.subject).key
          : 'solo:' + c.id;
      const arr = map.get(key) ?? [];
      arr.push(c);
      map.set(key, arr);
    }
    const groups = [...map.entries()].map(([key, arr]) => {
      const sorted = [...arr].sort((a, b) =>
        (a.occurredAt ?? '') < (b.occurredAt ?? '') ? -1 : 1);
      return {
        key,
        subject: normalizeThreadSubject(sorted[0].subject ?? '').display
          || this.typeLabel(sorted[0].type),
        items: sorted,
        latest: sorted.reduce((m, c) => ((c.occurredAt ?? '') > m ? c.occurredAt : m), '')
      };
    });
    groups.sort((a, b) => (a.latest < b.latest ? 1 : -1));
    return groups.map(({ key, subject, items }) => ({ key, subject, items }));
  });
  readonly pendingItems = computed(() =>
    this.visibleJobsItems().filter(c => PlaybookV2Api.isPending(c)));

  constructor() {
    effect(() => {
      void this.entry().id;
      void this.load();
    });
    effect(() => {
      this.pendingCount.emit(this.useJobs()
        ? this.pendingItems().length
        : this.entryAssets().filter(a => {
            try {
              const m = JSON.parse(a.metaJson ?? '{}') as AssetMeta;
              return !!m.arrangement && m.handlingStatus === 'Pending';
            } catch { return false; }
          }).length);
    });
  }

  isPending(c: CommItem): boolean { return PlaybookV2Api.isPending(c); }

  async load(): Promise<void> {
    const appId = this.entry().jobApplicationId;
    if (!appId) { this.items.set([]); return; }
    try {
      const rows = await this.api.listCommunications(appId).toPromise();
      this.items.set((rows ?? []).map(r => PlaybookV2Api.commFromJobs(r)));
    } catch { this.items.set([]); }
  }

  reload(): void {
    void this.load();
    this.changed.emit();
  }

  typeIcon(t: string): string {
    return ({ Email: '✉️', Call: '📞', Interview: '🎤', Message: '💬', Note: '📝' } as
      Record<string, string>)[t] ?? '📄';
  }

  typeLabel(t: string): string {
    switch (t) {
      case 'Email': return this.t('pb.v2.c.typeEmail');
      case 'Call': return this.t('pb.v2.c.typeCall');
      case 'Interview': return this.t('pb.v2.c.typeInterview');
      case 'Message': return this.t('pb.v2.c.typeMessage');
      case 'Note': return this.t('pb.v2.c.typeNote');
      default: return t;
    }
  }

  preview(content: string): string {
    return content.length > 240 ? content.slice(0, 240) + '…' : content;
  }

  toggleFull(id: string): void {
    this.fullId.set(this.fullId() === id ? null : id);
  }

  matchingRound(c: CommItem): { id: string; no: number } | null {
    const stage = c.meta.arrangement?.stage?.toLowerCase();
    if (!stage) return null;
    const hit = this.rounds().find(r => r.stage.toLowerCase().includes(stage)
      || stage.includes(r.stage.toLowerCase()));
    return hit ? { id: hit.id, no: roundDisplayNo(this.rounds(), hit) } : null;
  }

  private async persistMeta(c: CommItem, meta: AssetMeta, status?: string): Promise<void> {
    const appId = this.entry().jobApplicationId!;
    await this.api.setCommunicationMeta(appId, c.id, {
      metaJson: JSON.stringify(meta),
      handlingStatus: status ?? c.handlingStatus ?? null
    }).toPromise();
    if (c.meta.assetId) {
      await this.api.updateAssetMeta(this.entry().id, c.meta.assetId, meta)
        .toPromise().catch(() => undefined);
    }
  }

  async setStatus(c: CommItem, status: 'Done' | 'Ignored'): Promise<void> {
    try {
      await this.persistMeta(c, { ...c.meta, handlingStatus: status }, status);
      this.snack.open(status === 'Ignored' ? this.t('pb.v2.c.parked') : this.t('pb.v2.c.doneSnack'), this.t('common.close'), { duration: 2000 });
      await this.load();
      this.changed.emit();
    } catch {
      this.snack.open(this.t('pb.v2.c.opFailed'), this.t('common.close'), { duration: 3000 });
    }
  }

  async createRoundFrom(c: CommItem): Promise<void> {
    const arr = c.meta.arrangement;
    if (!arr) return;
    try {
      const created = await this.api.addRound(this.entry().id, {
        stage: arr.stage || 'Interview',
        scheduledDate: arr.date ?? null,
        scheduledTime: arr.time ?? null,
        interviewers: arr.interviewers ?? null,
        format: arr.format ?? null
      }).toPromise();
      const roundId = created!.id;
      await this.moveContentIntoRound(c, roundId);
      await this.persistMeta(c,
        { ...c.meta, handlingStatus: 'Done', movedToRoundId: roundId }, 'Done');
      this.snack.open(this.t('pb.v2.c.roundCreated'), this.t('common.close'), { duration: 2500 });
      await this.load();
      this.changed.emit();
    } catch {
      this.snack.open(this.t('pb.v2.c.createRoundFailed'), this.t('common.close'), { duration: 3000 });
    }
  }

  async syncToRound(c: CommItem, roundId: string): Promise<void> {
    const arr = c.meta.arrangement;
    if (!arr) return;
    try {
      const round = this.rounds().find(r => r.id === roundId);
      await this.api.updateRound(this.entry().id, roundId, {
        stage: round?.stage ?? arr.stage ?? 'Interview',
        scheduledDate: arr.date ?? round?.scheduledDate ?? null,
        scheduledTime: arr.time ?? round?.scheduledTime ?? null,
        interviewers: arr.interviewers ?? round?.interviewers ?? null,
        format: arr.format ?? round?.format ?? null,
        location: round?.location ?? null,
        meetingLink: round?.meetingLink ?? null,
        outcome: round?.outcome ?? 'Pending',
        feedback: round?.feedback ?? null,
        version: round?.version ?? null
      }).toPromise();
      await this.persistMeta(c, { ...c.meta, handlingStatus: 'Done', movedToRoundId: roundId }, 'Done');
      this.snack.open(this.t('pb.v2.c.syncedRound'), this.t('common.close'), { duration: 2500 });
      await this.load();
      this.changed.emit();
    } catch {
      this.snack.open(this.t('pb.v2.p.syncFailed'), this.t('common.close'), { duration: 3000 });
    }
  }

  async moveToRound(c: CommItem, roundId: string): Promise<void> {
    if (!roundId) return;
    try {
      await this.moveContentIntoRound(c, roundId);
      await this.persistMeta(c, { ...c.meta, movedToRoundId: roundId });
      this.snack.open(this.t('pb.v2.c.movedToRoundMat'), this.t('common.close'), { duration: 2200 });
      await this.load();
      this.changed.emit();
    } catch {
      this.snack.open(this.t('pb.v2.c.moveFailed'), this.t('common.close'), { duration: 3000 });
    }
  }

  /** 把沟通内容落到轮次材料:已有镜像材料则改归属;没有则新建一份文本材料。 */
  private async moveContentIntoRound(c: CommItem, roundId: string): Promise<void> {
    if (c.meta.assetId) {
      await this.api.reassignAsset(this.entry().id, c.meta.assetId, roundId).toPromise();
      return;
    }
    // phase 落值与展示推导同一规则:沟通发生日 vs 目标轮次日期(不再写死 Pre)。
    const targetRound = (this.entry().rounds ?? []).find(r => r.id === roundId);
    const attached = await this.api.attachAsset(this.entry().id, {
      kind: c.type === 'Email' ? 'Text' : 'Notes',
      fileName: c.subject || this.typeLabel(c.type),
      transcriptText: c.content,
      roundId,
      phase: derivePhase(c.type === 'Email', c.occurredAt, targetRound?.scheduledDate),
      metaJson: JSON.stringify({ ...c.meta, communicationId: c.id })
    }).toPromise();
    if (attached?.id) {
      c.meta = { ...c.meta, assetId: attached.id };
    }
  }

  async remove(c: CommItem): Promise<void> {
    try {
      const appId = this.entry().jobApplicationId!;
      await this.api.deleteCommunication(appId, c.id).toPromise();
      this.snack.open(this.t('pb.v2.m.deleted'), this.t('common.close'), { duration: 2000 });
      await this.load();
      this.changed.emit();
    } catch {
      this.snack.open(this.t('pb.v2.d.deleteFailed'), this.t('common.close'), { duration: 3000 });
    }
  }
}
