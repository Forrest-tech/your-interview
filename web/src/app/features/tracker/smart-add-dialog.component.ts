import { Component, computed, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatSelectModule } from '@angular/material/select';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import {
  MatDialogModule, MatDialogRef, MAT_DIALOG_DATA
} from '@angular/material/dialog';
import { MatChipsModule } from '@angular/material/chips';
import { MatTooltipModule } from '@angular/material/tooltip';

import { TrackerApi, ParsedJd, ParsedInvite } from '../../core/api/tracker-api.service';
import { I18nService } from '../../core/i18n/i18n.service';
import { Application } from '../../core/models/api.models';

/** 弹窗入参:邀请模式需要一份现有投递清单供选择归属。 */
export interface SmartAddDialogData {
  applications: Application[];
  /** 预选的投递(从某条卡片的详情里唤起时用)。 */
  applicationId?: string | null;
}

/** 弹窗回执:交给调用方决定提示语与是否刷新。 */
export interface SmartAddDialogResult {
  ok: boolean;
  message: string;
}

const WORK_MODES = ['Remote', 'Hybrid', 'Onsite'];
const PRIORITIES = ['High', 'Medium', 'Low'];
const ROUND_STAGES = ['Screen', 'Technical', 'SystemDesign', 'Behavioral', 'Final'];
const ROUND_STAGE_LABELS: Record<string, string> = {
  Screen: '初筛',
  Technical: '技术面',
  SystemDesign: '系统设计',
  Behavioral: '行为面',
  Final: '终面'
};
const INVITE_FORMATS = ['Phone', 'Video', 'Onsite'];
const INVITE_FORMAT_LABELS: Record<string, string> = {
  Phone: '电话',
  Video: '视频',
  Onsite: '现场'
};

/**
 * 智能粘贴弹窗(需求 6.2.2 / 6.2.3)。
 *
 * 交互刻意做成"粘贴 → 看结果 → 改 → 存"四步,而不是"粘贴即入库":
 * 解析是概率行为,直接入库会让错字段以很低的成本污染看板,
 * 而让用户扫一眼再确认的成本几乎为零。解析失败时表单保持可手工填写 ——
 * 需求里的"解析失败可回退手动填"就是这个意思:入口可以失败,录入不能中断。
 */
@Component({
  selector: 'app-smart-add-dialog',
  standalone: true,
  imports: [
    CommonModule, FormsModule, MatDialogModule, MatButtonModule, MatIconModule,
    MatFormFieldModule, MatInputModule, MatSelectModule, MatProgressBarModule,
    MatChipsModule, MatTooltipModule
  ],
  template: `
    <h2 mat-dialog-title>
      <mat-icon>auto_awesome</mat-icon>
      {{ mode() === 'jd' ? '智能粘贴岗位' : '粘贴面试邀请' }}
    </h2>

    <mat-dialog-content class="sad-body">
      <div class="mode-switch">
        <button type="button" class="mode-btn" [class.on]="mode() === 'jd'"
                (click)="mode.set('jd')">
          <mat-icon>work_outline</mat-icon> 岗位 JD
        </button>
        <button type="button" class="mode-btn" [class.on]="mode() === 'invite'"
                (click)="mode.set('invite')">
          <mat-icon>event_available</mat-icon> 面试邀请
        </button>
      </div>

      <!-- ============ 第一步:粘贴 ============ -->
      <mat-form-field appearance="outline" class="full">
        <mat-label>{{ mode() === 'jd' ? '粘贴 JD 链接 / 正文 / 截图' : '粘贴邀请邮件或消息' }}</mat-label>
        <textarea matInput rows="5" [(ngModel)]="raw"
                  placeholder="支持 LinkedIn / Indeed / Greenhouse / Workday / 公司官网链接,或直接粘贴 JD 正文"></textarea>
        <mat-icon matPrefix>content_paste</mat-icon>
      </mat-form-field>

      <div class="paste-row">
        <label class="ai-toggle">
          <input type="checkbox" [(ngModel)]="useAi">
          用 AI 补全规则解不出的字段
        </label>
        <span class="spacer"></span>
        <button mat-stroked-button type="button" [disabled]="!canParse() || busy()" (click)="parse()">
          <mat-icon>auto_awesome</mat-icon>{{ parsed() ? '重新解析' : '解析' }}
        </button>
      </div>

      @if (busy()) { <mat-progress-bar mode="indeterminate"></mat-progress-bar> }

      @if (msg()) {
        <div class="note" [class.warn]="status() !== 'Ok'">
          <mat-icon>{{ status() === 'Ok' ? 'check_circle' : 'info' }}</mat-icon>
          <span>{{ msg() }}</span>
        </div>
      }

      <!-- ============ 第二步:确认并修改结果 ============ -->
      @if (mode() === 'jd') {
        @if (parsed()) {
          <div class="grid">
            <mat-form-field appearance="outline">
              <mat-label>公司</mat-label>
              <input matInput name="jdCompany" [(ngModel)]="jd.company">
            </mat-form-field>
            <mat-form-field appearance="outline">
              <mat-label>职位</mat-label>
              <input matInput name="jdRole" [(ngModel)]="jd.role">
            </mat-form-field>
            <mat-form-field appearance="outline">
              <mat-label>地点</mat-label>
              <input matInput name="jdLocation" [(ngModel)]="jd.location">
            </mat-form-field>
            <mat-form-field appearance="outline">
              <mat-label>薪资</mat-label>
              <input matInput name="jdSalary" [(ngModel)]="jd.salary">
            </mat-form-field>
            <mat-form-field appearance="outline">
              <mat-label>办公形式</mat-label>
              <mat-select name="jdWorkMode" [(ngModel)]="jd.workMode">
                <mat-option [value]="null">未识别</mat-option>
                @for (w of workModes; track w) { <mat-option [value]="w">{{ w }}</mat-option> }
              </mat-select>
            </mat-form-field>
            <mat-form-field appearance="outline">
              <mat-label>优先级</mat-label>
              <mat-select name="jdPriority" [(ngModel)]="jd.priority">
                @for (p of priorities; track p) { <mat-option [value]="p">{{ p }}</mat-option> }
              </mat-select>
            </mat-form-field>
            <mat-form-field appearance="outline">
              <mat-label>申请截止</mat-label>
              <input matInput type="date" name="jdDeadline" [(ngModel)]="jd.deadline">
            </mat-form-field>
            <mat-form-field appearance="outline">
              <mat-label>初始状态</mat-label>
              <mat-select name="jdStatus" [(ngModel)]="jd.status">
                @for (s of createStatuses; track s) {
                  <mat-option [value]="s">{{ s }}</mat-option>
                }
              </mat-select>
            </mat-form-field>
          </div>

          <mat-form-field appearance="outline" class="full">
            <mat-label>岗位链接</mat-label>
            <input matInput name="jdLink" [(ngModel)]="jd.link">
          </mat-form-field>

          <mat-form-field appearance="outline" class="full">
            <mat-label>JD 摘要</mat-label>
            <textarea matInput rows="3" name="jdSummary" [(ngModel)]="jd.jdSummary"></textarea>
          </mat-form-field>

          <mat-form-field appearance="outline" class="full">
            <mat-label>匹配关键词(逗号分隔)</mat-label>
            <input matInput name="jdKeywords" [(ngModel)]="jd.matchKeywords">
          </mat-form-field>
        }
      } @else {
        @if (invite()) {
          <div class="grid">
            <mat-form-field appearance="outline" class="full">
              <mat-label>关联到哪条投递</mat-label>
              <mat-select name="invApp" [(ngModel)]="inviteApplicationId">
                <mat-option [value]="null">选择投递记录…</mat-option>
                @for (a of applications; track a.id) {
                  <mat-option [value]="a.id">{{ a.companyName }} · {{ a.role }}</mat-option>
                }
              </mat-select>
            </mat-form-field>

            <mat-form-field appearance="outline">
              <mat-label>轮次</mat-label>
              <input matInput type="number" min="1" name="invRound" [(ngModel)]="inv.roundNo">
            </mat-form-field>
            <mat-form-field appearance="outline">
              <mat-label>轮次类型</mat-label>
              <mat-select name="invStage" [(ngModel)]="inv.stage">
                <mat-option [value]="null">未识别</mat-option>
                @for (s of stages; track s) {
                  <mat-option [value]="s">{{ stageLabel(s) }}</mat-option>
                }
              </mat-select>
            </mat-form-field>
            <mat-form-field appearance="outline">
              <mat-label>形式</mat-label>
              <mat-select name="invFormat" [(ngModel)]="inv.format">
                <mat-option [value]="null">未识别</mat-option>
                @for (f of formats; track f) {
                  <mat-option [value]="f">{{ formatLabel(f) }}</mat-option>
                }
              </mat-select>
            </mat-form-field>
            <mat-form-field appearance="outline">
              <mat-label>面试日期</mat-label>
              <input matInput type="date" name="invDate" [(ngModel)]="inv.date">
            </mat-form-field>
            <mat-form-field appearance="outline">
              <mat-label>时间</mat-label>
              <input matInput type="time" name="invTime" [(ngModel)]="inv.time">
            </mat-form-field>
            <mat-form-field appearance="outline">
              <mat-label>面试官</mat-label>
              <input matInput name="invInterviewer" [(ngModel)]="inv.interviewer">
            </mat-form-field>
            <mat-form-field appearance="outline">
              <mat-label>地点 / 会议链接</mat-label>
              <input matInput name="invLocation" [(ngModel)]="inv.location">
            </mat-form-field>
          </div>

          <p class="hint">
            关联后会登记一轮面试并把投递推进到「面试中」,同时在实战机经里生成一条草稿。
          </p>
        }
      }
    </mat-dialog-content>

    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>取消</button>
      @if (mode() === 'jd') {
        <button mat-raised-button color="primary" [disabled]="!canSaveJd() || busy()" (click)="saveJd()">
          保存为投递记录
        </button>
      } @else {
        <button mat-raised-button color="primary" [disabled]="!canLinkInvite() || busy()" (click)="linkInvite()">
          一键关联
        </button>
      }
    </mat-dialog-actions>
  `,
  styles: [`
    .sad-body { width: min(760px, 90vw); }
    .mode-switch { display: flex; gap: 8px; margin-bottom: 12px; }
    .mode-btn {
      display: inline-flex; align-items: center; gap: 6px;
      padding: 6px 14px; border-radius: 999px; cursor: pointer;
      border: 1px solid rgba(0,0,0,0.14); background: transparent; font-size: 13px;
    }
    .mode-btn mat-icon { font-size: 18px; width: 18px; height: 18px; }
    .mode-btn.on { background: rgba(63,81,181,0.12); border-color: rgba(63,81,181,0.5); color: #3f51b5; font-weight: 600; }
    .full { width: 100%; }
    mat-form-field { width: 100%; }
    .grid { display: grid; grid-template-columns: 1fr 1fr; gap: 10px; }
    @media (max-width: 640px) { .grid { grid-template-columns: 1fr; } }
    .paste-row { display: flex; align-items: center; gap: 10px; margin: 6px 0 10px; }
    .spacer { flex: 1; }
    .ai-toggle { display: inline-flex; align-items: center; gap: 6px; font-size: 12.5px; opacity: .85; }
    .note {
      display: flex; align-items: flex-start; gap: 6px; margin: 10px 0;
      padding: 8px 12px; border-radius: 6px; font-size: 13px;
      background: rgba(76,175,80,0.10); color: #2e7d32;
    }
    .note.warn { background: rgba(255,152,0,0.12); color: #a05a00; }
    .note mat-icon { font-size: 18px; width: 18px; height: 18px; }
    .hint { font-size: 12.5px; opacity: .7; margin: 8px 0 0; }
  `]
})
export class SmartAddDialogComponent {
  readonly dialogRef =
    inject(MatDialogRef<SmartAddDialogComponent, SmartAddDialogResult | null>);
  readonly data = inject<SmartAddDialogData>(MAT_DIALOG_DATA);
  private readonly trackerApi = inject(TrackerApi);
  private readonly i18n = inject(I18nService);
  t = (key: string): string => this.i18n.t(key);

  readonly applications = this.data?.applications ?? [];

  readonly mode = signal<'jd' | 'invite'>('jd');
  readonly busy = signal(false);
  readonly msg = signal<string | null>(null);
  readonly status = signal<string>('');
  readonly parsed = signal<ParsedJd | null>(null);
  readonly invite = signal<ParsedInvite | null>(null);

  raw = '';
  useAi = true;

  readonly workModes = WORK_MODES;
  readonly priorities = PRIORITIES;
  readonly stages = ROUND_STAGES;
  readonly formats = INVITE_FORMATS;
  readonly createStatuses = ['Saved', 'Applied'];

  /** JD 解析结果的可编辑副本 —— 解析只填初始值,用户改什么算什么。 */
  jd: {
    company: string; role: string; location: string; salary: string;
    workMode: string | null; priority: string; link: string; jdSummary: string;
    matchKeywords: string; deadline: string | null; status: string; jdText: string | null;
  } = {
    company: '', role: '', location: '', salary: '', workMode: null,
    priority: 'Medium', link: '', jdSummary: '', matchKeywords: '',
    deadline: null, status: 'Saved', jdText: null
  };

  /** 邀请解析结果的可编辑副本。日期与时间拆开存 —— 表单控件就是两个。 */
  inv: {
    roundNo: number | null; stage: string | null; format: string | null;
    date: string | null; time: string | null; interviewer: string; location: string;
  } = { roundNo: null, stage: null, format: null, date: null, time: null, interviewer: '', location: '' };

  inviteApplicationId: string | null = this.data?.applicationId ?? null;

  canParse = computed(() => this.raw.trim().length > 0 && !this.busy());
  canSaveJd = computed(() => this.jd.company.trim().length > 0 && this.jd.role.trim().length > 0);
  canLinkInvite = computed(() => !!this.inviteApplicationId);

  stageLabel(s: string): string { return ROUND_STAGE_LABELS[s] ?? s; }
  formatLabel(f: string): string { return INVITE_FORMAT_LABELS[f] ?? f; }

  parse(): void {
    this.busy.set(true);
    this.msg.set(null);

    const done = <T>(apply: (v: T) => void) => ({
      next: (v: T) => { this.busy.set(false); apply(v); },
      error: (e: Error) => { this.busy.set(false); this.status.set('Failed'); this.msg.set(e.message); }
    });

    if (this.mode() === 'jd') {
      this.trackerApi.parseJd(this.raw, this.useAi).subscribe(done<ParsedJd>((r) => {
        this.parsed.set(r);
        this.status.set(r.status);
        this.msg.set(r.message);
        // 图片/完全解析不出时保留空白表单 —— 用户可以直接手工填,入口不中断
        this.jd = {
          company: r.company ?? '', role: r.role ?? '', location: r.location ?? '',
          salary: r.salary ?? '', workMode: r.workMode, priority: r.priority ?? 'Medium',
          link: r.link ?? '', jdSummary: r.jdSummary ?? '', matchKeywords: r.matchKeywords ?? '',
          deadline: r.deadline ? r.deadline.slice(0, 10) : null,
          status: 'Saved', jdText: r.jdText
        };
      }));
    } else {
      this.trackerApi.parseInvite(this.raw, this.useAi).subscribe(done<ParsedInvite>((r) => {
        this.invite.set(r);
        this.status.set(r.status);
        this.msg.set(r.message);
        const at = r.scheduledAt ? new Date(r.scheduledAt) : null;
        this.inv = {
          roundNo: r.roundNo,
          stage: r.stage,
          format: r.format,
          date: at ? this.isoDate(at) : null,
          time: at ? `${`${at.getHours()}`.padStart(2, '0')}:${`${at.getMinutes()}`.padStart(2, '0')}` : null,
          interviewer: r.interviewer ?? '',
          location: r.location ?? r.rawLink ?? ''
        };
        this.inviteApplicationId ??= this.guessApplication(r.company);
      }));
    }
  }

  saveJd(): void {
    if (!this.canSaveJd()) return;
    this.busy.set(true);
    this.trackerApi.smartAdd({
      company: this.jd.company.trim(),
      role: this.jd.role.trim(),
      location: this.jd.location || null,
      salary: this.jd.salary || null,
      workMode: this.jd.workMode,
      jdSummary: this.jd.jdSummary || null,
      matchKeywords: this.jd.matchKeywords || null,
      priority: this.jd.priority || null,
      link: this.jd.link || null,
      jdText: this.jd.jdText,
      source: this.parsed()?.detectedSource ?? null,
      status: this.jd.status,
      deadline: this.jd.deadline
    }).subscribe({
      next: (r) => {
        this.busy.set(false);
        this.dialogRef.close({ ok: true, message: `已创建「${r.companyName} · ${this.jd.role}」` });
      },
      error: (e: Error) => { this.busy.set(false); this.status.set('Failed'); this.msg.set(e.message); }
    });
  }

  linkInvite(): void {
    if (!this.inviteApplicationId) return;
    this.busy.set(true);
    this.trackerApi.linkInvite(this.inviteApplicationId, {
      stage: this.inv.stage,
      scheduledDate: this.inv.date,
      interviewer: this.inv.interviewer || null,
      format: this.inv.format,
      notes: this.inv.location ? `地点/会议:${this.inv.location}` : null
    }).subscribe({
      next: (r) => {
        this.busy.set(false);
        this.dialogRef.close({ ok: true, message: `已登记第 ${r.roundNo} 轮面试` });
      },
      error: (e: Error) => { this.busy.set(false); this.status.set('Failed'); this.msg.set(e.message); }
    });
  }

  private isoDate(d: Date): string {
    const m = `${d.getMonth() + 1}`.padStart(2, '0');
    const day = `${d.getDate()}`.padStart(2, '0');
    return `${d.getFullYear()}-${m}-${day}`;
  }

  /** 按解析出的公司名猜一条最可能的投递 —— 只是省一次选择,猜错用户仍可改。 */
  private guessApplication(company: string | null): string | null {
    if (!company) return null;
    const hit = this.applications.find(
      (a) => (a.companyName ?? '').toLowerCase() === company.toLowerCase());
    return hit?.id ?? null;
  }
}
