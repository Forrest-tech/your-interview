import { Component, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { Router } from '@angular/router';
import { MatCardModule } from '@angular/material/card';
import { MatIconModule } from '@angular/material/icon';
import { MatButtonModule } from '@angular/material/button';

/**
 * 403 页面。
 *
 * 单独做一页而不是弹个 toast:用户点错链接时,需要一个"停下来的地方"告诉他
 * 发生了什么、以及唯一正确的下一步(action 只给一个,避免选择困难)。
 */
@Component({
  selector: 'app-forbidden',
  standalone: true,
  imports: [CommonModule, MatCardModule, MatIconModule, MatButtonModule],
  templateUrl: './forbidden.component.html',
  styleUrl: './forbidden.component.scss'
})
export class ForbiddenComponent {
  private readonly router = inject(Router);

  goDashboard(): void {
    this.router.navigateByUrl('/dashboard');
  }
}
