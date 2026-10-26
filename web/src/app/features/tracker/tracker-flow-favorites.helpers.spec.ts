import { describe, expect, it } from '@jest/globals';
import {
  FlowAppLike,
  favCountsByStatus,
  favoriteItems,
  itemsAtStatus,
  nextFlowStageSelection
} from './tracker-flow-favorites.helpers';

/**
 * 票73:流程视图收藏纯逻辑 + 「切换即同步」口径——同一收藏集合(Set)在
 * 切换(增删 id)后,徽标计数与条带行立即随之变化;看板/列表/详情走
 * TrackerPreferencesService.isFavorite 读同一集合,不在此重复。
 */

interface App extends FlowAppLike { role: string; companyName: string }

const APPS: App[] = [
  { id: 'a1', status: 'Applied', role: 'Dev', companyName: 'Acme' },
  { id: 'a2', status: 'Interview', role: 'Dev', companyName: 'Beta' },
  { id: 'a3', status: 'Interview', role: 'QA', companyName: 'Core' },
  { id: 'a4', status: 'Offer', role: 'Dev', companyName: 'Duo' },
  { id: 'a5', status: 'Applied', role: 'PM', companyName: 'Edge' }
];

describe('favCountsByStatus', () => {
  it('按当前状态数已收藏数(节点徽标)', () => {
    const favs = new Set(['a1', 'a2', 'a3', 'a5']);
    const counts = favCountsByStatus(APPS, favs);
    expect(counts.get('Applied')).toBe(2);
    expect(counts.get('Interview')).toBe(2);
    expect(counts.get('Offer')).toBeUndefined();
  });

  it('收藏集合里的未知 id 不计数(交集口径)', () => {
    const counts = favCountsByStatus(APPS, new Set(['zz', 'a4']));
    expect(counts.get('Offer')).toBe(1);
    expect(counts.size).toBe(1);
  });

  it('无收藏 → 空 Map(徽标不显示)', () => {
    expect(favCountsByStatus(APPS, new Set()).size).toBe(0);
    expect(favCountsByStatus([], new Set(['a1'])).size).toBe(0);
  });
});

describe('favoriteItems / itemsAtStatus(条带两态)', () => {
  it('默认态:全部已收藏,保持全量顺序', () => {
    const favs = new Set(['a3', 'a1', 'a4']);
    expect(favoriteItems(APPS, favs).map(a => a.id)).toEqual(['a1', 'a3', 'a4']);
  });

  it('阶段态:该阶段全部投递(收藏与否都列)', () => {
    expect(itemsAtStatus(APPS, 'Interview').map(a => a.id)).toEqual(['a2', 'a3']);
    expect(itemsAtStatus(APPS, 'Saved')).toEqual([]);
  });
});

describe('切换同步:同一集合增删后推导立即变化', () => {
  it('条带里取消收藏 → 默认条带少一行、徽标计数减一', () => {
    const favs = new Set(['a2', 'a3']);
    expect(favoriteItems(APPS, favs).map(a => a.id)).toEqual(['a2', 'a3']);
    expect(favCountsByStatus(APPS, favs).get('Interview')).toBe(2);

    favs.delete('a2'); // = toggleFavorite('a2') 后的集合
    expect(favoriteItems(APPS, favs).map(a => a.id)).toEqual(['a3']);
    expect(favCountsByStatus(APPS, favs).get('Interview')).toBe(1);
  });

  it('阶段态里点亮收藏 → 徽标计数加一、默认条带多一行', () => {
    const favs = new Set<string>(['a1']);
    favs.add('a4'); // = toggleFavorite('a4') 后的集合
    expect(favCountsByStatus(APPS, favs).get('Offer')).toBe(1);
    expect(favoriteItems(APPS, favs).map(a => a.id)).toEqual(['a1', 'a4']);
  });
});

describe('nextFlowStageSelection', () => {
  it('未选点节点 → 选中;点同一节点 → 回默认;点另一节点 → 切换', () => {
    expect(nextFlowStageSelection(null, 'Interview')).toBe('Interview');
    expect(nextFlowStageSelection('Interview', 'Interview')).toBeNull();
    expect(nextFlowStageSelection('Interview', 'Offer')).toBe('Offer');
  });
});
