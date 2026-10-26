import { Component, inject } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { PageTitleService } from './core/title/page-title.service';

/**
 * 应用根组件。
 * 它只做一件事:提供路由出口。所有页面结构(顶栏/侧栏)由 ShellComponent 负责,
 * 这样登录页可以不受后台布局影响(它不在 Shell 之下)。
 * 另注入 PageTitleService 启动页签标题(REQ-12):登录页与 Shell 子树都经根组件,
 * 一处接线全站覆盖。
 */
@Component({
  selector: 'app-root',
  standalone: true,
  imports: [RouterOutlet],
  template: '<router-outlet />'
})
export class AppComponent {
  private readonly pageTitle = inject(PageTitleService);
}
