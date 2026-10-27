import { Injectable } from '@angular/core';
import { environment } from '../../../environments/environment';

/**
 * 票241 Logto 前端接入（手写 PKCE，不引新依赖）。
 *
 * 为什么手写：Logto 的 SPA 接入就是标准 OIDC + PKCE，市面上的 logto-js SDK
 * 只是把下面这几十行包了一层；本平台只用 authorize / token / refresh 三个端点，
 * 手写反而让每一步（state 校验、code_challenge、resource 参数）都看得见、测得到。
 *
 * 安全要点：
 *   · verifier 只放 sessionStorage（页签级，关页即焚），绝不放 localStorage；
 *   · 回调必验 state（防 CSRF），state/verifier 消费后立即删除，二次回调直接拒绝；
 *   · clientId 是公开值（SPA 本来就暴露），真正的密钥一律不在前端；
 *   · token 换取走 Logto 的 /oidc/token 直连（浏览器 → Logto），不经过网关。
 */

export interface LogtoAuthConfig {
  endpoint: string;
  clientId: string;
  redirectUri: string;
  audience: string;
  /**
   * 票251 B7：Console 里 API resource 上定义的业务 scope，必须写 Permissions.* 的
   * 点值（例如 interviews.write），绝不能写 C# 常量名（InterviewsWrite）——后端
   * claims 门按点值精确匹配，常量名匹配不上就是零权限。归属见 SETUP.md §3：
   * scope 定义归 Console，点值清单归环境配置，本文件只负责原样带上。
   */
  apiScopes: string[];
}

export interface LogtoTokenResponse {
  access_token: string;
  refresh_token?: string;
  expires_in: number;
  token_type: string;
}

const VERIFIER_KEY = 'yi.logto.verifier';
const STATE_KEY = 'yi.logto.state';
const RETURN_KEY = 'yi.logto.returnUrl';

const VERIFIER_CHARS =
  'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~';

/** 环境配置 → Logto 配置；未启用或关键字段缺失时返回 null（调用方据此隐藏入口）。 */
export function logtoConfig(): LogtoAuthConfig | null {
  const l = environment.logto;
  if (!l?.enabled || !l.endpoint || !l.clientId) return null;
  return {
    endpoint: l.endpoint.replace(/\/$/, ''),
    clientId: l.clientId,
    redirectUri: l.redirectUri || `${window.location.origin}/auth/logto/callback`,
    audience: l.audience || '',
    apiScopes: l.apiScopes ?? []
  };
}

/** PKCE verifier：按 RFC 7636，43–128 位 URL 安全字符；随机源由调用方注入（可测）。 */
export function generateVerifier(randomBytes: Uint8Array): string {
  let out = '';
  for (let i = 0; i < randomBytes.length; i++) {
    out += VERIFIER_CHARS[randomBytes[i] % VERIFIER_CHARS.length];
  }
  return out;
}

/** base64url（RFC 4648 §5）：+ → -、/ → _、去尾部 =。 */
export function base64UrlEncode(buf: ArrayBuffer): string {
  const bytes = new Uint8Array(buf);
  let s = '';
  for (const b of bytes) s += String.fromCharCode(b);
  return btoa(s).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

/** S256 code_challenge = base64url(SHA-256(verifier))。 */
export async function codeChallenge(verifier: string): Promise<string> {
  const digest = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(verifier));
  return base64UrlEncode(digest);
}

/**
 * 授权 URL。first_screen=register 直接打开 Logto 的注册表单（官方 first screen 参数），
 * 不是前端自己画注册页；resource 带上 API resource identifier 时，换回的 access token
 * 才带业务 scope（claims 门的权限载体就靠它，空 audience 时不发该参数）。
 * 票251 B7：scope 必须带上 apiScopes —— 授权时不请求的 scope，换到的 token 里
 * 就是空串，claims 门给零权限，批过的用户也会被当 pending 打回登录页。
 */
export function buildAuthorizeUrl(
  cfg: LogtoAuthConfig,
  opts: { state: string; codeChallenge: string; firstScreen: 'sign_in' | 'register' }
): string {
  const scope = ['openid profile email offline_access', ...(cfg.apiScopes ?? [])].join(' ');
  const q = new URLSearchParams({
    client_id: cfg.clientId,
    redirect_uri: cfg.redirectUri,
    response_type: 'code',
    scope,
    code_challenge: opts.codeChallenge,
    code_challenge_method: 'S256',
    state: opts.state,
    first_screen: opts.firstScreen
  });
  if (cfg.audience) q.set('resource', cfg.audience);
  return `${cfg.endpoint}/oidc/auth?${q.toString()}`;
}

/**
 * token 端点请求体（code 换取与 refresh 共用形状，grant_type 区分）。
 * 票251 B6：按 RFC 8707，换 token 时必须重复 resource —— 不带它时 Logto 只发
 * 默认受众的不透明 token（非 JWT），后端无法 JWT 校验，/me 直接 401。
 * audience 非空时两 grant 都带 resource；空 audience 时不发（与 authorize 一致）。
 */
export function buildTokenBody(
  cfg: LogtoAuthConfig,
  grant: { grant_type: 'authorization_code'; code: string; codeVerifier: string } |
         { grant_type: 'refresh_token'; refreshToken: string }
): URLSearchParams {
  const body = new URLSearchParams({
    grant_type: grant.grant_type,
    client_id: cfg.clientId,
    redirect_uri: cfg.redirectUri
  });
  if (grant.grant_type === 'authorization_code') {
    body.set('code', grant.code);
    body.set('code_verifier', grant.codeVerifier);
  } else {
    body.set('refresh_token', grant.refreshToken);
  }
  if (cfg.audience) body.set('resource', cfg.audience);
  return body;
}

@Injectable({ providedIn: 'root' })
export class LogtoService {
  config(): LogtoAuthConfig | null {
    return logtoConfig();
  }

  isEnabled(): boolean {
    return this.config() !== null;
  }

  /** 跳 Logto 授权页（登录或注册表单由 firstScreen 决定）；state/verifier 存 sessionStorage。 */
  async beginLogin(firstScreen: 'sign_in' | 'register', returnUrl = '/dashboard'): Promise<void> {
    const cfg = this.config();
    if (!cfg) return;
    const rand = new Uint8Array(64);
    crypto.getRandomValues(rand);
    const verifier = generateVerifier(rand);
    const stateBytes = new Uint8Array(32);
    crypto.getRandomValues(stateBytes);
    const state = generateVerifier(stateBytes);
    const challenge = await codeChallenge(verifier);
    sessionStorage.setItem(VERIFIER_KEY, verifier);
    sessionStorage.setItem(STATE_KEY, state);
    sessionStorage.setItem(RETURN_KEY, returnUrl);
    window.location.href = buildAuthorizeUrl(cfg, { state, codeChallenge: challenge, firstScreen });
  }

  /** 回调页消费的 returnUrl（登录后回到的页面；非法值一律回 /dashboard）。 */
  consumeReturnUrl(): string {
    const u = sessionStorage.getItem(RETURN_KEY) ?? '/dashboard';
    sessionStorage.removeItem(RETURN_KEY);
    return u.startsWith('/') && !u.startsWith('//') ? u : '/dashboard';
  }

  /** 回调：验 state → code 换 token。state/verifier 无论成败都一次性消费。 */
  async handleCallback(code: string, state: string): Promise<LogtoTokenResponse> {
    const cfg = this.config();
    if (!cfg) throw new Error('Logto 未配置');
    const savedState = sessionStorage.getItem(STATE_KEY);
    const verifier = sessionStorage.getItem(VERIFIER_KEY);
    sessionStorage.removeItem(STATE_KEY);
    sessionStorage.removeItem(VERIFIER_KEY);
    if (!savedState || savedState !== state) throw new Error('state 不匹配，可能是过期回调，请重新发起登录');
    if (!verifier) throw new Error('缺少 PKCE verifier，请重新发起登录');
    const res = await fetch(`${cfg.endpoint}/oidc/token`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
      body: buildTokenBody(cfg, { grant_type: 'authorization_code', code, codeVerifier: verifier })
    });
    if (!res.ok) throw new Error(`Logto 换 token 失败（${res.status}）`);
    return res.json() as Promise<LogtoTokenResponse>;
  }

  /** Logto 刷新（收敛后旧刷新 410 时，前端走这里续 Logto 会话，不走旧 /api/auth/refresh）。 */
  async refreshLogto(refreshToken: string): Promise<LogtoTokenResponse> {
    const cfg = this.config();
    if (!cfg) throw new Error('Logto 未配置');
    const res = await fetch(`${cfg.endpoint}/oidc/token`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
      body: buildTokenBody(cfg, { grant_type: 'refresh_token', refreshToken })
    });
    if (!res.ok) throw new Error(`Logto 刷新失败（${res.status}）`);
    return res.json() as Promise<LogtoTokenResponse>;
  }

  /** 退出到 Logto end-session（清 Logto 侧会话后回登录页）；本地令牌由调用方先清。 */
  logoutRedirect(): void {
    const cfg = this.config();
    if (!cfg) return;
    window.location.href =
      `${cfg.endpoint}/oidc/session/end?post_logout_redirect_uri=${encodeURIComponent(window.location.origin + '/login')}`;
  }
}
