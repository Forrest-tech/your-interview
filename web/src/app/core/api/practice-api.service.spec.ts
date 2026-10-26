/**
 * 票134 单测：getMaterials 整树包形态门（PracticeApi.treePackageNodes）。
 *
 * 基建口径（同 dictionary-ticket130.spec）：本仓 Jest = node 环境 + @angular/* 替身，
 * 无 TestBed / DOM 渲染。故只测静态纯函数形态门，不测 TestBed 接线；
 * service→组件 error 路径（treeLoadFailed）另由活体验证覆盖。
 *
 * 断言口径：
 * - 正常包（含空 nodes 包）→ 原样返回 nodes，绝不抛错（空包走空态，不是错误态）。
 * - 裸数组 / nodes 非数组 / null / undefined → 抛错（调用方 error 路径置 treeLoadFailed）。
 */
import { describe, expect, it } from '@jest/globals';
import { PracticeApi } from './practice-api.service';
import type { MaterialNodeDto } from './practice-api.service';

const node = (id: string): MaterialNodeDto => ({
  id,
  name: `n-${id}`,
  folder: false,
  sortOrder: 0,
  expanded: false,
  content: null,
  markColor: null,
  categoryId: null,
  children: [],
});

describe('票134 treePackageNodes 形态门', () => {
  it('正常包 → nodes 原样返回', () => {
    const nodes = [node('a'), node('b')];
    expect(PracticeApi.treePackageNodes({ treeVersion: 'v1', nodes })).toBe(nodes);
  });

  it('空 nodes 包 → 返回 []（走空态，不是错误态）', () => {
    expect(PracticeApi.treePackageNodes({ treeVersion: 'v1', nodes: [] })).toEqual([]);
  });

  it('旧后端裸数组 → 抛错（不得静默吞成空树）', () => {
    expect(() => PracticeApi.treePackageNodes([node('a')])).toThrow(/unexpected response shape/);
  });

  it('nodes 非数组（字符串 / 对象）→ 抛错', () => {
    expect(() => PracticeApi.treePackageNodes({ treeVersion: 'v1', nodes: 'x' })).toThrow(
      /unexpected response shape/,
    );
    expect(() => PracticeApi.treePackageNodes({ treeVersion: 'v1', nodes: { id: 'a' } })).toThrow(
      /unexpected response shape/,
    );
  });

  it('nodes 缺失 → 抛错', () => {
    expect(() => PracticeApi.treePackageNodes({ treeVersion: 'v1' })).toThrow(
      /unexpected response shape/,
    );
  });

  it('null / undefined 响应 → 抛错（不是 TypeError 裸奔）', () => {
    expect(() => PracticeApi.treePackageNodes(null)).toThrow(/unexpected response shape/);
    expect(() => PracticeApi.treePackageNodes(undefined)).toThrow(/unexpected response shape/);
  });
});
