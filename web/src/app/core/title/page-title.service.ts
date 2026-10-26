import { Injectable, effect, inject } from '@angular/core';
import { Title } from '@angular/platform-browser';
import { NavigationEnd, Router } from '@angular/router';
import { filter } from 'rxjs';
import { I18nService } from '../i18n/i18n.service';
import { formatPageTitle, titleKeyForUrl } from './page-title.helpers';

/**
 * 页签标题服务(REQ-12):根组件注入一次即全站生效。
 *
 * - 订阅 NavigationEnd,用 urlAfterRedirects(重定向后的真实路径)查模块键;
 * - 语言是 signal:effect 订阅 lang,切语言不换路由也立即刷新标题;
 * - 只写 document.title,不改路由、不拦截导航。
 */
@Injectable({ providedIn: 'root' })
export class PageTitleService {
  private readonly router = inject(Router);
  private readonly i18n = inject(I18nService);
  private readonly title = inject(Title);

  private currentKey: string | null = null;

  constructor() {
    this.router.events
      .pipe(filter((e): e is NavigationEnd => e instanceof NavigationEnd))
      .subscribe((e) => {
        this.currentKey = titleKeyForUrl(e.urlAfterRedirects);
        this.apply();
      });
    effect(() => {
      this.i18n.lang();
      this.apply();
    });
  }

  private apply(): void {
    const moduleName = this.currentKey ? this.i18n.t(this.currentKey) : null;
    this.title.setTitle(formatPageTitle(moduleName));
  }
}
