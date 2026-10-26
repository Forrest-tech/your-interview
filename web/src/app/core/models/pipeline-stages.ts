/**
 * 票123 R-9:共享的 pipeline 阶段常量。
 *
 * 定稿(REMEDIATION-PLAN §R-9 Surface 1):「the canonical list currently lives as
 * a field of the Tracker component. It is extracted to a shared constant both
 * components consume (**a move, not a copy** — Tracker behavior unchanged), and
 * that extraction is part of this item's scope.」
 *
 * 所以这里是**搬家**,不是复制:Tracker 组件的 `pipelineStages` 改为读这个常量,
 * Dashboard 的 Pipeline Band 读同一个常量,两处不可能再分叉。
 *
 * 状态类型复用 core/models/api.models.ts 里已有的 `ApplicationStatus` ——
 * 不另起一个平行类型(那样迟早会分叉)。
 */
import type { ApplicationStatus } from './api.models';

/**
 * 流程的规范阶段序列 —— 漏斗从保存到录用意向的五个阶段。
 *
 * 顺序即语义:Dashboard 的 Pipeline Band 与 Tracker 列表 stepper 用的是同一串。
 */
export const PIPELINE_STAGES: readonly ApplicationStatus[] = [
  'Saved',
  'Applied',
  'Screen',
  'Interview',
  'Offer',
] as const;

/**
 * 终局裁决节点 —— 漏斗在 Offer 之后的断点处。
 *
 * 单独列出而不是塞进 PIPELINE_STAGES:阶段序列是「向前推进」的一串,
 * 裁决是序列**之后**的另一个节点(中间有一段虚线断口)。
 */
export const PIPELINE_VERDICT: ApplicationStatus = 'Rejected';
