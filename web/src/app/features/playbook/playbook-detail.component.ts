import {
  Component, computed, effect, inject, signal, viewChild
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatMenuModule } from '@angular/material/menu';
import { MatSnackBar } from '@angular/material/snack-bar';
import { ApiClient } from '../../core/api/api-client';
import { I18nService } from '../../core/i18n/i18n.service';
import {
  Application, InterviewDetail, InterviewRound
} from '../../core/models/api.models';
import { PlaybookV2Api } from './redesign/playbook-v2.api';
import { RoundReview, safeJson } from './redesign/v2.models';
import { IntakePanelComponent, IntakeType } from './redesign/intake/intake-panel.component';
import { RoundCardComponent } from './redesign/round/round-card.component';
import { CommLogComponent } from './redesign/round/comm-log.component';

interface ResumeFileItem { id: string; fileName: string; isPrimary?: boolean; }

/**
 * Playbook 详情页(2026-10-20 最终定稿 v2 全新设计):
 * ① 条目头(公司/岗位/状态 + 投递与 Recruiter 信息 + 下一步卡 + 岗位四格条)
 * ② 条目级统一入口「＋ 添加材料」(邮件 / 录音·转写 / 备注)
 * ③ 页面页签只剩 轮次 / 沟通记录(沟通有未处理安排时红点计数)
 * ④ 轮次 stepper(一屏一轮)+ Round 工作卡
 * ⑤ 轮次页下方保留:复盘与待办(行动项 + 复盘记录)
 * 旧版面(概览/简报/流水线等页签)按定稿取消;日历仍是独立页,不受影响。
 */
@Component({
  selector: 'app-playbook-detail',
  standalone: true,
  imports: [
    CommonModule, FormsModule, RouterLink,
    MatButtonModule, MatIconModule, MatMenuModule,
    IntakePanelComponent, RoundCardComponent, CommLogComponent
  ],
  templateUrl: './playbook-detail.component.html',
  styleUrl: './playbook-detail.component.scss'
})
export class PlaybookDetailComponent {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly api = inject(ApiClient);
  private readonly v2 = inject(PlaybookV2Api);
  private readonly snack = inject(MatSnackBar);
  readonly i18n = inject(I18nService);

  readonly id = signal('');
  readonly entry = signal<InterviewDetail | null>(null);
  readonly application = signal<Application | null>(null);
  readonly resumeFile = signal<ResumeFileItem | null>(null);
  readonly loading = signal(true);
  readonly loadError = signal<string | null>(null);

  readonly pageTab = signal<'rounds' | 'comm'>('rounds');
  readonly selectedRoundId = signal<string | null>(null);
  readonly pendingComm = signal(0);
  readonly expandedCell = signal<'jd' | 'company' | 'resume' | 'score' | null>(null);
  readonly editingBasic = signal(false);
  readonly confirmDelete = signal(false);
  readonly newActionTitle = signal('');
  readonly debriefDraft = signal('');

  readonly intake = viewChild(IntakePanelComponent);

  // 基本信息编辑草稿
  readonly bCompany = signal('');
  readonly bRole = signal('');
  readonly bResult = signal('');
  readonly bNotes = signal('');

  readonly rounds = computed(() =>
    [...(this.entry()?.rounds ?? [])].sort((a, b) => a.order - b.order));

  /** 头部联系人行:各轮面试官汇总(Recruiter 沟通对象在沟通记录里)。 */
  readonly recruiterLine = computed(() => {
    const names = new Set<string>();
    for (const r of this.rounds()) {
      for (const n of (r.interviewers ?? '').split(/[,、/]/)) {
        const t = n.trim();
        if (t) names.add(t);
      }
    }
    return [...names].join('、');
  });

  readonly selectedRound = computed(() => {
    const rs = this.rounds();
    if (!rs.length) return null;
    return rs.find(r => r.id === this.selectedRoundId()) ?? rs[rs.length - 1];
  });

  readonly statusText = computed(() => {
    const e = this.entry();
    if (!e) return '';
    if (e.result) return e.result;
    if (this.rounds().some(r => r.outcome === 'Pending')) return '进行中';
    if ((e.status as string) === 'Analyzed') return '已完成';
    return e.result ?? '已完成';
  });

  readonly readiness = computed(() => {
    const e = this.entry();
    if (!e) return 0;
    let s = 0;
    if (e.jdText || e.jdSummary) s += 20;
    if (e.companyProfile) s += 15;
    if (this.rounds().some(r => r.prepReportJson)) s += 25;
    if (e.questionsToAskJson && e.questionsToAskJson !== '[]') s += 15;
    const next = this.rounds().find(r => r.outcome === 'Pending');
    if (next?.checklistJson) {
      try {
        const items = JSON.parse(next.checklistJson) as { done?: boolean }[];
        if (items.length) {
          s += Math.round(25 * items.filter(i => i.done).length / items.length);
        }
      } catch { /* 清单损坏时不计分 */ }
    }
    return Math.min(100, s);
  });

  /** 下一步建议:按"最该做的一件事"排序,避免把待办一股脑堆给用户。 */
  readonly nextStep = computed(() => {
    const e = this.entry();
    if (!e) return null;
    if (this.pendingComm() > 0) {
      return {
        text: `处理沟通记录里的 ${this.pendingComm()} 条新安排`,
        action: 'comm' as const, btn: '去处理', roundId: undefined as string | undefined
      };
    }
    const today = new Date().toISOString().slice(0, 10);
    const upcoming = this.rounds().find(r =>
      r.outcome === 'Pending' && (!r.scheduledDate || r.scheduledDate >= today));
    if (upcoming) {
      return {
        text: `备考 Round ${upcoming.order} · ${upcoming.stage}` +
          (upcoming.scheduledDate ? `(${upcoming.scheduledDate}${upcoming.scheduledTime ? ' ' + upcoming.scheduledTime : ''})` : ''),
        action: 'prep' as const, btn: '去备考', roundId: upcoming.id as string | undefined
      };
    }
    const unreviewed = [...this.rounds()].reverse()
      .find(r => r.outcome !== 'Pending' && !r.reviewJson);
    if (unreviewed) {
      return {
        text: `Round ${unreviewed.order} 已结束,还没生成复盘分析`,
        action: 'review' as const, btn: '去复盘', roundId: unreviewed.id as string | undefined
      };
    }
    const app = this.application();
    if (app?.nextFollowUpAt && app.nextFollowUpAt.slice(0, 10) <= today) {
      return {
        text: '到了跟进时间,给 Recruiter 发一封简短跟进邮件',
        action: 'comm' as const, btn: '看沟通记录', roundId: undefined as string | undefined
      };
    }
    return {
      text: '暂时没有待办,等对方回复;超过一周没消息可主动跟进一次',
      action: 'none' as const, btn: '', roundId: undefined as string | undefined
    };
  });

  constructor() {
    this.route.paramMap.subscribe(pm => {
      const id = pm.get('id');
      if (id && id !== this.id()) {
        this.id.set(id);
        void this.load();
      }
    });
    // 首次加载后选默认轮次:第一个未开始的,否则最近一轮。
    effect(() => {
      const rs = this.rounds();
      if (rs.length && !rs.some(r => r.id === this.selectedRoundId())) {
        const pending = rs.find(r => r.outcome === 'Pending');
        this.selectedRoundId.set((pending ?? rs[rs.length - 1]).id);
      }
    }, { allowSignalWrites: true });
  }

  async load(): Promise<void> {
    this.loading.set(true);
    this.loadError.set(null);
    try {
      const e = await this.api.get<InterviewDetail>(`/api/interviews/${this.id()}`).toPromise();
      if (!e) throw new Error('empty');
      this.entry.set(e);
      this.debriefDraft.set(parseDebrief(e.debriefJson));
      if (e.jobApplicationId) {
        this.api.get<Application>(`/api/jobs/applications/${e.jobApplicationId}`).subscribe({
          next: app => this.application.set(app),
          error: () => undefined
        });
        this.api.get<ResumeFileItem[]>(`/api/jobs/applications/${e.jobApplicationId}/resume-files`)
          .subscribe({
            next: files => this.resumeFile.set(
              files.find(f => f.isPrimary) ?? files[0] ?? null),
            error: () => undefined
          });
        void this.refreshPendingComm(e.jobApplicationId);
      } else {
        this.pendingComm.set(countPendingFromAssets(e));
      }
    } catch {
      this.loadError.set('加载失败,请返回列表重试。');
    } finally {
      this.loading.set(false);
    }
  }

  private async refreshPendingComm(appId: string): Promise<void> {
    try {
      const rows = await this.v2.listCommunications(appId).toPromise();
      const items = (rows ?? []).map(r => PlaybookV2Api.commFromJobs(r));
      this.pendingComm.set(items.filter(c =>
        !c.meta.movedToRoundId && PlaybookV2Api.isPending(c)).length);
    } catch { /* 红点统计失败不阻塞页面 */ }
  }

  onCommPending(n: number): void { this.pendingComm.set(n); }

  async onChildChanged(): Promise<void> {
    const keepRound = this.selectedRoundId();
    await this.load();
    if (keepRound && this.rounds().some(r => r.id === keepRound)) {
      this.selectedRoundId.set(keepRound);
    }
  }

  // ---------------- 头部动作 ----------------

  openIntake(type: IntakeType = 'email', roundId?: string): void {
    this.intake()?.openPanel(type, roundId);
    window.scrollTo({ top: 0, behavior: 'smooth' });
  }

  goMock(): void {
    const e = this.entry();
    void this.router.navigate(['/mock'], {
      queryParams: { company: e?.companyName ?? '', role: e?.role ?? '' }
    });
  }

  openJd(): void {
    const link = this.application()?.link;
    if (link) { window.open(link, '_blank', 'noopener'); return; }
    this.expandedCell.set(this.expandedCell() === 'jd' ? null : 'jd');
  }

  doNextStep(): void {
    const ns = this.nextStep();
    if (!ns) return;
    if (ns.action === 'comm') this.pageTab.set('comm');
    else if ((ns.action === 'prep' || ns.action === 'review') && ns.roundId) {
      this.selectedRoundId.set(ns.roundId);
      this.pageTab.set('rounds');
    }
  }

  toggleCell(cell: 'jd' | 'company' | 'resume' | 'score'): void {
    this.expandedCell.set(this.expandedCell() === cell ? null : cell);
  }

  roundScore(r: InterviewRound): number | null {
    return safeJson<RoundReview>(r.reviewJson)?.overall ?? null;
  }

  roundStateIcon(r: InterviewRound): string {
    if (r.outcome === 'Passed') return '✓';
    if (r.outcome === 'Failed') return '✗';
    if (r.outcome === 'Cancelled') return '—';
    return '·';
  }

  // ---------------- 基本信息 / 删除 ----------------

  startEditBasic(): void {
    const e = this.entry();
    if (!e) return;
    this.bCompany.set(e.companyName);
    this.bRole.set(e.role);
    this.bResult.set(e.result ?? '');
    this.bNotes.set(e.notes ?? '');
    this.editingBasic.set(true);
    this.confirmDelete.set(false);
  }

  async saveBasic(): Promise<void> {
    const e = this.entry();
    if (!e) return;
    try {
      await this.api.put(`/api/interviews/${e.id}`, {
        companyName: this.bCompany(),
        role: this.bRole(),
        companyProfile: e.companyProfile ?? null,
        jdText: e.jdText ?? null,
        jdSummary: e.jdSummary ?? null,
        roundNo: e.roundNo,
        interviewDate: e.interviewDate ?? null,
        interviewFormat: e.interviewFormat ?? null,
        interviewers: e.interviewers ?? null,
        location: e.location ?? null,
        result: this.bResult() || null,
        notes: this.bNotes() || null,
        questionsToAskJson: e.questionsToAskJson ?? null,
        debriefJson: e.debriefJson ?? null
      }).toPromise();
      this.editingBasic.set(false);
      this.snack.open('已保存', '关闭', { duration: 2000 });
      await this.load();
    } catch {
      this.snack.open('保存失败', '关闭', { duration: 3000 });
    }
  }

  async deleteEntry(): Promise<void> {
    if (!this.confirmDelete()) { this.confirmDelete.set(true); return; }
    try {
      await this.api.delete(`/api/interviews/${this.id()}`).toPromise();
      void this.router.navigate(['/playbook']);
    } catch {
      this.snack.open('删除失败', '关闭', { duration: 3000 });
    }
  }

  // ---------------- 复盘与待办 ----------------

  async addAction(): Promise<void> {
    const title = this.newActionTitle().trim();
    if (!title) return;
    try {
      await this.api.post(`/api/interviews/${this.id()}/actions`, { title }).toPromise();
      this.newActionTitle.set('');
      await this.load();
    } catch {
      this.snack.open('添加失败', '关闭', { duration: 3000 });
    }
  }

  async toggleAction(actionId: string): Promise<void> {
    await this.api.put(`/api/interviews/${this.id()}/actions/${actionId}/toggle`, {}).toPromise()
      .catch(() => undefined);
    await this.load();
  }

  async deleteAction(actionId: string): Promise<void> {
    await this.api.delete(`/api/interviews/${this.id()}/actions/${actionId}`).toPromise()
      .catch(() => undefined);
    await this.load();
  }

  async saveDebrief(): Promise<void> {
    const e = this.entry();
    if (!e) return;
    try {
      await this.api.put(`/api/interviews/${e.id}`, {
        companyName: e.companyName,
        role: e.role,
        companyProfile: e.companyProfile ?? null,
        jdText: e.jdText ?? null,
        jdSummary: e.jdSummary ?? null,
        roundNo: e.roundNo,
        interviewDate: e.interviewDate ?? null,
        interviewFormat: e.interviewFormat ?? null,
        interviewers: e.interviewers ?? null,
        location: e.location ?? null,
        result: e.result ?? null,
        notes: e.notes ?? null,
        questionsToAskJson: e.questionsToAskJson ?? null,
        debriefJson: JSON.stringify({ text: this.debriefDraft() })
      }).toPromise();
      this.snack.open('复盘已保存', '关闭', { duration: 2000 });
    } catch {
      this.snack.open('保存失败', '关闭', { duration: 3000 });
    }
  }
}

function parseDebrief(json: string | null | undefined): string {
  if (!json) return '';
  try {
    const j = JSON.parse(json) as { text?: string; notes?: string };
    return j.text ?? j.notes ?? '';
  } catch {
    return json;
  }
}

function countPendingFromAssets(e: InterviewDetail): number {
  return (e.assets ?? []).filter(a => {
    if (a.roundId) return false;
    try {
      const m = JSON.parse(a.metaJson ?? '{}') as { arrangement?: unknown; handlingStatus?: string };
      return !!m.arrangement && m.handlingStatus === 'Pending';
    } catch { return false; }
  }).length;
}
