import { Component } from '@angular/core';
import { RouterOutlet } from '@angular/router';

/**
 * 应用根组件。
 * 它只做一件事:提供路由出口。所有页面结构(顶栏/侧栏)由 ShellComponent 负责,
 * 这样登录页可以不受后台布局影响(它不在 Shell 之下)。
 */
@Component({
  selector: 'app-root',
  standalone: true,
  imports: [RouterOutlet],
  template: '<router-outlet />'
})
export class AppComponent {}
