import {
  Component, EventEmitter, Output, computed, effect, inject, input, signal
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatSnackBar } from '@angular/material/snack-bar';
import { InterviewDetail } from '../../../../core/models/api.models';
import { PlaybookV2Api } from '../playbook-v2.api';
import { AssetMeta, CommItem } from '../v2.models';
import { MaterialsListComponent } from './materials-list.component';

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
          ⚠ 有 {{ pendingItems().length }} 条沟通里提取到了新安排,还没处理 ——
          处理完(建轮次 / 同步 / 先放着)这条提醒就消失。
        </div>
      }

      @if (useJobs()) {
        @if (!items().length && !looseAssets().length) {
          <p class="empty">还没有沟通记录。用上方「＋ 添加材料」粘贴第一封邮件或记一条电话备注。</p>
        }
        @for (c of visibleJobsItems(); track c.id) {
          <article class="citem" [class.pending]="isPending(c)" data-testid="comm-item">
            <div class="ci-head">
              <span class="ci-icon">{{ typeIcon(c.type) }}</span>
              <span class="ci-title">{{ c.subject || typeLabel(c.type) }}</span>
              @if (c.contactName) { <span class="ci-contact">{{ c.contactName }}</span> }
              <span class="ci-date">{{ (c.occurredAt ?? '').slice(0, 10) }}</span>
              @if (isPending(c)) { <span class="pend-tag">未处理</span> }
              @else if (c.handlingStatus === 'Ignored') { <span class="done-tag">先放着</span> }
              @else if (c.handlingStatus === 'Done') { <span class="done-tag">已处理</span> }
              <span class="spacer"></span>
              <button type="button" class="icon-btn danger" (click)="remove(c)" title="删除">
                <mat-icon>delete_outline</mat-icon>
              </button>
            </div>
            @if (c.meta.summary) { <p class="ci-summary">摘要:{{ c.meta.summary }}</p> }
            <p class="ci-body">{{ preview(c.content) }}</p>
            @if (c.content.length > 240) {
              <button type="button" class="mini-btn" (click)="toggleFull(c.id)">
                {{ fullId() === c.id ? '收起' : '查看全文' }}
              </button>
              @if (fullId() === c.id) { <pre class="ci-full">{{ c.content }}</pre> }
            }
            @if (c.meta.arrangement; as arr) {
              <div class="arr-box">
                <div class="arr-title">📅 提取到新安排:
                  {{ arr.date ?? '日期待定' }} {{ arr.time ?? '' }}
                  {{ arr.stage ? '· ' + arr.stage : '' }}
                  {{ arr.interviewers ? '· ' + arr.interviewers : '' }}
                  {{ arr.format ? '· ' + arr.format : '' }}
                </div>
                @if (isPending(c)) {
                  <div class="arr-actions">
                    <button mat-raised-button color="primary" (click)="createRoundFrom(c)">建轮次并关联</button>
                    @if (matchingRound(c); as mr) {
                      <button mat-stroked-button (click)="syncToRound(c, mr.id)">
                        同步到 Round {{ mr.order }}(改期)
                      </button>
                    }
                    <button mat-stroked-button (click)="setStatus(c, 'Ignored')">先放着</button>
                    <button mat-button (click)="setStatus(c, 'Done')">标为已处理</button>
                  </div>
                }
              </div>
            }
            @if (!c.meta.arrangement) {
              <div class="move-row">
                <label>移到轮次
                  <select class="fld sm" [ngModel]="''" (ngModelChange)="moveToRound(c, $event)">
                    <option value="">选择轮次…</option>
                    @for (r of rounds(); track r.id) {
                      <option [value]="r.id">Round {{ r.order }} · {{ r.stage }}</option>
                    }
                  </select>
                </label>
              </div>
            }
          </article>
        }
        @if (looseAssets().length) {
          <h4 class="loose-title">条目级材料(未关联到具体轮次)</h4>
          <p class="hint">这些材料直接存在面试条目下(比如从某轮改归属过来的),用每件的「归属」下拉可以移回轮次。</p>
          <app-materials-list [entryId]="entry().id" [assets]="looseAssets()"
                              [rounds]="entry().rounds ?? []" (changed)="reload()" />
        }
      } @else {
        <p class="hint">这个条目没有关联 Tracker 投递,沟通记录用条目级材料承载(与轮次材料同一套,可互相改归属)。</p>
        <app-materials-list [entryId]="entry().id" [assets]="entryAssets()"
                            [rounds]="entry().rounds ?? []" (changed)="reload()" />
      }
    </div>
  `,
  styles: [`
    .comm { padding: 6px 2px; }
    .pending-banner { background: #fdecea; border: 1px solid #f0b8b2; color: #7c1d16;
                        border-radius: 10px; padding: 10px 14px; font-size: 13px; margin-bottom: 12px; }
    .empty, .hint { font-size: 13px; color: var(--yi-text-muted, #888); }
    .citem { border: 1px solid var(--yi-border, #ddd); border-radius: 12px; padding: 12px 14px; margin: 10px 0; }
    .citem.pending { border-color: #e57373; }
    .ci-head { display: flex; align-items: center; gap: 9px; flex-wrap: wrap; }
    .ci-icon { font-size: 15px; }
    .ci-title { font-size: 13.5px; font-weight: 600; }
    .ci-contact, .ci-date { font-size: 12px; color: var(--yi-text-muted, #888); }
    .pend-tag { font-size: 10.5px; font-weight: 700; color: #b3261e; border: 1px solid #f0b8b2;
                border-radius: 999px; padding: 1px 8px; }
    .done-tag { font-size: 10.5px; font-weight: 600; color: var(--yi-text-muted, #888);
                border: 1px solid var(--yi-border, #ccc); border-radius: 999px; padding: 1px 8px; }
    .spacer { flex: 1; }
    .icon-btn { border: none; background: transparent; cursor: pointer; color: inherit; display: inline-flex; }
    .icon-btn.danger { color: #b3261e; }
    .icon-btn mat-icon { font-size: 19px; width: 19px; height: 19px; }
    .ci-summary { font-size: 12.5px; color: var(--yi-text-muted, #666); margin: 7px 0 0; }
    .ci-body { font-size: 13px; line-height: 1.6; margin: 6px 0 0; white-space: pre-wrap; }
    .ci-full { white-space: pre-wrap; font-size: 12.5px; line-height: 1.6; font-family: inherit;
               border-top: 1px dashed var(--yi-border, #ddd); padding-top: 8px; }
    .mini-btn { border: 1px solid var(--yi-border, #ccc); background: transparent; color: inherit;
                border-radius: 999px; padding: 4px 11px; font-size: 12px; cursor: pointer; margin-top: 6px; }
    .arr-box { border: 1px dashed #e6a23c; border-radius: 10px; padding: 10px 12px; margin-top: 10px;
               background: color-mix(in srgb, #e6a23c 6%, transparent); }
    .arr-title { font-size: 13px; font-weight: 600; }
    .arr-actions { display: flex; gap: 8px; margin-top: 9px; flex-wrap: wrap; }
    .loose-title { font-size: 14px; margin: 18px 0 4px; }
    .move-row { margin-top: 9px; font-size: 12px; color: var(--yi-text-muted, #777); }
    .fld.sm { border: 1px solid var(--yi-border, #ccc); border-radius: 7px; padding: 4px 7px;
              font-size: 12px; background: transparent; color: inherit; margin-left: 6px; }
  `]
})
export class CommLogComponent {
  readonly entry = input.required<InterviewDetail>();
  @Output() readonly changed = new EventEmitter<void>();
  @Output() readonly pendingCount = new EventEmitter<number>();

  private readonly api = inject(PlaybookV2Api);
  private readonly snack = inject(MatSnackBar);

  readonly items = signal<CommItem[]>([]);
  readonly fullId = signal<string | null>(null);

  readonly useJobs = computed(() => !!this.entry().jobApplicationId);
  readonly rounds = computed(() =>
    [...(this.entry().rounds ?? [])].sort((a, b) => a.order - b.order));
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
    return ({ Email: '邮件', Call: '电话', Interview: '面试', Message: '消息', Note: '备注' } as
      Record<string, string>)[t] ?? t;
  }

  preview(content: string): string {
    return content.length > 240 ? content.slice(0, 240) + '…' : content;
  }

  toggleFull(id: string): void {
    this.fullId.set(this.fullId() === id ? null : id);
  }

  matchingRound(c: CommItem): { id: string; order: number } | null {
    const stage = c.meta.arrangement?.stage?.toLowerCase();
    if (!stage) return null;
    const hit = this.rounds().find(r => r.stage.toLowerCase().includes(stage)
      || stage.includes(r.stage.toLowerCase()));
    return hit ? { id: hit.id, order: hit.order } : null;
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
      this.snack.open(status === 'Ignored' ? '已先放着' : '已标为已处理', '关闭', { duration: 2000 });
      await this.load();
      this.changed.emit();
    } catch {
      this.snack.open('操作失败', '关闭', { duration: 3000 });
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
      this.snack.open('已建轮次并关联这条沟通', '关闭', { duration: 2500 });
      await this.load();
      this.changed.emit();
    } catch {
      this.snack.open('建轮次失败', '关闭', { duration: 3000 });
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
        feedback: round?.feedback ?? null
      }).toPromise();
      await this.persistMeta(c, { ...c.meta, handlingStatus: 'Done' }, 'Done');
      this.snack.open('已同步到已有轮次', '关闭', { duration: 2500 });
      await this.load();
      this.changed.emit();
    } catch {
      this.snack.open('同步失败', '关闭', { duration: 3000 });
    }
  }

  async moveToRound(c: CommItem, roundId: string): Promise<void> {
    if (!roundId) return;
    try {
      await this.moveContentIntoRound(c, roundId);
      await this.persistMeta(c, { ...c.meta, movedToRoundId: roundId });
      this.snack.open('已移入所选轮次的材料', '关闭', { duration: 2200 });
      await this.load();
      this.changed.emit();
    } catch {
      this.snack.open('移动失败', '关闭', { duration: 3000 });
    }
  }

  /** 把沟通内容落到轮次材料:已有镜像材料则改归属;没有则新建一份文本材料。 */
  private async moveContentIntoRound(c: CommItem, roundId: string): Promise<void> {
    if (c.meta.assetId) {
      await this.api.reassignAsset(this.entry().id, c.meta.assetId, roundId).toPromise();
      return;
    }
    const attached = await this.api.attachAsset(this.entry().id, {
      kind: c.type === 'Email' ? 'Text' : 'Notes',
      fileName: c.subject || this.typeLabel(c.type),
      transcriptText: c.content,
      roundId,
      phase: 'Pre',
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
      this.snack.open('已删除', '关闭', { duration: 2000 });
      await this.load();
      this.changed.emit();
    } catch {
      this.snack.open('删除失败', '关闭', { duration: 3000 });
    }
  }
}
