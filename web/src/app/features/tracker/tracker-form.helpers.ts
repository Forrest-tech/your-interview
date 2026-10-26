import { Application, ApplicationStatus } from '../../core/models/api.models';

export const STATUS_ORDER: ApplicationStatus[] = [
  'Saved', 'Applied', 'Screen', 'Interview', 'Offer', 'Accepted', 'Rejected', 'Ghosted', 'Paused', 'Withdrawn'
];

/**
 * 新建对话框允许直接设置的初始状态(只能是从 Saved 合法流转出去的那几个;
 * 其余状态(面试中/Offer 等)需要走正式的状态流转,不能在新建时越级设置)。
 */
export const CREATE_STATUS_OPTIONS: ApplicationStatus[] = ['Saved', 'Applied', 'Screen', 'Interview', 'Paused', 'Rejected'];

/** 从 Saved 走到目标状态的合法路径(后端状态机不允许越级)。 */
export const STATUS_PATH_FROM_SAVED: Record<string, ApplicationStatus[]> = {
  'Applied': ['Applied'],
  'Screen': ['Applied', 'Screen'],
  'Interview': ['Applied', 'Screen', 'Interview'],
  'Paused': ['Paused'],
  'Rejected': ['Rejected'],
};

/**
 * 投递表单的可编辑形状。
 * 与 Application 分开定义:新建时表单没有 id/createdAt 这类服务端字段,
 * 硬套 Application 会到处写 undefined,不如用一个贴合表单的接口。
 */
export interface ApplicationForm {
  companyName: string;
  role: string;
  location: string;
  salary: string;
  status: ApplicationStatus;
  priority: string;
  appliedDate: string;
  link: string;
  notes: string;
  needsConnectFirst: boolean;
  /** 对应后端 OutreachMessage(外联留言,如"已发 LinkedIn 私信")。 */
  outreachMessage: string;
  /** ★ #6:投递的文档版本 (如 "Forrest_Lin_Resume_Corpay.pdf")。存 notes 前缀。 */
  sentDocuments: string;
  /** 职位描述 JD,供关键词提取和备战计划使用。 */
  jdText: string;
}

export function emptyForm(): ApplicationForm {
  return {
    companyName: '', role: '', location: '', salary: '',
    status: 'Applied', priority: 'Medium', appliedDate: todayIso(), link: '',
    notes: '', needsConnectFirst: false, outreachMessage: '', sentDocuments: '',
    jdText: ''
  };
}

export function todayIso(): string {
  const d = new Date();
  const m = `${d.getMonth() + 1}`.padStart(2, '0');
  const day = `${d.getDate()}`.padStart(2, '0');
  return `${d.getFullYear()}-${m}-${day}`;
}

/** Application → 表单形状。缺省值就地补,让编辑弹窗不出现 undefined。 */
export function fromApplication(a: Application): ApplicationForm {
  const notes = a.notes ?? '';
  // ★ #6:从 notes 提取 [DOCS:...] 标记的文档版本
  const m = notes.match(/^\[DOCS:([^\]]+)\]\s*/);
  return {
    companyName: a.companyName ?? '',
    role: a.role ?? '',
    location: a.location ?? '',
    salary: a.salary ?? '',
    status: a.status ?? 'Applied',
    priority: a.priority ?? 'Medium',
    appliedDate: (a.appliedDate ?? '').slice(0, 10) || todayIso(),
    link: a.link ?? '',
    notes: m ? notes.slice(m[0].length) : notes,
    needsConnectFirst: a.needsConnectFirst ?? false,
    outreachMessage: a.outreachMessage ?? '',
    sentDocuments: m ? m[1] : '',
    jdText: a.jdText ?? ''
  };
}

export function trimForm(f: ApplicationForm): ApplicationForm {
  return {
    ...f,
    companyName: f.companyName.trim(),
    role: f.role.trim(),
    location: f.location.trim(),
    salary: f.salary.trim(),
    link: f.link.trim(),
    notes: f.notes.trim(),
    outreachMessage: f.outreachMessage.trim()
  };
}
