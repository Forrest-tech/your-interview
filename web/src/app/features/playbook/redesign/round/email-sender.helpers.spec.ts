import { describe, expect, it } from '@jest/globals';
import { formatSenderLine } from './email-sender.helpers';

/** 票 313：展开区发件人行格式（名 + 地址组合与三态回落）。 */
describe('formatSenderLine', () => {
  it('有名 + 有地址 → `Name <email>`', () => {
    expect(formatSenderLine('Khalil Musazade', 'Khalil.Musazade@fleetcor.eu'))
      .toBe('Khalil Musazade <Khalil.Musazade@fleetcor.eu>');
  });
  it('仅地址 → 地址', () => {
    expect(formatSenderLine('', 'Khalil.Musazade@fleetcor.eu'))
      .toBe('Khalil.Musazade@fleetcor.eu');
    expect(formatSenderLine(null, 'Khalil.Musazade@fleetcor.eu'))
      .toBe('Khalil.Musazade@fleetcor.eu');
  });
  it('仅名 → 名', () => {
    expect(formatSenderLine('Khalil Musazade', '')).toBe('Khalil Musazade');
    expect(formatSenderLine('Khalil Musazade', undefined)).toBe('Khalil Musazade');
  });
  it('两者皆无或全空白 → null（整行不渲染）', () => {
    expect(formatSenderLine('', '')).toBeNull();
    expect(formatSenderLine(null, undefined)).toBeNull();
    expect(formatSenderLine('   ', '  ')).toBeNull();
  });
  it('首尾空白被修剪', () => {
    expect(formatSenderLine('  Khalil Musazade ', ' Khalil.Musazade@fleetcor.eu '))
      .toBe('Khalil Musazade <Khalil.Musazade@fleetcor.eu>');
  });
});
