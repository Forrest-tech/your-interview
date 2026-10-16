/**
 * Q&A 题库模块(F1/F5)本地文案字典,key 前缀 pb.qa.。
 *
 * 接线方式:coordinator 把本字典并入 core/i18n/i18n.service.ts 的 DICT
 * 即可;在此之前组件内的 t() 会优先查本字典,构建与运行都不报错。
 */
export const QA_I18N: Record<string, { zh: string; en: string; fr?: string }> = {
  // ---------- 顶部聚合头 ----------
  'pb.qa.totalQuestions': { zh: '题目总数', en: 'Questions' },
  'pb.qa.avgDifficulty': { zh: '平均难度', en: 'Avg difficulty' },
  'pb.qa.stuckQuestions': { zh: '卡壳 {n} 道', en: '{n} stuck' },
  'pb.qa.distribution': { zh: '题型分布', en: 'By category' },
  'pb.qa.noQuestions': {
    zh: '还没有问答记录。分析完成后会自动拆出,也可以在下方手工补录。',
    en: 'No Q&A records yet. They will be extracted after analysis, or add them manually below.'
  },

  // ---------- 筛选条 ----------
  'pb.qa.filterStatus': { zh: '状态', en: 'Status' },
  'pb.qa.filterCategory': { zh: '题型', en: 'Category' },
  'pb.qa.status.all': { zh: '全部', en: 'All' },
  'pb.qa.status.stuck': { zh: '卡壳', en: 'Stuck' },
  'pb.qa.status.todo': { zh: '待练', en: 'To do' },
  'pb.qa.status.practicing': { zh: '练习中', en: 'Practicing' },
  'pb.qa.status.mastered': { zh: '已掌握', en: 'Mastered' },
  'pb.qa.showing': { zh: '共 {n} 道', en: '{n} shown' },

  // ---------- 题型 ----------
  'pb.qa.cat.Technical': { zh: '技术', en: 'Technical' },
  'pb.qa.cat.SystemDesign': { zh: '系统设计', en: 'System design' },
  'pb.qa.cat.Behavioral': { zh: '行为', en: 'Behavioral' },
  'pb.qa.cat.Coding': { zh: '编程', en: 'Coding' },
  'pb.qa.cat.Culture': { zh: '文化', en: 'Culture' },
  'pb.qa.cat.Other': { zh: '其他', en: 'Other' },

  // ---------- 掌握度徽章 ----------
  'pb.qa.mastery.0': { zh: '待练', en: 'To do' },
  'pb.qa.mastery.1': { zh: '练习中', en: 'Practicing' },
  'pb.qa.mastery.2': { zh: '已掌握', en: 'Mastered' },
  'pb.qa.mastery.3': { zh: '还卡壳', en: 'Stuck' },
  'pb.qa.masteryHint': { zh: '点击切换掌握度', en: 'Click to change mastery' },
  'pb.qa.masterySaved': { zh: '掌握度已更新', en: 'Mastery updated' },

  // ---------- 逐题卡片 ----------
  'pb.qa.difficultyOf': { zh: '难度 {n}/5', en: 'Difficulty {n}/5' },
  'pb.qa.myAnswer': { zh: '我的回答', en: 'My answer' },
  'pb.qa.myAnswerPh': { zh: '当时是怎么答的……', en: 'How you answered at the time…' },
  'pb.qa.noMyAnswer': { zh: '(没有记录回答内容)', en: '(no answer recorded)' },
  'pb.qa.editAnswer': { zh: '编辑', en: 'Edit' },
  'pb.qa.saveAnswer': { zh: '保存回答', en: 'Save answer' },
  'pb.qa.answerSaved': { zh: '回答已保存', en: 'Answer saved' },
  'pb.qa.recommended': { zh: '参考答案', en: 'Reference answer' },
  'pb.qa.suggestedApproach': { zh: '参考思路', en: 'Suggested Approach' },
  'pb.qa.noRecommended': { zh: '(还没有参考答案)', en: '(no reference answer yet)' },
  'pb.qa.stuckReason': { zh: '卡壳原因', en: 'Why stuck' },
  'pb.qa.history': { zh: '历史回答', en: 'Answer history' },
  'pb.qa.historyHint': { zh: '同题在其他公司的回答版本', en: 'Versions of this question from other interviews' },
  'pb.qa.historyEmpty': { zh: '其他面试中没找到这道题的回答版本。', en: 'No versions of this question in other interviews.' },
  'pb.qa.historyLoading': { zh: '加载历史回答…', en: 'Loading history…' },
  'pb.qa.noDate': { zh: '日期未知', en: 'Date unknown' },

  // ---------- 新增题目 ----------
  'pb.qa.addToggle': { zh: '新增题目', en: 'Add question' },
  'pb.qa.addTitle': { zh: '手工补录题目', en: 'Add a question manually' },
  'pb.qa.qTextLabel': { zh: '题目 *', en: 'Question *' },
  'pb.qa.qTextPh': { zh: '尽可能记原话', en: 'Quote as closely as you can' },
  'pb.qa.qAnswerLabel': { zh: '我的回答', en: 'My answer' },
  'pb.qa.qCategoryLabel': { zh: '题型', en: 'Category' },
  'pb.qa.qDifficultyLabel': { zh: '难度(1-5)', en: 'Difficulty (1-5)' },
  'pb.qa.qStuckLabel': { zh: '当时卡住了', en: 'I got stuck' },
  'pb.qa.qStuckReasonLabel': { zh: '卡壳原因', en: 'Why stuck' },
  'pb.qa.qStuckReasonPh': { zh: '哪一步卡住了……', en: 'Where did you get stuck…' },
  'pb.qa.qRequired': { zh: '请先填写题目', en: 'Question text is required' },
  'pb.qa.added': { zh: '已添加', en: 'Added' },

  // ---------- 删除确认(B7:替代原生 confirm) ----------
  'pb.qa.delete': { zh: '删除', en: 'Delete' },
  'pb.qa.confirmDeleteTitle': { zh: '删除这道题?', en: 'Delete this question?' },
  'pb.qa.confirmDeleteBody': {
    zh: '删除后不可恢复,确定删除「{q}」吗?',
    en: 'This cannot be undone. Delete "{q}"?'
  },
  'pb.qa.deleted': { zh: '已删除', en: 'Deleted' },

  // ---------- 通用 ----------
  'pb.qa.retry': { zh: '重试', en: 'Retry' },
  'pb.qa.loading': { zh: '加载中…', en: 'Loading…' },

  // ---------- F5 技术覆盖小条 ----------
  'pb.qa.coverage.title': { zh: '技术覆盖', en: 'Coverage' },
  'pb.qa.coverage.empty': { zh: '本场面试还没有题目。', en: 'No questions in this interview yet.' },
  'pb.qa.coverage.count': { zh: '{n} 道', en: '{n}' }
};
