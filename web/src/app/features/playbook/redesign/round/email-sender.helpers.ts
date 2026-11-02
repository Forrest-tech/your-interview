/**
 * 票 313：展开区发件人行纯函数（供组件与 Jest 同用）。
 * 有名 + 有地址 → `Name <email>`；仅地址 → 地址；仅名 → 名；
 * 两者皆无或全空白 → null（调用方整行不渲染）。
 */
export function formatSenderLine(
  from: string | null | undefined,
  fromEmail: string | null | undefined,
): string | null {
  const name = (from ?? '').trim();
  const email = (fromEmail ?? '').trim();
  if (name && email) return `${name} <${email}>`;
  return name || email || null;
}
