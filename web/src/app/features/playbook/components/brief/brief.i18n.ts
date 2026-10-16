/**
 * F3/F4/F5/F6 本地文案 —— 绝不改 core/i18n/i18n.service.ts。
 *
 * 约定：key 前缀 `pb.br.`。模板里直接用 `t('pb.br.xxx')`；
 * 各组件的 t() 先查这张表（按当前语言），查不到再回退全局 I18nService
 * （所以 `common.save`、`pb.roundOutcome.Passed` 这类全局键照样能用）。
 * Coordinator 接线时把这张表并入全局 service 即可，组件侧不用改。
 */
export const BRIEF_I18N: Record<string, { zh: string; en: string; fr?: string }> = {
  // ---------- F3 备战简报 ----------
  'pb.br.title': { zh: '备战简报', en: 'Interview Brief' },
  'pb.br.countdown.title': { zh: '下一轮倒计时', en: 'Next round countdown' },
  'pb.br.countdown.none': { zh: '暂无排期的轮次', en: 'No upcoming rounds scheduled' },
  'pb.br.countdown.today': { zh: '就是今天，加油！', en: 'Today — good luck!' },
  'pb.br.countdown.tomorrow': { zh: '明天', en: 'Tomorrow' },
  'pb.br.countdown.daysLeft': { zh: '还剩 {n} 天', en: '{n} days left' },
  'pb.br.countdown.join': { zh: '加入会议', en: 'Join meeting' },

  'pb.br.company.title': { zh: '公司研究', en: 'Company research' },
  'pb.br.company.empty': { zh: '还没写公司研究，点编辑补充 3–5 条要点。', en: 'No company research yet — click Edit to add 3–5 key points.' },
  'pb.br.company.edit': { zh: '编辑', en: 'Edit' },

  'pb.br.kw.title': { zh: 'JD 关键词', en: 'JD keywords' },
  'pb.br.kw.empty': { zh: '暂无 JD 文本', en: 'No JD text yet' },
  'pb.br.kw.times': { zh: '{n} 次', en: '×{n}' },

  'pb.br.top5.title': { zh: '必答题 Top5', en: 'Top 5 must-answer questions' },
  'pb.br.top5.empty': { zh: '暂无题目', en: 'No questions yet' },
  'pb.br.top5.goQA': { zh: '去 Q&A 练习', en: 'Practice in Q&A' },
  'pb.br.top5.noAnswer': { zh: '尚未作答', en: 'Not answered yet' },
  'pb.br.top5.stuckTag': { zh: '卡壳', en: 'Stuck' },

  'pb.br.ask.title': { zh: '我要问面试官的问题', en: 'Questions to ask the interviewer' },
  'pb.br.ask.addPh': { zh: '添加一个问题…', en: 'Add a question…' },
  'pb.br.ask.add': { zh: '添加', en: 'Add' },
  'pb.br.ask.empty': { zh: '还没准备问题，想到就记下来。', en: 'No questions prepared yet — jot them down as they come.' },

  'pb.br.cl.title': { zh: '出发前 Checklist', en: 'Pre-interview checklist' },
  'pb.br.cl.addPh': { zh: '新增事项，回车确认…', en: 'Add an item, Enter to confirm…' },
  'pb.br.cl.empty': { zh: '清单是空的。', en: 'The checklist is empty.' },
  'pb.br.cl.genDefault': { zh: '生成默认清单', en: 'Generate default checklist' },
  'pb.br.cl.d1': { zh: '检查着装是否得体', en: 'Check your outfit' },
  'pb.br.cl.d2': { zh: '测试设备 / 网络 / 麦克风', en: 'Test devices, network and microphone' },
  'pb.br.cl.d3': { zh: '准备感谢信草稿', en: 'Draft the thank-you email' },

  'pb.br.saved': { zh: '已保存', en: 'Saved' },

  // ---------- F4 轮次时间线 ----------
  'pb.br.rounds.nextTitle': { zh: '下一轮', en: 'Next round' },
  'pb.br.rounds.goBrief': { zh: '去 Brief 备战', en: 'Go to Brief' },
  'pb.br.rounds.noneUpcoming': { zh: '没有未到期的轮次', en: 'No upcoming rounds' },
  'pb.br.rounds.timelineTitle': { zh: '轮次时间线', en: 'Rounds timeline' },
  'pb.br.rounds.addTitle': { zh: '新增一轮', en: 'Add a round' },
  'pb.br.rounds.stagePh': { zh: '阶段', en: 'Stage' },
  'pb.br.rounds.add': { zh: '新增', en: 'Add' },
  'pb.br.rounds.date': { zh: '日期', en: 'Date' },
  'pb.br.rounds.time': { zh: '时间', en: 'Time' },
  'pb.br.rounds.interviewers': { zh: '面试官', en: 'Interviewers' },
  'pb.br.rounds.format': { zh: '形式', en: 'Format' },
  'pb.br.rounds.location': { zh: '地点', en: 'Location' },
  'pb.br.rounds.outcome': { zh: '结果', en: 'Outcome' },
  'pb.br.rounds.meetingLink': { zh: '会议链接', en: 'Meeting link' },
  'pb.br.rounds.notes': { zh: '备注', en: 'Notes' },
  'pb.br.rounds.feedback': { zh: '反馈', en: 'Feedback' },
  'pb.br.rounds.emailsTitle': { zh: '邮件往来', en: 'Email thread' },
  'pb.br.rounds.emailEmpty': { zh: '暂无邮件记录', en: 'No emails recorded' },
  'pb.br.rounds.emailFromPh': { zh: '发件人', en: 'From' },
  'pb.br.rounds.emailSubjectPh': { zh: '主题', en: 'Subject' },
  'pb.br.rounds.emailDatePh': { zh: '日期', en: 'Date' },
  'pb.br.rounds.emailSnippetPh': { zh: '摘要（可选）', en: 'Snippet (optional)' },
  'pb.br.rounds.emailAdd': { zh: '添加邮件', en: 'Add email' },
  'pb.br.rounds.emailSaved': { zh: '邮件已更新', en: 'Emails updated' },
  'pb.br.rounds.transcriptTitle': { zh: '转写稿', en: 'Transcript' },
  'pb.br.rounds.transcriptEmpty': { zh: '暂无转写稿', en: 'No transcript yet' },
  'pb.br.rounds.edit': { zh: '编辑', en: 'Edit' },
  'pb.br.rounds.delete': { zh: '删除', en: 'Delete' },
  'pb.br.rounds.confirmDelete': { zh: '确认删除这一轮吗？', en: 'Delete this round?' },
  'pb.br.rounds.deleted': { zh: '已删除', en: 'Deleted' },
  'pb.br.rounds.empty': { zh: '还没有轮次记录', en: 'No rounds recorded yet' },
  'pb.br.stage.Screen': { zh: '初筛', en: 'Screen' },
  'pb.br.stage.Technical': { zh: '技术面', en: 'Technical' },
  'pb.br.stage.SystemDesign': { zh: '系统设计', en: 'System Design' },
  'pb.br.stage.Behavioral': { zh: '行为面', en: 'Behavioral' },
  'pb.br.stage.Final': { zh: '终面', en: 'Final' },

  // ---------- F5 页头信息条 ----------
  'pb.br.hd.recruiter': { zh: '招聘联系人', en: 'Recruiter' },
  'pb.br.hd.unknown': { zh: '未知', en: 'Unknown' },
  'pb.br.hd.lastContact': { zh: '上次联系', en: 'Last contact' },
  'pb.br.hd.nextRound': { zh: '下一轮', en: 'Next round' },
  'pb.br.hd.noSchedule': { zh: '未排期', en: 'Not scheduled' },

  // ---------- F6 技术覆盖图谱（列表页） ----------
  'pb.br.cov.title': { zh: '技术覆盖图谱', en: 'Tech coverage map' },
  'pb.br.cov.sub': { zh: '各技术类别被问次数与掌握情况', en: 'How often each category was asked, and mastery' },
  'pb.br.cov.expand': { zh: '展开', en: 'Expand' },
  'pb.br.cov.collapse': { zh: '收起', en: 'Collapse' },
  'pb.br.cov.askedTimes': { zh: '被问 {n} 次', en: 'Asked {n}×' },
  'pb.br.cov.mastered': { zh: '掌握', en: 'Mastered' },
  'pb.br.cov.stuck': { zh: '卡壳', en: 'Stuck' },
  'pb.br.cov.empty': { zh: '暂无覆盖数据', en: 'No coverage data yet' },
};
