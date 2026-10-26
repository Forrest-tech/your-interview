import { Injectable, inject, signal } from '@angular/core';
import { Observable, catchError, map, of, switchMap, tap } from 'rxjs';

import { TrackerApi, ApplicationQuestionDto } from './tracker-api.service';
import { AuthService } from '../auth/auth.service';
import {
  EnsureState, mergeFavoriteIds, parseStringArray, shouldAttemptImport, shouldEnsureLoad,
} from './tracker-preferences.helpers';

/**
 * Tracker 收藏 + 按投递自备问题的**单一读写路径**(COD-10a/票14)。
 *
 * 此前这两类数据只有 localStorage,且收藏在列表组件与详情弹窗各有一套
 * 独立实现读写同一 key(yi-tracker-favorites),状态可分叉。现在:
 *   - 真源 = Jobs 后端(application_favorites / application_questions 两表);
 *   - 列表与详情都只经本服务读写 —— 收藏信号是全应用一份,详情里切换
 *     收藏,列表立即同步,不再有第二份状态;
 *   - 首次加载时把本地已有数据一次性导入后端(迁移式导入,见下)。
 *
 * 导入与本地键的处置(不丢数据原则):
 *   - 导入成功后打标(yi-tracker-favorites-imported / yi-tracker-questions-imported-{id}),
 *     后续加载不再重复发起导入;后端导入本身也幂等(收藏按 (UserId, ApplicationId)
 *     去重、自备问题按正文去重),双保险 —— 即使标记丢失,重复导入也不会产生重复行;
 *   - 本地旧键**保留不清除**:后端数据成为真源后它们不再被读取,留作用户本地的
 *     最后一份兜底副本;清除没有任何收益,保留零风险(展示只读后端)。
 *
 * 票82 同型修正(与 TechStackLocalService 同口径):一次性闩锁改为
 * 「失败不闩锁(回 idle 下次重试)+ 按账号闩锁(换账号重载)+ 未登录不触发」,
 * 旧实现失败也置 loaded 且不认账号,同一 SPA 会话内导入永不重试、
 * 换账号看到上一账号的收藏残留。
 */
@Injectable({ providedIn: 'root' })
export class TrackerPreferencesService {
  private readonly trackerApi = inject(TrackerApi);
  private readonly auth = inject(AuthService);

  private static readonly FAV_KEY = 'yi-tracker-favorites';
  private static readonly FAV_IMPORTED_KEY = 'yi-tracker-favorites-imported';
  private static readonly QUESTION_KEY_PREFIX = 'yi-tracker-questions-';
  private static readonly QUESTION_IMPORTED_PREFIX = 'yi-tracker-questions-imported-';

  /** 全应用唯一一份收藏状态(列表与详情共用)。 */
  readonly favorites = signal<Set<string>>(new Set());

  private favoritesState: EnsureState = 'idle';
  /** favoritesState==='loaded' 时对应的用户(票82:换账号必须重载)。 */
  private favoritesUserId: string | null = null;

  isFavorite(id: string): boolean {
    return this.favorites().has(id);
  }

  /**
   * 首次调用时:读后端收藏 → (未导入过且本地有旧数据时)一次性导入 → 以后端为准刷新。
   * 票82:发起由 shouldEnsureLoad 判定;本轮完整结束才闩锁 loaded,
   * 导入失败/后端不可达回 idle,下次进 Tracker 自动重试。
   */
  ensureFavoritesLoaded(): void {
    const uid = this.auth.user()?.id ?? null;
    if (!shouldEnsureLoad(this.favoritesState, this.favoritesUserId, uid)) return;
    this.favoritesState = 'loading';
    let complete = true;

    this.trackerApi.listFavorites().pipe(
      switchMap((rows) => {
        const serverIds = rows.map((r) => r.applicationId);
        const local = this.readLocalFavorites();
        if (!shouldAttemptImport(uid !== null,
          this.isImported(TrackerPreferencesService.FAV_IMPORTED_KEY), local.length)) {
          return of(serverIds);
        }
        return this.trackerApi.importFavorites(local).pipe(
          tap(() => this.markImported(TrackerPreferencesService.FAV_IMPORTED_KEY)),
          switchMap(() => this.trackerApi.listFavorites()),
          map((rows2) => rows2.map((r) => r.applicationId)),
          // 导入失败:不打标、不闩锁(下次加载重试),本次展示先用「后端 ∪ 本地」不丢数据
          catchError(() => {
            complete = false;
            return of(mergeFavoriteIds(serverIds, local));
          })
        );
      }),
      // 后端不可达:回落本地旧数据展示(不打标、不闩锁、不冒充已同步)
      catchError(() => {
        complete = false;
        return of(this.readLocalFavorites());
      })
    ).subscribe((ids) => {
      this.favorites.set(new Set(ids));
      if (complete && uid !== null && (this.auth.user()?.id ?? null) === uid) {
        this.favoritesUserId = uid;
        this.favoritesState = 'loaded';
      } else {
        this.favoritesState = 'idle';
      }
    });
  }

  /** 切换收藏:乐观更新共享信号,后端失败回滚。返回切换后是否为收藏。 */
  toggleFavorite(id: string): boolean {
    const adding = !this.favorites().has(id);
    const next = new Set(this.favorites());
    if (adding) next.add(id); else next.delete(id);
    this.favorites.set(next);

    const req = adding
      ? this.trackerApi.addFavorite(id)
      : this.trackerApi.removeFavorite(id);
    req.subscribe({
      error: () => {
        const back = new Set(this.favorites());
        if (adding) back.delete(id); else back.add(id);
        this.favorites.set(back);
      }
    });
    return adding;
  }

  /**
   * 读某条投递的自备问题(后端为准)。未导入过且本地有旧数据时先一次性导入再读回,
   * 调用方拿到的是导入后的完整列表(对账即以此列表条数为准)。
   */
  loadQuestions(applicationId: string): Observable<ApplicationQuestionDto[]> {
    const importedKey = TrackerPreferencesService.QUESTION_IMPORTED_PREFIX + applicationId;
    const local = this.readLocalQuestions(applicationId);
    // 票82:未登录不尝试导入(本方法无闩锁,失败本就下次打开重试,口径不变)
    if (!shouldAttemptImport(this.auth.user() !== null, this.isImported(importedKey), local.length)) {
      return this.trackerApi.listQuestions(applicationId);
    }
    return this.trackerApi.importQuestions(applicationId, local).pipe(
      tap(() => this.markImported(importedKey)),
      switchMap(() => this.trackerApi.listQuestions(applicationId)),
      // 导入失败:不打标(下次打开重试),本次仍读后端已有的部分
      catchError(() => this.trackerApi.listQuestions(applicationId))
    );
  }

  addQuestion(applicationId: string, text: string): Observable<{ id: string }> {
    return this.trackerApi.addQuestion(applicationId, text);
  }

  deleteQuestion(applicationId: string, questionId: string): Observable<void> {
    return this.trackerApi.deleteQuestion(applicationId, questionId);
  }

  // ---------- localStorage 旧数据读取(仅导入用,展示永不读这里) ----------

  private readLocalFavorites(): string[] {
    try {
      return parseStringArray(localStorage.getItem(TrackerPreferencesService.FAV_KEY));
    } catch { return []; }
  }

  private readLocalQuestions(applicationId: string): string[] {
    try {
      return parseStringArray(
        localStorage.getItem(TrackerPreferencesService.QUESTION_KEY_PREFIX + applicationId));
    } catch { return []; }
  }

  private isImported(key: string): boolean {
    try { return localStorage.getItem(key) === '1'; } catch { return false; }
  }

  private markImported(key: string): void {
    try { localStorage.setItem(key, '1'); } catch { /* 忽略:后端导入幂等,丢标只是下次多一次空导入 */ }
  }
}
