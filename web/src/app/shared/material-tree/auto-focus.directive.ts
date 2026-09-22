import { AfterViewInit, Directive, ElementRef, inject } from '@angular/core';

/**
 * 插入即聚焦并全选 —— 给树的"重命名输入框"用。
 *
 * 为什么需要(Forrest 2026-09-20 报"新建好后直接跳出了"):
 *   新建节点后应停在改名输入框上等用户输入,而不是新建完就完事。
 *   autofocus 属性对动态插入的元素在部分浏览器不生效,这里用指令显式
 *   focus() + select(),新建/重命名每次插入输入框都会触发一次。
 */
@Directive({
  selector: '[appAutoFocus]',
  standalone: true
})
export class AutoFocusDirective implements AfterViewInit {
  private readonly el = inject(ElementRef);

  ngAfterViewInit(): void {
    const input = this.el.nativeElement as HTMLInputElement;
    input.focus();
    input.select();          // 默认名全选,直接打字即可覆盖
  }
}
