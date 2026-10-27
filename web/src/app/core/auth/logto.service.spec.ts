/**
 * 票241 Logto PKCE 纯函数单测（Jest = node 环境 + @angular 替身，只测纯函数，
 * 不测 TestBed 接线；beginLogin/handleCallback 的浏览器部分由活体验证覆盖）。
 */
import { describe, expect, it } from '@jest/globals';
import {
  base64UrlEncode,
  buildAuthorizeUrl,
  buildTokenBody,
  codeChallenge,
  generateVerifier
} from './logto.service';

const CFG = {
  endpoint: 'http://localhost:3001',
  clientId: 'test-client',
  redirectUri: 'http://localhost:4200/auth/logto/callback',
  audience: 'api',
  apiScopes: ['interviews.write', 'jobs.read']
};

describe('票241 Logto PKCE', () => {
  it('verifier：固定随机字节 → 精确字符映射（66 字符表取模）', () => {
    // 字符表：A-Z(0-25) a-z(26-51) 0-9(52-61) -._~(62-65)
    expect(generateVerifier(new Uint8Array([0, 1, 65, 66, 131]))).toBe('AB~A~');
    expect(generateVerifier(new Uint8Array(64)).length).toBe(64);
    expect(generateVerifier(new Uint8Array([62, 63, 64, 65]))).toBe('-._~');
  });

  it('base64url：+ → -、/ → _、去尾部 =', () => {
    // 0xfb 0xff → 标准 base64 是 '+/8='，base64url 应为 '-_8'
    expect(base64UrlEncode(new Uint8Array([0xfb, 0xff]).buffer)).toBe('-_8');
    expect(base64UrlEncode(new Uint8Array([0x61]).buffer)).toBe('YQ');
  });

  it('codeChallenge：SHA-256("abc") 的已知答案（43 位，无 padding）', async () => {
    await expect(codeChallenge('abc')).resolves.toBe('ungWv48Bz-pBQUDeXa4iI7ADYaOWF3qctBD_YfIAFa0');
    await expect(codeChallenge('abc')).resolves.not.toBe(await codeChallenge('abd'));
  });

  it('授权 URL：含全部 OIDC 参数 + first_screen + resource（audience 非空时）', () => {
    const url = new URL(buildAuthorizeUrl(CFG, {
      state: 's1', codeChallenge: 'c1', firstScreen: 'register'
    }));
    expect(url.origin + url.pathname).toBe('http://localhost:3001/oidc/auth');
    const q = url.searchParams;
    expect(q.get('client_id')).toBe('test-client');
    expect(q.get('redirect_uri')).toBe('http://localhost:4200/auth/logto/callback');
    expect(q.get('response_type')).toBe('code');
    expect(q.get('scope')).toContain('offline_access');
    expect(q.get('code_challenge')).toBe('c1');
    expect(q.get('code_challenge_method')).toBe('S256');
    expect(q.get('state')).toBe('s1');
    expect(q.get('first_screen')).toBe('register');
    expect(q.get('resource')).toBe('api');
  });

  it('票251 B7：授权 scope 必须带上 apiScopes（点值），否则换到的 token 业务 scope 为空', () => {
    const url = new URL(buildAuthorizeUrl(CFG, {
      state: 's1', codeChallenge: 'c1', firstScreen: 'sign_in'
    }));
    const scope = url.searchParams.get('scope') ?? '';
    expect(scope).toContain('interviews.write');
    expect(scope).toContain('jobs.read');
    expect(scope).toContain('openid');
    const bare = new URL(buildAuthorizeUrl({ ...CFG, apiScopes: [] }, {
      state: 's1', codeChallenge: 'c1', firstScreen: 'sign_in'
    }));
    expect(bare.searchParams.get('scope')).not.toContain('interviews.write');
  });

  it('授权 URL：audience 为空时不发 resource（token 里就没有业务 scope，调用方应知晓）', () => {
    const url = new URL(buildAuthorizeUrl({ ...CFG, audience: '' }, {
      state: 's1', codeChallenge: 'c1', firstScreen: 'sign_in'
    }));
    expect(url.searchParams.has('resource')).toBe(false);
    expect(url.searchParams.get('first_screen')).toBe('sign_in');
  });

  it('token 请求体：code 换取带 code_verifier，刷新带 refresh_token', () => {
    const code = buildTokenBody(CFG, { grant_type: 'authorization_code', code: 'abc', codeVerifier: 'v1' });
    expect(code.get('grant_type')).toBe('authorization_code');
    expect(code.get('code')).toBe('abc');
    expect(code.get('code_verifier')).toBe('v1');
    expect(code.has('refresh_token')).toBe(false);

    const refresh = buildTokenBody(CFG, { grant_type: 'refresh_token', refreshToken: 'r1' });
    expect(refresh.get('grant_type')).toBe('refresh_token');
    expect(refresh.get('refresh_token')).toBe('r1');
    expect(refresh.has('code')).toBe(false);
  });

  it('票251 B6：token 体必须重复 resource（RFC 8707），否则只发不透明 token', () => {
    const code = buildTokenBody(CFG, { grant_type: 'authorization_code', code: 'abc', codeVerifier: 'v1' });
    expect(code.get('resource')).toBe('api');
    const refresh = buildTokenBody(CFG, { grant_type: 'refresh_token', refreshToken: 'r1' });
    expect(refresh.get('resource')).toBe('api');
    const noAudience = buildTokenBody({ ...CFG, audience: '' }, { grant_type: 'authorization_code', code: 'abc', codeVerifier: 'v1' });
    expect(noAudience.has('resource')).toBe(false);
  });
});
