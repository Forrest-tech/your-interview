import { Injectable, inject, signal } from '@angular/core';
import { EMPTY, Observable, catchError, finalize, map, of, switchMap, tap } from 'rxjs';

import { ApiClient } from './api-client';
import { AuthService } from '../auth/auth.service';
import {
  EnsureState, PlaybookStarStory, StarStoryDto,
  collectLegacyDebriefs, collectLegacyPasted, parseStarStories, shouldAttemptImport,
  shouldEnsureLoad, shouldStartStorySaveFor, toLocalStory, toStoryInput,
} from './playbook-local.helpers';

interface ImportResult { imported: number; total: number }

/**
 * Playbook 本地数据的**单一读写路径**(COD-10b/票15,口径同票14
 * TrackerPreferencesService)。
 *
 * 此前 STAR 故事库只存 localStorage(pb.starStories);历史复盘(pb.debrief.*)
 * 与粘贴材料(pb-pasted-*)是 v2 改版前的孤儿键,无任何读写方,只剩 Tracker
 * 备份在搬运。现在:
 *   - 故事库真源 = Interviews 后端(star_stories 表),本服务信号是全应用一份;
 *   - 复盘/粘贴历史键一次性导入后端归档表(legacy_debriefs /
 *     legacy_pasted_materials,原值逐字),后端可查;现行 UI 没有这两类数据
 *     的呈现面,本服务不新增呈现,只搬数据(定稿零改动);
 *   - 首次加载一次性导入本地存量:导入成功后打标(pb.localData.imported.*),
 *     后续不再重复发起;后端导入本身幂等(故事按 clientId、归档按条目去重),
 *     双保险 —— 标记丢失也不会产生重复行;
 *   - 本地旧键**保留不清除**(票14 先例):后端成为真源后不再读取,留作本地
 *     最后一份兜底副本;Tracker 备份导出仍读这些键,行为不变。
 *
 * 票82 同型修正(与 TechStackLocalService 同口径):一次性闩锁改为
 * 「失败不闩锁(回 idle 下次重试)+ 按账号闩锁(换账号重载)+ 未登录不触发」;
 * importLegacyOnce 旧实现同步置 done —— POST 成败未定即封死本会话重试,
 * 改为两类都结算后才置 done,任一失败回 idle(已成功类已打标不会重发)。
 */
@Injectable({ providedIn: 'root' })
export class PlaybookLocalService {
  private readonly api = inject(ApiClient);
  private readonly auth = inject(AuthService);

  private static readonly STAR_KEY = 'pb.starStories';
  private static readonly STAR_IMPORTED_KEY = 'pb.localData.imported.starStories';
  private static readonly DEBRIEF_IMPORTED_KEY = 'pb.localData.imported.debriefs';
  private static readonly PASTED_IMPORTED_KEY = 'pb.localData.imported.pasted';

  /** 全应用唯一一份故事库状态(Playbook 列表页消费)。 */
  readonly starStories = signal<PlaybookStarStory[]>([]);

  private storiesState: EnsureState = 'idle';
  /** storiesState==='loaded' 时对应的用户(票82:换账号必须重载)。 */
  private storiesUserId: string | null = null;
  private legacyState: 'idle' | 'running' | 'done' = 'idle';
  /** legacyState==='done' 时对应的用户(票82)。 */
  private legacyUserId: string | null = null;

  /**
   * 首次调用:读后端故事 → (未导入过且本地有旧数据时)一次性导入 → 以后端为准刷新。
   * 票82:发起由 shouldEnsureLoad 判定;完整结束才闩锁 loaded,失败回 idle 重试。
   */
  ensureStarStoriesLoaded(): void {
    const uid = this.auth.user()?.id ?? null;
    if (!shouldEnsureLoad(this.storiesState, this.storiesUserId, uid)) return;
    this.storiesState = 'loading';
    let complete = true;

    this.api.get<StarStoryDto[]>('/api/interviews/star-stories').pipe(
      switchMap((rows) => {
        const serverStories = (rows ?? []).map(toLocalStory);
        const local = this.readLocalStories();
        if (!shouldAttemptImport(uid !== null,
          this.isImported(PlaybookLocalService.STAR_IMPORTED_KEY), local.length)) {
          return of(serverStories);
        }
        return this.api.post<ImportResult>('/api/interviews/star-stories/import', {
          stories: local.map(toStoryInput),
        }).pipe(
          tap(() => this.markImported(PlaybookLocalService.STAR_IMPORTED_KEY)),
          switchMap(() => this.api.get<StarStoryDto[]>('/api/interviews/star-stories')),
          map((rows2) => (rows2 ?? []).map(toLocalStory)),
          // 导入失败:不打标、不闩锁(下次加载重试),本次展示先用后端已有数据
          catchError(() => {
            complete = false;
            return of(serverStories);
          })
        );
      }),
      // 后端不可达:回落本地旧数据展示(不打标、不闩锁、不冒充已同步)
      catchError(() => {
        complete = false;
        return of(this.readLocalStories());
      })
    ).subscribe((stories) => {
      this.starStories.set(stories);
      if (complete && uid !== null && (this.auth.user()?.id ?? null) === uid) {
        this.storiesUserId = uid;
        this.storiesState = 'loaded';
      } else {
        this.storiesState = 'idle';
      }
    });
  }

  /** 票78:保存在飞的 clientId 集合(服务级守卫,防同故事同帧连发双 PUT)。 */
  private readonly storySavesInFlight = new Set<string>();

  /**
   * 保存一个故事:乐观更新信号,后端 PUT 失败则回读后端真源纠正。
   * 票78:改为返回 Observable(同票92 removeCategory 先例)—— 调用方订阅后
   * 才真正发起,并在结算时释放自己的组件级守卫;同一 clientId 已有保存在
   * 飞时直接返回 EMPTY(不重复发 PUT,后端 upsert 收敛是第二道防线)。
   */
  saveStory(story: PlaybookStarStory): Observable<unknown> {
    if (!shouldStartStorySaveFor(this.storySavesInFlight, story.id)) return EMPTY;
    this.storySavesInFlight.add(story.id);

    const list = [...this.starStories()];
    const idx = list.findIndex((x) => x.id === story.id);
    if (idx >= 0) list[idx] = story; else list.push(story);
    this.starStories.set(list);

    return this.api.put(`/api/interviews/star-stories/${encodeURIComponent(story.id)}`, {
      title: story.title, situation: story.situation, task: story.task,
      action: story.action, result: story.result, tags: story.tags,
    }).pipe(
      catchError(() => { this.reloadStories(); return of(null); }),
      finalize(() => this.storySavesInFlight.delete(story.id))
    );
  }

  /** 删除一个故事:乐观移除,后端 DELETE 失败则回读纠正。 */
  deleteStory(clientId: string): void {
    this.starStories.set(this.starStories().filter((x) => x.id !== clientId));
    this.api.delete(`/api/interviews/star-stories/${encodeURIComponent(clientId)}`)
      .subscribe({ error: () => this.reloadStories() });
  }

  /**
   * 历史孤儿键一次性导入(复盘 + 粘贴材料)。两类各自打标、各自幂等,
   * 一类失败不挡另一类;失败不打标,下次进 Playbook 列表页重试。
   * 票82:done 只在「两类都已结算且无失败、且仍是同一用户」时置位;
   * 任一失败回 idle —— 已成功的那类已打标,重试时不会重发。
   */
  importLegacyOnce(): void {
    const uid = this.auth.user()?.id ?? null;
    if (!uid) return;
    if (this.legacyState === 'running') return;
    if (this.legacyState === 'done' && this.legacyUserId === uid) return;

    const pairs = this.snapshotLocalPairs();
    const jobs: { key: string; url: string; items: unknown[] }[] = [];

    const debriefs = collectLegacyDebriefs(pairs);
    if (shouldAttemptImport(true, this.isImported(PlaybookLocalService.DEBRIEF_IMPORTED_KEY), debriefs.length)) {
      jobs.push({ key: PlaybookLocalService.DEBRIEF_IMPORTED_KEY, url: '/api/interviews/legacy-debriefs/import', items: debriefs });
    }

    const pasted = collectLegacyPasted(pairs);
    if (shouldAttemptImport(true, this.isImported(PlaybookLocalService.PASTED_IMPORTED_KEY), pasted.length)) {
      jobs.push({ key: PlaybookLocalService.PASTED_IMPORTED_KEY, url: '/api/interviews/legacy-pasted/import', items: pasted });
    }

    if (jobs.length === 0) {
      this.legacyUserId = uid;
      this.legacyState = 'done';
      return;
    }

    this.legacyState = 'running';
    let remaining = jobs.length;
    let failed = false;
    for (const job of jobs) {
      this.api.post<ImportResult>(job.url, { items: job.items })
        .pipe(catchError(() => of(null)))
        .subscribe((r) => {
          if (r) this.markImported(job.key); else failed = true;
          if (--remaining === 0) {
            if (!failed && (this.auth.user()?.id ?? null) === uid) {
              this.legacyUserId = uid;
              this.legacyState = 'done';
            } else {
              this.legacyState = 'idle';
            }
          }
        });
    }
  }

  // ---------- 内部 ----------

  private reloadStories(): void {
    this.api.get<StarStoryDto[]>('/api/interviews/star-stories')
      .pipe(catchError(() => of([] as StarStoryDto[])))
      .subscribe((rows) => this.starStories.set((rows ?? []).map(toLocalStory)));
  }

  private readLocalStories(): PlaybookStarStory[] {
    try { return parseStarStories(localStorage.getItem(PlaybookLocalService.STAR_KEY)); }
    catch { return []; }
  }

  private snapshotLocalPairs(): { key: string; value: string | null }[] {
    const pairs: { key: string; value: string | null }[] = [];
    try {
      for (let i = 0; i < localStorage.length; i++) {
        const k = localStorage.key(i);
        if (k) pairs.push({ key: k, value: localStorage.getItem(k) });
      }
    } catch { /* 无痕模式等读不到就跳过 */ }
    return pairs;
  }

  private isImported(key: string): boolean {
    try { return localStorage.getItem(key) === '1'; } catch { return false; }
  }

  private markImported(key: string): void {
    try { localStorage.setItem(key, '1'); } catch { /* 忽略:后端导入幂等,丢标只是下次多一次空导入 */ }
  }
}
