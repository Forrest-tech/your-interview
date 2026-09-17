import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterLink } from '@angular/router';
import { MatCardModule } from '@angular/material/card';
import { MatIconModule } from '@angular/material/icon';
import { MatButtonModule } from '@angular/material/button';
import { MatChipsModule } from '@angular/material/chips';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { catchError, of } from 'rxjs';
import { ApiClient } from '../../core/api/api-client';
import { AuthService } from '../../core/auth/auth.service';
import { AuthUser } from '../../core/models/api.models';

import { MatTooltipModule } from '@angular/material/tooltip';
/** 一个权限分组 —— 权限字符串本身形如 "admin.users.read",按前缀分组比平铺一长串更好读。 */
interface PermissionGroup {
  prefix: string;
  label: string;
  permissions: string[];
}

/**
 * 个人资料页。
 *
 * 数据来源刻意以 GET /api/auth/me 为准而不是直接读本地缓存的 AuthUser:
 * 本地缓存可能是几天前登录时写下的,权限被管理员调整后并不知情;
 * 每次进页面重新拉一次,保证"我看到的就是后端当前认的"。
 * displayName 等展示字段在接口失败时回退到 AuthService 的缓存,避免整页空白。
 */
@Component({
  selector: 'app-profile',
  standalone: true,
  imports: [
    CommonModule, RouterLink, MatCardModule, MatIconModule,
    MatButtonModule, MatChipsModule, MatProgressBarModule, MatTooltipModule
  ],
  templateUrl: './profile.component.html',
  styleUrl: './profile.component.scss'
})
export class ProfileComponent implements OnInit {
  private readonly api = inject(ApiClient);
  readonly auth = inject(AuthService);

  readonly loading = signal(true);
  readonly error = signal<string | null>(null);
  readonly me = signal<AuthUser | null>(null);

  /** 接口失败时用本地缓存兜底 —— 用户至少还能看到自己是谁。 */
  readonly user = computed<AuthUser | null>(() => this.me() ?? this.auth.user());

  /**
   * 是否处于"接口失败"态。三态判定统一走这几个 computed,
   * 避免模板里散落 error() && !loading() 之类的组合条件写错。
   */
  readonly hasError = computed(() => !this.loading() && this.error() !== null);

  /** 接口失败且连本地缓存都没有 —— 无任何可展示数据,只能引导重新登录。 */
  readonly hasNothingToShow = computed(() => this.hasError() && this.user() === null);

  /**
   * 是否展示"正在用缓存兜底"的降级提示。
   * 失败 + 有缓存 = 页面还能看,但要明确告知不是最新的。
   * 失败 + 无缓存 = 走 hasNothingToShow 的兜底块,不再叠加这个提示。
   */
  readonly usedFallback = computed(() => this.hasError() && this.user() !== null);

  /** 成功加载且确实没有权限 —— 真正的空态,与"加载失败"不是一回事。 */
  readonly isEmptyPermissions = computed(
    () => !this.loading() && !this.hasError() && this.permissionCount() === 0
  );

  /** 成功加载且拿到了账号 —— 才渲染身份卡与权限清单。 */
  readonly hasUser = computed(() => !this.loading() && this.user() !== null);

  readonly displayName = computed(() =>
    this.user()?.displayName || this.auth.displayName() || '未命名用户'
  );

  readonly initials = computed(() => {
    const name = this.displayName().trim();
    const source = name.length > 0 ? name : (this.user()?.email ?? '?');
    return source.slice(0, 1).toUpperCase();
  });

  readonly permissionCount = computed(() => this.user()?.permissions?.length ?? 0);

  /**
   * 权限分组:按第一个 "." 之前的前缀切分。
   * 无前缀的散装权限统一归到"其他",不让它们消失。
   */
  readonly permissionGroups = computed<PermissionGroup[]>(() => {
    const list = this.user()?.permissions ?? [];
    const map = new Map<string, string[]>();

    for (const p of list) {
      const dot = p.indexOf('.');
      const prefix = dot > 0 ? p.slice(0, dot) : 'other';
      const bucket = map.get(prefix);
      if (bucket) bucket.push(p);
      else map.set(prefix, [p]);
    }

    return [...map.entries()]
      .map(([prefix, permissions]) => ({
        prefix,
        label: this.groupLabel(prefix),
        permissions: permissions.slice().sort()
      }))
      .sort((a, b) => a.prefix.localeCompare(b.prefix));
  });

  ngOnInit(): void {
    this.load();
  }

  load(): void {
    this.loading.set(true);
    this.error.set(null);

    this.api.get<AuthUser>('/api/auth/me').subscribe({
      next: (u) => {
        this.me.set(u);
        this.loading.set(false);
      },
      // 失败不整页报错:本地缓存的 user 仍可展示,只在顶部提示"不是最新的"
      error: (e: Error) => {
        this.error.set(e.message || '网络异常,请稍后重试');
        this.loading.set(false);
      }
    });
  }

  /** 权限前缀 → 中文分组名。目的只是让分组有个人话标题,未收录的前缀直接显示原文。 */
  private groupLabel(prefix: string): string {
    const known: Record<string, string> = {
      admin: '管理后台',
      jobs: '投递跟踪',
      interviews: '实战机经',
      knowledge: '技术栈',
      mock: 'AI 模拟',
      analytics: '数据分析',
      profile: '个人资料',
      other: '其他权限'
    };
    return known[prefix] ?? prefix;
  }
}
