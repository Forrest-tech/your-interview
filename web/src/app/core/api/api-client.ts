import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable, catchError, throwError } from 'rxjs';
import { environment } from '../../../environments/environment';

/**
 * 统一的 HTTP 访问层。
 *
 * 为什么不让组件直接注入 HttpClient:
 *   1. 基址、错误处理、查询参数拼装只写一遍,改一处全局生效
 *   2. 组件里不出现 URL 字符串 —— 后端改路径时前端只需改这里
 *   3. 空值参数统一过滤(否则会发出 ?search=null 这种脏请求)
 */
@Injectable({ providedIn: 'root' })
export class ApiClient {
  private readonly http = inject(HttpClient);
  private readonly base = environment.apiBaseUrl;

  get<T>(path: string, params?: Record<string, unknown>): Observable<T> {
    return this.http.get<T>(this.url(path), { params: toParams(params) })
      .pipe(catchError(this.rethrow));
  }

  post<T>(path: string, body?: unknown): Observable<T> {
    return this.http.post<T>(this.url(path), body ?? {})
      .pipe(catchError(this.rethrow));
  }

  put<T>(path: string, body?: unknown): Observable<T> {
    return this.http.put<T>(this.url(path), body ?? {})
      .pipe(catchError(this.rethrow));
  }

  patch<T>(path: string, body?: unknown): Observable<T> {
    return this.http.patch<T>(this.url(path), body ?? {})
      .pipe(catchError(this.rethrow));
  }

  delete<T>(path: string): Observable<T> {
    return this.http.delete<T>(this.url(path))
      .pipe(catchError(this.rethrow));
  }

  /** 上传文件(录音等)。用 FormData,不设 Content-Type —— 让浏览器自动带 boundary。 */
  upload<T>(path: string, file: File, extra?: Record<string, string>): Observable<T> {
    const fd = new FormData();
    fd.append('file', file, file.name);
    for (const [k, v] of Object.entries(extra ?? {})) fd.append(k, v);
    return this.http.post<T>(this.url(path), fd)
      .pipe(catchError(this.rethrow));
  }

  /**
   * POST 并拿回二进制(示范朗读的 MP3 合成结果)。
   *
   * 为什么单独一个方法:HttpClient 默认按 JSON 解析响应,
   * 音频流那样做会直接报解析错。这里显式声明 responseType: 'blob'。
   */
  postBlob(path: string, body?: unknown): Observable<Blob> {
    return this.http.post(this.url(path), body ?? {}, { responseType: 'blob' })
      .pipe(catchError(this.rethrow));
  }

  /**
   * GET 并拿回二进制(录音回放)。
   *
   * ★ 第二十六轮:这个方法是"401 修复"的关键。
   *   录音回放端点 [Authorize] 保护,而 <audio src="/api/..."> 这种用法
   *   **由浏览器直接发请求,不会带 Authorization 头** → 必 401。
   *   正确做法:走 HttpClient(拦截器会自动附 Bearer,并享受 401 自动刷新),
   *   responseType:'blob' 拿回二进制,再用 URL.createObjectURL 交给 <audio>。
   */
  getBlob(path: string): Observable<Blob> {
    return this.http.get(this.url(path), { responseType: 'blob' })
      .pipe(catchError(this.rethrow));
  }

  /**
   * 用已有 FormData 做 POST。
   * 与 upload() 的区别:upload 接收单个 File,这里适合**多字段**表单
   * (录音上传要同时带 durationSeconds / contentType / language)。
   */
  postForm<T>(path: string, form: FormData): Observable<T> {
    return this.http.post<T>(this.url(path), form)
      .pipe(catchError(this.rethrow));
  }

  private url(path: string): string {
    return path.startsWith('http') ? path : `${this.base}${path.startsWith('/') ? '' : '/'}${path}`;
  }

  /** 把后端的 ProblemDetails 转成可读中文提示,同时保留原始错误供调用方判断状态码。 */
  private rethrow = (err: unknown): Observable<never> => {
    const e = err as { status?: number; error?: { detail?: string; title?: string; code?: string } };
    const detail = e?.error?.detail || e?.error?.title;

    let msg = detail ?? '请求失败';
    if (e?.status === 0) msg = '无法连接后端服务,请确认服务已启动';
    else if (e?.status === 401) msg = '登录已过期,请重新登录';
    else if (e?.status === 403) msg = '没有权限执行此操作';
    else if (e?.status === 404) msg = '数据不存在';
    else if (e?.status === 409) msg = detail ?? '当前状态不允许此操作';

    return throwError(() => Object.assign(new Error(msg), {
      status: e?.status, code: e?.error?.code, original: err
    }));
  };
}

function toParams(params?: Record<string, unknown>): HttpParams {
  let p = new HttpParams();
  for (const [k, v] of Object.entries(params ?? {})) {
    // null/undefined/空串不发送 —— 避免后端把 "null" 当字符串解析
    if (v === null || v === undefined || v === '') continue;
    p = p.set(k, String(v));
  }
  return p;
}
