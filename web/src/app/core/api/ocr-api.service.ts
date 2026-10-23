import { Injectable, inject } from '@angular/core';
import { ApiClient } from './api-client';

@Injectable({ providedIn: 'root' })
export class OcrApiService {
  private readonly api = inject(ApiClient);

  /** 共享 OCR 服务基址；未配置时回落网关 /api/ocr。 */
  get baseUrl(): string {
    return (globalThis as { __SHARED_OCR_BASE_URL__?: string }).__SHARED_OCR_BASE_URL__ ?? '/api/ocr';
  }
}
