import { Injectable, inject, signal } from '@angular/core';
import { Observable, catchError, forkJoin, map, of, switchMap, tap } from 'rxjs';

import { ApiClient } from './api-client';
import {
  CustomCategoryDto, ReviewActivityDto, ReviewActivityMap,
  localDateKey, parseCustomCategories, parseReviewActivity, shouldImport,
  toActivityEntries, toActivityMap,
} from './techstack-local.helpers';

interface ImportResult { imported: number; total: number }

/**
 * Tech Stack 本地数据的**单一读写路径**(COD-10c/票16,口径同票14
 * TrackerPreferencesService / 票15 PlaybookLocalService)。
 *
 * 此前两类数据只存 localStorage:自定义空分类(ts.customCategories)与
 * 复习活动(ts.reviewActivity,本地日期 → 次数)。现在:
 *   - 真源 = Knowledge 后端(tech_stack_categories / review_activities 表),
 *     本服务两个信号是全应用各一份(Tech Stack 页与分类弹窗共用);
 *   - 首次加载一次性导入本地存量:导入成功后打标(ts.localData.imported.*),
 *     后续不再重复发起;后端导入本身幂等(分类按名去重、复习同日取 max),
 *     双保险 —— 标记丢失也不会产生重复/虚增;
 *   - 日期口径不变:复习日期键仍由前端按本地日历算法(localDateKey)生成后
 *     上报,后端逐字存储,永不以 UTC 推导;
 *   - 本地旧键**保留不清除**(票14/15 先例):后端成为真源后不再读取,
 *     留作本地最后一份兜底副本;
 *   - 后端不可达时回落本地旧数据展示(不打标、不冒充已同步)。
 */
@Injectable({ providedIn: 'root' })
export class TechStackLocalService {
  private readonly api = inject(ApiClient);

  private static readonly CATEGORIES_KEY = 'ts.customCategories';
  private static readonly ACTIVITY_KEY = 'ts.reviewActivity';
  private static readonly CATEGORIES_IMPORTED_KEY = 'ts.localData.imported.categories';
  private static readonly ACTIVITY_IMPORTED_KEY = 'ts.localData.imported.reviewActivity';

  /** 全应用唯一一份自定义分类状态(名字列表,录入序)。 */
  readonly customCategories = signal<string[]>([]);
  /** 全应用唯一一份复习活动状态(本地日期串 → 次数)。 */
  readonly reviewActivity = signal<ReviewActivityMap>({});

  private state: 'idle' | 'loading' | 'loaded' = 'idle';

  /** 首次调用:读后端两类数据 → (未导入过且本地有旧数据时)一次性导入 → 以后端为准刷新。 */
  ensureLoaded(): void {
    if (this.state !== 'idle') return;
    this.state = 'loading';

    forkJoin({
      cats: this.api.get<CustomCategoryDto[]>('/api/knowledge/custom-categories')
        .pipe(catchError(() => of(null))),
      acts: this.api.get<ReviewActivityDto[]>('/api/knowledge/review-activity')
        .pipe(catchError(() => of(null))),
    }).subscribe(({ cats, acts }) => {
      if (cats === null || acts === null) {
        // 后端不可达:回落本地旧数据展示
        this.customCategories.set(this.readLocalCategories());
        this.reviewActivity.set(this.readLocalActivity());
        this.state = 'loaded';
        return;
      }
      this.importCategoriesIfNeeded((cats ?? []).map((c) => c.name))
        .subscribe((names) => this.customCategories.set(names));
      this.importActivityIfNeeded(toActivityMap(acts ?? []))
        .subscribe((m) => this.reviewActivity.set(m));
      this.state = 'loaded';
    });
  }

  /** 新增空分类:乐观更新信号,后端 PUT 失败则回读后端真源纠正。 */
  addCategory(name: string): void {
    const n = name.trim();
    if (!n) return;
    if (!this.customCategories().some((x) => x.toLowerCase() === n.toLowerCase())) {
      this.customCategories.update((list) => [...list, n]);
    }
    this.api.put(`/api/knowledge/custom-categories/${encodeURIComponent(n)}`)
      .subscribe({ error: () => this.reloadCategories() });
  }

  /** 删除空分类:乐观移除,后端 DELETE 失败则回读纠正。 */
  removeCategory(name: string): void {
    this.customCategories.update((list) =>
      list.filter((x) => x.toLowerCase() !== name.toLowerCase()));
    this.api.delete(`/api/knowledge/custom-categories/${encodeURIComponent(name)}`)
      .subscribe({ error: () => this.reloadCategories() });
  }

  /** 空分类改名(= 删旧 + 加新;有条目的分类改名仍走既有 topics/rename,不经此处)。 */
  renameCategory(oldName: string, newName: string): void {
    this.customCategories.update((list) => {
      const rest = list.filter((x) => x.toLowerCase() !== oldName.toLowerCase());
      // 合并改名(目标已存在)时不重复加同名行
      return rest.some((x) => x.toLowerCase() === newName.toLowerCase())
        ? rest : [...rest, newName];
    });
    this.api.delete(`/api/knowledge/custom-categories/${encodeURIComponent(oldName)}`)
      .pipe(
        catchError(() => of(null)),
        switchMap(() => this.api.put(
          `/api/knowledge/custom-categories/${encodeURIComponent(newName)}`)),
      )
      .subscribe({ error: () => this.reloadCategories() });
  }

  /**
   * 记录一次复习(grade 成功后调用):日期键按本地日历口径现算,信号先
   * 乐观 +1,后端 record 累加为准;失败则回读纠正。
   */
  recordReview(): void {
    const key = localDateKey(new Date());
    this.reviewActivity.update((m) => ({ ...m, [key]: (m[key] ?? 0) + 1 }));
    this.api.post<ReviewActivityDto>('/api/knowledge/review-activity/record', { date: key })
      .subscribe({ error: () => this.reloadActivity() });
  }

  // ---------- 内部 ----------

  private importCategoriesIfNeeded(serverNames: string[]): Observable<string[]> {
    const local = this.readLocalCategories();
    if (!shouldImport(this.isImported(TechStackLocalService.CATEGORIES_IMPORTED_KEY), local.length)) {
      return of(serverNames);
    }
    return this.api.post<ImportResult>('/api/knowledge/custom-categories/import', { names: local }).pipe(
      tap(() => this.markImported(TechStackLocalService.CATEGORIES_IMPORTED_KEY)),
      switchMap(() => this.api.get<CustomCategoryDto[]>('/api/knowledge/custom-categories')),
      map((rows) => (rows ?? []).map((c) => c.name)),
      // 导入失败:不打标(下次加载重试),本次先用后端已有数据
      catchError(() => of(serverNames)),
    );
  }

  private importActivityIfNeeded(serverMap: ReviewActivityMap): Observable<ReviewActivityMap> {
    const local = this.readLocalActivity();
    if (!shouldImport(this.isImported(TechStackLocalService.ACTIVITY_IMPORTED_KEY),
      Object.keys(local).length)) {
      return of(serverMap);
    }
    return this.api.post<ImportResult>('/api/knowledge/review-activity/import', {
      entries: toActivityEntries(local),
    }).pipe(
      tap(() => this.markImported(TechStackLocalService.ACTIVITY_IMPORTED_KEY)),
      switchMap(() => this.api.get<ReviewActivityDto[]>('/api/knowledge/review-activity')),
      map((rows) => toActivityMap(rows ?? [])),
      catchError(() => of(serverMap)),
    );
  }

  private reloadCategories(): void {
    this.api.get<CustomCategoryDto[]>('/api/knowledge/custom-categories')
      .pipe(catchError(() => of([] as CustomCategoryDto[])))
      .subscribe((rows) => this.customCategories.set((rows ?? []).map((c) => c.name)));
  }

  private reloadActivity(): void {
    this.api.get<ReviewActivityDto[]>('/api/knowledge/review-activity')
      .pipe(catchError(() => of([] as ReviewActivityDto[])))
      .subscribe((rows) => this.reviewActivity.set(toActivityMap(rows ?? [])));
  }

  private readLocalCategories(): string[] {
    try { return parseCustomCategories(localStorage.getItem(TechStackLocalService.CATEGORIES_KEY)); }
    catch { return []; }
  }

  private readLocalActivity(): ReviewActivityMap {
    try { return parseReviewActivity(localStorage.getItem(TechStackLocalService.ACTIVITY_KEY)); }
    catch { return {}; }
  }

  private isImported(key: string): boolean {
    try { return localStorage.getItem(key) === '1'; } catch { return false; }
  }

  private markImported(key: string): void {
    try { localStorage.setItem(key, '1'); } catch { /* 忽略:后端导入幂等,丢标只是下次多一次空导入 */ }
  }
}
