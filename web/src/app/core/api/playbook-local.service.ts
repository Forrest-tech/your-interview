import { Injectable, inject, signal } from '@angular/core';
import { catchError, map, of, switchMap, tap } from 'rxjs';

import { ApiClient } from './api-client';
import {
  PlaybookStarStory, StarStoryDto,
  collectLegacyDebriefs, collectLegacyPasted, parseStarStories, shouldImport,
  toLocalStory, toStoryInput,
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
 */
@Injectable({ providedIn: 'root' })
export class PlaybookLocalService {
  private readonly api = inject(ApiClient);

  private static readonly STAR_KEY = 'pb.starStories';
  private static readonly STAR_IMPORTED_KEY = 'pb.localData.imported.starStories';
  private static readonly DEBRIEF_IMPORTED_KEY = 'pb.localData.imported.debriefs';
  private static readonly PASTED_IMPORTED_KEY = 'pb.localData.imported.pasted';

  /** 全应用唯一一份故事库状态(Playbook 列表页消费)。 */
  readonly starStories = signal<PlaybookStarStory[]>([]);

  private storiesState: 'idle' | 'loading' | 'loaded' = 'idle';
  private legacyState: 'idle' | 'running' | 'done' = 'idle';

  /** 首次调用:读后端故事 → (未导入过且本地有旧数据时)一次性导入 → 以后端为准刷新。 */
  ensureStarStoriesLoaded(): void {
    if (this.storiesState !== 'idle') return;
    this.storiesState = 'loading';

    this.api.get<StarStoryDto[]>('/api/interviews/star-stories').pipe(
      switchMap((rows) => {
        const serverStories = (rows ?? []).map(toLocalStory);
        const local = this.readLocalStories();
        if (!shouldImport(this.isImported(PlaybookLocalService.STAR_IMPORTED_KEY), local.length)) {
          return of(serverStories);
        }
        return this.api.post<ImportResult>('/api/interviews/star-stories/import', {
          stories: local.map(toStoryInput),
        }).pipe(
          tap(() => this.markImported(PlaybookLocalService.STAR_IMPORTED_KEY)),
          switchMap(() => this.api.get<StarStoryDto[]>('/api/interviews/star-stories')),
          map((rows2) => (rows2 ?? []).map(toLocalStory)),
          // 导入失败:不打标(下次加载重试),本次展示先用后端已有数据
          catchError(() => of(serverStories))
        );
      }),
      // 后端不可达:回落本地旧数据展示(不打标、不冒充已同步)
      catchError(() => of(this.readLocalStories()))
    ).subscribe((stories) => {
      this.starStories.set(stories);
      this.storiesState = 'loaded';
    });
  }

  /** 保存一个故事:乐观更新信号,后端 PUT 失败则回读后端真源纠正。 */
  saveStory(story: PlaybookStarStory): void {
    const list = [...this.starStories()];
    const idx = list.findIndex((x) => x.id === story.id);
    if (idx >= 0) list[idx] = story; else list.push(story);
    this.starStories.set(list);

    this.api.put(`/api/interviews/star-stories/${encodeURIComponent(story.id)}`, {
      title: story.title, situation: story.situation, task: story.task,
      action: story.action, result: story.result, tags: story.tags,
    }).subscribe({ error: () => this.reloadStories() });
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
   */
  importLegacyOnce(): void {
    if (this.legacyState !== 'idle') return;
    this.legacyState = 'running';

    const pairs = this.snapshotLocalPairs();

    const debriefs = collectLegacyDebriefs(pairs);
    if (shouldImport(this.isImported(PlaybookLocalService.DEBRIEF_IMPORTED_KEY), debriefs.length)) {
      this.api.post<ImportResult>('/api/interviews/legacy-debriefs/import', { items: debriefs })
        .pipe(catchError(() => of(null)))
        .subscribe((r) => { if (r) this.markImported(PlaybookLocalService.DEBRIEF_IMPORTED_KEY); });
    }

    const pasted = collectLegacyPasted(pairs);
    if (shouldImport(this.isImported(PlaybookLocalService.PASTED_IMPORTED_KEY), pasted.length)) {
      this.api.post<ImportResult>('/api/interviews/legacy-pasted/import', { items: pasted })
        .pipe(catchError(() => of(null)))
        .subscribe((r) => { if (r) this.markImported(PlaybookLocalService.PASTED_IMPORTED_KEY); });
    }

    this.legacyState = 'done';
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
