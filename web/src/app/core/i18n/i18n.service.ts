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

  // ---------- AI 面试练习 ----------
  'nav.practice': { zh: 'AI 面试练习', en: 'AI Practice', fr: 'Pratique IA' },
  'practice.materials': { zh: '面试素材', en: 'Materials', fr: 'Documents' },
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
  'practice.noRecordings': { zh: '还没有录音。点上方「开始录音」录一遍,录音会保存在这里。', en: 'No recordings yet. Hit Record above to capture a take.', fr: 'Aucun enregistrement. Cliquez sur Enregistrer.' },
  'practice.wordDetail': { zh: '逐词明细', en: 'Word-by-word detail', fr: 'Détail par mot' },
  'practice.question': { zh: '题目', en: 'Question', fr: 'Question' },
  // 第二十二轮:顶部路径改用父文件夹名;顶层文件无文件夹时用此兜底
  'practice.uncategorized': { zh: '未分类', en: 'Uncategorized', fr: 'Non classé' },
  'practice.completed': { zh: '已完成', en: 'Completed', fr: 'Terminé' },
  'practice.expandTree': { zh: '展开素材', en: 'Show materials', fr: 'Afficher les supports' },
  'practice.collapseTree': { zh: '收起素材', en: 'Hide materials', fr: 'Masquer les supports' },
  'practice.prev': { zh: '上一题', en: 'Previous', fr: 'Précédent' },
  'practice.next': { zh: '下一题', en: 'Next', fr: 'Suivant' },
  'practice.submitScoring': { zh: '提交 AI 评分', en: 'Submit AI Scoring', fr: 'Lancer le score IA' },
  'practice.retryTip': { zh: '删掉这条录音并立刻重新录一遍', en: 'Delete this take and record again', fr: 'Supprimer et réenregistrer' },
  'practice.retryPickFirst': { zh: '先在上方录音列表点选一条录音', en: 'Select a recording in the list first', fr: 'Sélectionnez un enregistrement' },
  'practice.dragResize': { zh: '拖动调整宽度', en: 'Drag to resize', fr: 'Glisser pour redimensionner' },
  'practice.retry': { zh: '重新作答', en: 'Retry', fr: 'Recommencer' },
  // 2026-09-16(Forrest 本轮):Retry 未选中录音时当"刷新列表"用。
  'practice.retryRefreshed': { zh: '已刷新录音列表', en: 'Recording list refreshed', fr: 'Liste actualisée' },
  // 2026-09-16(Forrest 本轮):录音结束后出现的"提交"按钮。
  'practice.submitTake': { zh: '提交', en: 'Submit', fr: 'Soumettre' },
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
  'practice.recHistory': { zh: '历史录音', en: 'Recordings', fr: 'Enregistrements' },
  'practice.viewReport': { zh: '查看报告', en: 'View Report', fr: 'Voir le rapport' },
  'practice.points': { zh: '分', en: 'pts', fr: 'pts' },
  'practice.noPhonetic': { zh: '暂无音标', en: 'No phonetic data', fr: 'Pas de phonétique' },
  'practice.saveChanges': { zh: '保存修改', en: 'Save Changes', fr: 'Enregistrer' },
  'practice.saved': { zh: '已保存', en: 'Saved', fr: 'Enregistré' },
  'practice.unsavedHint': { zh: '有未保存的修改', en: 'You have unsaved changes', fr: 'Modifications non enregistrées' },
  'practice.savedHint': { zh: '素材已保存到本地', en: 'Materials saved locally', fr: 'Supports enregistrés localement' },
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
  'practice.reportEmpty': { zh: '还没有评测结果。录一段朗读并点击「AI 评分」后,这里会显示发音准确度、流利度、逐词对比与错误统计。', en: 'No assessment yet. Record a take and click "AI Scoring" to see pronunciation accuracy, fluency, word-by-word diff and error stats here.', fr: 'Aucune évaluation. Enregistrez puis lancez l evaluation.' },

  // ---------- 2026-09-16 第十九轮:骨架重构新增 ----------
  'practice.mark': { zh: '标记', en: 'Mark', fr: 'Marquer' },
  'practice.markWord': { zh: '标记当前词', en: 'Mark this word', fr: 'Marquer ce mot' },
  'practice.markClear': { zh: '清除标记', en: 'Clear mark', fr: 'Effacer la marque' },
  'practice.jumpTo': { zh: '跳转', en: 'Go', fr: 'Aller' },
  'practice.jumpLabel': { zh: '跳转到第几题', en: 'Jump to question number', fr: 'Aller à la question' },
  'practice.jumpInvalid': { zh: '请输入有效的题号。', en: 'Enter a valid question number.', fr: 'Entrez un numéro valide.' },
  'practice.recordingHistoryCount': { zh: '录音记录', en: 'Recordings', fr: 'Enregistrements' },
  'practice.saveFailed': { zh: '保存失败,请检查浏览器存储权限', en: 'Save failed — check browser storage permissions', fr: 'Échec de l enregistrement' },
  'practice.sampleReading': { zh: '示范朗读', en: 'Sample Reading', fr: 'Lecture exemple' },
  'practice.runScoring': { zh: 'AI 评分', en: 'Run AI Scoring', fr: 'Lancer le score IA' },
  'practice.reset': { zh: '重置', en: 'Reset', fr: 'Réinitialiser' },
  'practice.resetHint': { zh: '重置后才能重新评分', en: 'Reset before scoring again', fr: 'Réinitialisez avant de rescorer' },
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
    return 'zh';
  }
}
