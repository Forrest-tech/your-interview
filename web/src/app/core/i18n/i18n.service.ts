import { Injectable, computed, effect, signal } from '@angular/core';

export type Lang = 'zh' | 'en' | 'fr';

export interface LangOption {
  code: Lang;
  label: string;
  short: string;
}

export const LANG_OPTIONS: LangOption[] = [
  { code: 'zh', label: '中文', short: '中' },
  { code: 'en', label: 'English', short: 'EN' },
  { code: 'fr', label: 'Français', short: 'FR' }
];

const STORAGE_KEY = 'yi.lang';

/**
 * 极简 i18n。不引 ngx-translate —— 本项目文案量可控,
 * 自建字典 + signal 足够,且省一个依赖、少一层运行时。
 *
 * 用法:
 *   模板: {{ t('nav.dashboard') }}
 *   代码: t('dash.total')
 */
const DICT: Record<string, Record<Lang, string>> = {
  // ---------- 顶部主导航 ----------
  'nav.dashboard': { zh: '总览', en: 'Dashboard', fr: 'Tableau de bord' },
  'nav.tracker': { zh: '投递跟踪', en: 'Tracker', fr: 'Suivi' },
  'nav.playbook': { zh: '实战机经', en: 'Playbook', fr: 'Journal' },
  'nav.techstack': { zh: '技术栈', en: 'Tech Stack', fr: 'Technologies' },
  'nav.mock': { zh: 'AI 实战模拟', en: 'AI Mock', fr: 'Simulation IA' },
  'nav.analytics': { zh: '数据分析', en: 'Analytics', fr: 'Analytique' },
  'nav.admin': { zh: '管理后台', en: 'Admin', fr: 'Administration' },

  // ---------- 账户 / 通用动作 ----------
  'account.profile': { zh: '我的账户', en: 'My Account', fr: 'Mon compte' },
  'account.logout': { zh: '退出登录', en: 'Sign out', fr: 'Déconnexion' },
  'account.login': { zh: '登录', en: 'Sign in', fr: 'Connexion' },
  'common.language': { zh: '语言', en: 'Language', fr: 'Langue' },
  'common.refresh': { zh: '刷新', en: 'Refresh', fr: 'Actualiser' },
  'common.loadFailed': { zh: '加载失败', en: 'Failed to load', fr: 'Échec du chargement' },
  'common.collapse': { zh: '折叠/展开', en: 'Collapse / expand', fr: 'Réduire / développer' },
  'common.save': { zh: '保存', en: 'Save', fr: 'Enregistrer' },
  'common.cancel': { zh: '取消', en: 'Cancel', fr: 'Annuler' },
  'common.optional': { zh: '选填', en: 'Optional', fr: 'Facultatif' },
  'common.delete': { zh: '删除', en: 'Delete', fr: 'Supprimer' },
  'common.edit': { zh: '编辑', en: 'Edit', fr: 'Modifier' },
  'common.create': { zh: '新建', en: 'Create', fr: 'Créer' },
  'common.search': { zh: '搜索', en: 'Search', fr: 'Rechercher' },
  'common.empty': { zh: '暂无数据', en: 'No data', fr: 'Aucune donnée' },
  'common.close': { zh: '关闭', en: 'Close', fr: 'Fermer' },
  'common.back': { zh: '返回', en: 'Back', fr: 'Retour' },
  'common.loading': { zh: '加载中', en: 'Loading', fr: 'Chargement' },
  'common.all': { zh: '全部', en: 'All', fr: 'Tout' },
  'common.backToMain': { zh: '返回主页', en: 'Back to Main', fr: 'Retour à l’accueil' },

  // ---------- 总览 ----------
  'dash.title': { zh: '总览', en: 'Dashboard', fr: 'Tableau de bord' },
  'dash.welcome': { zh: '欢迎回来', en: 'Welcome back', fr: 'Bon retour' },
  'dash.total': { zh: '总投递', en: 'Total Applications', fr: 'Candidatures totales' },
  'dash.inProgress': { zh: '进行中', en: 'In Progress', fr: 'En cours' },
  'dash.interviewing': { zh: '面试中', en: 'Interviews', fr: 'Entretiens' },
  'dash.offer': { zh: 'Offer', en: 'Offers', fr: 'Offres' },
  'dash.radar': { zh: '能力雷达 · 最弱三项', en: 'Competency Radar · Weakest Three', fr: 'Radar de compétences · Trois plus faibles' },
  'dash.radarSub': { zh: '权重最高的是技术深度与结构,优先后两项', en: 'Technical depth and structure carry the most weight — prioritise those two', fr: 'La profondeur technique et la structure pèsent le plus — priorisez ces deux' },
  'dash.noAnalysis': { zh: '还没有面试分析数据。去「实战机经」导入一场面试录音即可开始。', en: 'No interview analysis yet. Import an interview recording in Playbook to get started.', fr: 'Aucune analyse d’entretien. Importez un enregistrement dans Journal pour commencer.' },
  'dash.best': { zh: '历史最佳', en: 'Best', fr: 'Meilleur' },
  'dash.viewFull': { zh: '查看完整分析', en: 'View full analysis', fr: 'Voir l’analyse complète' },
  'dash.next': { zh: '下一步', en: 'Next steps', fr: 'Étapes suivantes' },
  'dash.nextSub': { zh: '按当前进度推荐的动作', en: 'Recommended actions based on your progress', fr: 'Actions recommandées selon votre progression' },
  'dash.importAudio': { zh: '导入面试录音分析', en: 'Import interview recording', fr: 'Importer un enregistrement' },
  'dash.reviewStack': { zh: '复习技术栈', en: 'Review tech stack', fr: 'Réviser les technologies' },
  'dash.startMock': { zh: '开一场 AI 模拟', en: 'Start an AI mock', fr: 'Lancer une simulation IA' },
  'dash.newApp': { zh: '记录新投递', en: 'Log a new application', fr: 'Ajouter une candidature' },

  // ---------- 行动中心(M4) ----------
  'dash.actionCenter': { zh: '行动中心 · 今天该做什么', en: 'Action Center · What to do today', fr: 'Centre d’action · Que faire aujourd’hui' },
  'dash.followUpsDue': { zh: '待跟进', en: 'Follow-ups due', fr: 'Suivis à faire' },
  'dash.deadlinesSoon': { zh: '临近截止', en: 'Deadlines approaching', fr: 'Échéances proches' },
  'dash.weakAreas': { zh: '能力短板', en: 'Weak areas', fr: 'Points faibles' },
  'dash.pipeline': { zh: '投递漏斗', en: 'Pipeline', fr: 'Pipeline' },
  'dash.goHandle': { zh: '去处理', en: 'Handle', fr: 'Traiter' },
  'dash.goPractice': { zh: '去练习', en: 'Practice', fr: 'Pratiquer' },
  'dash.viewTracker': { zh: '查看投递', en: 'View applications', fr: 'Voir les candidatures' },
  'dash.followUpAt': { zh: '跟进于', en: 'Follow up on', fr: 'Suivre le' },
  'dash.deadlineOn': { zh: '截止于', en: 'Due on', fr: 'Échéance le' },
  'dash.none': { zh: '今天没有紧急待办,保持节奏 🎯', en: 'Nothing urgent today — keep the momentum 🎯', fr: 'Rien d’urgent aujourd’hui — gardez le cap 🎯' },
  'dash.allClear': { zh: '全部清空', en: 'All clear', fr: 'Tout est clair' },
  'dash.items': { zh: '项', en: 'items', fr: 'éléments' },

  // ---------- 六维能力 ----------
  'dim.overall': { zh: '总分', en: 'Overall', fr: 'Global' },
  'dim.pronunciation': { zh: '发音', en: 'Pronunciation', fr: 'Prononciation' },
  'dim.fluency': { zh: '流畅度', en: 'Fluency', fr: 'Fluidité' },
  'dim.structure': { zh: '结构', en: 'Structure', fr: 'Structure' },
  'dim.technicalDepth': { zh: '技术深度', en: 'Technical Depth', fr: 'Profondeur technique' },
  'dim.relevance': { zh: '相关性', en: 'Relevance', fr: 'Pertinence' },
  'dim.sentenceIntegrity': { zh: '句子完整', en: 'Sentence Integrity', fr: 'Intégrité des phrases' },
  'dim.accuracy': { zh: '准确度', en: 'Accuracy', fr: 'Précision' },
  'dim.completeness': { zh: '完整度', en: 'Completeness', fr: 'Complétude' },
  'dim.prosody': { zh: '韵律', en: 'Prosody', fr: 'Prosodie' },

  // ---------- 页面内子导航(左侧栏) ----------
  'sub.overview': { zh: '概览', en: 'Overview', fr: 'Aperçu' },
  'sub.questions': { zh: '问答', en: 'Q&A', fr: 'Questions' },
  'sub.assets': { zh: '材料', en: 'Assets', fr: 'Documents' },
  'sub.edit': { zh: '编辑', en: 'Edit', fr: 'Modifier' },
  'sub.companies': { zh: '公司', en: 'Companies', fr: 'Entreprises' },
  'sub.sessions': { zh: '场次', en: 'Sessions', fr: 'Séances' },
  'sub.concepts': { zh: '概念条目', en: 'Concepts', fr: 'Concepts' },
  'sub.dueReview': { zh: '待复习', en: 'Due review', fr: 'À réviser' },
  'sub.status': { zh: '状态', en: 'Status', fr: 'Statut' },
  'sub.users': { zh: '用户', en: 'Users', fr: 'Utilisateurs' },
  'sub.roles': { zh: '角色', en: 'Roles', fr: 'Rôles' },
  'sub.audit': { zh: '审计日志', en: 'Audit log', fr: 'Journal d’audit' },

  // ---------- 投递状态 ----------
  'status.Saved': { zh: '已收藏', en: 'Saved', fr: 'Enregistré' },
  'status.Applied': { zh: '已投递', en: 'Applied', fr: 'Postulé' },
  'status.Screen': { zh: '初筛', en: 'Screen', fr: 'Préqualification' },
  'status.Interview': { zh: '面试中', en: 'Interview', fr: 'Entretien' },
  'status.Offer': { zh: 'Offer', en: 'Offer', fr: 'Offre' },
  'status.Rejected': { zh: '已拒', en: 'Rejected', fr: 'Refusé' },
  'status.Paused': { zh: '暂停', en: 'Paused', fr: 'En pause' },
  'status.Withdrawn': { zh: '已撤回', en: 'Withdrawn', fr: 'Retiré' },
  'status.Accepted': { zh: '已录用', en: 'Accepted', fr: 'Accepté' },
  'status.Ghosted': { zh: '失联', en: 'Ghosted', fr: 'Sans nouvelles' },
  'outcome.Passed': { zh: '通过', en: 'Passed', fr: 'Réussi' },
  'outcome.Failed': { zh: '未通过', en: 'Failed', fr: 'Échoué' },

  // ---------- AI 面试练习 ----------
  'nav.practice': { zh: 'AI 面试练习', en: 'AI Practice', fr: 'Pratique IA' },
  // ★ 2026-09-23(Forrest):素材树头部不再显示 "Materials / 面试素材" 字段,
  //   该词条随之删除(面板靠工具按钮与树本身已足够表意)。
  'practice.emptyHint': { zh: '数据库里还没有素材。点上方按钮新建文件夹或文件，内容会直接存进数据库。', en: 'No materials in the database yet. Use the buttons above to create a folder or file; content is saved directly to the database.', fr: "Aucun document en base de données. Utilisez les boutons ci-dessus pour créer un dossier ou un fichier ; le contenu est enregistré directement dans la base." },
  'practice.treeLoading': { zh: '正在从数据库加载素材…', en: 'Loading materials from the database…', fr: 'Chargement des documents depuis la base…' },
  'practice.treeUnavailable': { zh: '无法连接服务端，素材未加载。为避免覆盖数据库里的真实数据，本次已禁止写入。请点击重试。', en: 'Cannot reach the server, materials were not loaded. To avoid overwriting real data in the database, writing is disabled. Please retry.', fr: "Serveur injoignable, documents non chargés. Pour éviter d'écraser les données réelles, l'écriture est désactivée. Veuillez réessayer." },
  'practice.retryLoad': { zh: '重试', en: 'Retry', fr: 'Réessayer' },
  'practice.noSelection': { zh: '未选择素材', en: 'No material selected', fr: 'Aucun document sélectionné' },
  'practice.pickHint': { zh: '从左侧选一条素材,或自己新建文件夹与文件。', en: 'Pick a material on the left, or create your own folders and files.', fr: 'Choisissez un document à gauche, ou créez vos propres dossiers et fichiers.' },
  'practice.placeholder': { zh: '在这里写你的面试稿……', en: 'Write your interview script here...', fr: 'Rédigez votre script d entretien ici...' },
  'practice.save': { zh: '保存', en: 'Save', fr: 'Enregistrer' },
  'practice.clear': { zh: '清空', en: 'Clear', fr: 'Effacer' },
  'practice.record': { zh: '开始录音练习', en: 'Start recording', fr: 'Démarrer l enregistrement' },
  'practice.play': { zh: '播放', en: 'Play', fr: 'Lire' },
  'practice.stop': { zh: '停止', en: 'Stop', fr: 'Arrêter' },
  'practice.stageHint': { zh: '当前为页面预览,录音与 AI 评分待接入', en: 'Page preview only — recording and AI scoring come next', fr: 'Aperçu uniquement — enregistrement et notation IA à venir' },
  'practice.setup': { zh: '练习设置', en: 'Practice setup', fr: 'Configuration' },
  'practice.mode': { zh: '练习模式', en: 'Mode', fr: 'Mode' },
  'practice.modeRead': { zh: '朗读素材', en: 'Read aloud', fr: 'Lecture à voix haute' },
  'practice.modeQa': { zh: '抽题作答', en: 'Question drill', fr: 'Exercice de questions' },
  'practice.modeFree': { zh: '自由讲述', en: 'Free talk', fr: 'Discours libre' },
  'practice.target': { zh: '素材范围', en: 'Scope', fr: 'Portée' },
  'practice.selfIntro': { zh: '自我介绍', en: 'Self introduction', fr: 'Présentation personnelle' },
  'practice.techIntro': { zh: '技术介绍', en: 'Technical introduction', fr: 'Présentation technique' },
  'practice.scores': { zh: '本次评分', en: 'Scores', fr: 'Scores' },
  'practice.history': { zh: '练习记录', en: 'History', fr: 'Historique' },
  'practice.noHistory': { zh: '还没有练习记录。', en: 'No practice sessions yet.', fr: 'Aucune session pour l instant.' },
  'practice.recordings': { zh: '我的录音', en: 'My recordings', fr: 'Mes enregistrements' },
  'practice.startRec': { zh: '开始录音', en: 'Record', fr: 'Enregistrer' },
  'practice.stopRec': { zh: '停止录音', en: 'Stop recording', fr: 'Arrêter' },
  'practice.deleteRec': { zh: '删除这条录音', en: 'Delete this recording', fr: 'Supprimer cet enregistrement' },
  'practice.gradeRec': { zh: 'AI 发音评分', en: 'Score pronunciation', fr: 'Noter la prononciation' },
  'practice.recUnsupported': { zh: '当前浏览器不支持录音,请改用 Chrome / Edge / Safari。', en: 'This browser cannot record audio. Please use Chrome, Edge, or Safari.', fr: 'Ce navigateur ne peut pas enregistrer. Utilisez Chrome, Edge ou Safari.' },
  // ★ 第五十一轮:胶囊上不再有文字标签,空态文案改为指向"麦克风按钮"。
  'practice.noRecordings': { zh: '还没有录音。点上方绿色麦克风按钮录一遍,录音会保存在这里。', en: 'No recordings yet. Hit the green mic button above to capture a take.', fr: 'Aucun enregistrement. Cliquez sur le bouton micro ci-dessus.' },
  'practice.wordDetail': { zh: '逐词明细', en: 'Word-by-word detail', fr: 'Détail par mot' },
  'practice.question': { zh: '题目', en: 'Question', fr: 'Question' },
  // 第二十二轮:顶部路径改用父文件夹名;顶层文件无文件夹时用此兜底
  'practice.uncategorized': { zh: '未分类', en: 'Uncategorized', fr: 'Non classé' },
  'practice.completed': { zh: '已完成', en: 'Completed', fr: 'Terminé' },
  'practice.expandTree': { zh: '展开素材', en: 'Show materials', fr: 'Afficher les supports' },
  'practice.collapseTree': { zh: '收起素材', en: 'Hide materials', fr: 'Masquer les supports' },
  'practice.prev': { zh: '上一题', en: 'Previous', fr: 'Précédent' },
  'practice.next': { zh: '下一题', en: 'Next', fr: 'Suivant' },
  // ★ 第五十一轮:practice.submitScoring 删除 —— 底部"提交 AI 评分"与胶囊上的
  //   Run AI Scoring 都已消失,评分只剩列表行内 ✦ 按钮(用 practice.runAiScoring)。
  'practice.dragResize': { zh: '拖动调整宽度', en: 'Drag to resize', fr: 'Glisser pour redimensionner' },
  // 2026-09-16(Forrest 本轮):Retry 未选中录音时当"刷新列表"用。
  // ★ 第五十轮:Retry / 重置按钮已全部移除,第 48 轮那段"禁用原因"也随之作废。
  // ★ 第五十一轮:评分按钮(列表行 ✦)仍会在加载期间置灰。
  // ★ 第五十轮:Reset 已移除,提示里不再提"重置"。
  'practice.scoreNeedsSubmit': { zh: '请先点「Submit」提交这条录音,评分才可用', en: 'Submit this take first — scoring works on saved takes', fr: 'Soumettez d abord cette prise, puis évaluez' },
  // 2026-09-16(Forrest 本轮):录音结束后出现的"提交"按钮。
  'practice.submitTake': { zh: '提交', en: 'Submit', fr: 'Soumettre' },
  /** 第四十九轮:上一条没提交就重录时的告知(诚实说明它已被丢弃)。 */
  'practice.prevTakeDiscarded': {
    zh: '上一条录音还没提交,已丢弃 —— 现在开始新的录音。',
    en: 'The previous take was not submitted, so it was discarded — now recording a new one.',
    fr: 'La prise précédente n a pas été soumise, elle a été supprimée — nouvel enregistrement.'
  },
  'practice.submitTakeTip': { zh: '把这条录音保存到列表', en: 'Save this take to the list', fr: 'Enregistrer cette prise dans la liste' },
  'practice.pendingTake': { zh: '待提交录音', en: 'Take ready', fr: 'Prise prête' },
  'practice.pendingHint': { zh: '点"提交"保存到下方列表', en: 'Click Submit to add it to the list below', fr: 'Cliquez sur Soumettre pour l’ajouter à la liste' },
  'practice.discardTake': { zh: '丢弃这条录音', en: 'Discard this take', fr: 'Jeter cette prise' },
  'practice.submitNeedsRecording': {
    zh: '还没有可提交的录音 —— 先录一段,录好后按钮会激活',
    en: 'No take to submit yet — record one first and this button activates',
    fr: 'Aucune prise à soumettre — enregistrez-en une, le bouton s’activera'
  },
  'practice.previewTake': {
    zh: '试听',
    en: 'Preview',
    fr: 'Écouter'
  },
  'practice.recNo': { zh: '第', en: 'Take', fr: 'Prise' },
  'practice.recIdle': { zh: '录音', en: 'Record', fr: 'Enregistrer' },
  'practice.recordingHint': { zh: '停止', en: 'Stop', fr: 'Arrêter' },
  'practice.recReady': { zh: '回听', en: 'Play', fr: 'Écouter' },
  'practice.recPaused': { zh: '暂停', en: 'Pause', fr: 'Pause' },
  // 2026-09-16(Forrest 本轮):措辞统一为 TTS Engine —— 中文不再叫“示范朗读引擎”。
  // 该弹窗现在是全站**唯一**的朗读引擎选择入口(设置页那张卡片已删)。
  'practice.ttsEngine': { zh: 'TTS 引擎', en: 'TTS Engine', fr: 'Moteur vocal' },
  'practice.ttsEngineHint': {
    zh: '选择示范朗读使用的方式',
    en: 'Choose how sample readings are spoken',
    fr: 'Choisissez la voix de lecture des exemples'
  },
  'practice.ttsBrowser': { zh: '浏览器默认 TTS', en: 'Browser TTS', fr: 'TTS du navigateur' },
  'practice.ttsBrowserNote': { zh: '免费 · 本地合成 · 即时可用', en: 'Free · on-device · instant', fr: 'Gratuit · local · instantané' },
  'practice.ttsAzure': { zh: 'Azure 语音', en: 'Azure Voice', fr: 'Voix Azure' },
  'practice.ttsAzureNote': { zh: '高质量音色 · 消耗额度', en: 'High quality · uses quota', fr: 'Haute qualité · consomme du quota' },
  // 2026-09-20(Forrest 第三十六轮):TTS Engine 二选一已下线 ——
  // 示范朗读全局只用 Azure 语音合成。浮层里只留语言与 Azure 配置。
  'practice.ttsSettings': { zh: '朗读设置', en: 'Reading settings', fr: 'Paramètres de lecture' },
  'practice.ttsSettingsHint': {
    zh: '示范朗读由 Azure 语音合成,需先配置密钥',
    en: 'Sample readings are synthesized by Azure Speech and need a configured key',
    fr: 'Les lectures sont synthétisées par Azure Speech et nécessitent une clé configurée'
  },
  'practice.azureNotReady': {
    zh: '尚未配置 Azure Speech 密钥,示范朗读暂不可用',
    en: 'Azure Speech key is missing, so read-aloud is unavailable',
    fr: 'Clé Azure Speech absente : la lecture est indisponible'
  },
  'practice.azurePlayBlocked': {
    zh: '请先配置 Azure Speech 密钥,再进行示范朗读',
    en: 'Configure the Azure Speech key before using read-aloud',
    fr: 'Configurez la clé Azure Speech avant la lecture'
  },
  'practice.recHistory': { zh: '历史录音', en: 'Recordings', fr: 'Enregistrements' },
  'practice.viewReport': { zh: '查看报告', en: 'View Report', fr: 'Voir le rapport' },
  'practice.points': { zh: '分', en: 'pts', fr: 'pts' },
  'practice.noPhonetic': { zh: '暂无音标', en: 'No phonetic data', fr: 'Pas de phonétique' },
  'practice.saveChanges': { zh: '保存修改', en: 'Save Changes', fr: 'Enregistrer' },
  'practice.saved': { zh: '已保存', en: 'Saved', fr: 'Enregistré' },
  'practice.unsavedHint': { zh: '有未保存的修改', en: 'You have unsaved changes', fr: 'Modifications non enregistrées' },
  'practice.savedHint': { zh: '素材已保存到本地', en: 'Materials saved locally', fr: 'Supports enregistrés localement' },
  // ★ 2026-09-20(Forrest):素材树的编辑模式 —— 只有点「编辑」才能改,
  //   点「保存修改」确认后才写数据库;取消则放弃未保存的改动。
  'practice.editTree': { zh: '编辑', en: 'Edit', fr: 'Modifier' },
  'practice.editTreeHint': {
    zh: '点击编辑素材内容',
    en: 'Click to edit content',
    fr: 'Cliquez pour modifier le contenu'
  },
  // 一键展开/收起整棵树用的是共享控件自己的词条(tree.expandAll / tree.collapseAll)。
  // ---------- 练习类别(★ 2026-09-25 Forrest) ----------
  'practice.categoryLabel': { zh: '练习类别', en: 'Practice category', fr: 'Catégorie de pratique' },
  'practice.categoryAll': { zh: '全部', en: 'All', fr: 'Tous' },
  'practice.categoryUncategorized': { zh: '未分类', en: 'Uncategorized', fr: 'Non classés' },
  'practice.categoryManage': { zh: '配置类别', en: 'Manage categories', fr: 'Gérer les catégories' },
  'practice.categoryManageTitle': { zh: '练习类别', en: 'Practice categories', fr: 'Catégories de pratique' },
  'practice.categoryHint': {
    zh: '拖动排序;双击名称可重命名;删除类别后,其下素材会归入"未分类"。',
    en: 'Drag to reorder; double-click a name to rename; deleting a category moves its materials to "Uncategorized".',
    fr: 'Glissez pour trier; double-cliquez pour renommer; la suppression d\'une catégorie renvoie ses supports dans « Non classés ».'
  },
  'practice.categoryDrag': { zh: '拖动排序', en: 'Drag to reorder', fr: 'Glisser pour trier' },
  'practice.categoryRename': { zh: '重命名', en: 'Rename', fr: 'Renommer' },
  'practice.categoryDelete': { zh: '删除类别', en: 'Delete category', fr: 'Supprimer la catégorie' },
  'practice.categoryDelConfirm': { zh: '确认删除？', en: 'Delete?', fr: 'Supprimer ?' },
  'practice.categoryEmpty': { zh: '还没有类别,在下面添加一个吧。', en: 'No categories yet — add one below.', fr: 'Aucune catégorie — ajoutez-en une ci-dessous.' },
  'practice.categoryNewPlaceholder': {
    zh: '新类别名称,如:面试自我介绍',
    en: 'New category name, e.g. Self-introduction',
    fr: 'Nouvelle catégorie, ex. Présentation'
  },
  'practice.categoryAdd': { zh: '添加', en: 'Add', fr: 'Ajouter' },
  // ★ 2026-09-25 第二轮(Forrest):未分类系统行 / Move-to 类别分区 / 导入模板
  'practice.categorySystemNote': {
    zh: '系统保留 · 删除的类别归入此处',
    en: 'System · deleted categories land here',
    fr: 'Système · les catégories supprimées arrivent ici'
  },
  'tree.moveToCategory': { zh: '移动到类别', en: 'Move to category', fr: 'Déplacer vers la catégorie' },
  'practice.templateTitle': { zh: '导入模板', en: 'Import template', fr: 'Importer un modèle' },
  'practice.templatePlaceholder': { zh: '选择一个模板…', en: 'Choose a template…', fr: 'Choisir un modèle…' },
  'practice.templateImport': { zh: '导入', en: 'Import', fr: 'Importer' },
  'practice.tpl.jobInterview.label': { zh: '求职面试', en: 'Job Interview', fr: 'Entretien d\u2019embauche' },
  'practice.tpl.jobInterview.desc': {
    zh: '自我介绍、技术、行为与反问四段式面试素材骨架。',
    en: 'A self-intro / technical / behavioral / questions-to-ask skeleton.',
    fr: 'Un squelette présentation / technique / comportemental / questions.'
  },
  'practice.tpl.dailyEnglish.label': { zh: '生活英语', en: 'Daily English', fr: 'Anglais au quotidien' },
  'practice.tpl.dailyEnglish.desc': {
    zh: '日常寒暄、旅行出行、购物点餐三大场景。',
    en: 'Small talk, travel, and shopping & dining scenarios.',
    fr: 'Scénarios : discussions, voyages, achats et repas.'
  },
  'practice.tpl.studyPlan.label': { zh: '学习计划', en: 'Study Plan', fr: 'Plan d\u2019études' },
  'practice.tpl.studyPlan.desc': {
    zh: '每日练习打卡与错题复习笔记骨架。',
    en: 'Daily practice log and mistake-review notes skeleton.',
    fr: 'Journal quotidien et notes de révision des erreurs.'
  },
  // 模板骨架节点名(导入后可随意改名)
  'practice.ti.selfIntro': { zh: '自我介绍', en: 'Self-introduction', fr: 'Présentation' },
  'practice.ti.intro1m': { zh: '一分钟自我介绍', en: 'One-minute self-intro', fr: 'Présentation d\u2019une minute' },
  'practice.ti.whyCompany': { zh: '为什么选择这家公司', en: 'Why this company', fr: 'Pourquoi cette entreprise' },
  'practice.ti.tech': { zh: '技术问题', en: 'Technical questions', fr: 'Questions techniques' },
  'practice.ti.techList': { zh: '高频技术题清单', en: 'Frequent tech questions', fr: 'Questions techniques fréquentes' },
  'practice.ti.behavioral': { zh: '行为面试', en: 'Behavioral', fr: 'Comportemental' },
  'practice.ti.star': { zh: 'STAR 故事集', en: 'STAR stories', fr: 'Histoires STAR' },
  'practice.ti.questions': { zh: '反问环节', en: 'Questions to ask', fr: 'Questions à poser' },
  'practice.ti.questionsList': { zh: '问面试官的问题', en: 'For the interviewer', fr: 'Pour l\u2019intervieweur' },
  'practice.ti.dailyTalk': { zh: '日常寒暄', en: 'Small talk', fr: 'Discussions' },
  'practice.ti.smallTalkTopics': { zh: '寒暄话题库', en: 'Small-talk topics', fr: 'Sujets de discussion' },
  'practice.ti.travel': { zh: '旅行出行', en: 'Travel', fr: 'Voyage' },
  'practice.ti.airport': { zh: '机场与交通', en: 'Airport & transit', fr: 'Aéroport et transport' },
  'practice.ti.dining': { zh: '购物与点餐', en: 'Shopping & dining', fr: 'Achats et repas' },
  'practice.ti.restaurant': { zh: '餐厅点餐', en: 'Ordering at a restaurant', fr: 'Commander au restaurant' },
  'practice.ti.dailyPractice': { zh: '每日练习', en: 'Daily practice', fr: 'Pratique quotidienne' },
  'practice.ti.practiceLog': { zh: '练习打卡记录', en: 'Practice log', fr: 'Journal de pratique' },
  'practice.ti.notes': { zh: '复习笔记', en: 'Review notes', fr: 'Notes de révision' },
  'practice.ti.mistakeBook': { zh: '错题本', en: 'Mistake notebook', fr: 'Cahier d\u2019erreurs' },
  // ---------- 素材导入 / 导出(★ 2026-09-25 Forrest 第四轮) ----------
  'transfer.open': { zh: '导入 / 导出素材', en: 'Import / export materials', fr: 'Importer / exporter les supports' },
  'transfer.title': { zh: '素材管理', en: 'Manage materials', fr: 'Gérer les supports' },
  'transfer.categoriesTab': { zh: '类别', en: 'Categories', fr: 'Catégories' },
  'transfer.exportTab': { zh: '导出', en: 'Export', fr: 'Exporter' },
  'transfer.importTab': { zh: '导入', en: 'Import', fr: 'Importer' },
  'transfer.close': { zh: '关闭', en: 'Close', fr: 'Fermer' },
  'transfer.sourceCategory': { zh: '导出来源', en: 'Export from', fr: 'Exporter depuis' },
  'transfer.pickFolders': { zh: '选择要导出的内容', en: 'Choose what to export', fr: 'Choisir quoi exporter' },
  // ★ 第八轮:勾选语义 = 勾了什么导出什么(所见即所得)
  'transfer.pickHint': {
    zh: '勾选的内容才会导出；勾选文件夹会连里面的子项一起勾上。',
    en: 'Only checked items are exported; checking a folder also checks everything inside it.',
    fr: 'Seuls les éléments cochés sont exportés ; cocher un dossier coche aussi son contenu.'
  },
  'transfer.expandAll': { zh: '全部展开', en: 'Expand all', fr: 'Tout déplier' },
  'transfer.collapseAll': { zh: '全部收起', en: 'Collapse all', fr: 'Tout replier' },
  'transfer.selectAll': { zh: '全选', en: 'Select all', fr: 'Tout sélectionner' },
  'transfer.clearAll': { zh: '清空', en: 'Clear', fr: 'Effacer' },
  'transfer.format': { zh: '导出格式', en: 'Format', fr: 'Format' },
  'transfer.fmt.markdown': { zh: 'Markdown（带层级编号）', en: 'Markdown (numbered)', fr: 'Markdown (numéroté)' },
  'transfer.fmt.txt': { zh: '纯文本 TXT', en: 'Plain text (.txt)', fr: 'Texte brut (.txt)' },
  'transfer.fmt.pdf': { zh: 'PDF（打印 / 另存为）', en: 'PDF (print)', fr: 'PDF (impression)' },
  'transfer.fmt.json': { zh: 'JSON（可再导入）', en: 'JSON (re-importable)', fr: 'JSON (réimportable)' },
  'transfer.fmt.xml': { zh: 'XML（可再导入）', en: 'XML (re-importable)', fr: 'XML (réimportable)' },
  'transfer.fileName': { zh: '文件名', en: 'File name', fr: 'Nom du fichier' },
  'transfer.doExport': { zh: '导出文件', en: 'Export', fr: 'Exporter' },
  'transfer.exportDone': { zh: '已导出', en: 'Exported', fr: 'Exporté' },
  'transfer.exportNone': { zh: '请先勾选要导出的内容', en: 'Select at least one item first', fr: 'Sélectionnez au moins un élément' },
  'transfer.pdfHint': {
    zh: 'PDF 会打开系统打印窗口,在目标里选「另存为 PDF」即可。',
    en: 'PDF opens the print dialog — pick "Save as PDF" as destination.',
    fr: 'Le PDF ouvre la fenêtre d\u2019impression — choisissez « Enregistrer en PDF ».'
  },
  'transfer.importTarget': { zh: '导入到', en: 'Import into', fr: 'Importer dans' },
  'transfer.targetExisting': { zh: '已有类别', en: 'Existing category', fr: 'Catégorie existante' },
  'transfer.targetNew': { zh: '新建类别', en: 'New category', fr: 'Nouvelle catégorie' },
  'transfer.targetNone': { zh: '未分类', en: 'Uncategorized', fr: 'Non classés' },
  'transfer.newCategoryPlaceholder': { zh: '新类别名称', en: 'New category name', fr: 'Nom de la nouvelle catégorie' },
  'transfer.pickFile': { zh: '选择文件', en: 'Choose a file', fr: 'Choisir un fichier' },
  'transfer.fileHint': {
    zh: '支持 JSON / XML / TXT / Markdown;层级用编号或缩进表示,内容行写在标题下面;以 // 开头的行是说明,会被忽略。',
    en: 'JSON / XML / TXT / Markdown; hierarchy from numbering or indentation, content below its title. Lines starting with // are notes and are ignored.',
    fr: 'JSON / XML / TXT / Markdown; hiérarchie par numérotation ou indentation, contenu sous son titre. Les lignes commençant par // sont des notes ignorées.'
  },
  'transfer.preview': { zh: '预览', en: 'Preview', fr: 'Aperçu' },
  'transfer.previewCounts': { zh: '文件夹 {folders} · 素材 {files}', en: 'Folders {folders} · Items {files}', fr: 'Dossiers {folders} · Éléments {files}' },
  'transfer.doImport': { zh: '导入到素材树', en: 'Import into tree', fr: 'Importer dans l\u2019arbre' },
  'transfer.importEmpty': { zh: '没有识别到可导入的内容', en: 'No importable content found', fr: 'Aucun contenu importable' },
  'transfer.readFailed': { zh: '文件读取失败', en: 'Failed to read the file', fr: 'Échec de lecture du fichier' },
  // ★ 第五轮:文件名可编辑(基础名 + 自动扩展名)与风格化文件选择
  'transfer.fileNameHint': {
    zh: '扩展名会按所选格式自动添加，改格式时自动跟随。',
    en: 'The extension is added automatically and follows the chosen format.',
    fr: 'L\u2019extension est ajoutée automatiquement et suit le format choisi.'
  },
  'transfer.chooseFile': { zh: '浏览文件…', en: 'Browse…', fr: 'Parcourir…' },
  'transfer.noFilePicked': { zh: '未选择文件', en: 'No file chosen', fr: 'Aucun fichier choisi' },
  // ★ 第九轮:保存位置 = 用户自己选的本地文件夹(默认浏览器下载文件夹)
  'transfer.saveLocation': { zh: '保存位置', en: 'Save to', fr: 'Enregistrer dans' },
  'transfer.saveDefault': { zh: '下载文件夹（默认）', en: 'Downloads (default)', fr: 'Téléchargements (par défaut)' },
  'transfer.saveChange': { zh: '选择文件夹…', en: 'Choose folder…', fr: 'Choisir un dossier…' },
  'transfer.saveReset': { zh: '恢复默认', en: 'Use default', fr: 'Par défaut' },
  'transfer.saveDefaultHint': {
    zh: '默认存到浏览器的下载文件夹；点「选择文件夹」可指定本机任意目录，之后导出会直接写进去。',
    en: 'By default files land in your Downloads folder; choose a folder to write exports straight into it from then on.',
    fr: 'Par défaut, les fichiers vont dans Téléchargements ; choisissez un dossier pour y écrire les exports directement.'
  },
  'transfer.savePickedHint': {
    zh: '导出的文件会直接写入这个文件夹，不再弹出询问窗口。',
    en: 'Exported files are written straight into this folder — no extra dialog.',
    fr: 'Les fichiers exportés sont écrits directement dans ce dossier, sans fenêtre supplémentaire.'
  },
  'transfer.dirDenied': {
    zh: '所选文件夹的访问权限已失效，这次存到了下载文件夹；重新选择一次即可继续。',
    en: 'Access to the chosen folder expired — saved to Downloads this time. Pick the folder again to keep using it.',
    fr: 'L\u2019accès au dossier choisi a expiré — enregistré dans Téléchargements cette fois. Choisissez le dossier à nouveau.'
  },
  'transfer.dirFailed': {
    zh: '写入所选文件夹失败，已改存到下载文件夹。',
    en: 'Couldn\u2019t write to the chosen folder — saved to Downloads instead.',
    fr: 'Impossible d\u2019écrire dans le dossier choisi — enregistré dans Téléchargements.'
  },
  'transfer.saveUnsupported': {
    zh: '当前浏览器不支持选择本地文件夹，文件会直接存到下载文件夹（改用 Chrome / Edge 即可选择）。',
    en: 'This browser can\u2019t pick a local folder — files go straight to Downloads (switch to Chrome / Edge to choose one).',
    fr: 'Ce navigateur ne permet pas de choisir un dossier local — les fichiers vont dans Téléchargements (utilisez Chrome / Edge pour le choisir).'
  },
  // ★ 第六轮:导出可选路径(系统「另存为」)+ 导入示例文件
  'transfer.sampleBtn': { zh: '示例文件', en: 'Sample file', fr: 'Exemple' },
  'transfer.sampleDone': {
    zh: '示例文件已下载，按里面的说明填写后再选择导入',
    en: 'Sample downloaded — follow the instructions inside, then import it',
    fr: 'Exemple téléchargé — suivez les instructions, puis importez-le'
  },
  'transfer.sample.title': {
    zh: 'Your Interview 素材导入示例：按下面几条说明填写，保存后在「导入」页选择本文件。',
    en: 'Your Interview sample import file — follow the notes below, save, then pick this file on the Import tab.',
    fr: 'Exemple d\u2019import Your Interview — suivez les notes ci-dessous, enregistrez, puis choisissez ce fichier dans l\u2019onglet Importer.'
  },
  'transfer.sample.howtoBody': {
    zh: '以 // 开头的行是说明，导入时会自动忽略，可以留着也可以删掉。\n数字编号表示层级：1 是最外层，1.1 是它的子项，1.1.1 再深一层。\n带编号的行是标题（文件夹或素材名），不要以「> 」开头。\n正文写在标题正下方，每一行都以「> 」开头；需要空行就写一个单独的「>」。\n填好保存后，在「导入」页选择这个文件；也可以原样导入本文件先看看效果。',
    en: 'Lines starting with // are notes — ignored on import, so you can keep or delete them.\nThe numbering is the nesting: 1 is the top level, 1.1 its child, 1.1.1 goes one level deeper.\nA numbered line is a title (folder or material name) — never start it with "> ".\nWrite an item\'s content on the lines right below its title, each starting with "> "; use a single ">" for a blank line.\nSave the file, then pick it on the Import tab — or import this file as-is to see the result.',
    fr: 'Les lignes commençant par // sont des notes — ignorées à l\u2019import, à garder ou à supprimer.\nLa numérotation définit la hiérarchie : 1 est le niveau principal, 1.1 son enfant, 1.1.1 plus profond.\nUne ligne numérotée est un titre (dossier ou élément) — ne commencez jamais par « > ».\nÉcrivez le contenu d\u2019un élément juste sous son titre, chaque ligne commençant par « > » ; pour une ligne vide écrivez un simple « > ».\nEnregistrez le fichier, puis choisissez-le dans l\u2019onglet Importer — ou importez ce fichier tel quel pour voir le résultat.'
  },
  'transfer.sample.body1': {
    zh: '你好，我叫……。我有五年后端开发经验，主导过支付系统的重构。',
    en: 'Hello, my name is … I have five years of backend experience and led the re-platforming of our payment system.',
    fr: 'Bonjour, je m\u2019appelle … J\u2019ai cinq ans d\u2019expérience backend et j\u2019ai piloté la refonte de notre système de paiement.'
  },
  'transfer.sample.body2': {
    zh: '选一个你做过的并发系统：讲清楚需求、技术选型的取舍和最终结果。',
    en: 'Pick one high-traffic system you built: cover requirements, key trade-offs, and the outcome.',
    fr: 'Choisissez un système à fort trafic que vous avez construit : besoins, arbitrages clés et résultat.'
  },
  'transfer.sample.body3': {
    zh: '打扰一下，请问 CA123 航班的登机口在哪里？',
    en: 'Excuse me, which gate does flight CA123 board at?',
    fr: 'Excusez-moi, à quelle porte embarque le vol CA123 ?'
  },
  // 解析结果的提示走「词条 + 参数」,不在代码里硬写某一种语言
  'transfer.warn.jsonInvalid': { zh: 'JSON 解析失败：{detail}', en: 'Invalid JSON: {detail}', fr: 'JSON invalide : {detail}' },
  'transfer.warn.xmlInvalid': { zh: 'XML 格式有误，无法解析', en: 'Malformed XML, cannot parse', fr: 'XML mal formé, impossible à analyser' },
  'transfer.warn.none': { zh: '没有识别到可导入的结构', en: 'No importable structure found', fr: 'Aucune structure importable' },
  'transfer.warn.skipped': {
    zh: '开头 {count} 行没有标题，已忽略',
    en: 'Ignored {count} line(s) without a heading',
    fr: '{count} ligne(s) sans titre ignorée(s)'
  },
  // ---------- 统一确认弹窗(2026-09-20:全站弹窗同一种样式) ----------
  'dialog.cancel': { zh: '取消', en: 'Cancel', fr: 'Annuler' },
  // ★ 第五十轮(Forrest):删除类确认弹窗的按钮统一为「确认」——
  //   弹窗标题已经说明要做什么,按钮再写一遍"删除"是重复;
  //   确认弹窗只负责"确认/取消"这一件事。
  'dialog.deleteConfirm': { zh: '确认', en: 'Confirm', fr: 'Confirmer' },
  // ★ 第五十轮(Forrest):确认弹窗的按钮统一为「确认」——
  //   弹窗标题已经说明要做什么(保存/删除),按钮只负责"确认/取消"。
  //   (三选一的刷新拦截弹窗除外:每个按钮各代表一个动作,必须写明动作。)
  'dialog.saveConfirm': { zh: '确认', en: 'Confirm', fr: 'Confirmer' },
  // ★ 2026-09-23(Forrest 第九轮):弹窗只负责"确认/取消",不再解释存储机制 ——
  //   "会保存到数据库"是默认行为,不需要专门写出来。
  'dialog.saveTitle': { zh: '保存修改？', en: 'Save changes?', fr: 'Enregistrer les modifications ?' },
  'dialog.saveDone': { zh: '已保存', en: 'Saved', fr: 'Enregistré' },
  'dialog.discardToast': { zh: '已放弃未保存的修改', en: 'Unsaved changes discarded', fr: 'Modifications abandonnées' },
  'dialog.deleteNodeTitle': { zh: '删除「{name}」？', en: 'Delete "{name}"?', fr: 'Supprimer « {name} » ?' },
  'dialog.deleteNodeBody': {
    zh: '该条目及其子素材会一并删除，其上的录音也会被清掉。',
    en: 'The item and its children will be removed along with their recordings.',
    fr: "L'élément et ses enfants seront supprimés avec leurs enregistrements."
  },
  'dialog.deleteRecTitle': { zh: '删除这条录音？', en: 'Delete this recording?', fr: 'Supprimer cet enregistrement ?' },
  // ★ 第五十轮:录音删除弹窗不再附"无法恢复"说明行(第五十轮截图反馈:
  //   确认弹窗保持最简 —— 一句问话 + 取消/确认)。
  // ★ 2026-09-23(Forrest 第九轮):刷新拦截弹窗同样只说"保存还是放弃"。
  'dialog.unsavedTitle': { zh: '有未保存的修改', en: 'You have unsaved changes', fr: 'Modifications non enregistrées' },
  'dialog.unsavedBody': {
    zh: '刷新会丢失这些修改。要先保存吗？',
    en: 'Reloading will discard them. Save first?',
    fr: 'Le rechargement les perdra. Enregistrer d’abord ?'
  },
  'dialog.saveAndReload': { zh: '保存并刷新', en: 'Save & reload', fr: 'Enregistrer et recharger' },
  'dialog.discardAndReload': { zh: '放弃并刷新', en: 'Discard & reload', fr: 'Abandonner et recharger' },
  'dialog.stay': { zh: '留在本页', en: 'Stay on page', fr: 'Rester sur la page' },

  // ---------- 素材树控件(共享组件) ----------
  // ★ 2026-09-23(Forrest):树上的 tooltip / 菜单全部接入语言设置。
  // ★ 2026-09-23(Forrest 第十轮):树头标题恢复 —— "材料",随语言设置显示。
  'tree.title': { zh: '材料', en: 'Materials', fr: 'Documents' },
  'tree.expandAll': { zh: '展开全部', en: 'Expand all', fr: 'Tout déplier' },
  'tree.collapseAll': { zh: '收起全部', en: 'Collapse all', fr: 'Tout replier' },
  'tree.newFolder': { zh: '新建文件夹', en: 'New folder', fr: 'Nouveau dossier' },
  'tree.newSubFolder': { zh: '新建子文件夹', en: 'New subfolder', fr: 'Nouveau sous-dossier' },
  'tree.newFile': { zh: '新建素材', en: 'New material', fr: 'Nouveau document' },
  'tree.moveTo': { zh: '移动到…', en: 'Move to…', fr: 'Déplacer vers…' },
  'tree.rename': { zh: '重命名', en: 'Rename', fr: 'Renommer' },
  'tree.delete': { zh: '删除', en: 'Delete', fr: 'Supprimer' },
  'tree.rootLevel': { zh: '最外层', en: 'Top level', fr: 'Premier niveau' },
  'tree.noFolder': { zh: '没有其它可选文件夹', en: 'No other folder available', fr: 'Aucun autre dossier' },
  'tree.newFolderName': { zh: '新建文件夹', en: 'New folder', fr: 'Nouveau dossier' },
  'tree.newFileName': { zh: '新建素材', en: 'New material', fr: 'Nouveau document' },
  'tree.emptyHint': {
    zh: '还没有素材。用上方按钮新建文件夹与文件。',
    en: 'No materials yet. Use the buttons above to create folders and files.',
    fr: 'Aucun document. Utilisez les boutons ci-dessus pour créer dossiers et fichiers.'
  },

  // ---------- 分页 / 筛选 / 重试(全站 tooltip 通用) ----------
  'common.prevPage': { zh: '上一页', en: 'Previous page', fr: 'Page précédente' },
  'common.nextPage': { zh: '下一页', en: 'Next page', fr: 'Page suivante' },
  'common.clearFilter': { zh: '清空筛选', en: 'Clear filters', fr: 'Effacer les filtres' },
  'common.retry': { zh: '重试', en: 'Retry', fr: 'Réessayer' },
  'common.relogin': { zh: '重新登录', en: 'Sign in again', fr: 'Se reconnecter' },
  'common.httpStatus': { zh: 'HTTP {n}', en: 'HTTP {n}', fr: 'HTTP {n}' },
  // ---------- 管理后台(M2.4 全量补全) ----------
  'admin.refreshStats': { zh: '刷新统计', en: 'Refresh stats', fr: 'Actualiser les stats' },
  'admin.activate': { zh: '启用', en: 'Activate', fr: 'Activer' },
  'admin.deactivate': { zh: '停用', en: 'Deactivate', fr: 'Désactiver' },
  'admin.resetPwd': { zh: '重置密码', en: 'Reset password', fr: 'Réinitialiser le mot de passe' },
  'admin.title': { zh: '管理后台', en: 'Admin console', fr: 'Administration' },
  'admin.sub': { zh: '用户、角色与操作审计', en: 'Users, roles and audit trail', fr: 'Utilisateurs, rôles et audit' },
  'admin.tabUsers': { zh: '用户', en: 'Users', fr: 'Utilisateurs' },
  'admin.tabRoles': { zh: '角色', en: 'Roles', fr: 'Rôles' },
  'admin.tabAudit': { zh: '审计日志', en: 'Audit log', fr: 'Journal d’audit' },
  'admin.metricUsers': { zh: '用户总数', en: 'Total users', fr: 'Utilisateurs' },
  'admin.metricActive': { zh: '活跃用户', en: 'Active users', fr: 'Actifs' },
  'admin.metricNew7d': { zh: '近 7 天新增', en: 'New (7d)', fr: 'Nouveaux (7j)' },
  'admin.metricRoles': { zh: '角色数', en: 'Roles', fr: 'Rôles' },
  'admin.metricAudit': { zh: '审计事件', en: 'Audit events', fr: 'Événements' },
  'admin.metricFailed': { zh: '24h 失败登录', en: 'Failed logins (24h)', fr: 'Échecs (24h)' },
  'admin.metricLocked': { zh: '锁定账号', en: 'Locked accounts', fr: 'Comptes verrouillés' },
  'admin.searchUser': { zh: '搜索邮箱 / 显示名', en: 'Search email / name', fr: 'Courriel / nom' },
  'admin.totalUsers': { zh: '共 {n} 个用户', en: '{n} users', fr: '{n} utilisateurs' },
  'admin.totalLogs': { zh: '共 {n} 条记录', en: '{n} records', fr: '{n} entrées' },
  'admin.noUser': { zh: '没有匹配的用户。', en: 'No matching users.', fr: 'Aucun utilisateur.' },
  'admin.noRole': { zh: '还没有定义任何角色。', en: 'No roles defined yet.', fr: 'Aucun rôle.' },
  'admin.noLog': { zh: '暂无审计记录。', en: 'No audit records yet.', fr: 'Aucune entrée d’audit.' },
  'admin.colEmail': { zh: '邮箱', en: 'Email', fr: 'Courriel' },
  'admin.colName': { zh: '显示名', en: 'Display name', fr: 'Nom' },
  'admin.colRoles': { zh: '角色', en: 'Roles', fr: 'Rôles' },
  'admin.colStatus': { zh: '状态', en: 'Status', fr: 'Statut' },
  'admin.colLastLogin': { zh: '最近登录', en: 'Last login', fr: 'Dernière connexion' },
  'admin.colActions': { zh: '操作', en: 'Actions', fr: 'Actions' },
  'admin.colTime': { zh: '时间', en: 'Time', fr: 'Heure' },
  'admin.colAction': { zh: '动作', en: 'Action', fr: 'Action' },
  'admin.colTarget': { zh: '对象', en: 'Target', fr: 'Cible' },
  'admin.colActor': { zh: '操作人', en: 'Actor', fr: 'Auteur' },
  'admin.colIp': { zh: 'IP', en: 'IP', fr: 'IP' },
  'admin.colDetail': { zh: '详情', en: 'Detail', fr: 'Détail' },
  'admin.newUser': { zh: '新建用户', en: 'New user', fr: 'Nouvel utilisateur' },
  'admin.editUser': { zh: '编辑用户', en: 'Edit user', fr: 'Modifier' },
  'admin.fieldEmail': { zh: '邮箱', en: 'Email', fr: 'Courriel' },
  'admin.fieldName': { zh: '显示名', en: 'Display name', fr: 'Nom' },
  'admin.fieldPassword': { zh: '密码', en: 'Password', fr: 'Mot de passe' },
  'admin.pwdHint': {
    zh: '至少 12 位,需含大写、小写、数字与符号',
    en: 'At least 12 chars with upper, lower, digit and symbol',
    fr: '12 caractères min. : majuscule, minuscule, chiffre et symbole'
  },
  'admin.pwdHintReset': { zh: '至少 8 位,建议含大小写字母与数字', en: 'At least 8 chars; mix case and digits', fr: '8 caractères min.' },
  'admin.fieldRoles': { zh: '角色', en: 'Roles', fr: 'Rôles' },
  'admin.rolesHint': { zh: '至少选择一个角色', en: 'Pick at least one role', fr: 'Choisissez au moins un rôle' },
  'admin.activeToggle': { zh: '账号启用', en: 'Account active', fr: 'Compte actif' },
  'admin.forcePwdChange': { zh: '下次登录强制改密', en: 'Force password change', fr: 'Changement obligatoire' },
  'admin.badgePwd': { zh: '待改密', en: 'Must change', fr: 'À changer' },
  'admin.badgeLocked': { zh: '已锁定', en: 'Locked', fr: 'Verrouillé' },
  'admin.unlock': { zh: '解锁账号', en: 'Unlock account', fr: 'Déverrouiller' },
  'admin.confirmActivate': { zh: '确认启用账号 {n}?', en: 'Activate {n}?', fr: 'Activer {n}?' },
  'admin.confirmDeactivate': { zh: '确认停用账号 {n}?', en: 'Deactivate {n}?', fr: 'Désactiver {n}?' },
  'admin.confirmDelete': { zh: '确认删除用户 {n}?此操作不可撤销。', en: 'Delete {n}? This cannot be undone.', fr: 'Supprimer {n}? Irréversible.' },
  'admin.confirmDeleteRole': { zh: '确认删除角色 {n}?', en: 'Delete role {n}?', fr: 'Supprimer le rôle {n}?' },
  'admin.saved': { zh: '已保存用户', en: 'User saved', fr: 'Enregistré' },
  'admin.created': { zh: '已创建用户', en: 'User created', fr: 'Créé' },
  'admin.deleted': { zh: '已删除用户', en: 'User deleted', fr: 'Supprimé' },
  'admin.activated': { zh: '已启用', en: 'Activated', fr: 'Activé' },
  'admin.deactivated': { zh: '已停用', en: 'Deactivated', fr: 'Désactivé' },
  'admin.unlocked': { zh: '账号已解锁', en: 'Account unlocked', fr: 'Déverrouillé' },
  'admin.pwdReset': { zh: '密码已重置,请通知用户', en: 'Password reset — notify the user', fr: 'Réinitialisé — informez l’utilisateur' },
  'admin.roleSaved': { zh: '已保存角色', en: 'Role saved', fr: 'Rôle enregistré' },
  'admin.roleDeleted': { zh: '已删除角色', en: 'Role deleted', fr: 'Rôle supprimé' },
  'admin.newRole': { zh: '新建角色', en: 'New role', fr: 'Nouveau rôle' },
  'admin.editRole': { zh: '编辑角色', en: 'Edit role', fr: 'Modifier le rôle' },
  'admin.fieldRoleName': { zh: '角色名', en: 'Role name', fr: 'Nom du rôle' },
  'admin.fieldRoleDesc': { zh: '说明', en: 'Description', fr: 'Description' },
  'admin.permCount': { zh: '{n} 项权限', en: '{n} permissions', fr: '{n} permissions' },
  'admin.userCount': { zh: '{n} 个用户', en: '{n} users', fr: '{n} utilisateurs' },
  'admin.systemRole': { zh: '系统内置', en: 'Built-in', fr: 'Système' },
  'admin.systemRoleReadonly': {
    zh: '系统内置角色不可删除,名称不可改(说明可维护)',
    en: 'Built-in roles cannot be deleted or renamed (description is editable)',
    fr: 'Rôles système : suppression et renommage interdits'
  },
  'admin.noPerm': { zh: '该角色没有任何权限', en: 'This role has no permissions', fr: 'Aucune permission' },
  'admin.selectAll': { zh: '全选', en: 'Select all', fr: 'Tout cocher' },
  'admin.clearAll': { zh: '全不选', en: 'Clear all', fr: 'Tout décocher' },
  'admin.filterAction': { zh: '按动作筛选', en: 'Filter by action', fr: 'Filtrer par action' },
  'admin.filterActor': { zh: '按操作人筛选', en: 'Filter by actor', fr: 'Filtrer par auteur' },
  'admin.filterFrom': { zh: '起始时间', en: 'From', fr: 'Du' },
  'admin.filterTo': { zh: '结束时间', en: 'To', fr: 'Au' },
  'admin.success': { zh: '成功', en: 'Success', fr: 'Succès' },
  'admin.failed': { zh: '失败', en: 'Failed', fr: 'Échec' },
  'admin.yourAccount': { zh: '你的账号', en: 'Your account', fr: 'Votre compte' },
  'admin.pageInfo': { zh: '第 {n} / {m} 页', en: 'Page {n} of {m}', fr: 'Page {n} sur {m}' },
  'mock.newSession': { zh: '新建模拟', en: 'New mock', fr: 'Nouvelle simulation' },
  'playbook.newEntry': { zh: '新建条目', en: 'New entry', fr: 'Nouvelle entrée' },
  'playbook.qaCount': { zh: '问答数', en: 'Q&A count', fr: 'Nombre de questions' },
  'playbook.weakCount': { zh: '短板数', en: 'Weak points', fr: 'Points faibles' },
  'playbook.assetCount': { zh: '材料数', en: 'Assets', fr: 'Documents' },
  'techstack.newEntry': { zh: '新增条目', en: 'Add entry', fr: 'Ajouter une entrée' },
  'techstack.topicDerived': {
    zh: '主题来自当前结果推导，后端主题接口不可用',
    en: 'Topics are derived from current results — the backend topic API is unavailable',
    fr: 'Thèmes déduits des résultats — l’API thèmes du serveur est indisponible'
  },
  'techstack.fromPlaybook': {
    zh: '来自实战机经复盘',
    en: 'From playbook reviews',
    fr: 'Depuis les revues du journal'
  },
  'techstack.startReview': { zh: '今日复习', en: 'Review due', fr: 'Réviser' },
  'techstack.showAnswer': { zh: '显示答案', en: 'Show answer', fr: 'Voir la réponse' },
  'techstack.revealHint': {
    zh: '先自己想一遍，再对照答案',
    en: 'Recall it yourself before checking',
    fr: 'Essayez de vous rappeler avant de vérifier'
  },
  'techstack.reviewExit': { zh: '退出', en: 'Exit', fr: 'Quitter' },
  'techstack.reviewDone': { zh: '本轮复习完成', en: 'Session complete', fr: 'Session terminée' },
  'techstack.reviewDoneMsg': {
    zh: '你复习了 {n} 个知识点，明天它们会以新的间隔回来。',
    en: 'You reviewed {n} items — they will return on a fresh SM-2 schedule.',
    fr: 'Vous avez révisé {n} notions — elles reviendront selon un nouveau rythme SM-2.'
  },
  'techstack.reviewEmpty': { zh: '今天没有待复习，棒！', en: 'Nothing due today — nice!', fr: 'Rien à réviser aujourd’hui !' },
  'techstack.reviewEmptyMsg': {
    zh: '保持节奏，有新的遗忘曲线到期时再来。',
    en: 'Keep the streak — come back when new items fall due.',
    fr: 'Gardez le rythme — revenez quand de nouvelles notions seront dues.'
  },
  'techstack.reviewNoDue': { zh: '暂时没有到期的知识点', en: 'No items are due right now', fr: 'Aucune notion n’est due pour l’instant' },
  'tracker.newApp': { zh: '新建投递', en: 'New application', fr: 'Nouvelle candidature' },
  'tracker.scrollLeft': { zh: '向左滚动', en: 'Scroll left', fr: 'Défiler vers la gauche' },
  'tracker.scrollRight': { zh: '向右滚动', en: 'Scroll right', fr: 'Défiler vers la droite' },
  'tracker.viewMode': { zh: '视图模式', en: 'View mode', fr: "Mode d'affichage" },
  'tracker.boardView': { zh: '看板视图', en: 'Board view', fr: 'Vue tableau' },
  'tracker.listView': { zh: '列表视图', en: 'List view', fr: 'Vue liste' },
  'tracker.columns': { zh: '列', en: 'Columns', fr: 'Colonnes' },
  'tracker.visibleColumns': { zh: '可见列', en: 'Visible columns', fr: 'Colonnes visibles' },
  'tracker.hideColumn': { zh: '隐藏此列', en: 'Hide column', fr: 'Masquer la colonne' },
  'tracker.changeStatus': { zh: '更改状态', en: 'Change status', fr: 'Changer le statut' },
  'tracker.statusChangeFailed': { zh: '状态更新失败,已回滚', en: 'Status update failed, reverted', fr: 'Échec de la mise à jour, annulée' },
  'tracker.status': { zh: '状态', en: 'Status', fr: 'Statut' },
  'tracker.exportCsv': { zh: '导出 CSV', en: 'Export CSV', fr: 'Exporter CSV' },
  'tracker.exportEmpty': { zh: '没有可导出的数据', en: 'Nothing to export', fr: 'Rien à exporter' },
  'tracker.followupHint': { zh: '需要跟进:已到跟进时间,或早期阶段投递超过14天无进展', en: 'Follow up needed', fr: 'Suivi nécessaire' },
  'tracker.prepareInterview': { zh: '备战:创建面试准备条目', en: 'Prepare: create interview prep entry', fr: 'Préparer' },
  'tracker.prepShort': { zh: '备战', en: 'Prep', fr: 'Préparer' },
  'tracker.prepCreated': { zh: '已创建备战条目,正在跳转', en: 'Prep entry created, navigating', fr: 'Créé' },
  'tracker.openLink': { zh: '打开岗位链接', en: 'Open job link', fr: 'Ouvrir le lien de l’offre' },
  'tracker.priority': { zh: '优先级 {n}', en: 'Priority {n}', fr: 'Priorité {n}' },
  'tracker.firstApp': { zh: '记录第一条投递', en: 'Log your first application', fr: 'Ajoutez votre première candidature' },
  'tracker.sortUpdated': { zh: '最近更新', en: 'Recently updated', fr: 'Récemment mis à jour' },
  'tracker.sortAppliedDesc': { zh: '投递日期(新→旧)', en: 'Applied (new→old)', fr: 'Candidature (récent→ancien)' },
  'tracker.sortAppliedAsc': { zh: '投递日期(旧→新)', en: 'Applied (old→new)', fr: 'Candidature (ancien→récent)' },
  'tracker.sortCompany': { zh: '公司名 A→Z', en: 'Company A→Z', fr: 'Entreprise A→Z' },
  'tracker.sortDeadline': { zh: '截止日期', en: 'Deadline', fr: 'Date limite' },
  'tracker.openAnswerLibrary': { zh: '问答库', en: 'Answers', fr: 'Réponses' },
  'tracker.insertAnswer': { zh: '插入常用回答', en: 'Insert answer', fr: 'Insérer une réponse' },
  'tracker.alTitle': { zh: '申请问答库', en: 'Answer library', fr: 'Bibliothèque de réponses' },
  'tracker.alNew': { zh: '新增条目', en: 'New entry', fr: 'Nouvelle entrée' },
  'tracker.alEdit': { zh: '编辑条目', en: 'Edit entry', fr: 'Modifier l’entrée' },
  'tracker.alCategory': { zh: '分类', en: 'Category', fr: 'Catégorie' },
  'tracker.alQuestion': { zh: '问题', en: 'Question', fr: 'Question' },
  'tracker.alAnswer': { zh: '我的标准回答', en: 'My answer', fr: 'Ma réponse' },
  'tracker.alEmpty': { zh: '还没有常用回答,点"新增条目"沉淀第一条。', en: 'No saved answers yet — add your first one.', fr: 'Aucune réponse enregistrée — ajoutez la première.' },
  'tracker.alInsert': { zh: '插入', en: 'Insert', fr: 'Insérer' },
  'tracker.alManage': { zh: '管理', en: 'Manage', fr: 'Gérer' },
  'tracker.alConfirmDelete': { zh: '确认删除这条常用回答?', en: 'Delete this saved answer?', fr: 'Supprimer cette réponse ?' },
  'tracker.alSaved': { zh: '已保存', en: 'Saved', fr: 'Enregistré' },
  'tracker.alCreated': { zh: '已新增常用回答', en: 'Answer added', fr: 'Réponse ajoutée' },
  'tracker.alDeleted': { zh: '已删除', en: 'Deleted', fr: 'Supprimé' },
  'tracker.comms': { zh: '沟通记录', en: 'Communications', fr: 'Communications' },
  'tracker.commsEmpty': { zh: '还没有沟通记录,点"新增"记录一次联系。', en: 'No communications yet — log your first contact.', fr: 'Aucune communication — enregistrez le premier contact.' },
  'tracker.commsAdd': { zh: '新增沟通', en: 'Add', fr: 'Ajouter' },
  'tracker.commsEdit': { zh: '编辑沟通', en: 'Edit', fr: 'Modifier' },
  'tracker.commsType': { zh: '类型', en: 'Type', fr: 'Type' },
  'tracker.commsSubject': { zh: '主题', en: 'Subject', fr: 'Objet' },
  'tracker.commsContent': { zh: '内容', en: 'Content', fr: 'Contenu' },
  'tracker.commsContact': { zh: '联系人', en: 'Contact', fr: 'Contact' },
  'tracker.commsOccurredAt': { zh: '发生时间', en: 'When', fr: 'Quand' },
  'tracker.commsConfirmDelete': { zh: '确认删除这条沟通记录?', en: 'Delete this communication?', fr: 'Supprimer cette communication ?' },
  'tracker.commsSaved': { zh: '已保存沟通记录', en: 'Communication saved', fr: 'Communication enregistrée' },
  'tracker.commsDeleted': { zh: '已删除沟通记录', en: 'Communication deleted', fr: 'Communication supprimée' },
  'tracker.commsTypeEmail': { zh: '邮件', en: 'Email', fr: 'E-mail' },
  'tracker.commsTypeCall': { zh: '电话', en: 'Call', fr: 'Appel' },
  'tracker.commsTypeInterview': { zh: '面试', en: 'Interview', fr: 'Entretien' },
  'tracker.commsTypeMessage': { zh: '消息', en: 'Message', fr: 'Message' },
  'tracker.commsTypeNote': { zh: '笔记', en: 'Note', fr: 'Note' },
  'tracker.commsUnknown': { zh: '其他', en: 'Other', fr: 'Autre' },

  // ---------- 投递跟踪 · 主页面(2026-10-07 i18n 补齐) ----------
  'tracker.subtitle': { zh: '按状态分列看清整个漏斗,卡住在哪一步一目了然', en: 'See the whole funnel by status — spot where you are stuck at a glance', fr: 'Visualisez l’entonnoir par statut — repérez d’un coup d’œil où vous bloquez' },
  'tracker.smartAdd': { zh: '智能粘贴', en: 'Smart paste', fr: 'Collage intelligent' },
  'tracker.total': { zh: '总投递', en: 'Total', fr: 'Total' },
  'tracker.searchLabel': { zh: '搜索公司 / 职位 / 备注', en: 'Search company / role / notes', fr: 'Rechercher entreprise / poste / notes' },
  'tracker.searchPlaceholder': { zh: '例如 Shopify', en: 'e.g. Shopify', fr: 'p. ex. Shopify' },
  'tracker.sortLabel': { zh: '排序', en: 'Sort', fr: 'Trier' },
  'tracker.empty': { zh: '还没有投递记录。', en: 'No applications yet.', fr: 'Aucune candidature pour le moment.' },
  'tracker.needsConnect': { zh: '需先 Connect', en: 'Connect first', fr: 'Connecter d’abord' },
  'tracker.matchScore': { zh: '匹配 {n}', en: 'Match {n}', fr: 'Score {n}' },
  'tracker.pager': { zh: '第{p} / {tp} 页 · 共 {t} 条', en: 'Page {p} of {tp} · {t} total', fr: 'Page {p} sur {tp} · {t} au total' },

  // ---------- 投递跟踪 · 新建/编辑弹窗 ----------
  'tracker.dlgEdit': { zh: '编辑投递', en: 'Edit application', fr: 'Modifier la candidature' },
  'tracker.dlgNew': { zh: '新建投递', en: 'New application', fr: 'Nouvelle candidature' },
  'tracker.fCompany': { zh: '公司名称', en: 'Company', fr: 'Entreprise' },
  'tracker.fRole': { zh: '职位', en: 'Role', fr: 'Poste' },
  'tracker.fLocation': { zh: '地点', en: 'Location', fr: 'Lieu' },
  'tracker.fLocationPh': { zh: '如 Toronto / Remote', en: 'e.g. Toronto / Remote', fr: 'p. ex. Toronto / Remote' },
  'tracker.fSalary': { zh: '薪资范围', en: 'Salary range', fr: 'Fourchette salariale' },
  'tracker.fSalaryPh': { zh: '如 90k-110k CAD', en: 'e.g. 90k-110k CAD', fr: 'p. ex. 90k-110k CAD' },
  'tracker.fStatus': { zh: '状态', en: 'Status', fr: 'Statut' },
  'tracker.fPriority': { zh: '优先级', en: 'Priority', fr: 'Priorité' },
  'tracker.fAppliedDate': { zh: '投递日期', en: 'Applied on', fr: 'Date de candidature' },
  'tracker.fOutreach': { zh: '外联留言', en: 'Outreach note', fr: 'Note de prospection' },
  'tracker.fOutreachPh': { zh: '如 已发 LinkedIn 私信', en: 'e.g. Sent LinkedIn DM', fr: 'p. ex. MP LinkedIn envoyé' },
  'tracker.fLink': { zh: '岗位链接', en: 'Job link', fr: 'Lien de l’offre' },
  'tracker.fNotes': { zh: '备注', en: 'Notes', fr: 'Notes' },
  'tracker.fConnectFirst': { zh: '需要先建立人脉(如先 Connect 再内推)', en: 'Need to network first (e.g. connect before referral)', fr: 'Réseauter d’abord (p. ex. se connecter avant une recommandation)' },
  'tracker.required': { zh: '公司名称与职位为必填项', en: 'Company and role are required', fr: 'L’entreprise et le poste sont requis' },
  'tracker.confirmDelete': { zh: '确认删除「{a}」这条投递记录?此操作不可撤销。', en: 'Delete the application “{a}”? This cannot be undone.', fr: 'Supprimer la candidature « {a} » ? Irréversible.' },
  'tracker.deleted': { zh: '已删除', en: 'Deleted', fr: 'Supprimé' },
  'tracker.created': { zh: '已创建投递记录', en: 'Application created', fr: 'Candidature créée' },
  'tracker.saved': { zh: '已保存修改', en: 'Changes saved', fr: 'Modifications enregistrées' },
  'tracker.opFailed': { zh: '操作失败,请重试。', en: 'Operation failed, please retry.', fr: 'Échec de l’opération, réessayez.' },

  // ---------- 投递跟踪 · 详情弹窗 ----------
  'tracker.dOverview': { zh: '概览', en: 'Overview', fr: 'Aperçu' },
  'tracker.dPriority': { zh: '优先级', en: 'Priority', fr: 'Priorité' },
  'tracker.dNeedNetwork': { zh: '需先建立人脉', en: 'Network first', fr: 'Réseauter d’abord' },
  'tracker.dRejectReason': { zh: '拒因:', en: 'Rejection reason:', fr: 'Motif du refus :' },
  'tracker.dLocation': { zh: '地点', en: 'Location', fr: 'Lieu' },
  'tracker.dSalary': { zh: '薪资', en: 'Salary', fr: 'Salaire' },
  'tracker.dAppliedDate': { zh: '投递日期', en: 'Applied on', fr: 'Date de candidature' },
  'tracker.dOutreachMsg': { zh: '外联留言', en: 'Outreach message', fr: 'Message de prospection' },
  'tracker.dResumeMatch': { zh: '简历匹配度', en: 'Resume match', fr: 'Adéquation CV' },
  'tracker.dPassRate': { zh: '预估通过率', en: 'Est. pass rate', fr: 'Taux de réussite estimé' },
  'tracker.dCreatedAt': { zh: '创建时间', en: 'Created', fr: 'Créé le' },
  'tracker.dUpdatedAt': { zh: '最近更新', en: 'Updated', fr: 'Mis à jour le' },
  'tracker.dScoreUnit': { zh: '分', en: 'pts', fr: 'pts' },
  'tracker.dJdSummary': { zh: 'JD 摘要', en: 'JD summary', fr: 'Résumé de l’offre' },
  'tracker.dMatchAnalysis': { zh: '简历匹配分析', en: 'Resume match analysis', fr: 'Analyse d’adéquation CV' },
  'tracker.dHitKw': { zh: '命中 {n} / {m} 个 JD 技术关键词', en: '{n} of {m} JD tech keywords matched', fr: '{n} mots-clés techniques sur {m} trouvés' },
  'tracker.dMissingKw': { zh: '缺少的关键词 ({n})', en: 'Missing keywords ({n})', fr: 'Mots-clés manquants ({n})' },
  'tracker.dHitKwTitle': { zh: '已命中 ({n})', en: 'Matched ({n})', fr: 'Trouvés ({n})' },
  'tracker.dSoftSkills': { zh: '另提及软素质要求(不计入分数):', en: 'Also mentioned soft skills (not scored):', fr: 'Compétences générales mentionnées (non notées) :' },
  'tracker.dCoverLetter': { zh: '求职信', en: 'Cover letter', fr: 'Lettre de motivation' },
  'tracker.dClChars': { zh: '{n} 字符', en: '{n} chars', fr: '{n} caractères' },
  'tracker.dClStale': { zh: '这封信基于简历 v{a} 生成,当前简历已是 v{b} —— 建议重新生成。', en: 'Generated from resume v{a}; current resume is v{b} — consider regenerating.', fr: 'Générée depuis le CV v{a} ; le CV actuel est v{b} — régénérez.' },
  'tracker.dClExtra': { zh: '额外要求(可选)', en: 'Extra requirements (optional)', fr: 'Exigences supplémentaires (facultatif)' },
  'tracker.dClExtraPh': { zh: '例:强调我在 Citigroup 的低延迟交易经验,语气务实一些', en: 'e.g. emphasize my low-latency trading experience at Citigroup, pragmatic tone', fr: 'p. ex. souligner mon expérience trading basse latence chez Citigroup, ton pragmatique' },
  'tracker.dClHint': { zh: 'AI 会读你的简历 + JD 全文 + 公司情报,再叠加这里的补充。', en: 'AI reads your resume + full JD + company intel, plus your notes here.', fr: 'L’IA lit votre CV + l’offre complète + les infos entreprise, plus vos notes.' },
  'tracker.dClGenerating': { zh: '正在生成…通常 20-60 秒,请勿关闭窗口。', en: 'Generating… usually 20–60 seconds, please keep this open.', fr: 'Génération… 20 à 60 secondes en général, gardez la fenêtre ouverte.' },
  'tracker.dClRegen': { zh: '重新生成', en: 'Regenerate', fr: 'Régénérer' },
  'tracker.dClGenerate': { zh: 'AI 生成', en: 'Generate with AI', fr: 'Générer avec l’IA' },
  'tracker.dClPreview': { zh: '预览', en: 'Preview', fr: 'Aperçu' },
  'tracker.dClEditMan': { zh: '手动编辑', en: 'Edit manually', fr: 'Modifier manuellement' },
  'tracker.dClConfirm': { zh: '标记为已确认', en: 'Mark as confirmed', fr: 'Marquer comme confirmée' },
  'tracker.dClEmpty': { zh: '还没有求职信。点上面的按钮,基于你的简历与该岗位 JD 生成一封。', en: 'No cover letter yet. Use the button above to generate one from your resume and this JD.', fr: 'Aucune lettre pour le moment. Utilisez le bouton ci-dessus pour en générer une.' },
  'tracker.dJdFull': { zh: 'JD 全文', en: 'Full JD', fr: 'Offre complète' },
  'tracker.dJdChars': { zh: '{n} 字符', en: '{n} chars', fr: '{n} caractères' },
  'tracker.dViewPosting': { zh: '查看原始发布页', en: 'View original posting', fr: 'Voir l’annonce d’origine' },
  'tracker.dCompanyIntel': { zh: '公司情报', en: 'Company intel', fr: 'Infos entreprise' },
  'tracker.dNotes': { zh: '备注', en: 'Notes', fr: 'Notes' },
  'tracker.dHistory': { zh: '状态流转', en: 'Status history', fr: 'Historique des statuts' },
  'tracker.dRounds': { zh: '面试轮次', en: 'Interview rounds', fr: 'Tours d’entretien' },
  'tracker.dRoundN': { zh: '第 {n} 轮', en: 'Round {n}', fr: 'Tour {n}' },
  'tracker.dJobLink': { zh: '岗位链接', en: 'Job link', fr: 'Lien de l’offre' },
  'tracker.dCommSubjectPh': { zh: '如 终面安排', en: 'e.g. Final interview scheduling', fr: 'p. ex. Planification de l’entretien final' },
  'tracker.dCommContactPh': { zh: '如 张经理', en: 'e.g. Hiring manager', fr: 'p. ex. Responsable du recrutement' },
  'tracker.dClOverwrite': { zh: '这会用新生成的内容覆盖当前求职信(包括你手改的部分)。继续?', en: 'This will overwrite the current cover letter (including your manual edits). Continue?', fr: 'Cela écrasera la lettre actuelle (y compris vos modifications). Continuer ?' },
  'tracker.dClEmptyErr': { zh: '求职信内容不能为空。', en: 'Cover letter cannot be empty.', fr: 'La lettre ne peut pas être vide.' },
  'tracker.dClDelete': { zh: '删除这封求职信?此操作不可撤销。', en: 'Delete this cover letter? This cannot be undone.', fr: 'Supprimer cette lettre ? Irréversible.' },
  'tracker.dClStatusGen': { zh: 'AI 生成', en: 'AI generated', fr: 'Générée par l’IA' },
  'tracker.dClStatusFinal': { zh: '已确认', en: 'Confirmed', fr: 'Confirmée' },
  'tracker.dClStatusDraft': { zh: '草稿', en: 'Draft', fr: 'Brouillon' },
  'tracker.dStrongMatch': { zh: '强匹配', en: 'Strong match', fr: 'Forte adéquation' },
  'tracker.dMedMatch': { zh: '一般匹配', en: 'Moderate match', fr: 'Adéquation moyenne' },
  'tracker.dWeakMatch': { zh: '弱匹配', en: 'Weak match', fr: 'Faible adéquation' },
  'tracker.historyCreated': { zh: '新建时设置', en: 'Set on creation', fr: 'Défini à la création' },

  // ---------- 投递跟踪 · 智能粘贴弹窗 ----------
  'tracker.saJd': { zh: '智能粘贴岗位', en: 'Smart paste job', fr: 'Collage intelligent d’offre' },
  'tracker.saInvite': { zh: '粘贴面试邀请', en: 'Paste interview invite', fr: 'Coller l’invitation d’entretien' },
  'tracker.saTabJd': { zh: '岗位 JD', en: 'Job JD', fr: 'Offre d’emploi' },
  'tracker.saTabInvite': { zh: '面试邀请', en: 'Interview invite', fr: 'Invitation d’entretien' },
  'tracker.saPasteJd': { zh: '粘贴 JD 链接 / 正文 / 截图', en: 'Paste JD link / text / screenshot', fr: 'Collez le lien / texte / capture de l’offre' },
  'tracker.saPasteInvite': { zh: '粘贴邀请邮件或消息', en: 'Paste the invite email or message', fr: 'Collez l’e-mail ou le message d’invitation' },
  'tracker.saPastePh': { zh: '支持 LinkedIn / Indeed / Greenhouse / Workday / 公司官网链接,或直接粘贴 JD 正文', en: 'Supports LinkedIn / Indeed / Greenhouse / Workday / company site links, or paste the JD text directly', fr: 'Accepte les liens LinkedIn / Indeed / Greenhouse / Workday / site entreprise, ou collez le texte de l’offre' },
  'tracker.saAiFill': { zh: '用 AI 补全规则解不出的字段', en: 'Let AI fill fields that rules cannot parse', fr: 'Laisser l’IA remplir les champs non reconnus' },
  'tracker.saParse': { zh: '解析', en: 'Parse', fr: 'Analyser' },
  'tracker.saReparse': { zh: '重新解析', en: 'Re-parse', fr: 'Réanalyser' },
  'tracker.saStep2': { zh: '确认并修改结果', en: 'Review and edit', fr: 'Vérifier et modifier' },
  'tracker.saCompany': { zh: '公司', en: 'Company', fr: 'Entreprise' },
  'tracker.saRole': { zh: '职位', en: 'Role', fr: 'Poste' },
  'tracker.saLocation': { zh: '地点', en: 'Location', fr: 'Lieu' },
  'tracker.saSalary': { zh: '薪资', en: 'Salary', fr: 'Salaire' },
  'tracker.saWorkMode': { zh: '办公形式', en: 'Work mode', fr: 'Mode de travail' },
  'tracker.saUnrecognized': { zh: '未识别', en: 'Not recognized', fr: 'Non reconnu' },
  'tracker.saPriority': { zh: '优先级', en: 'Priority', fr: 'Priorité' },
  'tracker.saDeadline': { zh: '申请截止', en: 'Deadline', fr: 'Date limite' },
  'tracker.saInitStatus': { zh: '初始状态', en: 'Initial status', fr: 'Statut initial' },
  'tracker.saJobLink': { zh: '岗位链接', en: 'Job link', fr: 'Lien de l’offre' },
  'tracker.saJdSummary': { zh: 'JD 摘要', en: 'JD summary', fr: 'Résumé de l’offre' },
  'tracker.saKeywords': { zh: '匹配关键词(逗号分隔)', en: 'Match keywords (comma-separated)', fr: 'Mots-clés (séparés par des virgules)' },
  'tracker.saLinkApp': { zh: '关联到哪条投递', en: 'Link to application', fr: 'Lier à la candidature' },
  'tracker.saSelectApp': { zh: '选择投递记录…', en: 'Select an application…', fr: 'Choisir une candidature…' },
  'tracker.saRound': { zh: '轮次', en: 'Round', fr: 'Tour' },
  'tracker.saRoundType': { zh: '轮次类型', en: 'Round type', fr: 'Type de tour' },
  'tracker.saFormat': { zh: '形式', en: 'Format', fr: 'Format' },
  'tracker.saInterviewDate': { zh: '面试日期', en: 'Interview date', fr: 'Date d’entretien' },
  'tracker.saTime': { zh: '时间', en: 'Time', fr: 'Heure' },
  'tracker.saInterviewers': { zh: '面试官', en: 'Interviewers', fr: 'Interlocuteurs' },
  'tracker.saMeetLink': { zh: '地点 / 会议链接', en: 'Location / meeting link', fr: 'Lieu / lien de réunion' },
  'tracker.saLinkHint': { zh: '关联后会登记一轮面试并把投递推进到「面试中」,同时在实战机经里生成一条草稿。', en: 'Linking logs an interview round, moves the application to Interview, and creates a draft in Playbook.', fr: 'La liaison enregistre un tour d’entretien, passe la candidature en Entretien et crée un brouillon dans Journal.' },
  'tracker.saSaveApp': { zh: '保存为投递记录', en: 'Save as application', fr: 'Enregistrer comme candidature' },
  'tracker.saLinkOnce': { zh: '一键关联', en: 'Link', fr: 'Lier' },
  'tracker.saCreated': { zh: '已创建「{a}」', en: 'Created “{a}”', fr: '« {a} » créé' },
  'tracker.saRoundLogged': { zh: '已登记第 {n} 轮面试', en: 'Round {n} interview logged', fr: 'Tour {n} enregistré' },
  'tracker.saLocNote': { zh: '地点/会议:', en: 'Location/meeting:', fr: 'Lieu/réunion :' },
  'tracker.rtScreen': { zh: '初筛', en: 'Screen', fr: 'Préqualification' },
  'tracker.rtTechnical': { zh: '技术面', en: 'Technical', fr: 'Technique' },
  'tracker.rtSystemDesign': { zh: '系统设计', en: 'System design', fr: 'Conception système' },
  'tracker.rtBehavioral': { zh: '行为面', en: 'Behavioral', fr: 'Comportemental' },
  'tracker.rtFinal': { zh: '终面', en: 'Final', fr: 'Final' },
  'tracker.fmPhone': { zh: '电话', en: 'Phone', fr: 'Téléphone' },
  'tracker.fmVideo': { zh: '视频', en: 'Video', fr: 'Vidéo' },
  'tracker.fmOnsite': { zh: '现场', en: 'Onsite', fr: 'Sur place' },

  // ---------- 投递跟踪 · 问答库弹窗 ----------
  'tracker.alCatPh': { zh: '如 自我介绍', en: 'e.g. Self-intro', fr: 'p. ex. Présentation' },
  'tracker.alRequired': { zh: '问题和回答均为必填', en: 'Question and answer are both required', fr: 'La question et la réponse sont requises' },
  'tracker.alDefaultCat': { zh: '通用', en: 'General', fr: 'Général' },
  'analytics.nEntries': { zh: '{n} 个条目', en: '{n} entries', fr: '{n} entrées' },

  // ---------- 播放 / 合成失败提示(2026-09-23:此前写死中文) ----------
  'practice.errUnknown': { zh: '未知错误', en: 'Unknown error', fr: 'Erreur inconnue' },
  'practice.treeBlocked': {
    zh: '未连接到服务端，已阻止本次写入以免覆盖云端数据。请先重试加载。',
    en: 'Not connected to the server — write blocked to avoid overwriting cloud data. Retry loading first.',
    fr: 'Serveur injoignable — écriture bloquée pour éviter d’écraser les données. Réessayez le chargement.'
  },
  'practice.noteLoadFail': { zh: '音频加载失败', en: 'Audio failed to load', fr: 'Échec du chargement audio' },
  'practice.notePlayFail': { zh: '音频播放失败', en: 'Audio failed to play', fr: 'Échec de lecture audio' },
  'practice.noteNotStarted': { zh: '音频未能启动', en: 'Audio did not start', fr: 'Audio non démarré' },
  'practice.audioLoadFailToast': {
    zh: '音频加载失败，请重试；若反复出现请重新合成。',
    en: 'Audio failed to load — retry; if it keeps happening, synthesize it again.',
    fr: 'Échec du chargement audio — réessayez ; si cela persiste, resynthétisez.'
  },
  'practice.audioPlayFailToast': {
    zh: '音频播放失败，请再点一次播放。',
    en: 'Audio failed to play — click play again.',
    fr: 'Échec de lecture — recliquez sur lecture.'
  },
  'practice.audioBlockedToast': {
    zh: '浏览器没有允许播放音频，请再点一次播放键。',
    en: 'The browser blocked audio playback — click play again.',
    fr: 'Le navigateur a bloqué la lecture — recliquez sur lecture.'
  },
  'practice.ttsNotConfigured': { zh: 'Azure 语音未配置', en: 'Azure voice not configured', fr: 'Voix Azure non configurée' },
  // ★ 2026-09-24(Forrest):正文语言与朗读语言不一致 —— 预检提示 + 一键修正。
  'practice.langMismatchZh': {
    zh: '这篇内容是中文，但当前朗读语言是 {lang}，可能无法正常朗读。',
    en: 'This text is in Chinese, but the reading language is {lang} — playback may fail.',
    fr: 'Ce texte est en chinois, mais la langue de lecture est {lang} — la lecture peut échouer.'
  },
  'practice.langMismatchEn': {
    zh: '这篇内容不是中文，但当前朗读语言是中文，发音会不自然。',
    en: 'This text is not in Chinese, but the reading language is Chinese — pronunciation will sound off.',
    fr: 'Ce texte n’est pas en chinois, mais la langue de lecture est le chinois — la prononciation sera incorrecte.'
  },
  'practice.langSwitchZh': { zh: '切换为中文', en: 'Switch to Chinese', fr: 'Passer au chinois' },
  'practice.langSwitchEn': { zh: '切换为 English (US)', en: 'Switch to English (US)', fr: 'Passer à l’anglais (US)' },
  'practice.ttsKeyInvalid': { zh: 'Azure 密钥/区域无效', en: 'Invalid Azure key or region', fr: 'Clé ou région Azure invalide' },
  'practice.ttsSynthFailed': { zh: '语音合成失败', en: 'Synthesis failed', fr: 'Échec de la synthèse' },
  'practice.recGone': {
    zh: '这条录音的音频文件已不存在（服务重建或清理时被删了）。请删掉这条记录后重录。',
    en: 'The audio for this recording is gone (removed when the service was rebuilt or cleaned). Delete this entry and re-record.',
    fr: 'L’audio de cet enregistrement a disparu. Supprimez la ligne et réenregistrez.'
  },
  'practice.recLoadFail': { zh: '录音回放加载失败：', en: 'Failed to load recording: ', fr: 'Échec du chargement : ' },
  'practice.retryLater': { zh: '请稍后重试。', en: 'Please try again later.', fr: 'Veuillez réessayer plus tard.' },

  'practice.zoomIn': { zh: '放大字号', en: 'Increase font size', fr: 'Agrandir le texte' },
  'practice.zoomOut': { zh: '缩小字号', en: 'Decrease font size', fr: 'Réduire le texte' },
  // 2026-09-16(Forrest 本轮第 2 条):中间的百分比数字可点击 → 输入精确值。
  'practice.zoomEditHint': { zh: '点击选择字号档位或输入精确百分比', en: 'Click to pick a size or type an exact percentage', fr: 'Cliquez pour choisir une taille ou saisir un pourcentage' },
  'practice.zoomCustom': { zh: '手动输入…', en: 'Custom value…', fr: 'Valeur personnalisée…' },
  'practice.azureKeyMissing': { zh: '尚未配置 Azure Speech 密钥，无法执行发音评分。', en: 'Azure Speech key is not configured, so pronunciation scoring is unavailable.', fr: "La clé Azure Speech n'est pas configurée ; l'évaluation de prononciation est indisponible." },
  'practice.gotoConfigKey': { zh: '前往配置 API Key ↗', en: 'Configure API Key ↗', fr: 'Configurer la clé API ↗' },
  'practice.azureFallbackNote': { zh: '后端合成接口未接入，选择 Azure 仍会回退到浏览器语音。', en: 'Backend synthesis is not wired up yet, so Azure falls back to browser speech.', fr: "La synthèse côté serveur n'est pas en place : Azure retombe sur la voix du navigateur." },
  'practice.needRecording': { zh: '请先录一段音频，再提交评分。', en: 'Record an audio take first, then submit for scoring.', fr: "Enregistrez d'abord un audio, puis soumettez pour évaluation." },
  'practice.needScoreRetry': { zh: '该录音已评分，重新作答后可再次提交。', en: 'This take is already scored. Re-record to submit again.', fr: "Cet enregistrement est déjà évalué. Réenregistrez pour soumettre à nouveau." },
  'practice.audioWaveform': { zh: '音频波形', en: 'Audio Waveform', fr: 'Forme d onde audio' },

  // ★ 第三十三轮(Forrest):额度消耗透明提示 —— 有消耗就报,没消耗就说清楚
  //   "这是本地存储的音频"。不猜、不含糊。
  'practice.ttsCostFresh': {
    zh: '消耗额度 · 本次调用 Azure 合成',
    en: 'Credits used · synthesized via Azure now',
    fr: 'Credits utilises · synthese Azure a l instant' },

  // ★ 第三十四轮:带精确数字的版本 —— {n} = 本次实际计费字符数。
  //   ⚠️ 用"字符"不用"token":Azure 语音 TTS 以合成字符数计费。
  'practice.ttsCostFreshN': {
    zh: '消耗额度 · 本次合成 {n} 字符',
    en: 'Credits used · {n} characters synthesized',
    fr: 'Credits utilises · {n} caracteres synthetises' },
  'practice.ttsCostLocalN': {
    zh: '未消耗额度(0 字符) · 播放本地存储的音频,本文本完整 {n} 字符',
    en: 'No credits used (0 chars) · playing locally stored audio; full text is {n} characters',
    fr: 'Aucun credit (0 caractere) · lecture du fichier local; texte complet: {n} caracteres' },
  'practice.scoreCostFreshN': {
    zh: '消耗额度 · 本次评估 {n} 秒音频',
    en: 'Credits used · {n} seconds of audio assessed',
    fr: 'Credits utilises · {n} secondes audio evaluees' },
  'practice.scoreCostLocalN': {
    zh: '未消耗额度 · 读取本地已存评分(当时评估 {n} 秒音频)',
    en: 'No credits used · reading stored score (assessed {n} seconds of audio)',
    fr: 'Aucun credit · score deja enregistre ({n} secondes audio evaluees)' },
  'practice.ttsCostFreshHint': {
    zh: '这段文本还没在本机合成过,本次调用 Azure 神经语音生成音频,会消耗额度。下次朗读同样内容会直接回放本地音频,不再消耗。',
    en: 'This text had no local audio yet, so Azure neural TTS generated it now and used credits. Replaying the same text later will serve the local file with no further usage.',
    fr: "Ce texte n'avait pas encore d'audio local: Azure a genere l'audio maintenant et des credits ont ete utilises." },
  'practice.ttsCostLocal': {
    zh: '未消耗额度 · 播放本地存储的音频',
    en: 'No credits used · playing locally stored audio',
    fr: 'Aucun credit utilise · lecture du fichier local' },
  'practice.ttsCostLocalHint': {
    zh: '同一段文本之前已经合成过,音频存在本机文件系统里。本次直接回放,没有调用 Azure,未消耗任何额度。',
    en: 'This exact text was synthesized before and the audio is stored on the server filesystem. It is served directly with no Azure call and no credit usage.',
    fr: "Ce texte a deja ete synthetise et l'audio est stocke sur le disque. Aucun appel Azure, aucun credit consomme." },

  'practice.scoreCostFresh': {
    zh: '消耗额度 · 本次调用 Azure 评分',
    en: 'Credits used · scored via Azure now',
    fr: 'Credits utilises · evaluation Azure a l instant' },
  'practice.scoreCostFreshHint': {
    zh: '这条录音还没有存过的评分,本次调用 Azure 发音评估,会消耗额度。评分结果已存入数据库,下次打开直接读取,不再重复消耗。',
    en: 'This recording had no stored score, so Azure pronunciation assessment ran now and used credits. The result is saved to the database; opening it again reads from storage with no further usage.',
    fr: "Cet enregistrement n'avait pas de score: Azure a evalue maintenant et des credits ont ete utilises. Le resultat est enregistre en base." },
  'practice.scoreCostLocal': {
    zh: '未消耗额度 · 读取本地已存评分',
    en: 'No credits used · reading stored score',
    fr: 'Aucun credit utilise · score deja enregistre' },
  'practice.scoreCostLocalHint': {
    zh: '这条录音之前已经评过分,分数存在数据库里。本次直接读取历史评分,没有调用 Azure,未消耗任何额度。若要重新评分并覆盖旧分,请先重置该录音的评分。',
    en: 'This recording was already scored and the result is in the database. It is read back directly with no Azure call and no credit usage. To re-score and overwrite, reset the score for this recording first.',
    fr: "Cet enregistrement a deja ete evalue et le score est en base. Lecture directe, aucun appel Azure, aucun credit consomme." },
  'practice.recPlay': { zh: 'Play', en: 'Play', fr: 'Lire' },
  'practice.runAiScoring': { zh: '点击进行AI评分', en: 'Run AI Scoring', fr: 'Lancer l évaluation IA' },
  'practice.collapseReport': { zh: '收起报告', en: 'Collapse', fr: 'Réduire' },
  'practice.viewReportDetail': { zh: '查看详情', en: 'View Report', fr: 'Voir le rapport' },
  'practice.gotoConfig': { zh: '前往配置 ↗', en: 'Configure ↗', fr: 'Configurer ↗' },
  // ★ 2026-09-19:朗读/评分语言(法语支持)。朗读与评分同源用同一个值。
  'practice.langTitle': { zh: '语语言', en: 'Reading language', fr: 'Langue de lecture' },
  'practice.langHint': { zh: '朗读与发音评分都按此语言。法语需 Azure 法语神经音色(fr-FR-DeniseNeural)。', en: 'Used for both read-aloud and pronunciation scoring. French needs a French neural voice (fr-FR-DeniseNeural).', fr: 'Utilisée pour la lecture et la notation. Le français exige une voix neuronale française (fr-FR-DeniseNeural).' },
  // ★ 第五十一轮:评分入口改为录音列表每行的「点击进行AI评分」按钮。
  'practice.reportEmpty': { zh: '还没有评测结果。录一段朗读并在下方录音条目上点击「点击进行AI评分」后,这里会显示发音准确度、流利度、逐词对比与错误统计。', en: 'No assessment yet. Record a take and click "Run AI Scoring" on it in the list below to see pronunciation accuracy, fluency, word-by-word diff and error stats here.', fr: 'Aucune évaluation. Enregistrez puis lancez l évaluation depuis la liste.' },

  // ---------- 2026-09-16 第十九轮:骨架重构新增 ----------
  'practice.mark': { zh: '标记', en: 'Mark', fr: 'Marquer' },
  // ★ 2026-09-23(Forrest 第九轮):标记改为 Notion 同款的颜色状态 ——
  //   无标记/橙色/红色/绿色;后续可以用颜色做筛选条件。
  'practice.markNone': { zh: '无标记', en: 'No mark', fr: 'Aucune marque' },
  'practice.markOrange': { zh: '橙色', en: 'Orange', fr: 'Orange' },
  'practice.markRed': { zh: '红色', en: 'Red', fr: 'Rouge' },
  'practice.markGreen': { zh: '绿色', en: 'Green', fr: 'Vert' },
  'practice.recordingHistoryCount': { zh: '录音记录', en: 'Recordings', fr: 'Enregistrements' },
  'practice.saveFailed': { zh: '保存失败,请检查浏览器存储权限', en: 'Save failed — check browser storage permissions', fr: 'Échec de l enregistrement' },
  'practice.sampleReading': { zh: '示范朗读', en: 'Sample Reading', fr: 'Lecture exemple' },
  // ★ 第五十一轮:practice.runScoring(胶囊 Run AI Scoring)、practice.reset
  //   (重置)两条已删除 —— 对应按钮都已按 Forrest 指令移除,留着只是死词条。
  'practice.scoreResults': { zh: '评分结果', en: 'Score Results', fr: 'Résultats' },
  'practice.recognition': { zh: '识别结果', en: 'Recognition', fr: 'Reconnaissance' },
  'practice.recognitionNote': { zh: '按参考原文显示发音、遗漏、插入和停顿问题。悬停单词可查看该词的错误类型与得分。', en: 'Shows pronunciation, omission, insertion and pause issues against the reference text. Hover a word for its error type and score.', fr: 'Affiche les problèmes de prononciation, omissions, insertions et pauses.' },
  'practice.wordScores': { zh: '逐词得分', en: 'Word Scores', fr: 'Scores par mot' },
  'dim.recognition': { zh: '识别', en: 'Recognition', fr: 'Reconnaissance' },
  'err.mispron': { zh: '发音错误', en: 'Mispronunciation', fr: 'Prononciation' },
  'err.omission': { zh: '遗漏', en: 'Omission', fr: 'Omission' },
  'err.insertion': { zh: '插入内容', en: 'Insertion', fr: 'Insertion' },
  'err.unexpectedBreak': { zh: '意外中断', en: 'Unexpected break', fr: 'Coupure' },
  'err.missingBreak': { zh: '缺少停顿', en: 'Missing break', fr: 'Pause manquante' },
  // 2026-09-16:无词级评估数据(Azure 未返回该词的 PronunciationAssessment)
  'err.noData': { zh: '无词级数据', en: 'No word data', fr: 'Aucune donnée' },
  'err.ok': { zh: '正常', en: 'OK', fr: 'Correct' },
  // ★ 第四十六轮(Forrest):评分链路的错误提示随系统语言 ——
  //   之前这些文案写死在录音服务里(中文),英文/法语界面也只会显示中文。
  'rec.fetchFail': { zh: '取回录音音频失败,无法评分', en: 'Failed to fetch recording audio — cannot score', fr: 'Échec de récupération de l audio, score impossible' },
  'rec.notSaved': { zh: '这条录音还没保存到服务端(或保存失败),无法评分。请等上传完成后再点,或重录一次。', en: 'This take is not saved on the server yet (or saving failed), so it cannot be scored. Wait for the upload to finish, or record again.', fr: 'Cet enregistrement n est pas encore sauvegardé, score impossible. Attendez la fin de l envoi ou réenregistrez.' },
  'rec.noAudio': { zh: '这条录音没有可用的音频数据,无法评分。请重新录制。', en: 'This take has no usable audio data and cannot be scored. Please record again.', fr: 'Cet enregistrement n a pas de données audio utilisables. Veuillez réenregistrer.' },
  'rec.decodeFail': { zh: '无法解析该录音格式。请重新录制,或改用 Chrome 打开本页。', en: 'Cannot decode this recording format. Please record again, or open this page in Chrome.', fr: 'Impossible de décoder ce format audio. Réenregistrez ou ouvrez la page dans Chrome.' },
  // 服务端状态码的本地化兜底(与 api-client 的中文兜底一一对应)
  'err.noBackend': { zh: '无法连接后端服务,请确认服务已启动', en: 'Cannot reach the backend service — make sure it is running', fr: 'Impossible de joindre le service backend' },
  'err.expired': { zh: '登录已过期,请重新登录', en: 'Session expired — please sign in again', fr: 'Session expirée, veuillez vous reconnecter' },
  'err.forbidden': { zh: '没有权限执行此操作', en: 'You do not have permission to do this', fr: 'Vous n avez pas la permission' },
  'err.notFound': { zh: '数据不存在', en: 'Data not found', fr: 'Données introuvables' },
  'err.server': { zh: '服务端内部错误,请查看服务端日志', en: 'Internal server error — check the server logs', fr: 'Erreur interne du serveur' },
  'common.na': { zh: '—', en: '—', fr: '—' },
  'verdict.excellent': { zh: '优秀', en: 'Excellent', fr: 'Excellent' },
  'verdict.good': { zh: '良好', en: 'Good', fr: 'Bien' },
  'verdict.fair': { zh: '尚可', en: 'Fair', fr: 'Passable' },
  'verdict.poor': { zh: '需加强', en: 'Needs work', fr: 'À travailler' },
  'sub.scores': { zh: '评分', en: 'Scores', fr: 'Scores' },
  'sub.history': { zh: '记录', en: 'History', fr: 'Historique' },

  // ---------- 品牌 / 账户(2026-09-15 图标化) ----------
  'brand.name': { zh: 'AI 面试教练', en: 'AI Interview Coach', fr: 'Coach d entretien IA' },
  'account.menu': { zh: '账户', en: 'Account', fr: 'Compte' },

  // ---------- 个人中心(M2.2) ----------
  'profile.sub': {
    zh: '管理你的资料、偏好与密码',
    en: 'Manage your profile, preferences and password',
    fr: 'Gérez votre profil, vos préférences et votre mot de passe'
  },
  'profile.nameLabel': { zh: '显示名称', en: 'Display name', fr: 'Nom affiché' },
  'profile.nameHint': {
    zh: '这个名字会显示在顶栏和菜单里',
    en: 'Shown in the top bar and menus',
    fr: 'Affiché dans la barre supérieure et les menus'
  },
  'profile.editName': { zh: '修改名称', en: 'Rename', fr: 'Renommer' },
  'profile.saving': { zh: '保存中…', en: 'Saving…', fr: 'Enregistrement…' },
  'profile.rolesLabel': { zh: '角色', en: 'Roles', fr: 'Rôles' },
  'profile.noRoles': { zh: '未分配角色', en: 'No roles assigned', fr: 'Aucun rôle' },
  'profile.createdAt': { zh: '注册于', en: 'Member since', fr: 'Membre depuis' },
  'profile.lastLogin': { zh: '最近登录', en: 'Last sign-in', fr: 'Dernière connexion' },
  'profile.prefs': { zh: '偏好设置', en: 'Preferences', fr: 'Préférences' },
  'profile.prefsHint': {
    zh: '保存在你的账号里,换设备登录也生效',
    en: 'Saved to your account — applies on any device',
    fr: 'Enregistrées dans votre compte — valables partout'
  },
  'profile.timezone': { zh: '时区', en: 'Time zone', fr: 'Fuseau horaire' },
  'prof.tzToronto': { zh: '多伦多 (EST/EDT)', en: 'Toronto (EST/EDT)', fr: 'Toronto (EST/EDT)' },
  'prof.tzVancouver': { zh: '温哥华 (PST/PDT)', en: 'Vancouver (PST/PDT)', fr: 'Vancouver (PST/PDT)' },
  'prof.tzNewYork': { zh: '纽约 (EST/EDT)', en: 'New York (EST/EDT)', fr: 'New York (EST/EDT)' },
  'prof.tzLosAngeles': { zh: '洛杉矶 (PST/PDT)', en: 'Los Angeles (PST/PDT)', fr: 'Los Angeles (PST/PDT)' },
  'prof.tzLondon': { zh: '伦敦 (GMT/BST)', en: 'London (GMT/BST)', fr: 'Londres (GMT/BST)' },
  'prof.tzParis': { zh: '巴黎 (CET/CEST)', en: 'Paris (CET/CEST)', fr: 'Paris (CET/CEST)' },
  'prof.tzShanghai': { zh: '上海 (CST)', en: 'Shanghai (CST)', fr: 'Shanghai (CST)' },
  'prof.tzHongKong': { zh: '香港 (HKT)', en: 'Hong Kong (HKT)', fr: 'Hong Kong (HKT)' },
  'prof.tzTokyo': { zh: '东京 (JST)', en: 'Tokyo (JST)', fr: 'Tokyo (JST)' },
  'profile.tzHint': {
    zh: '注册与登录时间按此时区显示',
    en: 'Dates are shown in this zone',
    fr: 'Les dates suivent ce fuseau'
  },
  'profile.security': { zh: '密码', en: 'Password', fr: 'Mot de passe' },
  'profile.pwdHint': {
    zh: '至少 12 位,需含大小写字母、数字和特殊字符;修改后所有设备需重新登录',
    en: 'At least 12 characters with upper and lower case, a digit and a symbol; all devices must sign in again',
    fr: 'Au moins 12 caractères avec majuscules, minuscules, chiffre et symbole ; reconnexion requise sur tous les appareils'
  },
  'profile.currentPwd': { zh: '当前密码', en: 'Current password', fr: 'Mot de passe actuel' },
  'profile.newPwd': { zh: '新密码', en: 'New password', fr: 'Nouveau mot de passe' },
  'profile.confirmPwd': { zh: '确认新密码', en: 'Confirm new password', fr: 'Confirmer le mot de passe' },
  'profile.changePwd': { zh: '修改密码', en: 'Change password', fr: 'Changer le mot de passe' },
  'profile.pwdWeak': {
    zh: '新密码不满足强度要求(至少 12 位,含大小写字母、数字与特殊字符)',
    en: 'The new password is not strong enough (12+ chars with upper/lower case, a digit and a symbol)',
    fr: 'Mot de passe insuffisant (12 caractères min., majuscules, minuscules, chiffre et symbole)'
  },
  'profile.pwdSame': {
    zh: '新密码不能与当前密码相同',
    en: 'The new password must differ from the current one',
    fr: 'Le nouveau mot de passe doit différer de l’actuel'
  },
  'profile.pwdMismatch': {
    zh: '两次输入的新密码不一致',
    en: 'The two new passwords differ',
    fr: 'Les mots de passe ne correspondent pas'
  },
  'profile.pwdChanged': {
    zh: '密码已修改,请重新登录',
    en: 'Password changed — please sign in again',
    fr: 'Mot de passe modifié — reconnectez-vous'
  },
  'profile.mustChange': {
    zh: '管理员要求你修改密码,请在下方完成',
    en: 'An administrator requires a password change — do it below',
    fr: 'Un administrateur exige un changement de mot de passe — effectuez-le ci-dessous'
  },
  'profile.perms': { zh: '权限清单', en: 'Permissions', fr: 'Autorisations' },
  'profile.permsSub': { zh: '共 {n} 项,按模块分组', en: '{n} in total, grouped by module', fr: '{n} au total, par module' },
  'profile.noPerms': {
    zh: '当前账号没有任何细粒度权限,仅能访问基础页面。',
    en: 'This account has no fine-grained permissions — basic pages only.',
    fr: 'Ce compte n’a aucune permission fine — pages de base uniquement.'
  },
  // ★ 2026-09-27:本机录音存储目录(用户可在"我的账户"里自选)
  'profile.storageTitle': { zh: '录音存储位置', en: 'Recording storage', fr: 'Stockage des enregistrements' },
  'profile.storageHint': {
    zh: '录音音频保存在你本机(Mac)的这个文件夹里,不进代码仓库',
    en: 'Audio files live in this folder on your Mac — never in the code repository',
    fr: 'Les fichiers audio sont stockés dans ce dossier sur votre Mac — jamais dans le dépôt'
  },
  'profile.storagePath': { zh: '录音保存位置', en: 'Recording folder', fr: 'Dossier des enregistrements' },
  'profile.storagePresetDocs': {
    zh: '文稿 Documents:~/Documents/your-interview/recordings（推荐）',
    en: 'Documents: ~/Documents/your-interview/recordings (recommended)',
    fr: 'Documents : ~/Documents/your-interview/recordings (recommandé)'
  },
  'profile.storagePresetRepo': {
    zh: '与仓库同级:~/dev/recordings（原默认）',
    en: 'Next to the repo: ~/dev/recordings (previous default)',
    fr: 'À côté du dépôt : ~/dev/recordings (défaut précédent)'
  },
  'profile.storagePresetCustom': { zh: '自定义路径…', en: 'Custom path…', fr: 'Chemin personnalisé…' },
  'profile.storageCustomPath': { zh: '完整绝对路径', en: 'Full absolute path', fr: 'Chemin absolu complet' },
  'profile.storagePathHint': {
    zh: '选好后点"保存路径";需要挂载时页面会给出要复制的配置',
    en: 'Pick one and hit Save; if mounting is needed the exact config appears below',
    fr: 'Choisissez puis enregistrez ; la config à copier apparaît si nécessaire'
  },
  'profile.storageSave': { zh: '保存路径', en: 'Save folder', fr: 'Enregistrer le dossier' },
  'profile.storageMigrate': { zh: '把已有录音搬到这里', en: 'Move existing recordings here', fr: 'Déplacer les enregistrements ici' },
  'profile.storageMountHint': {
    zh: '这个目录还没挂进 Docker 容器,暂时仍写入原目录。两步生效:① 先在 Mac 上把原录音文件夹搬到这个新位置;② 把下面的配置写进 .env 并重建容器。',
    en: 'This folder is not mounted into the container yet, so recordings still go to the previous folder. Two steps: (1) move the old recordings folder to the new location on your Mac; (2) put the config below into .env and recreate the container.',
    fr: 'Ce dossier n’est pas encore monté dans le conteneur, les enregistrements vont donc encore dans l’ancien dossier. Deux étapes : (1) déplacez l’ancien dossier vers le nouvel emplacement sur votre Mac ; (2) ajoutez la config ci-dessous dans .env et recréez le conteneur.'
  },
  'profile.storageCopy': { zh: '复制配置', en: 'Copy config', fr: 'Copier la config' },
  'profile.storageCopied': { zh: '已复制,粘贴到终端执行即可', en: 'Copied — paste it in your terminal', fr: 'Copié — collez-le dans votre terminal' },
  'profile.storageActive': {
    zh: '正在使用:{n}(录音就写在这里)',
    en: 'Active:{n} — recordings are written here',
    fr: 'Actif :{n} — les enregistrements y sont écrits'
  },
  'profile.storageDefault': {
    zh: '未自定义,使用部署默认目录:{n}',
    en: 'Using the deployment default: {n}',
    fr: 'Utilisation du dossier par défaut : {n}'
  },
  'profile.storagePending': {
    zh: '路径已保存,但容器里还看不到它 —— 需要挂载后重启服务',
    en: 'Saved, but the container cannot see it yet — mount it and restart the service',
    fr: 'Enregistré, mais le conteneur ne le voit pas encore — montez-le puis redémarrez'
  },
  'profile.storageUnusable': {
    zh: '这个路径不可用,录音仍写入原目录',
    en: 'This path is unusable — recordings still go to the previous folder',
    fr: 'Ce chemin est inutilisable — les enregistrements vont encore dans l’ancien dossier'
  },
  'profile.storageUnknown': { zh: '暂时无法确定存储位置', en: 'Storage location unknown right now', fr: 'Emplacement de stockage inconnu' },
  'profile.storageSaved': { zh: '已保存', en: 'Saved', fr: 'Enregistré' },
  'profile.storageNeedAbsolute': {
    zh: '请先把 ~ 换成完整路径,例如 /Users/你的用户名/Documents/your-interview/recordings(容器里识别不了 ~)',
    en: 'Replace ~ with the full path first, e.g. /Users/you/Documents/your-interview/recordings (containers cannot expand ~)',
    fr: 'Remplacez ~ par le chemin complet, ex. /Users/you/Documents/your-interview/recordings (le conteneur ne peut pas développer ~)'
  },
  'profile.storageSaveFail': { zh: '保存失败,请检查路径后重试', en: 'Save failed — check the path and retry', fr: 'Échec de l’enregistrement — vérifiez le chemin' },
  'profile.storageMigrated': {
    zh: '已完成:搬了 {n} 个录音文件(数据库记录不变)',
    en: 'Done: {n} recording file(s) moved (database records unchanged)',
    fr: 'Terminé : {n} fichier(s) déplacé(s) (enregistrements DB inchangés)'
  },
  'profile.storageMigrateFail': { zh: '迁移失败,请先确认新目录已生效', en: 'Migration failed — make sure the new folder is active', fr: 'Échec de la migration — vérifiez que le dossier est actif' },
  'profile.storageFiles': { zh: '当前目录已有:', en: 'Current folder holds:', fr: 'Contenu actuel du dossier :' },

  'profile.fetchFail': { zh: '未能获取最新资料', en: 'Could not fetch the latest profile', fr: 'Impossible de récupérer le profil' },
  'profile.notLatest': {
    zh: '以下内容来自本地缓存,可能不是最新的。',
    en: 'Below is a cached copy and may be out of date.',
    fr: 'Ci-dessous une copie en cache, potentiellement obsolète.'
  },

  'practice.edit': { zh: '编辑', en: 'Edit', fr: 'Modifier' },
  'practice.cancel': { zh: '取消编辑', en: 'Cancel', fr: 'Annuler' },
  'practice.modeEditing': { zh: '编辑中', en: 'Editing', fr: 'Édition' },
  'practice.modeReadonly': { zh: '只读', en: 'Read only', fr: 'Lecture seule' },
  'practice.emptyContent': { zh: '这条素材还没有内容,点编辑开始写。', en: 'This material is empty. Click Edit to start writing.', fr: 'Document vide. Cliquez sur Modifier pour commencer.' },
  'practice.demoRead': { zh: '示范朗读', en: 'Demo read-aloud', fr: 'Lecture modèle' },
  'practice.rate': { zh: '语速', en: 'Speed', fr: 'Vitesse' },
  'practice.runGrade': { zh: '开始 AI 评分', en: 'Run AI grading', fr: 'Lancer la notation IA' },
  'practice.grading': { zh: '评分中…', en: 'Grading…', fr: 'Notation…' },
  'practice.gradeNote': { zh: '评分不会自动生成,必须点上面按钮才会开始。', en: 'Grading never runs automatically — only when you click the button above.', fr: 'La notation ne se lance jamais automatiquement.' },
  'practice.gradeConfig': { zh: '评分配置', en: 'Grading settings', fr: 'Paramètres de notation' },
  'practice.strictness': { zh: '严格度', en: 'Strictness', fr: 'Sévérité' },
  'practice.weightTotal': { zh: '权重合计', en: 'Total weight', fr: 'Poids total' },
  'practice.checkGrammar': { zh: '检查语法', en: 'Check grammar', fr: 'Vérifier la grammaire' },
  'practice.giveAdvice': { zh: '给出改进建议', en: 'Give improvement advice', fr: 'Donner des conseils' },
  'practice.noScoresYet': { zh: '还没有评分结果。点上方按钮开始评分。', en: 'No scores yet. Click the button above to grade.', fr: 'Aucun score. Cliquez ci-dessus pour noter.' },

  // ---------- 语言自名(切换器内显示) ----------
  'lang.zh': { zh: '中文', en: '中文', fr: '中文' },
  'lang.en': { zh: 'English', en: 'English', fr: 'English' },
  'lang.fr': { zh: 'Français', en: 'Français', fr: 'Français' },

  // ---------- AI 语音设置页(/account/ai-setting) ----------
  // 2026-09-16(Forrest 第 6 条):该页原写死中文,与网站语言脱节。
  // 现全部接入 i18n,顶栏切语言时本页同步变化。
  // ---------- LLM 设置(面试前准备包,2026-09-18) ----------
  'ai.credTitle': { zh: '大模型 (LLM) 凭据', en: 'LLM Credentials', fr: 'Identifiants LLM' },
  'ai.credDesc': {
    zh: '面试前准备包用这里配置的模型生成预测问题、关注点与反问建议。Key 只存服务端,绝不下发浏览器。',
    en: 'The interview prep pack uses this model to generate predicted questions, focus areas and reverse questions. The key stays server-side only.',
    fr: 'Le modèle configuré ici génère les questions prévues, points d’attention et questions inverses. La clé reste côté serveur.'
  },
  'ai.providerLabel': { zh: '模型厂商', en: 'Provider', fr: 'Fournisseur' },
  'ai.providerHint': {
    zh: '选预设会自动填好端点与常用模型,可直接用。',
    en: 'Picking a preset fills in the endpoint and common models automatically.',
    fr: 'Choisir un préréglage remplit l’URL et les modèles courants.'
  },
  'ai.keyLabel': { zh: 'API Key', en: 'API Key', fr: 'Clé API' },
  'ai.keyPlaceholder': {
    zh: '粘贴该厂商控制台里的密钥',
    en: 'Paste the key from your provider console',
    fr: 'Collez la clé de la console du fournisseur'
  },
  'ai.modelLabel': { zh: '模型名', en: 'Model', fr: 'Modèle' },
  'ai.modelPlaceholder': { zh: '如 deepseek-chat / qwen-plus', en: 'e.g. deepseek-chat / qwen-plus', fr: 'ex. deepseek-chat / qwen-plus' },
  'ai.modelHint': {
    zh: '要调用哪个模型。可手改,不限于下拉里列出的。',
    en: 'Which model to call. Editable — not limited to the list.',
    fr: 'Le modèle à appeler. Modifiable, pas limité à la liste.'
  },
  'ai.baseUrlLabel': { zh: '端点地址 (BaseUrl)', en: 'Base URL', fr: 'URL de base' },
  'ai.baseUrlPlaceholder': { zh: 'https://api.deepseek.com/v1', en: 'https://api.deepseek.com/v1', fr: 'https://api.deepseek.com/v1' },
  'ai.baseUrlHint': {
    zh: '协议根地址,不含 /chat/completions —— 客户端会自己拼。',
    en: 'Protocol root, without /chat/completions — the client appends it.',
    fr: 'Racine du protocole, sans /chat/completions — le client l’ajoute.'
  },
  'ai.endpointLabel': { zh: 'Azure 资源端点', en: 'Azure Endpoint', fr: 'Point de terminaison Azure' },
  'ai.endpointPlaceholder': { zh: 'https://my-resource.openai.azure.com', en: 'https://my-resource.openai.azure.com', fr: 'https://my-resource.openai.azure.com' },
  'ai.apiVersionLabel': { zh: 'API 版本', en: 'API Version', fr: 'Version d’API' },
  'ai.apiVersionPlaceholder': { zh: '如 2024-10-21', en: 'e.g. 2024-10-21', fr: 'ex. 2024-10-21' },
  'ai.presetFirst': { zh: '请先选择模型厂商。', en: 'Pick a provider first.', fr: 'Choisissez d’abord un fournisseur.' },
  'ai.testOk': { zh: '连接正常,模型可用。', en: 'Connection OK — the model works.', fr: 'Connexion OK — le modèle fonctionne.' },
  'ai.testFail': { zh: '测试未通过:', en: 'Test failed: ', fr: 'Échec du test : ' },
  'ai.savedOk': { zh: '已保存并通过连通性测试', en: 'Saved and connectivity verified', fr: 'Enregistrée et connexion vérifiée' },
  'ai.samplePrefix': { zh: '模型回复:', en: 'Model replied: ', fr: 'Réponse du modèle : ' },
  'ai.validPrefix': { zh: '你有有效的 key:', en: 'You have a valid key:', fr: 'Vous disposez d’une clé valide :' },
  'ai.validSuffix': { zh: ',面试前准备包已可用。', en: ' — the prep pack is ready.', fr: ' — le pack de préparation est prêt.' },
  'ai.noKey': {
    zh: '尚未配置大模型。配置后即可生成面试前准备包。',
    en: 'No LLM configured yet. Configure one to generate prep packs.',
    fr: 'Aucun LLM configuré. Configurez-en un pour générer les packs.'
  },
  'ai.note1': {
    zh: '兼容 OpenAI 协议的厂商(DeepSeek、Qwen、Ollama 等)都用同一个客户端,换模型不改代码。',
    en: 'OpenAI-compatible providers (DeepSeek, Qwen, Ollama…) share one client — switching models needs no code change.',
    fr: 'Les fournisseurs compatibles OpenAI partagent un client — changer de modèle ne change pas le code.'
  },
  'ai.note2': {
    zh: '保存前必须测试通过 —— 后端也会独立再测一次,不通不入库。',
    en: 'A passing test is required before saving — the server re-tests independently and refuses to store a bad key.',
    fr: 'Un test réussi est requis avant l’enregistrement — le serveur reteste et refuse une clé invalide.'
  },
  'ai.note3': {
    zh: '生效优先级:本页保存的(数据库) > 环境变量 > 配置文件。本页保存后立即生效,无需重启服务。',
    en: 'Priority: saved here (database) > environment variables > config file. Effective immediately, no restart needed.',
    fr: 'Priorité : enregistré ici (base) > variables d’env. > fichier de config. Effet immédiat, sans redémarrage.'
  },
  'setting.back': { zh: '返回 AI 面试练习', en: 'Back to AI Practice', fr: 'Retour à la pratique IA' },
  'setting.title': { zh: 'AI 语音设置', en: 'AI Voice Settings', fr: 'Paramètres vocaux IA' },
  // 2026-09-16(Forrest 本轮第 6 条):设置页不再负责"选朗读引擎" ——
  // 引擎选择已归到练习页的 TTS 弹窗,本页只管 Azure 凭据。
  'setting.subtitle': {
    zh: '配置你的 Azure 语音服务凭据。朗读引擎请到 AI 面试练习页的 TTS 弹窗中选择。',
    en: 'Configure your Azure Speech credentials. Pick the reading engine from the TTS popup on the AI Practice page.',
    fr: 'Configurez vos identifiants Azure Speech. Choisissez le moteur de lecture dans la fenêtre TTS de la page d’entraînement IA.'
  },
  'setting.engineTitle': { zh: '示范朗读引擎', en: 'Sample Reading Engine', fr: 'Moteur de lecture' },
  // 2026-09-16(Forrest 本轮第 3 条):独立"Sample Reading Engine"区块已取消,
  // 统一归入 TTS 引擎。此键是设置页引擎卡片的新标题。
  'setting.ttsEngineTitle': { zh: 'TTS 引擎', en: 'TTS Engine', fr: 'Moteur vocal' },
  'setting.engineBrowserName': { zh: '浏览器内置语音', en: 'Browser Built-in Voice', fr: 'Voix du navigateur' },
  'setting.engineBrowserNote': { zh: '本地合成 · 免费 · 即时生成', en: 'On-device · free · instant', fr: 'Local · gratuit · instantané' },
  'setting.engineAzureName': { zh: 'Azure 神经网络语音', en: 'Azure Neural Voice', fr: 'Voix neuronale Azure' },
  'setting.engineAzureNote': { zh: '高质量自然人声 · 需消耗额度', en: 'Natural voice · uses quota', fr: 'Voix naturelle · consomme du quota' },
  'setting.current': { zh: '当前', en: 'Current', fr: 'Actuel' },
  'setting.credTitle': { zh: 'Azure Speech 凭据', en: 'Azure Speech Credentials', fr: 'Identifiants Azure Speech' },
  'setting.credDesc': {
    zh: '在 Azure 门户创建 Speech 资源后,从「密钥和终结点」页复制 Key 与区域填入下方。凭据仅用于服务端调用,不会写入浏览器存储。',
    en: 'Create a Speech resource in the Azure portal, then copy the Key and Region from the Keys and Endpoint page. Credentials are used server-side only and never stored in your browser.',
    fr: 'Créez une ressource Speech dans le portail Azure, puis copiez la clé et la région. Les identifiants servent côté serveur et ne sont jamais stockés dans le navigateur.'
  },
  'setting.keyLabel': { zh: 'Azure Speech Key', en: 'Azure Speech Key', fr: 'Clé Azure Speech' },
  'setting.keyPlaceholder': { zh: '粘贴 32 位密钥', en: 'Paste your 32-character key', fr: 'Collez votre clé de 32 caractères' },
  'setting.keyHint': {
    zh: '形如 1a2b3c4d5e6f7g8h9i0j…(32 位十六进制)',
    en: 'Format: 1a2b3c4d5e6f7g8h9i0j… (32 hex characters)',
    fr: 'Format : 1a2b3c4d5e6f7g8h9i0j… (32 caractères hexadécimaux)'
  },
  'setting.regionLabel': { zh: '区域 (Region)', en: 'Region', fr: 'Région' },
  'setting.regionHint': {
    zh: '必须与资源实际创建区域一致,否则会返回 401/403',
    en: 'Must match the region where the resource was created, or it returns 401/403',
    fr: 'Doit correspondre à la région de la ressource, sinon 401/403'
  },
  'setting.save': { zh: '保存密钥', en: 'Save key', fr: 'Enregistrer la clé' },
  'setting.test': { zh: '测试连接', en: 'Test connection', fr: 'Tester la connexion' },
  'setting.testing': { zh: '测试中…', en: 'Testing…', fr: 'Test en cours…' },
  'setting.testFirst': {
    zh: '请先测试连接,通过后即可保存。',
    en: 'Test the connection first — saving unlocks once it passes.',
    fr: 'Testez d’abord la connexion ; l’enregistrement se débloque une fois validé.'
  },
  'setting.keyPreview': { zh: '当前输入:', en: 'Current input: ', fr: 'Saisie actuelle : ' },
  'setting.hasKey': { zh: '服务端已保存密钥', en: 'A key is saved on the server', fr: 'Une clé est enregistrée côté serveur' },
  'setting.noKey': {
    zh: '尚未保存任何密钥。填写并测试通过后保存。',
    en: 'No key saved yet. Fill it in, test it, then save.',
    fr: 'Aucune clé enregistrée. Saisissez-la, testez, puis enregistrez.'
  },
  'setting.savedMasked': { zh: '已保存:', en: 'Saved: ', fr: 'Enregistrée : ' },
  'setting.sourceDb': { zh: '来源:本页保存', en: 'Source: saved here', fr: 'Source : enregistrée ici' },
  // 2026-09-16(Forrest 本轮第 2 条):已有有效凭据时的展示文案。
  // 分两段拼装:前缀 + 掩码 key + 后缀 —— 掩码由服务端回传,不能写死在字典里。
  'setting.validKeyPrefix': { zh: '你有有效的 key:', en: 'You have a valid key:', fr: 'Vous disposez d’une clé valide :' },
  'setting.validKeySuffix': {
    zh: ',可以尽情享受 Azure Speech 服务。',
    en: ' — enjoy the full Azure Speech experience.',
    fr: ' — profitez pleinement du service Azure Speech.'
  },
  'setting.assessTitle': { zh: '发音评测', en: 'Pronunciation Assessment', fr: 'Évaluation de prononciation' },
  'setting.assess1': {
    zh: '练习页的 AI 评分走服务端 Azure 发音评测接口,Key 由服务端持有。',
    en: 'AI scoring on the practice page goes through the server-side Azure pronunciation API; the key stays on the server.',
    fr: 'La notation IA passe par l’API Azure côté serveur ; la clé reste sur le serveur.'
  },
  'setting.assess2': {
    zh: '当前订阅档位(Free F0)只返回发音准确度,流利度与完整度拿不到,界面显示「—」。',
    en: 'The Free F0 tier returns accuracy only; fluency and completeness are unavailable and shown as “—”.',
    fr: 'L’offre Free F0 ne renvoie que la précision ; fluidité et complétude affichent « — ».'
  },
  'setting.assess3': {
    zh: '评测结果不返回音标,单词详情里音标位置显示「暂无音标」。',
    en: 'The API does not return phonemes; word details show “no phoneme data”.',
    fr: 'L’API ne renvoie pas de phonèmes ; le détail affiche « aucune donnée ».'
  },
  'setting.savedOk': { zh: '已保存并通过连通性测试', en: 'Saved and connectivity verified', fr: 'Enregistrée et connexion vérifiée' },
  'setting.prefSaved': { zh: '偏好已保存', en: 'Preference saved', fr: 'Préférence enregistrée' },
  'setting.savedButFail': { zh: '已保存,但连通性测试未通过:', en: 'Saved, but the connectivity test failed: ', fr: 'Enregistrée, mais le test a échoué : ' },
  'setting.saveFail': { zh: '保存失败:', en: 'Save failed: ', fr: 'Échec de l’enregistrement : ' },
  'setting.testFail': { zh: '测试未通过:', en: 'Test failed: ', fr: 'Échec du test : ' },
  'setting.testOk': { zh: '连接正常,密钥可用。', en: 'Connection OK — the key works.', fr: 'Connexion OK — la clé fonctionne.' },

  // ---------- 配置教程(2026-09-16 Forrest:参考截图添加配置教程) ----------
  'setting.tutTitle': { zh: '配置教程', en: 'Setup Guide', fr: 'Guide de configuration' },
  'setting.tutIntro': {
    zh: '这里填写的是您自己在微软 Azure 上创建的 Azure AI Speech 资源的 Speech Key 和 Speech Region。',
    en: 'Enter the Speech Key and Speech Region of the Azure AI Speech resource you created in your own Microsoft Azure account.',
    fr: 'Indiquez la clé Speech et la région Speech de la ressource Azure AI Speech créée dans votre propre compte Microsoft Azure.'
  },
  'setting.tutNote1': {
    zh: '当您使用“自行配置微软 Azure”模式时,本站实际调用的是您自己的微软 Azure Speech API,因此额度、计费、区域可用性和限制均以微软官方规则为准。',
    en: 'In “Bring your own Microsoft Azure” mode, this site calls your own Azure Speech API. Quotas, billing, regional availability, and limits are governed by Microsoft official terms.',
    fr: 'En mode « votre propre Microsoft Azure », ce site appelle votre propre API Azure Speech. Les quotas, la facturation, la disponibilité régionale et les limites relèvent des conditions officielles de Microsoft.'
  },
  'setting.tutNote2': {
    zh: '微软官方当前价格页显示,Free (F0) 层对 Speech to Text 提供每月 5 小时免费音频额度;超出后 Azure 可能会产生收费,具体价格和免费额度可能调整,请以微软官方页面为准。',
    en: 'Microsoft current pricing page states that the Free (F0) tier includes 5 hours of free speech-to-text audio per month. Beyond that, Azure may charge you. Prices and free allowances may change — always check Microsoft official page.',
    fr: 'La page tarifaire actuelle de Microsoft indique que le niveau Free (F0) inclut 5 heures d’audio de reconnaissance vocale gratuites par mois. Au-delà, Azure peut facturer. Les prix et quotas gratuits peuvent changer — vérifiez la page officielle de Microsoft.'
  },
  'setting.tutNote3': {
    zh: '如果您已购买平台流量包,系统会优先使用平台流量,尽量避免先消耗您个人 Azure 账号的额度。',
    en: 'If you have purchased a platform traffic pack, the system uses it first, to avoid consuming your personal Azure account quota whenever possible.',
    fr: 'Si vous avez acheté un forfait de trafic de la plateforme, le système l’utilise en priorité afin d’éviter de consommer le quota de votre compte Azure personnel.'
  },
  'setting.tutStepsTitle': { zh: '大概步骤', en: 'Rough steps', fr: 'Étapes principales' },
  'setting.tutStep1': { zh: '登录 Azure Portal', en: 'Sign in to the Azure Portal', fr: 'Connectez-vous au portail Azure' },
  'setting.tutStep2': { zh: '创建一个 Azure AI Speech 资源。', en: 'Create an Azure AI Speech resource.', fr: 'Créez une ressource Azure AI Speech.' },
  'setting.tutStep3': {
    zh: '打开该资源,进入 Keys and Endpoint 页面。',
    en: 'Open the resource and go to the “Keys and Endpoint” page.',
    fr: 'Ouvrez la ressource et allez sur la page « Clés et point de terminaison ».'
  },
  'setting.tutStep4': { zh: '复制其中一个 Key。', en: 'Copy one of the keys.', fr: 'Copiez l’une des clés.' },
  'setting.tutStep5': {
    zh: '记录该资源所在区域,例如 eastus。',
    en: 'Note the resource region, for example eastus.',
    fr: 'Notez la région de la ressource, par exemple eastus.'
  },
  'setting.tutStep6': {
    zh: '回到本站,填写 Speech Key 和 Speech Region 并保存。',
    en: 'Back on this site, fill in the Speech Key and Speech Region, then save.',
    fr: 'De retour sur ce site, saisissez la clé Speech et la région Speech, puis enregistrez.'
  },
  'setting.tutDocsTitle': { zh: '微软相关文档', en: 'Microsoft documentation', fr: 'Documentation Microsoft' },
  'setting.tutDocPricing': {
    zh: '查看免费额度与官方价格',
    en: 'Free tier and official pricing',
    fr: 'Niveau gratuit et tarifs officiels'
  },
  'setting.tutDocKeys': {
    zh: 'Keys and Endpoint 在哪里?',
    en: 'Where are Keys and Endpoint?',
    fr: 'Où trouver Clés et point de terminaison ?'
  },

  'pb.subtitle': { zh: '一场面试一条记录:先传录音/转写文本,再跑分析拿到六维与短板。', en: 'One record per interview: upload audio or paste a transcript, then run analysis for the six dimensions and weak points.', fr: 'Un dossier par entretien : importez l’audio ou collez la transcription, puis lancez l’analyse pour les six dimensions et les points faibles.' },
  'pb.stat.total': { zh: '总场次', en: 'Total interviews', fr: 'Entretiens totaux' },
  'pb.stat.analyzed': { zh: '已分析', en: 'Analyzed', fr: 'Analysés' },
  'pb.stat.pending': { zh: '待分析', en: 'Pending', fr: 'En attente' },
  'pb.stat.avgScore': { zh: '平均总分', en: 'Average score', fr: 'Note moyenne' },
  'pb.stat.weaknesses': { zh: '累计短板', en: 'Total weak points', fr: 'Faiblesses totales' },
  'pb.filter.company': { zh: '公司', en: 'Company', fr: 'Entreprise' },
  'pb.filter.allCompanies': { zh: '全部公司', en: 'All companies', fr: 'Toutes les entreprises' },
  'pb.filter.companyCount': { zh: '{n} 场', en: '{n} interviews', fr: '{n} entretiens' },
  'pb.filter.status': { zh: '状态', en: 'Status', fr: 'Statut' },
  'pb.filter.allStatuses': { zh: '全部状态', en: 'All statuses', fr: 'Tous les statuts' },
  'pb.empty.title': { zh: '还没有机经记录', en: 'No interview records yet', fr: 'Aucun dossier d’entretien' },
  'pb.empty.msg': { zh: '面试完趁记忆还热,先把这一场建下来,再补录音或转写文本。', en: 'Right after an interview, log it while it’s fresh — then add the recording or transcript.', fr: 'Juste après un entretien, créez le dossier tant que c’est frais — ajoutez ensuite l’audio ou la transcription.' },
  'pb.empty.create': { zh: '新建第一条', en: 'Create the first one', fr: 'Créer le premier' },
  'pb.card.transcribed': { zh: '已转写', en: 'Transcribed', fr: 'Transcrit' },
  'pb.card.viewReview': { zh: '查看复盘', en: 'View review', fr: 'Voir le bilan' },
  'pb.pager.info': { zh: '第 {page} / {totalPages} 页 · 共 {total} 条', en: 'Page {page} of {totalPages} · {total} total', fr: 'Page {page} sur {totalPages} · {total} au total' },
  'pb.dialog.title': { zh: '新建机经条目', en: 'New interview record', fr: 'Nouveau dossier d’entretien' },
  'pb.dialog.sub': { zh: '先记下公司、岗位、轮次与日期,材料和问答可以稍后补', en: 'Log the company, role, round and date first — assets and Q&A can be added later', fr: 'Notez d’abord l’entreprise, le poste, le tour et la date — documents et Q&R viendront après' },
  'pb.dialog.saving': { zh: '保存中…', en: 'Saving…', fr: 'Enregistrement…' },
  'pb.dialog.created': { zh: '创建并进入', en: 'Create & open', fr: 'Créer et ouvrir' },
  'pb.form.companyName': { zh: '公司名 *', en: 'Company *', fr: 'Entreprise *' },
  'pb.form.role': { zh: '岗位 *', en: 'Role *', fr: 'Poste *' },
  'pb.form.roundNo': { zh: '第几轮', en: 'Round no.', fr: 'N° du tour' },
  'pb.form.interviewDate': { zh: '面试日期', en: 'Interview date', fr: 'Date de l’entretien' },
  'pb.form.interviewFormat': { zh: '形式', en: 'Format', fr: 'Format' },
  'pb.form.formatUnspecified': { zh: '未指定', en: 'Not specified', fr: 'Non précisé' },
  'pb.form.interviewers': { zh: '面试官', en: 'Interviewers', fr: 'Intervieweurs' },
  'pb.form.location': { zh: '地点', en: 'Location', fr: 'Lieu' },
  'pb.form.jdSummary': { zh: 'JD 摘要', en: 'JD summary', fr: 'Résumé de l’offre' },
  'pb.form.jdText': { zh: 'JD 原文', en: 'Full JD', fr: 'Offre complète' },
  'pb.form.companyProfile': { zh: '公司背景', en: 'Company background', fr: 'Présentation de l’entreprise' },
  'pb.form.notes': { zh: '备注', en: 'Notes', fr: 'Notes' },
  'pb.form.interviewersPh': { zh: '姓名/职位', en: 'Name / title', fr: 'Nom / poste' },
  'pb.form.locationPh': { zh: '线上 / 多伦多办公室', en: 'Online / Toronto office', fr: 'En ligne / Bureau de Toronto' },
  'pb.form.jdSummaryPh': { zh: '岗位要求的关键词,便于后续对比', en: 'Keywords from the job requirements, for later comparison', fr: 'Mots-clés des exigences du poste, pour comparaison ultérieure' },
  'pb.form.jdTextPh': { zh: '可整段粘贴招聘页面内容', en: 'You can paste the whole job posting here', fr: 'Collez ici tout le contenu de l’offre' },
  'pb.form.companyProfilePh': { zh: '规模/行业/技术栈,便于以后回忆', en: 'Size / industry / tech stack, as a reminder for later', fr: 'Taille / secteur / stack technique, pour mémoire' },
  'pb.form.notesPh': { zh: '现场感受、面试官关注点等', en: 'Impressions from the session, what interviewers focused on, etc.', fr: 'Impressions de la session, points d’attention des intervieweurs, etc.' },
  'pb.edit.requiredFields': { zh: '公司名与岗位为必填项', en: 'Company and role are required', fr: 'L’entreprise et le poste sont obligatoires' },
  'pb.list.confirmDelete': { zh: '确认删除「{company} · {role}」这条机经?\n删除后材料与问答一并移除,不可恢复。', en: 'Delete the record "{company} · {role}"?\nAssets and Q&A will be removed too and cannot be recovered.', fr: 'Supprimer le dossier « {company} · {role} » ?\nLes documents et les Q&R seront aussi supprimés, sans possibilité de récupération.' },
  'pb.created': { zh: '条目已创建', en: 'Record created', fr: 'Dossier créé' },
  'pb.deleted': { zh: '已删除', en: 'Deleted', fr: 'Supprimé' },
  'pb.round': { zh: '第 {n} 轮', en: 'Round {n}', fr: 'Tour {n}' },
  'pb.status.Draft': { zh: '草稿', en: 'Draft', fr: 'Brouillon' },
  'pb.status.AssetsUploaded': { zh: '材料已上传', en: 'Assets uploaded', fr: 'Documents importés' },
  'pb.status.Transcribing': { zh: '转写中', en: 'Transcribing', fr: 'Transcription en cours' },
  'pb.status.Transcribed': { zh: '已转写', en: 'Transcribed', fr: 'Transcrit' },
  'pb.status.Analyzing': { zh: '分析中', en: 'Analyzing', fr: 'Analyse en cours' },
  'pb.status.Analyzed': { zh: '已分析', en: 'Analyzed', fr: 'Analysé' },
  'pb.status.Failed': { zh: '失败', en: 'Failed', fr: 'Échec' },
  'pb.format.phoneScreen': { zh: '电话初筛', en: 'Phone screen', fr: 'Présélection téléphonique' },
  'pb.format.techRound1': { zh: '技术一面', en: 'Technical round 1', fr: 'Entretien technique 1' },
  'pb.format.techRound2': { zh: '技术二面', en: 'Technical round 2', fr: 'Entretien technique 2' },
  'pb.format.systemDesign': { zh: '系统设计', en: 'System design', fr: 'Conception de système' },
  'pb.format.behavioral': { zh: '行为面', en: 'Behavioral', fr: 'Comportemental' },
  'pb.format.hiringManager': { zh: 'Hiring Manager', en: 'Hiring Manager', fr: 'Hiring Manager' },
  'pb.format.final': { zh: '终面', en: 'Final round', fr: 'Tour final' },
  'pb.format.other': { zh: '其他', en: 'Other', fr: 'Autre' },
  'pb.detail.backToList': { zh: '返回机经列表', en: 'Back to records', fr: 'Retour aux dossiers' },
  'pb.detail.missingId': { zh: '缺少条目 ID', en: 'Missing record ID', fr: 'ID de dossier manquant' },
  'pb.detail.noScore': { zh: '尚未分析,暂无分数。上传材料后点击「触发分析」。', en: 'Not analyzed yet — no scores. Upload assets and click "Run analysis".', fr: 'Pas encore analysé — aucune note. Importez des documents puis cliquez sur « Lancer l’analyse ».' },
  'pb.detail.polling': { zh: '流水线进行中,每 15 秒自动刷新…', en: 'Pipeline running — auto-refreshing every 15 seconds…', fr: 'Pipeline en cours — actualisation automatique toutes les 15 secondes…' },
  'pb.detail.failed': { zh: '分析失败', en: 'Analysis failed', fr: 'Échec de l’analyse' },
  'pb.detail.failedHint': { zh: '修正材料或转写文本后,重新点击「触发分析」即可。', en: 'Fix the assets or transcript, then click "Run analysis" again.', fr: 'Corrigez les documents ou la transcription, puis relancez « Lancer l’analyse ».' },
  'pb.action.startTranscription': { zh: '开始转写', en: 'Start transcription', fr: 'Démarrer la transcription' },
  'pb.action.triggerAnalysis': { zh: '触发分析', en: 'Run analysis', fr: 'Lancer l’analyse' },
  'pb.action.transcriptionStarted': { zh: '转写已提交,稍后自动刷新', en: 'Transcription started — will refresh automatically', fr: 'Transcription lancée — actualisation automatique à venir' },
  'pb.action.analysisStarted': { zh: '分析已提交,稍后自动刷新', en: 'Analysis started — will refresh automatically', fr: 'Analyse lancée — actualisation automatique à venir' },
  'pb.tab.rounds': { zh: '轮次 ({n})', en: 'Rounds ({n})', fr: 'Tours ({n})' },
  'pb.tab.questions': { zh: '问答 ({n})', en: 'Q&A ({n})', fr: 'Questions ({n})' },
  'pb.tab.assets': { zh: '材料 ({n})', en: 'Assets ({n})', fr: 'Documents ({n})' },
  'pb.tab.guidance': { zh: '指导材料', en: 'Guidance', fr: 'Guide' },
  'pb.overview.basicTitle': { zh: '基本信息', en: 'Basic info', fr: 'Infos de base' },
  'pb.overview.aiSummary': { zh: 'AI 总评', en: 'AI summary', fr: 'Synthèse IA' },
  'pb.overview.aiSummarySub': { zh: '这场面试的整体判断与最该改的地方', en: 'Overall judgement of this interview and what to fix first', fr: 'Jugement global de cet entretien et ce qu’il faut corriger en priorité' },
  'pb.overview.noAnalysis': { zh: '还没有分析结果。上传录音或粘贴转写文本后点击「触发分析」。', en: 'No analysis results yet. Upload a recording or paste a transcript, then click "Run analysis".', fr: 'Aucun résultat d’analyse. Importez un enregistrement ou collez une transcription, puis cliquez sur « Lancer l’analyse ».' },
  'pb.overview.dims': { zh: '六维能力', en: 'Six dimensions', fr: 'Six dimensions' },
  'pb.overview.dimsSub': { zh: '低于 55 分的维度应优先补', en: 'Dimensions below 55 should be fixed first', fr: 'Les dimensions sous 55 doivent être corrigées en priorité' },
  'pb.overview.noScore': { zh: '暂无分数。', en: 'No scores yet.', fr: 'Aucune note.' },
  'pb.dimHint.overall': { zh: '六维加权结果', en: 'Weighted result of the six dimensions', fr: 'Résultat pondéré des six dimensions' },
  'pb.dimHint.pronunciation': { zh: '音准与重音', en: 'Accuracy and stress', fr: 'Justesse et accent' },
  'pb.dimHint.fluency': { zh: '停顿与语速', en: 'Pauses and pace', fr: 'Pauses et débit' },
  'pb.dimHint.structure': { zh: '是否有清晰框架', en: 'Whether the structure is clear', fr: 'Clarté de la structure' },
  'pb.dimHint.technicalDepth': { zh: '是否讲到原理与权衡', en: 'Whether principles and trade-offs were covered', fr: 'Principes et arbitrages couverts ou non' },
  'pb.dimHint.relevance': { zh: '是否答到点上', en: 'Whether answers hit the point', fr: 'Réponses pertinentes ou non' },
  'pb.metrics.title': { zh: '客观声学指标', en: 'Objective speech metrics', fr: 'Métriques vocales objectives' },
  'pb.metrics.sub': { zh: '只统计候选人自己的发言 · 舒适语速约 130–170 词/分钟', en: 'Candidate’s own speech only · comfortable pace ≈ 130–170 words/min', fr: 'Parole du candidat uniquement · rythme confortable ≈ 130–170 mots/min' },
  'pb.metric.wpm': { zh: '词/分钟', en: 'Words/min', fr: 'Mots/min' },
  'pb.metric.avgSentence': { zh: '平均句长(词)', en: 'Avg sentence length (words)', fr: 'Longueur moyenne des phrases (mots)' },
  'pb.metric.shortSentences': { zh: '过短句(<5词)', en: 'Short sentences (<5 words)', fr: 'Phrases trop courtes (<5 mots)' },
  'pb.metric.fillers': { zh: '填充词总数', en: 'Filler words', fr: 'Mots de remplissage' },
  'pb.metric.pauses': { zh: '长停顿(>2s)', en: 'Long pauses (>2s)', fr: 'Longues pauses (>2 s)' },
  'pb.metric.repetition': { zh: '自我重复', en: 'Self-repetitions', fr: 'Auto-répétitions' },
  'pb.metric.pronAccuracy': { zh: '发音准确度', en: 'Pronunciation accuracy', fr: 'Précision de prononciation' },
  'pb.metric.wordCount': { zh: '总词数', en: 'Total words', fr: 'Total de mots' },
  'pb.metrics.fillersTitle': { zh: '填充词明细', en: 'Filler word details', fr: 'Détail des mots de remplissage' },
  'pb.metrics.problemWordsTitle': { zh: '术语发音关注', en: 'Terms to watch for pronunciation', fr: 'Termes à surveiller' },
  'pb.metrics.accuracyTip': { zh: '准确度 {n} 分', en: 'Accuracy: {n}', fr: 'Précision : {n}' },
  'pb.metrics.structureTitle': { zh: '结构骨架标记', en: 'Structure markers', fr: 'Marqueurs de structure' },
  'pb.metrics.structureFound': { zh: '检出分点/转折/收尾标记 {n} 类 —— 有标记说明在尝试结构化表达', en: '{n} structure marker types detected — markers show an attempt at structured expression', fr: '{n} types de marqueurs détectés — ils montrent un effort de structuration' },
  'pb.metrics.structureNone': { zh: '全程未检出分点/转折/收尾标记 —— 表达偏平铺直叙,建议刻意练习 "First… Second… Third…" 和 trade-off 收尾。', en: 'No structure markers detected — the delivery reads as flat narration. Deliberately practise "First… Second… Third…" and trade-off closings.', fr: 'Aucun marqueur de structure détecté — le discours est trop linéaire. Pratiquez délibérément « First… Second… Third… » et les conclusions trade-off.' },
  'pb.relevance.title': { zh: '问答切题度', en: 'Q&A relevance', fr: 'Pertinence des Q&R' },
  'pb.relevance.sub': { zh: '问题 vs 回答对照 —— 卡壳 / 诊断存疑的标出来', en: 'Question vs answer, side by side — stuck or questionable answers flagged', fr: 'Question contre réponse — réponses bloquées ou douteuses signalées' },
  'pb.relevance.stuck': { zh: '卡壳', en: 'Got stuck', fr: 'Bloqué' },
  'pb.relevance.smooth': { zh: '作答流畅', en: 'Answered smoothly', fr: 'Réponse fluide' },
  'pb.relevance.q': { zh: '问：', en: 'Q: ', fr: 'Q : ' },
  'pb.relevance.a': { zh: '答：', en: 'A: ', fr: 'R : ' },
  'pb.relevance.noAnswer': { zh: '（未记录回答）', en: '(no answer recorded)', fr: '(réponse non enregistrée)' },
  'pb.weakness.title': { zh: '短板清单({n})', en: 'Weaknesses ({n})', fr: 'Faiblesses ({n})' },
  'pb.weakness.sub': { zh: '按严重程度排序,每条都带证据与改法', en: 'Sorted by severity — each with evidence and a fix', fr: 'Triées par gravité — chacune avec preuve et correctif' },
  'pb.weakness.empty': { zh: '还没有短板记录。分析完成后会自动生成,也可以在下方手工添加。', en: 'No weakness records yet. They’ll be generated after analysis, or add one manually below.', fr: 'Aucune faiblesse enregistrée. Elles seront générées après l’analyse, ou ajoutez-en une ci-dessous.' },
  'pb.weakness.severity': { zh: '严重度 {n}', en: 'Severity {n}', fr: 'Gravité {n}' },
  'pb.weakness.count': { zh: '已出现 {n} 次', en: 'Appeared {n} times', fr: 'Apparu {n} fois' },
  'pb.weakness.evidence': { zh: '原话证据', en: 'Quote as evidence', fr: 'Citation à l’appui' },
  'pb.weakness.addTitle': { zh: '手工添加短板', en: 'Add a weakness manually', fr: 'Ajouter une faiblesse manuellement' },
  'pb.weakness.category': { zh: '类别', en: 'Category', fr: 'Catégorie' },
  'pb.weakness.severityLabel': { zh: '严重度 (1-5)', en: 'Severity (1-5)', fr: 'Gravité (1-5)' },
  'pb.weakness.titleLabel': { zh: '标题 *', en: 'Title *', fr: 'Titre *' },
  'pb.weakness.titlePh': { zh: '如:回答缺少结论先行', en: 'e.g. answers lack a conclusion-first structure', fr: 'p. ex. réponses sans conclusion d’abord' },
  'pb.weakness.detailLabel': { zh: '说明', en: 'Details', fr: 'Détails' },
  'pb.weakness.evidenceLabel': { zh: '证据(面试原话)', en: 'Evidence (interview quote)', fr: 'Preuve (citation de l’entretien)' },
  'pb.weakness.suggestionLabel': { zh: '改进建议', en: 'Suggested fix', fr: 'Correctif suggéré' },
  'pb.weakness.add': { zh: '添加短板', en: 'Add weakness', fr: 'Ajouter la faiblesse' },
  'pb.weakness.titleRequired': { zh: '请填写短板标题', en: 'Please enter a weakness title', fr: 'Veuillez saisir un titre de faiblesse' },
  'pb.weakness.added': { zh: '短板已记录', en: 'Weakness recorded', fr: 'Faiblesse enregistrée' },
  'pb.weakness.confirmDelete': { zh: '删除短板「{title}」?', en: 'Delete weakness "{title}"?', fr: 'Supprimer la faiblesse « {title} » ?' },
  'pb.question.intro': { zh: '复盘的价值几乎全在这页:把「当时怎么答的」和「应该怎么答」并排放在一起。', en: 'Almost all review value is on this page: "how you answered then" next to "how you should answer".', fr: 'Presque toute la valeur du bilan est ici : « comment vous avez répondu » à côté de « comment répondre ».' },
  'pb.question.empty': { zh: '还没有问答记录。分析完成后会自动拆出,也可以在下方手工补录。', en: 'No Q&A records yet. They’ll be extracted after analysis, or add them manually below.', fr: 'Aucune Q&R. Elles seront extraites après l’analyse, ou ajoutez-les ci-dessous.' },
  'pb.question.stuck': { zh: '卡住了', en: 'Got stuck', fr: 'Bloqué' },
  'pb.question.difficulty': { zh: '难度 {n}/5', en: 'Difficulty {n}/5', fr: 'Difficulté {n}/5' },
  'pb.question.myAnswer': { zh: '我的回答', en: 'My answer', fr: 'Ma réponse' },
  'pb.question.noMyAnswer': { zh: '(没有记录回答内容)', en: '(no answer recorded)', fr: '(réponse non enregistrée)' },
  'pb.question.recommended': { zh: '推荐回答', en: 'Recommended answer', fr: 'Réponse recommandée' },
  'pb.question.noRecommended': { zh: '(还没有推荐回答)', en: '(no recommended answer yet)', fr: '(pas encore de réponse recommandée)' },
  'pb.question.assessment': { zh: '点评', en: 'Assessment', fr: 'Évaluation' },
  'pb.question.missedPoints': { zh: '遗漏的要点', en: 'Missed points', fr: 'Points manqués' },
  'pb.question.stuckReason': { zh: '卡住原因:', en: 'Stuck because: ', fr: 'Bloqué car : ' },
  'pb.question.addTitle': { zh: '手工补录问答', en: 'Add Q&A manually', fr: 'Ajouter des Q&R manuellement' },
  'pb.question.addSub': { zh: '趁记忆还在,把没被录音覆盖的问题补上', en: 'While it’s fresh, add the questions the recording missed', fr: 'Tant que c’est frais, ajoutez les questions manquées par l’enregistrement' },
  'pb.question.questionLabel': { zh: '面试官的问题 *', en: 'Interviewer’s question *', fr: 'Question de l’intervieweur *' },
  'pb.question.questionPh': { zh: '尽可能记原话', en: 'Quote as closely as you can', fr: 'Citez au plus près' },
  'pb.question.myAnswerLabel': { zh: '我的回答', en: 'My answer', fr: 'Ma réponse' },
  'pb.question.myAnswerPh': { zh: '当时是怎么答的', en: 'How you answered at the time', fr: 'Comment vous avez répondu' },
  'pb.question.categoryLabel': { zh: '类别', en: 'Category', fr: 'Catégorie' },
  'pb.question.difficultyLabel': { zh: '难度 (1-5)', en: 'Difficulty (1-5)', fr: 'Difficulté (1-5)' },
  'pb.question.gotStuck': { zh: '当时卡住了', en: 'I got stuck', fr: 'J’étais bloqué' },
  'pb.question.stuckReasonLabel': { zh: '卡住原因', en: 'Why you got stuck', fr: 'Raison du blocage' },
  'pb.question.stuckReasonPh': { zh: '知识点不熟 / 没听懂问题 / 紧张', en: 'Didn’t know the topic / misheard the question / nervous', fr: 'Sujet mal maîtrisé / question mal comprise / stress' },
  'pb.question.selfAssessmentLabel': { zh: '自评(哪里答得好/不好)', en: 'Self-assessment (what went well / badly)', fr: 'Auto-évaluation (bien / mal)' },
  'pb.question.recommendedLabel': { zh: '推荐回答(事后想到的更好答法)', en: 'Recommended answer (the better version you thought of later)', fr: 'Réponse recommandée (meilleure version trouvée après)' },
  'pb.question.add': { zh: '添加问答', en: 'Add Q&A', fr: 'Ajouter la Q&R' },
  'pb.question.questionRequired': { zh: '请填写面试官的问题', en: 'Please enter the interviewer’s question', fr: 'Veuillez saisir la question de l’intervieweur' },
  'pb.question.added': { zh: '问答已添加', en: 'Q&A added', fr: 'Q&R ajoutée' },
  'pb.question.confirmDelete': { zh: '删除这条问答?', en: 'Delete this Q&A?', fr: 'Supprimer cette Q&R ?' },
  'pb.import.toTechStack': { zh: '导入到 TechStack', en: 'Import to TechStack', fr: 'Importer vers TechStack' },
  'pb.import.title': { zh: '勾选导入 TechStack', en: 'Select to import to TechStack', fr: 'Sélectionner pour TechStack' },
  'pb.import.sub': { zh: '已选 {selected} / {total} · 重复条目会自动跳过', en: '{selected} of {total} selected · duplicates are skipped automatically', fr: '{selected} sur {total} sélectionnés · les doublons sont ignorés' },
  'pb.import.loading': { zh: '加载候选中…', en: 'Loading candidates…', fr: 'Chargement des candidats…' },
  'pb.import.empty': { zh: '没有可导入的问答。', en: 'No Q&A available to import.', fr: 'Aucune Q&R à importer.' },
  'pb.import.selectAll': { zh: '全选', en: 'Select all', fr: 'Tout sélectionner' },
  'pb.import.selectNone': { zh: '全不选', en: 'Select none', fr: 'Tout désélectionner' },
  'pb.import.confirm': { zh: '确认导入 ({n})', en: 'Confirm import ({n})', fr: 'Confirmer l’import ({n})' },
  'pb.import.selectAtLeastOne': { zh: '请至少勾选一条', en: 'Please select at least one', fr: 'Veuillez en sélectionner au moins une' },
  'pb.import.done': { zh: '导入完成:新增 {created} 条,跳过重复 {skipped} 条', en: 'Import done: {created} created, {skipped} duplicates skipped', fr: 'Import terminé : {created} créés, {skipped} doublons ignorés' },
  'pb.import.stuck': { zh: '卡住了', en: 'Got stuck', fr: 'Bloqué' },
  'pb.import.questionLabel': { zh: '问题', en: 'Question', fr: 'Question' },
  'pb.import.categoryLabel': { zh: '分类', en: 'Category', fr: 'Catégorie' },
  'pb.import.answerLabel': { zh: '答案 / 要点', en: 'Answer / key points', fr: 'Réponse / points clés' },
  'pb.import.answerPh': { zh: '留空则不带答案导入', en: 'Leave empty to import without an answer', fr: 'Laisser vide pour importer sans réponse' },
  'pb.round.title': { zh: '面试轮次', en: 'Interview rounds', fr: 'Tours d’entretien' },
  'pb.round.sub': { zh: '一家公司多轮,每轮独立记录时间 / 面试官 / 结果', en: 'One company, multiple rounds — each round logs its own time / interviewers / outcome', fr: 'Une entreprise, plusieurs tours — chacun avec sa date / ses intervieweurs / son résultat' },
  'pb.round.empty': { zh: '还没有轮次记录。旧数据的第 1 轮会在迁移后自动出现,也可以手动新增。', en: 'No rounds recorded yet. Round 1 from old data will appear after migration, or add one manually.', fr: 'Aucun tour enregistré. Le tour 1 des anciennes données apparaîtra après migration, ou ajoutez-en un manuellement.' },
  'pb.round.addTitle': { zh: '新增一轮', en: 'Add a round', fr: 'Ajouter un tour' },
  'pb.round.add': { zh: '新增轮次', en: 'Add round', fr: 'Ajouter le tour' },
  'pb.round.added': { zh: '已新增一轮', en: 'Round added', fr: 'Tour ajouté' },
  'pb.round.saved': { zh: '轮次已保存', en: 'Round saved', fr: 'Tour enregistré' },
  'pb.round.upcoming': { zh: '即将到来的面试', en: 'Upcoming interviews', fr: 'Entretiens à venir' },
  'pb.round.join': { zh: '加入会议', en: 'Join', fr: 'Rejoindre' },
  'pb.round.addToCal': { zh: '添加到日历', en: 'Add to calendar', fr: 'Ajouter au calendrier' },
  'pb.round.viewGuidance': { zh: '查看备考材料', en: 'View prep guidance', fr: 'Voir le guide' },
  'pb.round.prepTitle': { zh: '面试前准备清单', en: 'Prep checklist', fr: 'Liste de préparation' },
  'pb.round.prepPh': { zh: '添加一个准备项,回车确认', en: 'Add a prep item, Enter to confirm', fr: 'Ajouter' },
  'pb.round.emailsTitle': { zh: '相关邮件', en: 'Related emails', fr: 'E-mails liés' },
  'pb.round.emailSubPh': { zh: '邮件主题', en: 'Subject', fr: 'Objet' },
  'pb.round.emailFromPh': { zh: '发件人', en: 'From', fr: 'De' },
  'pb.round.emailSnipPh': { zh: '摘要(可选)', en: 'Snippet (optional)', fr: 'Extrait' },
  'pb.round.transcriptTitle': { zh: '录音与文稿', en: 'Recording & transcript', fr: 'Enregistrement' },
  'pb.round.recording': { zh: '打开录音', en: 'Open recording', fr: 'Ouvrir' },
  'pb.round.timeLabel': { zh: '时间', en: 'Time', fr: 'Heure' },
  'pb.round.meetingLinkLabel': { zh: '会议链接', en: 'Meeting link', fr: 'Lien' },
  'pb.round.meetingLinkPh': { zh: 'Teams / Zoom 链接', en: 'Teams / Zoom URL', fr: 'URL Teams/Zoom' },
  'pb.round.transcriptLabel': { zh: '面试文稿', en: 'Transcript', fr: 'Transcription' },
  'pb.round.recordingLabel': { zh: '录音链接', en: 'Recording URL', fr: 'URL' },
  'pb.round.syntheticHint': { zh: '这是从条目基本信息合成的轮次。点击下方"新增轮次"创建正式轮次,即可使用备考清单、邮件等完整功能。', en: 'Synthetic round from entry info. Add a real round below for the full workspace.', fr: 'Tour synthétique.' },
  'pb.round.confirmDelete': { zh: '删除第 {n} 轮?', en: 'Delete round {n}?', fr: 'Supprimer le tour {n} ?' },
  'pb.round.stageLabel': { zh: '阶段', en: 'Stage', fr: 'Étape' },
  'pb.round.resultLabel': { zh: '结果', en: 'Outcome', fr: 'Résultat' },
  'pb.round.dateLabel': { zh: '日期', en: 'Date', fr: 'Date' },
  'pb.round.formatLabel': { zh: '形式', en: 'Format', fr: 'Format' },
  'pb.round.interviewersLabel': { zh: '面试官:', en: 'Interviewers: ', fr: 'Intervieweurs : ' },
  'pb.round.interviewersField': { zh: '面试官', en: 'Interviewers', fr: 'Intervieweurs' },
  'pb.round.locationLabel': { zh: '地点', en: 'Location', fr: 'Lieu' },
  'pb.round.notesLabel': { zh: '备注', en: 'Notes', fr: 'Notes' },
  'pb.round.feedbackLabel': { zh: '反馈', en: 'Feedback', fr: 'Retours' },
  'pb.round.feedback': { zh: '反馈', en: 'Feedback', fr: 'Retours' },
  'pb.roundOutcome.Pending': { zh: '待定', en: 'Pending', fr: 'En attente' },
  'pb.roundOutcome.Passed': { zh: '通过', en: 'Passed', fr: 'Réussi' },
  'pb.roundOutcome.Rejected': { zh: '被拒', en: 'Rejected', fr: 'Refusé' },
  'pb.roundOutcome.Ghosted': { zh: '失联', en: 'Ghosted', fr: 'Sans nouvelles' },
  'pb.roundOutcome.Cancelled': { zh: '取消', en: 'Cancelled', fr: 'Annulé' },
  'pb.roundOutcome.NoShow': { zh: '缺席', en: 'No-show', fr: 'Absent' },
  'pb.jobStatus.Pending': { zh: '待投递', en: 'Queued', fr: 'En attente' },
  'pb.jobStatus.Dispatched': { zh: '已投递', en: 'Dispatched', fr: 'Envoyé' },
  'pb.jobStatus.Succeeded': { zh: '已完成', en: 'Succeeded', fr: 'Réussi' },
  'pb.jobStatus.Failed': { zh: '失败', en: 'Failed', fr: 'Échec' },
  'pb.jobStatus.Dead': { zh: '投递终止', en: 'Dead', fr: 'Abandonné' },
  'pb.jobType.transcription': { zh: '转写分析', en: 'Transcription', fr: 'Transcription' },
  'pb.jobType.analysis': { zh: '分析', en: 'Analysis', fr: 'Analyse' },
  'pb.asset.intro': { zh: '录音走「上传 → 转写」两步;手上有现成文本的,也可以直接建一条 Transcript 材料。', en: 'Audio goes through "upload → transcribe"; if you already have text, create a Transcript asset directly.', fr: 'L’audio suit « import → transcription » ; si vous avez déjà le texte, créez directement un document Transcript.' },
  'pb.asset.uploadTitle': { zh: '上传录音', en: 'Upload audio', fr: 'Importer l’audio' },
  'pb.asset.uploadSub': { zh: '支持 mp3 / m4a / wav / webm / ogg;上传后点「开始转写」自动跑分析', en: 'Supports mp3 / m4a / wav / webm / ogg — click "Start transcription" after upload to run analysis', fr: 'Prend en charge mp3 / m4a / wav / webm / ogg — cliquez sur « Démarrer la transcription » après l’import' },
  'pb.asset.empty': { zh: '还没有材料。上传面试录音,或粘贴转写文本。', en: 'No assets yet. Upload an interview recording or paste a transcript.', fr: 'Aucun document. Importez un enregistrement ou collez une transcription.' },
  'pb.asset.jobsTitle': { zh: '流水线记录', en: 'Pipeline log', fr: 'Journal du pipeline' },
  'pb.asset.jobsSub': { zh: '每次「开始转写」一行;失败原因与投递尝试留档可查', en: 'One row per "Start transcription" — failure reasons and dispatch attempts are kept on record', fr: 'Une ligne par « Démarrer la transcription » — causes d’échec et tentatives conservées' },
  'pb.asset.attempts': { zh: '尝试 {a}/{m}', en: 'Attempt {a}/{m}', fr: 'Tentative {a}/{m}' },
  'pb.asset.dispatchError': { zh: '投递错误:', en: 'Dispatch error: ', fr: 'Erreur d’envoi : ' },
  'pb.asset.fileMissing': { zh: '文件丢失', en: 'File missing', fr: 'Fichier manquant' },
  'pb.asset.fileCorrupt': { zh: '文件损坏', en: 'File corrupted', fr: 'Fichier corrompu' },
  'pb.asset.transcribed': { zh: '已转写', en: 'Transcribed', fr: 'Transcrit' },
  'pb.asset.untranscribed': { zh: '未转写', en: 'Not transcribed', fr: 'Non transcrit' },
  'pb.asset.verified': { zh: '已校验', en: 'Verified', fr: 'Vérifié' },
  'pb.asset.preview': { zh: '试听', en: 'Preview', fr: 'Écouter' },
  'pb.asset.previewLoading': { zh: '加载中…', en: 'Loading…', fr: 'Chargement…' },
  'pb.asset.audioLoadFail': { zh: '音频加载失败', en: 'Failed to load audio', fr: 'Échec du chargement audio' },
  'pb.asset.uploaded': { zh: '录音已上传,可点击「开始转写」', en: 'Audio uploaded — click "Start transcription"', fr: 'Audio importé — cliquez sur « Démarrer la transcription »' },
  'pb.transcript.emptyFirst': { zh: '请先粘贴转写文本', en: 'Please paste the transcript first', fr: 'Veuillez d’abord coller la transcription' },
  'pb.transcript.saved': { zh: '转写文本已保存', en: 'Transcript saved', fr: 'Transcription enregistrée' },
  'pb.transcript.pasteLabel': { zh: '粘贴转写文本', en: 'Paste transcript', fr: 'Coller la transcription' },
  'pb.transcript.replaceLabel': { zh: '重新粘贴转写文本(将覆盖)', en: 'Re-paste transcript (will overwrite)', fr: 'Recoller la transcription (écrasera l’ancienne)' },
  'pb.transcript.placeholder': { zh: '把转写工具输出的纯文本粘到这里', en: 'Paste the plain text from your transcription tool here', fr: 'Collez ici le texte brut de votre outil de transcription' },
  'pb.transcript.save': { zh: '保存转写文本', en: 'Save transcript', fr: 'Enregistrer la transcription' },
  'pb.edit.title': { zh: '条目信息', en: 'Record details', fr: 'Détails du dossier' },
  'pb.edit.sub': { zh: '分析跑完后仍可修正公司/岗位等元数据', en: 'Company / role and other metadata can still be fixed after analysis', fr: 'L’entreprise, le poste et autres métadonnées restent modifiables après l’analyse' },
  'pb.edit.resultLabel': { zh: '结果', en: 'Outcome', fr: 'Résultat' },
  'pb.edit.resultPh': { zh: '通过 / 被拒 / 待定', en: 'Passed / rejected / pending', fr: 'Réussi / refusé / en attente' },
  'pb.edit.saving': { zh: '保存中…', en: 'Saving…', fr: 'Enregistrement…' },
  'pb.edit.save': { zh: '保存修改', en: 'Save changes', fr: 'Enregistrer les modifications' },
  'pb.guidance.title': { zh: '面试指导材料', en: 'Interview guidance', fr: 'Guide d’entretien' },
  'pb.guidance.sub': { zh: '手动生成,站内浏览,版本保留', en: 'Generated manually, viewed in-app, versions kept', fr: 'Généré manuellement, consulté dans l’app, versions conservées' },
  'pb.guidance.generate': { zh: '生成指导材料', en: 'Generate guidance', fr: 'Générer le guide' },
  'pb.guidance.regenerate': { zh: '重新生成新版本', en: 'Regenerate new version', fr: 'Régénérer une version' },
  'pb.guidance.exportPdf': { zh: '导出 PDF(打印)', en: 'Export PDF (print)', fr: 'Exporter en PDF (impression)' },
  'pb.guidance.exportWord': { zh: '导出 Word', en: 'Export Word', fr: 'Exporter en Word' },
  'pb.guidance.paste': { zh: '粘贴备战材料', en: 'Paste prep material', fr: 'Coller le matériel' },
  'pb.guidance.pasteTitle': { zh: '粘贴备战材料 (Markdown)', en: 'Paste prep material (Markdown)', fr: 'Coller (Markdown)' },
  'pb.guidance.pasted': { zh: '已保存到本地', en: 'Saved locally', fr: 'Enregistré' },
  'pb.guidance.clearPasted': { zh: '清除粘贴内容', en: 'Clear pasted', fr: 'Effacer' },
  'pb.guidance.popupBlocked': { zh: '弹窗被拦截,请允许弹窗后重试', en: 'Popup blocked, please allow and retry', fr: 'Popup bloqué' },
  'pb.guidance.toc': { zh: '目录', en: 'Contents', fr: 'Sommaire' },
  'pb.guidance.inputTitle': { zh: '生成备战材料', en: 'Generate prep material', fr: 'Générer' },
  'pb.guidance.inputHint': { zh: '以下四项都会拼进 AI 提示词,留空则跳过该项', en: 'All four feed the AI prompt; leave blank to skip', fr: 'Tous alimentent le prompt' },
  'pb.guidance.inputJd': { zh: 'JD (职位描述)', en: 'JD', fr: 'Description du poste' },
  'pb.guidance.inputJdPh': { zh: '粘贴职位描述,已自动带入条目里的 JD', en: 'Paste the job description', fr: 'Coller la description' },
  'pb.guidance.inputResume': { zh: '我的简历', en: 'My resume', fr: 'Mon CV' },
  'pb.guidance.inputResumePh': { zh: '粘贴简历全文,AI 会针对你的背景定制备战内容', en: 'Paste your resume for tailored prep', fr: 'Coller votre CV' },
  'pb.guidance.inputExperiences': { zh: '全网面经', en: 'Interview experiences', fr: 'Retours d\'entretien' },
  'pb.guidance.inputExperiencesPh': { zh: '粘贴从网上搜到的面经、面试题', en: 'Paste interview questions/experiences found online', fr: 'Coller les retours trouvés en ligne' },
  'pb.guidance.inputCustom': { zh: '我的特别要求', en: 'My requirements', fr: 'Mes exigences' },
  'pb.guidance.inputCustomPh': { zh: '比如:重点准备系统设计、多给行为面试题', en: 'e.g. focus on system design', fr: 'ex. focus design' },
  'pb.action.goMock': { zh: '模拟面试', en: 'Mock Interview', fr: 'Entretien blanc' },
  'pb.action.goMockHint': { zh: '去模拟面试实战', en: 'Go to mock interview', fr: 'Aller au mock' },
  'pb.guidance.noJd': { zh: '还没有 JD。', en: 'No JD yet. ', fr: 'Pas de JD. ' },
  'pb.guidance.goTracker': { zh: '去 Tracker 添加', en: 'Add in Tracker', fr: 'Ajouter dans Tracker' },
  'pb.guidance.noResume': { zh: '还没有存简历。', en: 'No resume saved. ', fr: 'Pas de CV. ' },
  'pb.guidance.goProfile': { zh: '去 My Account 添加', en: 'Add in My Account', fr: 'Ajouter dans le profil' },
  'pb.guidance.resumeFromProfile': { zh: '已从 My Account 自动带入', en: 'Auto-filled from My Account', fr: 'Depuis le profil' },
  'profile.resumeTitle': { zh: '我的简历', en: 'My Resume', fr: 'Mon CV' },
  'profile.resumeHint': { zh: '存一份,生成备战材料时自动带入', en: 'Saved once, auto-filled when generating prep material', fr: 'Enregistré une fois' },
  'profile.resumeLabel': { zh: '简历全文', en: 'Resume text', fr: 'Texte du CV' },
  'profile.resumePh': { zh: '粘贴简历全文...', en: 'Paste your full resume...', fr: 'Coller votre CV...' },
  'profile.resumeSaved': { zh: '简历已保存', en: 'Resume saved', fr: 'CV enregistré' },
  'profile.resumeSaveFailed': { zh: '保存失败', en: 'Save failed', fr: 'Échec' },
  'profile.resumeUpload': { zh: '上传简历文件', en: 'Upload resume file', fr: 'Téléverser CV' },
  'profile.resumeExtractHint': { zh: '支持 txt/md 直接读取,PDF 自动提取文字,Word 请复制粘贴', en: 'txt/md read directly, PDF auto-extracted', fr: 'txt/md direct, PDF extrait' },
  'profile.resumeExtracting': { zh: '正在提取 PDF 文字...', en: 'Extracting PDF text...', fr: 'Extraction...' },
  'profile.resumeExtracted': { zh: '已提取文字,请检查后保存', en: 'Text extracted, review and save', fr: 'Texte extrait' },
  'profile.resumeExtractFailed': { zh: '提取失败,请手动粘贴', en: 'Extraction failed, please paste manually', fr: 'Échec d\'extraction' },
  'profile.resumeDocxHint': { zh: 'Word 请打开复制全文粘贴到下方', en: 'For Word, copy-paste the text below', fr: 'Copier-coller le texte' },
  'profile.resumeSavedLabel': { zh: '已保存的简历', en: 'Saved resume', fr: 'CV enregistré' },
  'pb.guidance.historyLabel': { zh: '历史版本', en: 'Version history', fr: 'Historique des versions' },
  'pb.guidance.empty': { zh: '还没有指导材料。点击「生成指导材料」,AI 会根据 JD、公司介绍、历史问答、短板和六维诊断生成一份备战文档。', en: 'No guidance yet. Click "Generate guidance" — the AI will build a prep document from the JD, company profile, past Q&A, weaknesses and the six-dimension diagnosis.', fr: 'Aucun guide. Cliquez sur « Générer le guide » — l’IA rédigera un document de préparation à partir de l’offre, du profil de l’entreprise, des Q&R passées, des faiblesses et du diagnostic en six dimensions.' },
  'pb.guidance.confirmGenerate': { zh: '生成新版本指导材料?会调用 AI,可能需要几十秒。', en: 'Generate a new version of the guidance? It calls the AI and may take tens of seconds.', fr: 'Générer une nouvelle version du guide ? Cela appelle l’IA et peut prendre des dizaines de secondes.' },
  'pb.guidance.generated': { zh: '指导材料 v{version} 已生成', en: 'Guidance v{version} generated', fr: 'Guide v{version} généré' },
  'pb.guidance.fileName': { zh: '面试指导材料-v{version}.doc', en: 'Interview-guidance-v{version}.doc', fr: 'Guide-entretien-v{version}.doc' },
  'pb.edit.saved': { zh: '已保存', en: 'Saved', fr: 'Enregistré' },
  'pb.round.interviewersPh': { zh: '如: David Castelino (Sr SWE)', en: 'e.g. David Castelino (Sr SWE)', fr: 'p. ex. David Castelino (Sr SWE)' },
  'ts.sub': { zh: '每个概念九块拆解:从一句话直觉到面试话术', en: 'Every concept in nine blocks: from a one-line intuition to interview phrasing', fr: 'Chaque notion en neuf blocs : d’une intuition en une phrase au discours d’entretien' },
  'ts.duplicates': { zh: '重复 {n}', en: '{n} duplicates', fr: '{n} doublons' },
  'ts.buckets.title': { zh: '熟练度分布', en: 'Mastery distribution', fr: 'Répartition de la maîtrise' },
  'ts.buckets.total': { zh: '共 {n} 个条目', en: '{n} items', fr: '{n} éléments' },
  'ts.filter.searchLabel': { zh: '搜索术语 / 释义', en: 'Search terms / definitions', fr: 'Rechercher terme / définition' },
  'ts.filter.searchPh': { zh: '例如 async', en: 'e.g. async', fr: 'p. ex. async' },
  'ts.filter.topic': { zh: '主题', en: 'Topic', fr: 'Thème' },
  'ts.filter.mastery': { zh: '熟练度', en: 'Mastery', fr: 'Maîtrise' },
  'ts.filter.source': { zh: '来源', en: 'Source', fr: 'Source' },
  'ts.source.tooltip': { zh: '已掌握 {m} / 待复习 {d}', en: 'Mastered {m} / due {d}', fr: 'Maîtrisé {m} / à réviser {d}' },
  'ts.source.jumpBack': { zh: '来自实战机经,点击跳回:{src}', en: 'From Playbook — click to open: {src}', fr: 'Du Journal — cliquez pour ouvrir : {src}' },
  'ts.source.from': { zh: '来自实战机经:{src}', en: 'From Playbook: {src}', fr: 'Du Journal : {src}' },
  'ts.list.total': { zh: '{n} 个条目', en: '{n} items', fr: '{n} éléments' },
  'ts.list.emptyFiltered': { zh: '没有符合筛选条件的条目。', en: 'No items match the filters.', fr: 'Aucun élément ne correspond aux filtres.' },
  'ts.list.emptyFirst': { zh: '知识库还是空的,点右上角新增第一条。', en: 'The knowledge base is empty — add the first entry from the top-right button.', fr: 'La base est vide — ajoutez la première entrée via le bouton en haut à droite.' },
  'ts.reviewCount': { zh: '复习 {n} 次', en: '{n} reviews', fr: '{n} révisions' },
  'ts.detail.difficulty': { zh: '难度 {n}', en: 'Difficulty {n}', fr: 'Difficulté {n}' },
  'ts.detail.importance': { zh: '重要度 {n}', en: 'Importance {n}', fr: 'Importance {n}' },
  'ts.detail.promote': { zh: '提升熟练度', en: 'Promote mastery', fr: 'Promouvoir la maîtrise' },
  'ts.detail.logReview': { zh: '记录复习', en: 'Log review', fr: 'Enregistrer une révision' },
  'ts.detail.nextReview': { zh: '下次复习 {date}', en: 'Next review: {date}', fr: 'Prochaine révision : {date}' },
  'ts.detail.easiness': { zh: '难度因子 {n}', en: 'Easiness factor {n}', fr: 'Facteur de facilité {n}' },
  'ts.detail.notScheduled': { zh: '尚未安排', en: 'Not scheduled', fr: 'Non planifié' },
  'ts.detail.noSelection': { zh: '从左侧选一个条目查看九块拆解。', en: 'Select an item on the left to see the nine-block breakdown.', fr: 'Sélectionnez un élément à gauche pour voir les neuf blocs.' },
  'ts.trail.title': { zh: '来源', en: 'Source', fr: 'Source' },
  'ts.trail.company': { zh: '公司 {v}', en: 'Company: {v}', fr: 'Entreprise : {v}' },
  'ts.trail.date': { zh: '日期 {v}', en: 'Date: {v}', fr: 'Date : {v}' },
  'ts.trail.siblings': { zh: '同一场面试一起进来的题({n})', en: 'Questions from the same interview ({n})', fr: 'Questions du même entretien ({n})' },
  'ts.blocks.empty': { zh: '这个条目还没填内容,先补齐「一句话直觉」再背其他部分。', en: 'This entry is empty — fill in the "one-line intuition" first, then the rest.', fr: 'Cette entrée est vide — remplissez d’abord « l’intuition en une phrase », puis le reste.' },
  'ts.badge.fromInterview': { zh: '机经', en: 'Playbook', fr: 'Journal' },
  'ts.block.question': { zh: '面试题干', en: 'Interview question', fr: 'Question d’entretien' },
  'ts.block.questionHint': { zh: '被问到时的原题', en: 'The question as asked', fr: 'La question telle que posée' },
  'ts.block.conceptExplanation': { zh: '概念讲解', en: 'Concept explained', fr: 'Notion expliquée' },
  'ts.block.conceptExplanationHint': { zh: '原理与机制', en: 'Principles and mechanics', fr: 'Principes et mécanismes' },
  'ts.block.keyPoints': { zh: '关键要点', en: 'Key points', fr: 'Points clés' },
  'ts.block.keyPointsHint': { zh: '答题必须覆盖的点', en: 'Points your answer must cover', fr: 'Points à couvrir dans la réponse' },
  'ts.block.commonMistakes': { zh: '常见误区', en: 'Common mistakes', fr: 'Erreurs fréquentes' },
  'ts.block.commonMistakesHint': { zh: '容易被追问打穿的地方', en: 'Where follow-ups can trip you up', fr: 'Où les questions de suivi peuvent vous piéger' },
  'ts.mastery.New': { zh: '未接触', en: 'New', fr: 'Nouveau' },
  'ts.mastery.Learning': { zh: '学习中', en: 'Learning', fr: 'En cours' },
  'ts.mastery.Familiar': { zh: '熟悉', en: 'Familiar', fr: 'Familier' },
  'ts.mastery.Proficient': { zh: '熟练', en: 'Proficient', fr: 'Compétent' },
  'ts.mastery.Mastered': { zh: '精通', en: 'Mastered', fr: 'Maîtrisé' },
  'ts.review.Again': { zh: '忘了', en: 'Again', fr: 'Oublié' },
  'ts.review.Hard': { zh: '很吃力', en: 'Hard', fr: 'Difficile' },
  'ts.review.Good': { zh: '记住了', en: 'Good', fr: 'Retenu' },
  'ts.review.Easy': { zh: '太简单', en: 'Easy', fr: 'Trop facile' },
  'ts.review.recorded': { zh: '已记录复习({grade}),下次 {date}', en: 'Review logged ({grade}) — next: {date}', fr: 'Révision enregistrée ({grade}) — prochaine : {date}' },
  'ts.review.notScheduled': { zh: '未排期', en: 'Not scheduled', fr: 'Non planifié' },
  'ts.promote.maxReached': { zh: '已经是最高熟练度,无需再提升', en: 'Already at max mastery — nothing to promote', fr: 'Maîtrise maximale déjà atteinte' },
  'ts.promote.confirm': { zh: '把「{title}」从「{from}」提升到「{to}」?', en: 'Promote "{title}" from {from} to {to}?', fr: 'Promouvoir « {title} » de {from} à {to} ?' },
  'ts.promote.done': { zh: '已提升为「{to}」', en: 'Promoted to {to}', fr: 'Promu à {to}' },
  'ts.created': { zh: '已新增条目', en: 'Entry created', fr: 'Entrée créée' },
  'ts.deleted': { zh: '已删除', en: 'Deleted', fr: 'Supprimé' },
  'ts.dup.merged': { zh: '已合并重复条目', en: 'Duplicates merged', fr: 'Doublons fusionnés' },
  'ts.dup.title': { zh: '重复条目({n} 组)', en: 'Duplicate entries ({n} groups)', fr: 'Doublons ({n} groupes)' },
  'ts.dup.empty': { zh: '没有发现重复条目。', en: 'No duplicates found.', fr: 'Aucun doublon trouvé.' },
  'ts.dup.count': { zh: '{n} 条重复', en: '{n} duplicates', fr: '{n} doublons' },
  'ts.dup.hasExplanation': { zh: '已有讲解正文', en: 'Has explanation', fr: 'A une explication' },
  'ts.dup.explanation': { zh: '讲解', en: 'Explanation', fr: 'Explication' },
  'ts.dup.keyPoints': { zh: '要点', en: 'Key points', fr: 'Points clés' },
  'ts.dup.keep': { zh: '保留这条', en: 'Keep this one', fr: 'Garder celui-ci' },
  'ts.dup.confirmMerge': { zh: '把同组的另外 {count} 条合并进「{title}」?\n保留条目的已有内容不会被覆盖,只补充它空缺的字段;被合并的条目会被移除。', en: 'Merge the other {count} items in this group into "{title}"?\nExisting content of the kept item won’t be overwritten — only missing fields are filled; merged items are removed.', fr: 'Fusionner les {count} autres éléments du groupe dans « {title} » ?\nLe contenu existant de l’élément conservé ne sera pas écrasé — seuls les champs manquants seront complétés ; les éléments fusionnés seront supprimés.' },
  'ts.dup.mergeFailed': { zh: '合并到第 {n} 条时失败', en: 'Failed merging item {n}', fr: 'Échec de la fusion de l’élément {n}' },
  'ts.stage.Screen': { zh: '初筛', en: 'Screen', fr: 'Présélection' },
  'ts.stage.Technical': { zh: '技术面', en: 'Technical', fr: 'Technique' },
  'ts.stage.SystemDesign': { zh: '系统设计', en: 'System design', fr: 'Conception de système' },
  'ts.stage.Behavioral': { zh: '行为面', en: 'Behavioral', fr: 'Comportemental' },
  'ts.stage.Final': { zh: '终面', en: 'Final', fr: 'Final' },
  'ts.dialog.title': { zh: '新增技术栈条目', en: 'New knowledge entry', fr: 'Nouvelle entrée' },
  'ts.dialog.titleLabel': { zh: '标题 *', en: 'Title *', fr: 'Titre *' },
  'ts.dialog.titlePh': { zh: '如 Clean Architecture / 依赖注入', en: 'e.g. Clean Architecture / dependency injection', fr: 'p. ex. Clean Architecture / injection de dépendances' },
  'ts.dialog.topicLabel': { zh: '分类 *', en: 'Category *', fr: 'Catégorie *' },
  'ts.dialog.topicPh': { zh: '如 架构 / C#/.NET / 云原生', en: 'e.g. Architecture / C#/.NET / cloud native', fr: 'p. ex. Architecture / C#/.NET / cloud natif' },
  'ts.dialog.questionLabel': { zh: '面试题干', en: 'Interview question', fr: 'Question d’entretien' },
  'ts.dialog.questionPh': { zh: '面试官会怎么问这个概念', en: 'How an interviewer would ask about this concept', fr: 'Comment un intervieweur poserait cette notion' },
  'ts.dialog.conceptLabel': { zh: '概念讲解', en: 'Concept explained', fr: 'Notion expliquée' },
  'ts.dialog.conceptPh': { zh: '原理、机制、为什么这样设计', en: 'Principles, mechanics, why it’s designed this way', fr: 'Principes, mécanismes, pourquoi c’est conçu ainsi' },
  'ts.dialog.keyPointsLabel': { zh: '关键要点(一行一条)', en: 'Key points (one per line)', fr: 'Points clés (un par ligne)' },
  'ts.dialog.keyPointsPh': { zh: '答题必须覆盖的点,每行一条', en: 'Points your answer must cover, one per line', fr: 'Points à couvrir, un par ligne' },
  'ts.dialog.keyPointsHint': { zh: '提交时会自动转成结构化列表', en: 'Converted to a structured list on submit', fr: 'Converti en liste structurée à l’envoi' },
  'ts.dialog.mistakesLabel': { zh: '常见误区(一行一条)', en: 'Common mistakes (one per line)', fr: 'Erreurs fréquentes (une par ligne)' },
  'ts.dialog.mistakesPh': { zh: '容易被追问打穿的地方,每行一条', en: 'Where follow-ups can trip you up, one per line', fr: 'Où les suivis peuvent vous piéger, une par ligne' },
  'ts.dialog.sourceTitle': { zh: '来源(可选)', en: 'Source (optional)', fr: 'Source (facultatif)' },
  'ts.dialog.sourceHint': { zh: '填了公司名,这条就会标成「来自实战机经」,日后能反查是哪家、第几轮问住的', en: 'Fill in the company and this entry will be tagged "from Playbook" — you can later trace which company and which round asked it', fr: 'Renseignez l’entreprise et l’entrée sera marquée « du Journal » — vous pourrez retrouver quelle entreprise et quel tour l’a posée' },
  'ts.dialog.companyLabel': { zh: '公司', en: 'Company', fr: 'Entreprise' },
  'ts.dialog.roundLabel': { zh: '第几轮', en: 'Round no.', fr: 'N° du tour' },
  'ts.dialog.stageLabel': { zh: '轮次类型', en: 'Round type', fr: 'Type de tour' },
  'ts.dialog.anyStage': { zh: '不限', en: 'Any', fr: 'Indifférent' },
  'ts.dialog.dateLabel': { zh: '面试日期', en: 'Interview date', fr: 'Date de l’entretien' },
  'ts.dialog.difficultyLabel': { zh: '难度 1-5', en: 'Difficulty 1-5', fr: 'Difficulté 1-5' },
  'ts.dialog.importanceLabel': { zh: '重要度 1-5', en: 'Importance 1-5', fr: 'Importance 1-5' },
  'ts.topic.derived': { zh: '推导', en: 'Derived', fr: 'Déduit' },
  'an.title': { zh: '数据分析', en: 'Analytics', fr: 'Analytique' },
  'an.subtitle': { zh: '六维能力、投递漏斗与知识补齐情况', en: 'Skill radar, application funnel, and knowledge coverage', fr: 'Radar de compétences, entonnoir et couverture des connaissances' },
  'an.radarTitle': { zh: '六维能力雷达', en: 'Skill radar', fr: 'Radar de compétences' },
  'an.radarSub': { zh: '实线为当前水平,虚线为历史最佳', en: 'Solid line: current level; dashed: personal best', fr: 'Trait plein : niveau actuel ; pointillés : record personnel' },
  'an.noRadar': { zh: '还没有雷达数据。先去「实战机经」导入一场面试录音,系统会据此评分。', en: 'No radar data yet. Import an interview recording in Playbook to get scored.', fr: 'Aucune donnée radar. Importez un enregistrement dans Journal pour être noté.' },
  'an.curLevel': { zh: '当前水平', en: 'Current', fr: 'Actuel' },
  'an.bestLevel': { zh: '历史最佳', en: 'Best', fr: 'Record' },
  'an.weakTitle': { zh: '优先补的三项', en: 'Top 3 to improve', fr: '3 priorités à améliorer' },
  'an.bestOf': { zh: '最佳 {n}', en: 'Best {n}', fr: 'Record {n}' },
  'an.funnelTitle': { zh: '投递漏斗', en: 'Application funnel', fr: 'Entonnoir de candidatures' },
  'an.funnelSub': { zh: '条长按各阶段最大值归一,不是占比', en: 'Bars normalized by each stage maximum, not proportions', fr: 'Barres normalisées par le maximum de chaque étape, pas des proportions' },
  'an.noFunnel': { zh: '还没有投递数据。去「投递跟踪」记一条就有了。', en: 'No application data yet. Log one in Tracker.', fr: 'Aucune donnée. Ajoutez une candidature dans Suivi.' },
  'an.masteryTitle': { zh: '技术栈熟练度', en: 'Tech stack mastery', fr: 'Maîtrise des technologies' },
  'an.masterySub': { zh: '按主题看已掌握比例', en: 'Mastery share by topic', fr: 'Part de maîtrise par sujet' },
  'an.noMastery': { zh: '还没有技术栈条目。', en: 'No tech stack entries yet.', fr: 'Aucune technologie pour le moment.' },
  'an.levelMastered': { zh: '精通', en: 'Mastered', fr: 'Maîtrisé' },
  'an.levelFamiliar': { zh: '熟悉', en: 'Familiar', fr: 'Familier' },
  'an.levelLearning': { zh: '学习中/未接触', en: 'Learning / New', fr: 'En cours / Nouveau' },
  'an.trendTitle': { zh: '近 30 天变化', en: 'Last 30 days', fr: '30 derniers jours' },
  'an.trendSub': { zh: '按首末两次评分对比,负数代表退步', en: 'First vs last score; negative means regression', fr: 'Premier vs dernier score ; négatif = régression' },
  'an.trendEmpty': { zh: '趋势数据为空。', en: 'No trend data.', fr: 'Aucune donnée de tendance.' },
  'an.trendUnavailable': { zh: '趋势接口暂不可用,跳过这一段。', en: 'Trend API unavailable — skipping this section.', fr: 'API de tendance indisponible — section ignorée.' },
  'auth.tagline': { zh: '求职与面试全流程平台', en: 'End-to-end job search and interview platform', fr: 'Plateforme complète de recherche d’emploi et d’entretiens' },
  'auth.fieldEmail': { zh: '邮箱', en: 'Email', fr: 'E-mail' },
  'auth.fieldPassword': { zh: '密码', en: 'Password', fr: 'Mot de passe' },
  'auth.signIn': { zh: '登录', en: 'Sign in', fr: 'Connexion' },
  'auth.signingIn': { zh: '登录中…', en: 'Signing in…', fr: 'Connexion…' },
  'auth.or': { zh: '或', en: 'or', fr: 'ou' },
  'auth.googleSignIn': { zh: '使用 Google 登录', en: 'Sign in with Google', fr: 'Se connecter avec Google' },
  'auth.googleEstablishing': { zh: '正在建立会话…', en: 'Establishing session…', fr: 'Établissement de la session…' },
  'auth.foot': { zh: '个人使用 · 数据仅存本地', en: 'Personal use · data stays local', fr: 'Usage personnel · données locales uniquement' },
  'auth.welcomeBack': { zh: '欢迎回来', en: 'Welcome back', fr: 'Bon retour' },
  'auth.signInHint': { zh: '登录以继续你的求职之旅', en: 'Sign in to continue your job search', fr: 'Connectez-vous pour continuer' },
  'auth.togglePassword': { zh: '显示/隐藏密码', en: 'Show/hide password', fr: 'Afficher/masquer le mot de passe' },
  'auth.humanHint': { zh: '请先完成上方的人机验证', en: 'Please complete the verification above first', fr: 'Veuillez d’abord effectuer la vérification ci-dessus' },
  'auth.errServerUnreachable': { zh: '连接不到服务器,请检查网络后重试。如果后端没有运行,请先启动后端服务。', en: 'Cannot reach the server. Check your connection and try again. If the backend is not running, start it first.', fr: 'Impossible de joindre le serveur. Vérifiez votre connexion et réessayez.' },
  'auth.humanCheckLabel': { zh: '我不是机器人', en: "I'm not a robot", fr: 'Je ne suis pas un robot' },
  'auth.humanCheckRetry': { zh: '验证失败,请重试', en: 'Verification failed, try again', fr: 'Échec de la vérification, réessayez' },
  'auth.humanCheckBrand': { zh: '人机验证', en: 'Human check', fr: 'Vérification humaine' },
  'auth.hl1Title': { zh: '投递跟踪', en: 'Application tracking', fr: 'Suivi des candidatures' },
  'auth.hl1Desc': { zh: '所有投递一处管理,状态流转一目了然', en: 'All your applications in one place, with clear status flow', fr: 'Toutes vos candidatures au même endroit' },
  'auth.hl2Title': { zh: '面试实战', en: 'Interview playbook', fr: 'Journal d’entretiens' },
  'auth.hl2Desc': { zh: '记录每一轮面试,沉淀问答与复盘', en: 'Log every round, build your Q&A and review library', fr: 'Consignez chaque entretien et vos questions-réponses' },
  'auth.hl3Title': { zh: 'AI 模拟面试', en: 'AI mock interviews', fr: 'Simulations d’entretien IA' },
  'auth.hl3Desc': { zh: '语音分析六维诊断,针对性提升表达', en: 'Six-dimension speech analysis to sharpen delivery', fr: 'Analyse vocale en six dimensions' },
  'mock.title': { zh: 'AI 模拟面试', en: 'AI Mock Interview', fr: 'Simulation d’entretien IA' },
  'mock.subtitle': { zh: '一题一答一评分。练完即时看到六维分数和具体问题,不用等真人反馈。', en: 'One question, one answer, one score. See your six-dimension scores and issues instantly.', fr: 'Une question, une réponse, une note. Voyez vos scores et problèmes aussitôt.' },
  'mock.continuePracticing': { zh: '继续练习', en: 'Continue practicing', fr: 'Continuer à pratiquer' },
  'mock.questionUnit': { zh: '题', en: 'questions', fr: 'questions' },
  'mock.continueAnswering': { zh: '继续答题', en: 'Continue answering', fr: 'Continuer à répondre' },
  'mock.historyPractices': { zh: '历史练习', en: 'Practice history', fr: 'Historique' },
  'mock.noRecords': { zh: '还没有练习记录', en: 'No practice records yet', fr: 'Aucune séance pour le moment' },
  'mock.noRecordsHint': { zh: '开一场主题专项,10 分钟就能拿到一轮针对性的反馈。', en: 'Start a topic drill — get targeted feedback in 10 minutes.', fr: 'Lancez un exercice ciblé — un retour en 10 minutes.' },
  'mock.startFirst': { zh: '开始第一场', en: 'Start your first', fr: 'Commencer la première' },
  'mock.statTotalScore': { zh: '总分', en: 'Total score', fr: 'Note totale' },
  'mock.statAnswered': { zh: '已答', en: 'Answered', fr: 'Répondues' },
  'mock.statScored': { zh: '已评', en: 'Scored', fr: 'Notées' },
  'mock.statStarted': { zh: '开始', en: 'Started', fr: 'Début' },
  'mock.viewDetail': { zh: '查看详情', en: 'View details', fr: 'Voir le détail' },
  'mock.dialogSub': { zh: '选定模式后,进入页面即可让 AI 出题', en: 'Pick a mode, then let AI generate questions on the page', fr: 'Choisissez un mode, puis laissez l’IA générer les questions' },
  'mock.fieldTitle': { zh: '标题', en: 'Title', fr: 'Titre' },
  'mock.titlePlaceholder': { zh: '如:EF Core 查询调优专项', en: 'e.g. EF Core query tuning drill', fr: 'p. ex. Exercice optimisation EF Core' },
  'mock.fieldMode': { zh: '模式', en: 'Mode', fr: 'Mode' },
  'mock.fieldTopic': { zh: '主题', en: 'Topic', fr: 'Sujet' },
  'mock.fieldTopicCompany': { zh: '目标公司', en: 'Target company', fr: 'Entreprise cible' },
  'mock.topicPlaceholder': { zh: '如:EF Core 查询调优', en: 'e.g. EF Core query tuning', fr: 'p. ex. Optimisation EF Core' },
  'mock.topicCompanyPlaceholder': { zh: '如:Geotab 风格的面试', en: 'e.g. Geotab-style interview', fr: 'p. ex. Entretien style Geotab' },
  'mock.fieldDifficulty': { zh: '难度 (1-5)', en: 'Difficulty (1-5)', fr: 'Difficulté (1-5)' },
  'mock.createAndStart': { zh: '创建并开始', en: 'Create and start', fr: 'Créer et commencer' },
  'mock.creating': { zh: '创建中…', en: 'Creating…', fr: 'Création…' },
  'mock.backToList': { zh: '返回模拟列表', en: 'Back to mock list', fr: 'Retour à la liste' },
  'mock.generatingSummary': { zh: '生成总评中…', en: 'Generating summary…', fr: 'Génération du bilan…' },
  'mock.finishAndSummarize': { zh: '结束并总结', en: 'Finish and summarize', fr: 'Terminer et résumer' },
  'mock.roundSummary': { zh: '本轮总评', en: 'Round summary', fr: 'Bilan du tour' },
  'mock.scoredCount': { zh: '共评分 {n} 题', en: '{n} questions scored', fr: '{n} questions notées' },
  'mock.weakestNote': { zh: '最弱环节:{n} —— 下次练习优先针对它。', en: 'Weakest area: {n} — target it next time.', fr: 'Point faible : {n} — ciblez-le la prochaine fois.' },
  'mock.timesCount': { zh: '{n} 次', en: '{n}×', fr: '{n}×' },
  'mock.avgSeverity': { zh: '均严重度 {n}', en: 'avg severity {n}', fr: 'gravité moy. {n}' },
  'mock.noQuestionsHint': { zh: '这个会话还没有题目。后端 AI 出题由分析服务生成 —— 回到列表页新建一场,或稍后刷新试试。', en: 'No questions yet. AI questions are generated by the analysis service — create a new session from the list or refresh later.', fr: 'Aucune question. L’IA les génère via le service d’analyse — créez une séance ou actualisez.' },
  'mock.questionSeq': { zh: '第 {n} 题', en: 'Question {n}', fr: 'Question {n}' },
  'mock.difficultyLabel': { zh: '难度 {n}/5', en: 'Difficulty {n}/5', fr: 'Difficulté {n}/5' },
  'mock.expectedPoints': { zh: '这题想听到的要点(点击展开)', en: 'What this question is looking for (click to expand)', fr: 'Ce que la question attend (cliquer pour développer)' },
  'mock.myAnswer': { zh: '我的回答', en: 'My answer', fr: 'Ma réponse' },
  'mock.answerPlaceholder': { zh: '按 STAR / 结论先行的结构作答……', en: 'Answer with STAR / conclusion-first structure…', fr: 'Répondez en STAR / conclusion d’abord…' },
  'mock.answerHint': { zh: '建议:先给结论,再给依据,最后说权衡。', en: 'Tip: conclusion first, then evidence, then trade-offs.', fr: 'Conseil : conclusion, preuves, puis arbitrages.' },
  'mock.submitAnswer': { zh: '提交回答', en: 'Submit answer', fr: 'Envoyer la réponse' },
  'mock.submitting': { zh: '提交中…', en: 'Submitting…', fr: 'Envoi…' },
  'mock.nextQuestion': { zh: '下一题', en: 'Next question', fr: 'Question suivante' },
  'mock.noAnswerRecorded': { zh: '(没有记录回答文本)', en: '(no answer text recorded)', fr: '(aucune réponse enregistrée)' },
  'mock.questionFeedback': { zh: '本题反馈', en: 'Feedback', fr: 'Retour' },
  'mock.overallPoints': { zh: '综合 {n} 分', en: 'Overall {n} pts', fr: 'Global {n} pts' },
  'mock.commentLabel': { zh: '点评', en: 'Comment', fr: 'Commentaire' },
  'mock.topIssue': { zh: '最大问题:{n}', en: 'Top issue: {n}', fr: 'Problème majeur : {n}' },
  'mock.issuesDetail': { zh: '具体问题', en: 'Issues in detail', fr: 'Problèmes détaillés' },
  'mock.severityLabel': { zh: '严重度 {n}', en: 'Severity {n}', fr: 'Gravité {n}' },
  'mock.recommendedAnswer': { zh: '推荐答案', en: 'Recommended answer', fr: 'Réponse recommandée' },
  'mock.betterStructure': { zh: '更好的结构', en: 'Better structure', fr: 'Meilleure structure' },
  'shell.terms': { zh: '条款', en: 'Terms', fr: 'Conditions' },
  'shell.privacy': { zh: '隐私', en: 'Privacy', fr: 'Confidentialité' },
  'shell.copyright': { zh: '版权', en: 'Copyright', fr: 'Droits d’auteur' },
  'shell.legalNotice': { zh: '您的购买受加拿大适用的消费者保护法律保护；您的订单将根据安大略省的法律进行处理。', en: 'Your purchase is protected by applicable Canadian consumer protection law; your order is processed under Ontario law.', fr: 'Votre achat est protégé par le droit canadien de la consommation ; votre commande est traitée selon le droit ontarien.' },
  'shell.copyLine': { zh: '© 2026 Your Interview · All Rights Reserved', en: '© 2026 Your Interview · All Rights Reserved', fr: '© 2026 Your Interview · Tous droits réservés' },
};

@Injectable({ providedIn: 'root' })
export class I18nService {
  private readonly _lang = signal<Lang>(this.readInitial());

  readonly lang = this._lang.asReadonly();
  readonly options = LANG_OPTIONS;
  readonly isZh = computed(() => this._lang() === 'zh');

  constructor() {
    // 语言变化时同步 <html lang>,并持久化
    effect(() => {
      const l = this._lang();
      try {
        localStorage.setItem(STORAGE_KEY, l);
      } catch {
        /* 隐私模式下 localStorage 可能不可写,忽略 */
      }
      if (typeof document !== 'undefined') {
        document.documentElement.lang = l === 'zh' ? 'zh-CN' : l;
      }
    });
  }

  setLang(l: Lang): void {
    this._lang.set(l);
  }

  /** 取当前语言的文案;缺词条时回退中文,再回退 key 本身。 */
  t(key: string): string {
    const entry = DICT[key];
    if (!entry) return key;
    return entry[this._lang()] ?? entry.zh ?? key;
  }

  /**
   * 带一个占位符的文案:'{n}' 会被替换成传入的值。
   * 模板里不能直接用 String()(Angular 模板表达式不暴露全局对象),
   * 所以计数类文案统一走这个方法。
   */
  tn(key: string, n: string | number | null | undefined): string {
    return this.t(key).replace('{n}', String(n ?? ''));
  }

  /**
   * 多占位符版本:'{a}' '{b}' … 会被 params 里同名的值替换。
   * 模板里用 tf('tracker.pager', { p: 1, tp: 5, t: 42 })。
   */
  tf(key: string, params: Record<string, string | number | null | undefined>): string {
    let s = this.t(key);
    for (const [k, v] of Object.entries(params)) s = s.replace(`{${k}}`, String(v ?? ''));
    return s;
  }

  /** 用于数字/日期本地化。 */
  locale(): string {
    switch (this._lang()) {
      case 'zh': return 'zh-CN';
      case 'fr': return 'fr-CA';
      default: return 'en-CA';
    }
  }

  private readInitial(): Lang {
    try {
      const saved = localStorage.getItem(STORAGE_KEY);
      if (saved === 'zh' || saved === 'en' || saved === 'fr') return saved;
    } catch {
      /* ignore */
    }
    return 'en';
  }
}
