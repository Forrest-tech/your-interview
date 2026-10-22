import { Injectable, inject, signal } from '@angular/core';
import { Observable, catchError, forkJoin, map, of, switchMap, tap, throwError } from 'rxjs';

import { ApiClient } from './api-client';
import { AuthService } from '../auth/auth.service';
import {
  CustomCategoryDto, EnsureState, ReviewActivityDto, ReviewActivityMap,
  localDateKey, parseCustomCategories, parseReviewActivity, shouldAttemptImport,
  shouldEnsureLoad, toActivityEntries, toActivityMap, validateCategoryName,
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
 *
 * 票82 修正(一次性闩锁三病):
 *   - **失败不闩锁**:后端不可达/导入失败后状态回 idle,下次进页面自动
 *     重试(旧实现失败也置 loaded,同一 SPA 会话内导入永不重试,界面还
 *     用本地回落数据装作正常 —— 静默丢导入机会);
 *   - **按账号闩锁**:loaded 只对加载时的用户有效;登出后换账号在同一
 *     SPA 会话登录(根单例不重建)会自动重载并导入新用户的旧数据
 *     (旧实现直接 return,新用户看到的是上一账号的残留信号数据);
 *   - **未登录不触发**:无登录用户时 ensureLoaded 直接 no-op,不烧闩锁。
 */
@Injectable({ providedIn: 'root' })
export class TechStackLocalService {
  private readonly api = inject(ApiClient);
  private readonly auth = inject(AuthService);

  private static readonly CATEGORIES_KEY = 'ts.customCategories';
  private static readonly ACTIVITY_KEY = 'ts.reviewActivity';
  private static readonly CATEGORIES_IMPORTED_KEY = 'ts.localData.imported.categories';
  private static readonly ACTIVITY_IMPORTED_KEY = 'ts.localData.imported.reviewActivity';

  /** 全应用唯一一份自定义分类状态(名字列表,录入序)。 */
  readonly customCategories = signal<string[]>([]);
  /** 全应用唯一一份复习活动状态(本地日期串 → 次数)。 */
  readonly reviewActivity = signal<ReviewActivityMap>({});

  private state: EnsureState = 'idle';
  /** state==='loaded' 时对应的用户(票82:换账号必须重载)。 */
  private loadedUserId: string | null = null;

  /**
   * 首次调用:读后端两类数据 → (未导入过且本地有旧数据时)一次性导入 → 以后端为准刷新。
   * 票82:是否发起由 shouldEnsureLoad 判定(未登录/在飞/已为本用户加载 → 跳过);
   * 本轮只有在「两类都完整结束(导入成功或无需导入)」时才闩锁 loaded,
   * 任一失败回 idle —— 下次进页面重试,旧键与标记原样保留。
   */
  ensureLoaded(): void {
    const uid = this.auth.user()?.id ?? null;
    if (!shouldEnsureLoad(this.state, this.loadedUserId, uid)) return;
    this.state = 'loading';
    const runUserId = uid;

    forkJoin({
      cats: this.api.get<CustomCategoryDto[]>('/api/knowledge/custom-categories')
        .pipe(catchError(() => of(null))),
      acts: this.api.get<ReviewActivityDto[]>('/api/knowledge/review-activity')
        .pipe(catchError(() => of(null))),
    }).subscribe(({ cats, acts }) => {
      if (cats === null || acts === null) {
        // 后端不可达:回落本地旧数据展示,但不闩锁(下次进页面重试导入)
        this.customCategories.set(this.readLocalCategories());
        this.reviewActivity.set(this.readLocalActivity());
        this.settle(runUserId, false);
        return;
      }
      forkJoin({
        catsRes: this.importCategoriesIfNeeded((cats ?? []).map((c) => c.name), runUserId !== null),
        actRes: this.importActivityIfNeeded(toActivityMap(acts ?? []), runUserId !== null),
      }).subscribe(({ catsRes, actRes }) => {
        this.customCategories.set(catsRes.names);
        this.reviewActivity.set(actRes.map);
        // 部分成功同理:失败的那类下次只重试它自己(成功类已打标不再重发)
        this.settle(runUserId, catsRes.complete && actRes.complete);
      });
    });
  }

  /** 本轮结束结算:完整且仍是同一用户才闩锁 loaded,否则回 idle 待重试。 */
  private settle(runUserId: string | null, complete: boolean): void {
    if (complete && runUserId !== null && (this.auth.user()?.id ?? null) === runUserId) {
      this.loadedUserId = runUserId;
      this.state = 'loaded';
    } else {
      this.state = 'idle';
    }
  }

  /**
   * 新增空分类:乐观更新信号,后端 PUT 成功才算数。
   * 票83:返回可订阅结果 —— 失败时回读后端真源纠正乐观信号 **并把错误
   * 透传给调用方显示**(旧实现 subscribe 只 reload、吞掉错误,分类管理器
   * 又无条件把名字加进弹窗列表 → 假成功、刷新即失,D4b)。
   * 提交前先跑纯校验 validateCategoryName(与后端 100 上限同口径):
   * 不合法直接以带 validation 的错误拒,不发请求。
   */
  addCategory(name: string): Observable<void> {
    const n = name.trim();
    const v = validateCategoryName(n);
    if (!v.valid) {
      return throwError(() => Object.assign(
        new Error(v.reason === 'tooLong' ? 'Category name too long' : 'Category name is empty'),
        { validation: v }));
    }
    if (!this.customCategories().some((x) => x.toLowerCase() === n.toLowerCase())) {
      this.customCategories.update((list) => [...list, n]);
    }
    return this.api.put<void>(`/api/knowledge/custom-categories/${encodeURIComponent(n)}`)
      .pipe(catchError((e: unknown) => {
        this.reloadCategories();
        return throwError(() => e);
      }));
  }

  /** 删除空分类:乐观移除,后端 DELETE 失败则回读纠正。 */
  removeCategory(name: string): void {
    this.customCategories.update((list) =>
      list.filter((x) => x.toLowerCase() !== name.toLowerCase()));
    this.api.delete(`/api/knowledge/custom-categories/${encodeURIComponent(name)}`)
      .subscribe({ error: () => this.reloadCategories() });
  }

  /**
   * 空分类改名(有条目的分类改名仍走既有 topics/rename,不经此处)。
   * 票83:与 addCategory 同口径 —— 先纯校验新名;请求改 **PUT 新名先行、
   * 成功后再 DELETE 旧名**(旧顺序 DELETE 先行、PUT 失败时旧名已删 =
   * 改名失败顺带丢数据);失败回读纠正乐观信号并透传错误给调用方显示,
   * 不再吞错假成功。
   */
  renameCategory(oldName: string, newName: string): Observable<void> {
    const n = newName.trim();
    const v = validateCategoryName(n);
    if (!v.valid) {
      return throwError(() => Object.assign(
        new Error(v.reason === 'tooLong' ? 'Category name too long' : 'Category name is empty'),
        { validation: v }));
    }
    // 同名(大小写不敏感)= 无操作:不能走 PUT 后 DELETE,会把自己删掉
    if (oldName.toLowerCase() === n.toLowerCase()) return of(undefined);
    this.customCategories.update((list) => {
      const rest = list.filter((x) => x.toLowerCase() !== oldName.toLowerCase());
      // 合并改名(目标已存在)时不重复加同名行
      return rest.some((x) => x.toLowerCase() === n.toLowerCase())
        ? rest : [...rest, n];
    });
    return this.api.put<void>(`/api/knowledge/custom-categories/${encodeURIComponent(n)}`)
      .pipe(
        switchMap(() => this.api.delete<void>(
          `/api/knowledge/custom-categories/${encodeURIComponent(oldName)}`)),
        catchError((e: unknown) => {
          this.reloadCategories();
          return throwError(() => e);
        }),
      );
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

  private importCategoriesIfNeeded(
    serverNames: string[], authenticated: boolean
  ): Observable<{ names: string[]; complete: boolean }> {
    const local = this.readLocalCategories();
    if (!shouldAttemptImport(authenticated,
      this.isImported(TechStackLocalService.CATEGORIES_IMPORTED_KEY), local.length)) {
      return of({ names: serverNames, complete: true });
    }
    let landed = false;
    return this.api.post<ImportResult>('/api/knowledge/custom-categories/import', { names: local }).pipe(
      // 落服务端才打标;打标后即使回读失败,本类也算完成(下次加载自然可见)
      tap(() => {
        landed = true;
        this.markImported(TechStackLocalService.CATEGORIES_IMPORTED_KEY);
      }),
      switchMap(() => this.api.get<CustomCategoryDto[]>('/api/knowledge/custom-categories')),
      map((rows) => ({ names: (rows ?? []).map((c) => c.name), complete: true })),
      // 导入失败:不打标、complete=false(上层不闩锁,下次加载重试),本次先用后端已有数据
      catchError(() => of({ names: serverNames, complete: landed })),
    );
  }

  private importActivityIfNeeded(
    serverMap: ReviewActivityMap, authenticated: boolean
  ): Observable<{ map: ReviewActivityMap; complete: boolean }> {
    const local = this.readLocalActivity();
    if (!shouldAttemptImport(authenticated,
      this.isImported(TechStackLocalService.ACTIVITY_IMPORTED_KEY),
      Object.keys(local).length)) {
      return of({ map: serverMap, complete: true });
    }
    let landed = false;
    return this.api.post<ImportResult>('/api/knowledge/review-activity/import', {
      entries: toActivityEntries(local),
    }).pipe(
      tap(() => {
        landed = true;
        this.markImported(TechStackLocalService.ACTIVITY_IMPORTED_KEY);
      }),
      switchMap(() => this.api.get<ReviewActivityDto[]>('/api/knowledge/review-activity')),
      map((rows) => ({ map: toActivityMap(rows ?? []), complete: true })),
      catchError(() => of({ map: serverMap, complete: landed })),
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
