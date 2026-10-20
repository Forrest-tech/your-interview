import {
  Component, EventEmitter, Output, computed, inject, input, signal
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatSnackBar } from '@angular/material/snack-bar';
import {
  InterviewDetail, InterviewRound
} from '../../../../core/models/api.models';
import { normalizeEmailDate, parsePastedEmail } from '../../components/brief/brief.helpers';
import { PlaybookV2Api } from '../playbook-v2.api';
import { AssetMeta, TranscriptSegment } from '../v2.models';
import { downloadBlob } from '../export/export-doc';

export type IntakeType = 'email' | 'transcript' | 'note';
type Target = { kind: 'round'; roundId: string } | { kind: 'comm' } | { kind: 'new' };

const CATEGORIES = [
  { key: 'invitation', label: '面试邀约' },
  { key: 'reschedule', label: '改期' },
  { key: 'rejection', label: '拒绝' },
  { key: 'followup', label: '跟进' },
  { key: 'other', label: '其它' }
];

/**
 * 条目级统一入口(2026-10-20 定稿):
 * 类型只有三种 —— 邮件 / 录音·转写文本 / 备注(电话记录写进后两种)。
 * 流程:粘贴 → 解析预览(先分类再提取,可改)→ 归属确认(系统按面试官/日期建议,
 * 可改;选新建轮次且有时间则建轮次+日历+.ics)→ 保存。
 * 任何类型都可附一张截图原图 —— 截图是附件,不是类型。
 */
@Component({
  selector: 'app-intake-panel',
  standalone: true,
  imports: [CommonModule, FormsModule, MatButtonModule, MatIconModule],
  template: `
    @if (open()) {
      <section class="panel" data-testid="intake-panel">
        <div class="panel-head">
          <h3>＋ 添加材料</h3>
          <button type="button" class="icon-btn" (click)="close()" aria-label="关闭">
            <mat-icon>close</mat-icon>
          </button>
        </div>

        <div class="type-tabs" role="tablist">
          @for (tb of typeTabs; track tb.key) {
            <button type="button" role="tab" class="ttab" [class.on]="type() === tb.key"
                    (click)="setType(tb.key)">{{ tb.label }}</button>
          }
        </div>

        @if (step() === 'input') {
          @if (type() === 'email') {
            <p class="hint">把邮件全文粘贴进来(含发件人/日期/主题行最好),点「解析预览」先看系统提取对不对,再确认归属。</p>
          } @else if (type() === 'transcript') {
            <p class="hint">粘贴通话/面试的转写文本(一行一段,行首写说话人,如「Jimmy Patague:…」)。有录音文件请在本轮「材料」页签上传 —— 录音是一整段文件,不在这里切分。</p>
          } @else {
            <p class="hint">电话沟通、口头约定、临时想法都记这里。带明确时间的面试安排,保存时可直接建成轮次。</p>
          }
          <input class="fld" placeholder="标题(可留空,保存时自动生成)"
                 [ngModel]="title()" (ngModelChange)="title.set($event)">
          <textarea class="paste" rows="9"
                    [placeholder]="type() === 'email' ? '粘贴邮件全文…' : type() === 'transcript' ? '粘贴转写文本…' : '写下这条备注…'"
                    [ngModel]="text()" (ngModelChange)="text.set($event)"></textarea>
          <div class="row">
            <label class="file-btn">
              <mat-icon>image</mat-icon> 附一张截图(原图,可选)
              <input type="file" accept="image/*" hidden (change)="onShot($event)">
            </label>
            @if (shotName()) { <span class="shot-name">{{ shotName() }}</span> }
            <span class="spacer"></span>
            <button mat-stroked-button (click)="close()">取消</button>
            <button mat-raised-button color="primary" [disabled]="!text().trim() || busy()"
                    (click)="parse()">解析预览</button>
          </div>
        } @else {
          <!-- 解析预览 -->
          <div class="preview" data-testid="intake-preview">
            @if (type() === 'email') {
              <div class="pv-grid">
                <label>分类
                  <select class="fld" [ngModel]="category()" (ngModelChange)="category.set($event)">
                    @for (c of categories; track c.key) { <option [value]="c.key">{{ c.label }}</option> }
                  </select>
                </label>
                <label>主题 <input class="fld" [ngModel]="fSubject()" (ngModelChange)="fSubject.set($event)"></label>
                <label>发件人 <input class="fld" [ngModel]="fFrom()" (ngModelChange)="fFrom.set($event)"></label>
                <label>邮件日期 <input class="fld" type="date" [ngModel]="fDate()" (ngModelChange)="fDate.set($event)"></label>
                <label>面试时间 <input class="fld" placeholder="如 2026-10-07 11:00" [ngModel]="fWhen()" (ngModelChange)="fWhen.set($event)"></label>
                <label>面试官 <input class="fld" placeholder="逗号分隔" [ngModel]="fInterviewers()" (ngModelChange)="fInterviewers.set($event)"></label>
                <label>形式
                  <select class="fld" [ngModel]="fFormat()" (ngModelChange)="fFormat.set($event)">
                    <option value="">未识别</option>
                    <option value="线上视频">线上视频</option>
                    <option value="线上做题平台">线上做题平台</option>
                    <option value="电话">电话</option>
                    <option value="现场">现场</option>
                  </select>
                </label>
              </div>
            } @else {
              <div class="pv-grid">
                <label>发生日期 <input class="fld" type="date" [ngModel]="fDate()" (ngModelChange)="fDate.set($event)"></label>
                <label>参与人 <input class="fld" placeholder="逗号分隔" [ngModel]="fInterviewers()" (ngModelChange)="fInterviewers.set($event)"></label>
              </div>
              @if (segments().length) {
                <p class="hint">识别到 {{ segments().length }} 段对话,说话人:{{ speakerNames() }}。保存后可在「面试实录」里批量改名。</p>
              }
              <label class="check-row">
                <input type="checkbox" [ngModel]="hasArrangement()" (ngModelChange)="hasArrangement.set($event)">
                这条里有一个待安排的面试(会出现在沟通记录的待处理里)
              </label>
              @if (hasArrangement()) {
                <div class="pv-grid">
                  <label>安排日期 <input class="fld" type="date" [ngModel]="arrDate()" (ngModelChange)="arrDate.set($event)"></label>
                  <label>时间 <input class="fld" placeholder="如 14:00" [ngModel]="arrTime()" (ngModelChange)="arrTime.set($event)"></label>
                  <label>阶段 <input class="fld" placeholder="如 Final / Design" [ngModel]="arrStage()" (ngModelChange)="arrStage.set($event)"></label>
                </div>
              }
            }

            @if (duplicate()) {
              <p class="warn">⚠ 这封邮件似乎已经存过了(发件人 + 日期 + 主题相同)。仍可保存,但请确认不是重复粘贴。</p>
            }

            <div class="sec">归属(系统建议已选中,可改)</div>
            <div class="targets">
              @for (r of rounds(); track r.id) {
                <label class="tgt">
                  <input type="radio" name="tgt" [checked]="isTargetRound(r.id)" (change)="target.set({kind:'round', roundId: r.id})">
                  Round {{ r.order }} · {{ r.stage }}{{ r.scheduledDate ? ' · ' + r.scheduledDate : '' }}
                </label>
              }
              <label class="tgt">
                <input type="radio" name="tgt" [checked]="target().kind === 'comm'" (change)="target.set({kind:'comm'})">
                沟通记录(条目级,不属于某一轮)
              </label>
              <label class="tgt">
                <input type="radio" name="tgt" [checked]="target().kind === 'new'" (change)="target.set({kind:'new'})">
                新建一个轮次
              </label>
            </div>
            @if (target().kind === 'new') {
              <div class="pv-grid">
                <label>日期 <input class="fld" type="date" [ngModel]="nrDate()" (ngModelChange)="nrDate.set($event)"></label>
                <label>时间 <input class="fld" placeholder="如 11:00" [ngModel]="nrTime()" (ngModelChange)="nrTime.set($event)"></label>
                <label>阶段 <input class="fld" placeholder="如 Coding" [ngModel]="nrStage()" (ngModelChange)="nrStage.set($event)"></label>
                <label>面试官 <input class="fld" [ngModel]="nrInterviewers()" (ngModelChange)="nrInterviewers.set($event)"></label>
                <label>形式 <input class="fld" placeholder="线上视频 / 电话 / 现场" [ngModel]="nrFormat()" (ngModelChange)="nrFormat.set($event)"></label>
              </div>
              <p class="hint">保存后该轮次会同时出现在本页轮次列表与应用内日历;有日期时会提供 .ics 下载(提醒:提前 1 天 + 提前 1 小时)。</p>
            }

            <div class="row">
              <button mat-stroked-button (click)="step.set('input')">← 返回修改原文</button>
              <span class="spacer"></span>
              <button mat-stroked-button (click)="close()">取消</button>
              <button mat-raised-button color="primary" [disabled]="busy()" (click)="save()">
                {{ busy() ? '保存中…' : '保存' }}
              </button>
            </div>
            @if (savedRoundIcs()) {
              <p class="ok-row">✓ 已保存。
                <button type="button" class="link-btn" (click)="downloadIcs()">下载 .ics 日历文件</button>
              </p>
            }
            @if (errorMsg()) { <p class="err">{{ errorMsg() }}</p> }
          </div>
        }
      </section>
    }
  `,
  styles: [`
    .panel { border: 1.5px solid var(--yi-accent, #3f51b5); border-radius: 14px; padding: 16px 18px;
             margin: 14px 0; background: var(--yi-surface, transparent); }
    .panel-head { display: flex; align-items: center; justify-content: space-between; }
    .panel-head h3 { margin: 0; font-size: 16px; }
    .icon-btn { border: none; background: transparent; cursor: pointer; color: inherit; display: inline-flex; }
    .type-tabs { display: flex; gap: 6px; margin: 10px 0; flex-wrap: wrap; }
    .ttab { border: 1px solid var(--yi-border, #ccc); background: transparent; color: inherit;
            border-radius: 999px; padding: 6px 14px; font-size: 13px; cursor: pointer; }
    .ttab.on { background: var(--yi-accent, #3f51b5); color: #fff; border-color: transparent; }
    .hint { font-size: 12.5px; color: var(--yi-text-muted, #777); margin: 6px 0; }
    .fld { width: 100%; border: 1px solid var(--yi-border, #ccc); border-radius: 8px;
           padding: 8px 10px; font-size: 13px; background: transparent; color: inherit; box-sizing: border-box; }
    .paste { width: 100%; border: 1px solid var(--yi-border, #ccc); border-radius: 10px; padding: 10px 12px;
             font-size: 13px; line-height: 1.55; background: transparent; color: inherit;
             box-sizing: border-box; resize: vertical; margin-top: 8px; }
    .row { display: flex; align-items: center; gap: 10px; margin-top: 12px; flex-wrap: wrap; }
    .spacer { flex: 1; }
    .file-btn { display: inline-flex; align-items: center; gap: 6px; font-size: 12.5px; cursor: pointer;
                border: 1px dashed var(--yi-border, #bbb); border-radius: 8px; padding: 6px 10px; }
    .file-btn mat-icon { font-size: 17px; width: 17px; height: 17px; }
    .shot-name { font-size: 12px; color: var(--yi-text-muted, #777); }
    .pv-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(180px, 1fr)); gap: 10px 14px; margin: 8px 0; }
    .pv-grid label { display: flex; flex-direction: column; gap: 4px; font-size: 12px; color: var(--yi-text-muted, #777); }
    .sec { font-size: 12px; font-weight: 700; color: var(--yi-text-muted, #777); margin: 14px 0 6px; }
    .targets { display: flex; flex-direction: column; gap: 6px; }
    .tgt { display: flex; align-items: center; gap: 8px; font-size: 13.5px; cursor: pointer; }
    .check-row { display: flex; align-items: center; gap: 8px; font-size: 13px; margin: 8px 0; cursor: pointer; }
    .warn { background: #fff7e0; border: 1px solid #e6c86e; color: #6d5410; border-radius: 8px;
            padding: 8px 12px; font-size: 12.5px; }
    .err { color: #b3261e; font-size: 12.5px; }
    .ok-row { font-size: 13px; color: #2e7d32; }
    .link-btn { border: none; background: none; color: var(--yi-accent, #3f51b5); cursor: pointer;
                font-size: 13px; text-decoration: underline; padding: 0; }
  `]
})
export class IntakePanelComponent {
  readonly entry = input.required<InterviewDetail>();
  @Output() readonly saved = new EventEmitter<void>();

  private readonly api = inject(PlaybookV2Api);
  private readonly snack = inject(MatSnackBar);

  readonly categories = CATEGORIES;
  readonly typeTabs: { key: IntakeType; label: string }[] = [
    { key: 'email', label: '邮件' },
    { key: 'transcript', label: '录音 / 转写文本' },
    { key: 'note', label: '备注' }
  ];

  readonly open = signal(false);
  readonly step = signal<'input' | 'preview'>('input');
  readonly type = signal<IntakeType>('email');
  readonly title = signal('');
  readonly text = signal('');
  readonly shotFile = signal<File | null>(null);
  readonly shotName = signal('');

  // 预览字段
  readonly category = signal('invitation');
  readonly fSubject = signal('');
  readonly fFrom = signal('');
  readonly fDate = signal('');
  readonly fWhen = signal('');
  readonly fInterviewers = signal('');
  readonly fFormat = signal('');
  readonly segments = signal<TranscriptSegment[]>([]);
  readonly hasArrangement = signal(false);
  readonly arrDate = signal('');
  readonly arrTime = signal('');
  readonly arrStage = signal('');
  readonly target = signal<Target>({ kind: 'comm' });
  readonly nrDate = signal('');
  readonly nrTime = signal('');
  readonly nrStage = signal('');
  readonly nrInterviewers = signal('');
  readonly nrFormat = signal('');

  readonly busy = signal(false);
  readonly errorMsg = signal<string | null>(null);
  readonly duplicate = signal(false);
  readonly savedRoundIcs = signal<{ entryId: string; roundId: string } | null>(null);

  readonly rounds = computed(() =>
    [...(this.entry().rounds ?? [])].sort((a, b) => a.order - b.order));

  speakerNames(): string {
    return [...new Set(this.segments().map(s => s.speaker))].join('、');
  }

  isTargetRound(id: string): boolean {
    const t = this.target();
    return t.kind === 'round' && t.roundId === id;
  }

  /** 父组件调用:打开面板,可预置类型与目标轮次。 */
  openPanel(type: IntakeType = 'email', roundId?: string): void {
    this.type.set(type);
    this.step.set('input');
    this.errorMsg.set(null);
    this.savedRoundIcs.set(null);
    this.target.set(roundId ? { kind: 'round', roundId } : { kind: 'comm' });
    this.open.set(true);
  }

  close(): void { this.open.set(false); }

  setType(t: IntakeType): void {
    this.type.set(t);
    this.step.set('input');
  }

  onShot(ev: Event): void {
    const f = (ev.target as HTMLInputElement).files?.[0] ?? null;
    this.shotFile.set(f);
    this.shotName.set(f ? f.name : '');
  }

  // ---------------- 解析 ----------------

  parse(): void {
    const raw = this.text();
    this.errorMsg.set(null);
    this.duplicate.set(false);
    if (this.type() === 'email') this.parseEmail(raw);
    else if (this.type() === 'transcript') this.parseTranscript(raw);
    else this.parseNote(raw);
    this.step.set('preview');
  }

  private parseEmail(raw: string): void {
    const p = parsePastedEmail(raw);
    this.fSubject.set(p.subject ?? '');
    this.fFrom.set(p.from ?? '');
    this.fDate.set(p.date ? (normalizeEmailDate(p.date) || '') : '');
    this.category.set(classifyEmail(p.subject ?? '', p.body ?? raw));
    const when = extractInterviewWhen(p.body ?? raw);
    this.fWhen.set(when ?? '');
    const names = extractInterviewerNames(p.body ?? raw);
    this.fInterviewers.set(names.join(', '));
    this.fFormat.set(extractFormat(p.body ?? raw));
    // 建议归属:面试日期命中某轮 → 该轮;否则邀约类建议新建/沟通,其余进沟通记录。
    const datePart = (when ?? '').slice(0, 10);
    const hit = this.rounds().find(r => r.scheduledDate && r.scheduledDate === datePart);
    if (hit) this.target.set({ kind: 'round', roundId: hit.id });
    else if (this.category() === 'invitation' && when) {
      this.target.set({ kind: 'new' });
      this.nrDate.set(datePart);
      this.nrTime.set((when ?? '').slice(11, 16));
      this.nrStage.set('');
      this.nrInterviewers.set(names.join(', '));
      this.nrFormat.set(this.fFormat());
    } else this.target.set({ kind: 'comm' });
    // 重复检测:发件人+日期+主题
    const key = dedupeKey(p.from ?? '', this.fDate(), p.subject ?? '');
    const dup = (this.entry().assets ?? []).some(a => {
      try {
        const m = JSON.parse(a.metaJson ?? '{}') as AssetMeta;
        return m.dedupeKey === key;
      } catch { return false; }
    });
    this.duplicate.set(dup);
  }

  private parseTranscript(raw: string): void {
    this.segments.set(parseSegments(raw));
    this.fDate.set(todayStr());
    const dateHit = this.rounds().find(r => r.scheduledDate === this.fDate());
    if (this.target().kind === 'comm' && dateHit) {
      this.target.set({ kind: 'round', roundId: dateHit.id });
    }
  }

  private parseNote(_raw: string): void {
    this.fDate.set(todayStr());
  }

  // ---------------- 保存 ----------------

  async save(): Promise<void> {
    this.busy.set(true);
    this.errorMsg.set(null);
    try {
      const entry = this.entry();
      let roundId: string | null = null;
      const t = this.target();
      if (t.kind === 'round') roundId = t.roundId;
      if (t.kind === 'new') {
        const created = await this.api.addRound(entry.id, {
          stage: this.nrStage().trim() || guessStage(this.category(), entry),
          scheduledDate: this.nrDate() || null,
          scheduledTime: this.nrTime() || null,
          interviewers: this.nrInterviewers() || null,
          format: this.nrFormat() || null
        }).toPromise();
        roundId = created!.id;
        if (this.nrDate()) this.savedRoundIcs.set({ entryId: entry.id, roundId });
      }

      // 截图先传(原图作为附件资产,归到同一目标)
      let shotId: string | undefined;
      const shot = this.shotFile();
      if (shot) {
        const up = await this.api.uploadFile(entry.id, shot,
          { roundId: roundId ?? '', phase: '' }).toPromise();
        shotId = up?.id;
      }

      const meta = this.buildMeta(shotId);
      const raw = this.text();
      const kind = this.type() === 'email' ? 'Text'
        : this.type() === 'transcript' ? 'Transcript' : 'Notes';
      const fileName = this.title().trim()
        || (this.type() === 'email' ? (this.fSubject() || '邮件') : '')
        || (this.type() === 'transcript' ? '转写文本' : '备注');
      const segs = this.type() === 'transcript' ? this.segments() : [];
      const attached = await this.api.attachAsset(entry.id, {
        kind,
        fileName,
        transcriptText: raw,
        transcriptSegmentsJson: segs.length ? JSON.stringify(segs) : null,
        roundId,
        phase: roundId ? (this.type() === 'email' ? 'Pre' : 'Post') : null,
        metaJson: JSON.stringify(meta)
      }).toPromise();
      const assetId = attached!.id;

      // 沟通目标且条目关联了投递:同源镜像到 Jobs 沟通时间线(Tracker 同表)。
      if (!roundId && entry.jobApplicationId) {
        const comm = await this.api.createCommunication(entry.jobApplicationId, {
          type: this.type() === 'email' ? 'Email' : this.type() === 'transcript' ? 'Call' : 'Note',
          subject: this.type() === 'email' ? (this.fSubject() || null) : (this.title().trim() || null),
          content: raw,
          contactName: this.fFrom() || this.fInterviewers() || null,
          occurredAt: new Date(`${this.fDate() || todayStr()}T12:00:00`).toISOString(),
          metaJson: JSON.stringify({ ...meta, assetId }),
          handlingStatus: meta.arrangement ? 'Pending' : null
        }).toPromise();
        if (comm?.id) {
          await this.api.updateAssetMeta(entry.id, assetId,
            { ...meta, communicationId: comm.id }).toPromise().catch(() => undefined);
        }
      }

      this.snack.open('已保存', '关闭', { duration: 2500 });
      this.text.set('');
      this.title.set('');
      this.shotFile.set(null);
      this.shotName.set('');
      if (!this.savedRoundIcs()) { this.open.set(false); }
      this.step.set('input');
      this.saved.emit();
    } catch (e) {
      this.errorMsg.set('保存失败:' + (e instanceof Error ? e.message : '未知错误'));
    } finally {
      this.busy.set(false);
    }
  }

  private buildMeta(shotId?: string): AssetMeta {
    const names = this.fInterviewers().split(/[,、,]/).map(s => s.trim()).filter(Boolean);
    const meta: AssetMeta = {
      channel: this.type() === 'email' ? 'email'
        : this.type() === 'transcript' ? 'call' : 'note',
      date: this.fDate() || undefined,
      interviewers: names.length ? names : undefined,
      screenshotAssetId: shotId
    };
    if (this.type() === 'email') {
      meta.category = this.category();
      meta.subject = this.fSubject() || undefined;
      meta.from = this.fFrom() || undefined;
      meta.interviewAt = this.fWhen() || undefined;
      meta.format = this.fFormat() || undefined;
      meta.dedupeKey = dedupeKey(meta.from ?? '', meta.date ?? '', meta.subject ?? '');
      if (this.category() === 'invitation' && this.fWhen()) {
        meta.arrangement = {
          date: this.fWhen().slice(0, 10),
          time: this.fWhen().slice(11, 16) || undefined,
          interviewers: names.join(', ') || undefined,
          format: this.fFormat() || undefined
        };
      }
    }
    if (this.hasArrangement()) {
      meta.arrangement = {
        date: this.arrDate() || undefined,
        time: this.arrTime() || undefined,
        stage: this.arrStage() || undefined,
        interviewers: names.join(', ') || undefined
      };
      meta.handlingStatus = 'Pending';
    }
    return meta;
  }

  downloadIcs(): void {
    const ics = this.savedRoundIcs();
    if (!ics) return;
    this.api.downloadIcs(ics.entryId, ics.roundId).subscribe({
      next: blob => downloadBlob(blob, 'interview.ics'),
      error: () => this.snack.open('日历文件生成失败', '关闭', { duration: 3000 })
    });
  }
}

// ---------------- 本地解析小工具 ----------------

function todayStr(): string {
  const d = new Date();
  const p = (n: number) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}`;
}

function dedupeKey(from: string, date: string, subject: string): string {
  return `${from.trim().toLowerCase()}|${date.trim()}|${subject.trim().toLowerCase()}`;
}

export function classifyEmail(subject: string, body: string): string {
  const s = `${subject}\n${body}`.toLowerCase();
  if (/reschedule|postpone|改期|重新安排|another time/.test(s)) return 'reschedule';
  if (/regret|unfortunately|not (be )?moving forward|未通过|很抱歉|遗憾地通知|other candidates/.test(s)) return 'rejection';
  if (/invit|interview|schedule|安排面试|邀请您参加|book your|confirm your interview/.test(s)) return 'invitation';
  if (/follow ?up|checking in|跟进|想确认|touching base/.test(s)) return 'followup';
  return 'other';
}

/** 从正文里找面试时间:优先 "Oct 7, 2026 at 11:00 AM" / "2026-10-07 11:00" 一类明确写法。 */
export function extractInterviewWhen(text: string): string | null {
  const iso = text.match(/(20\d{2})[-/](\d{1,2})[-/](\d{1,2})[ T](\d{1,2}):(\d{2})/);
  if (iso) {
    const p = (n: string) => n.padStart(2, '0');
    return `${iso[1]}-${p(iso[2])}-${p(iso[3])} ${p(iso[4])}:${iso[5]}`;
  }
  const months: Record<string, number> = {
    jan: 1, feb: 2, mar: 3, apr: 4, may: 5, jun: 6, jul: 7, aug: 8, sep: 9, oct: 10, nov: 11, dec: 12
  };
  const en = text.match(/(Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)[a-z]*\s+(\d{1,2})(?:st|nd|rd|th)?,?\s+(20\d{2})[^0-9]{0,12}?(\d{1,2}):(\d{2})\s*(AM|PM)?/i);
  if (en) {
    const mon = months[en[1].slice(0, 3).toLowerCase()];
    let hh = Number(en[4]);
    if (en[6]?.toUpperCase() === 'PM' && hh < 12) hh += 12;
    if (en[6]?.toUpperCase() === 'AM' && hh === 12) hh = 0;
    const p = (n: number) => String(n).padStart(2, '0');
    return `${en[3]}-${p(mon)}-${p(Number(en[2]))} ${p(hh)}:${en[5]}`;
  }
  return null;
}

function extractFormat(text: string): string {
  if (/jdoodle/i.test(text)) return '线上做题平台';
  if (/teams|zoom|google meet|video call/i.test(text)) return '线上视频';
  if (/phone call|电话沟通/i.test(text)) return '电话';
  if (/on-?site|in person|现场/i.test(text)) return '现场';
  return '';
}

function extractInterviewerNames(text: string): string[] {
  // 保守提取:只认 "with X Y" / "面试官:X" 这类明确句式,认不到就留空让用户补。
  const out = new Set<string>();
  for (const m of text.matchAll(/(?:面试官|interviewer)[:：]\s*([A-Za-z][A-Za-z .'-]{1,40})/gi)) {
    out.add(m[1].trim());
  }
  for (const m of text.matchAll(/with\s+([A-Z][a-z]+(?:\s+[A-Z][a-z]+){1,2})\s*[,.(]/g)) {
    out.add(m[1].trim());
  }
  return [...out].slice(0, 4);
}

function guessStage(category: string, _entry: InterviewDetail): string {
  return category === 'invitation' ? 'Interview' : 'Screen';
}

/** 把粘贴的转写文本切段:「说话人:内容」逐行;认不出说话人的行并入上一段。 */
export function parseSegments(raw: string): TranscriptSegment[] {
  const segs: TranscriptSegment[] = [];
  for (const line of raw.split(/\r?\n/)) {
    const t = line.trim();
    if (!t) continue;
    const m = t.match(/^([^:：]{1,40})[:：]\s*(.+)$/);
    if (m && !/^(https?|时间|日期|主题|subject|date|from|to)$/i.test(m[1].trim())) {
      segs.push({ speaker: m[1].trim(), text: m[2].trim() });
    } else if (segs.length) {
      segs[segs.length - 1].text += ' ' + t;
    } else {
      segs.push({ speaker: '我', text: t });
    }
  }
  // 合并相邻同说话人段落(粘贴文本常按行断句)
  const merged: TranscriptSegment[] = [];
  for (const s of segs) {
    const last = merged[merged.length - 1];
    if (last && last.speaker === s.speaker) last.text += ' ' + s.text;
    else merged.push({ ...s });
  }
  return merged;
}
