import { I18nService } from '../../../../core/i18n/i18n.service';

/**
 * Analysis 模块文案 —— key 前缀 pb.an.。
 *
 * 接线说明(coordinator):把 ANALYSIS_I18N 的条目合并进
 * web/src/app/core/i18n/i18n.service.ts 的 DICT 后,
 * analysisT() 可以退化成直接调用 i18n.t(key)(保留 params 替换逻辑即可)。
 */
export const ANALYSIS_I18N: Record<string, { zh: string; en: string; fr?: string }> = {
  // ---------- 复盘报告(F2) ----------
  'pb.an.reportTitle': { zh: '复盘报告', en: 'Analysis Report', fr: 'Rapport d’analyse' },
  'pb.an.firstAnalysis': { zh: '首次分析', en: 'First analysis', fr: 'Première analyse' },
  'pb.an.vsLast': { zh: '较上次', en: 'vs last', fr: 'vs précédent' },
  'pb.an.analyzedAt': { zh: '分析时间', en: 'Analyzed at', fr: 'Analysé le' },
  'pb.an.conclusions': { zh: '结论', en: 'Key takeaways', fr: 'Conclusions' },
  'pb.an.noConclusions': { zh: '暂无结论', en: 'No conclusions yet', fr: 'Aucune conclusion' },
  'pb.an.dimsTitle': { zh: '能力维度', en: 'Score dimensions', fr: 'Dimensions' },
  'pb.an.noDims': { zh: '暂无维度评分', en: 'No dimension scores yet', fr: 'Aucune note' },
  'pb.an.radarTitle': { zh: '维度雷达', en: 'Dimension radar', fr: 'Radar' },
  'pb.an.trendTitle': { zh: '得分趋势', en: 'Score trend', fr: 'Tendance' },
  'pb.an.trendCount': { zh: '共 {n} 次分析', en: '{n} analyses', fr: '{n} analyses' },
  'pb.an.questionsTitle': { zh: '逐题复盘', en: 'Question review', fr: 'Revue des questions' },
  'pb.an.noQuestions': { zh: '暂无题目', en: 'No questions yet', fr: 'Aucune question' },
  'pb.an.myAnswer': { zh: '我的回答', en: 'My answer', fr: 'Ma réponse' },
  'pb.an.recommendedAnswer': { zh: '参考答案', en: 'Recommended answer', fr: 'Réponse recommandée' },
  'pb.an.evidenceTitle': { zh: '证据 · 转写原文', en: 'Evidence · transcript', fr: 'Preuves · transcription' },
  'pb.an.noTranscript': { zh: '暂无转写', en: 'No transcript', fr: 'Aucune transcription' },
  'pb.an.stuck': { zh: '卡壳', en: 'Got stuck', fr: 'Bloqué' },
  'pb.an.noRecord': { zh: '未记录', en: 'Not recorded', fr: 'Non renseigné' },
  'pb.an.nextActionsTitle': { zh: '下一步行动', en: 'Next actions', fr: 'Prochaines actions' },

  // ---------- Next actions(A2) ----------
  'pb.an.todoTitle': { zh: '待办事项', en: 'To-do', fr: 'À faire' },
  'pb.an.addPlaceholder': { zh: '写下下一步行动…', en: 'Write a next action…', fr: 'Écrire une action…' },
  'pb.an.detailPlaceholder': { zh: '补充说明（选填）', en: 'Details (optional)', fr: 'Détails (facultatif)' },
  'pb.an.dueDate': { zh: '截止日期', en: 'Due date', fr: 'Échéance' },
  'pb.an.add': { zh: '添加', en: 'Add', fr: 'Ajouter' },
  'pb.an.titleRequired': { zh: '标题不能为空', en: 'Title is required', fr: 'Titre requis' },
  'pb.an.noActions': { zh: '暂无待办事项', en: 'No action items yet', fr: 'Aucune action' },
  'pb.an.allDone': { zh: '全部完成 🎉', en: 'All done 🎉', fr: 'Tout est fait 🎉' },
  'pb.an.openCount': { zh: '{n} 项待办', en: '{n} open', fr: '{n} en cours' },
  'pb.an.doneSection': { zh: '已完成', en: 'Done', fr: 'Terminé' },
  'pb.an.edit': { zh: '编辑', en: 'Edit', fr: 'Modifier' },
  'pb.an.save': { zh: '保存', en: 'Save', fr: 'Enregistrer' },
  'pb.an.cancel': { zh: '取消', en: 'Cancel', fr: 'Annuler' },
  'pb.an.delete': { zh: '删除', en: 'Delete', fr: 'Supprimer' },
  'pb.an.confirmDelete': { zh: '删除这条事项？', en: 'Delete this item?', fr: 'Supprimer cet élément ?' },
  'pb.an.confirmDeleteTitle': { zh: '删除事项', en: 'Delete item', fr: 'Supprimer l’élément' },
  'pb.an.added': { zh: '已添加', en: 'Added', fr: 'Ajouté' },
  'pb.an.saved': { zh: '已保存', en: 'Saved', fr: 'Enregistré' },
  'pb.an.deleted': { zh: '已删除', en: 'Deleted', fr: 'Supprimé' },
  'pb.an.updated': { zh: '已更新', en: 'Updated', fr: 'Mis à jour' },

  // ---------- Overview widgets(F5) ----------
  'pb.an.latestTakeaways': { zh: '最新复盘结论', en: 'Latest takeaways', fr: 'Dernières conclusions' },
  'pb.an.noSnapshot': { zh: '暂无分析快照，分析完成后会自动生成', en: 'No snapshot yet — it will appear after analysis', fr: 'Aucun instantané — il apparaîtra après l’analyse' },

  // ---------- 题型标签 ----------
  'pb.an.cat.Technical': { zh: '技术题', en: 'Technical', fr: 'Technique' },
  'pb.an.cat.SystemDesign': { zh: '系统设计', en: 'System Design', fr: 'Conception système' },
  'pb.an.cat.Behavioral': { zh: '行为题', en: 'Behavioral', fr: 'Comportemental' },
  'pb.an.cat.Coding': { zh: '编程题', en: 'Coding', fr: 'Codage' },
  'pb.an.cat.Culture': { zh: '文化匹配', en: 'Culture Fit', fr: 'Culture' },
  'pb.an.cat.Other': { zh: '其他', en: 'Other', fr: 'Autre' },

  // ---------- 短板清单(R01) ----------
  'pb.an.weaknessTitle': { zh: '短板清单', en: 'Weak points', fr: 'Points faibles' },
  'pb.an.noWeaknesses': { zh: '暂无短板记录', en: 'No weak points recorded', fr: 'Aucun point faible' },

  // ---------- 通用 ----------
  'pb.an.loading': { zh: '加载中…', en: 'Loading…', fr: 'Chargement…' },
  'pb.an.loadFailed': { zh: '加载失败', en: 'Failed to load', fr: 'Échec du chargement' },
  'pb.an.retry': { zh: '重试', en: 'Retry', fr: 'Réessayer' }
};

/**
 * 本目录组件的 t():先查 ANALYSIS_I18N,缺词条时回退全站 I18nService
 * (可直接复用 dim. / common. 等已有键,模板写法与其它组件一致)。
 * 支持 {name} 占位符替换。
 */
export function analysisT(
  i18n: I18nService
): (key: string, params?: Record<string, string | number>) => string {
  return (key, params) => {
    const entry = ANALYSIS_I18N[key];
    let s: string;
    if (entry) {
      const lang = i18n.lang();
      s = (lang === 'fr' ? entry.fr : undefined) ?? entry[lang] ?? entry.zh ?? key;
    } else {
      s = i18n.t(key);
    }
    if (params) {
      for (const [k, v] of Object.entries(params)) {
        s = s.replaceAll(`{${k}}`, String(v));
      }
    }
    return s;
  };
}
