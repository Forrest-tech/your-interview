#!/usr/bin/env node
/**
 * 票123 R-9 造数脚本（一次性，非产品代码）
 *
 * 目的:给管理员账号造出可被几何探针量到的活体数据 ——
 *   - 投递覆盖 Saved / Applied / Screen / Interview / Offer / Rejected,
 *     使 Tracker 列表 stepper 与 Dashboard band 上的节点同时出现
 *     reached / hollow / go / stop 四种状态。
 *   - 建一个带多轮次的 Playbook 条目,使 Playbook 详情的 Rounds Track
 *     能量到 .rnode .dot。
 *
 * 用法: node web/tools/seed-r9-fixtures.mjs
 */
import { randomUUID } from 'node:crypto';

const BASE = process.env.YI_API ?? 'http://localhost:5200';
const EMAIL = process.env.YI_EMAIL ?? 'admin@your-interview.local';
const PASSWORD = process.env.YI_PASSWORD ?? 'Admin@2026';

let token = '';

async function api(path, { method = 'GET', body } = {}) {
  const res = await fetch(`${BASE}${path}`, {
    method,
    headers: {
      'Content-Type': 'application/json',
      ...(token ? { Authorization: `Bearer ${token}` } : {}),
    },
    ...(body ? { body: JSON.stringify(body) } : {}),
  });
  const text = await res.text();
  let data;
  try { data = text ? JSON.parse(text) : null; } catch { data = text; }
  if (!res.ok) {
    throw new Error(`${method} ${path} -> ${res.status} ${text.slice(0, 400)}`);
  }
  return data;
}

async function main() {
  const login = await api('/api/auth/login', {
    method: 'POST',
    body: { email: EMAIL, password: PASSWORD },
  });
  token = login.tokens.accessToken;
  console.log('✔ 登录成功');

  // 1) 投递:每个状态至少一条(状态机允许的路径见 JobAggregates 状态流转表)
  const stages = [
    { status: 'Saved', company: 'R9 Fixture Saved', role: 'Saved Role' },
    { status: 'Applied', company: 'R9 Fixture Applied', role: 'Applied Role' },
    { status: 'Screen', company: 'R9 Fixture Screen', role: 'Screen Role' },
    { status: 'Interview', company: 'R9 Fixture Interview', role: 'Interview Role' },
    { status: 'Offer', company: 'R9 Fixture Offer', role: 'Offer Role' },
    { status: 'Rejected', company: 'R9 Fixture Rejected', role: 'Rejected Role' },
  ];

  const created = [];
  for (const s of stages) {
    const comp = await api('/api/jobs/companies', { method: 'POST', body: { name: s.company } });
    const companyId = comp.id ?? comp;
    const app = await api('/api/jobs/applications', {
      method: 'POST',
      body: {
        companyId,
        role: s.role,
        status: s.status,
        appliedDate: '2026-09-01',
        priority: 'Medium',
        location: 'Remote',
      },
    });
    created.push({ ...s, id: app.id ?? app });
    console.log(`  · ${s.status.padEnd(10)} -> ${app.id ?? app}`);
  }

  // 2) Playbook 条目 + 多轮次(过去的轮次 / 未来的轮次,让 reached 与 hollow 都出现)
  const entry = await api('/api/interviews', {
    method: 'POST',
    body: {
      companyName: 'R9 Fixture Interview',
      role: 'Interview Role',
      companyId: created.find(c => c.status === 'Interview').id,
      interviewFormat: 'Video',
      interviewDate: '2026-09-20',
      location: 'Remote',
      notes: 'R-9 几何探针用夹具',
    },
  });
  const entryId = entry.id ?? entry;
  console.log(`✔ Playbook 条目 ${entryId}`);

  const rounds = [
    { stage: 'Screen', scheduledDate: '2026-09-10', format: 'Video', outcome: 'Passed' },
    { stage: 'Technical', scheduledDate: '2026-09-22', format: 'Video', outcome: 'Pending' },
    { stage: 'SystemDesign', scheduledDate: '2026-09-29', format: 'Video', outcome: 'Pending' },
  ];
  for (const r of rounds) {
    const round = await api(`/api/interviews/${entryId}/rounds`, {
      method: 'POST',
      body: {
        stage: r.stage,
        scheduledDate: r.scheduledDate,
        format: r.format,
        location: 'Remote',
        scheduledTime: '10:00',
        interviewers: 'R9 Fixture Interviewer',
      },
    });
    console.log(`  · round ${r.stage} -> ${round.id ?? round}`);
  }

  // 3) 回读确认
  const apps = await api('/api/jobs/applications');
  const list = apps.items ?? apps.data ?? apps;
  console.log(`✔ 投递总数 ${Array.isArray(list) ? list.length : '?'}`);

  const entries = await api('/api/interviews?page=1&pageSize=50');
  const elist = entries.items ?? entries.data ?? entries;
  console.log(`✔ Playbook 条目数 ${Array.isArray(elist) ? elist.length : '?'}`);

  console.log('\nFIXTURE_ENTRY_ID=' + entryId);
}

main().catch((e) => { console.error('✘ 造数失败:', e.message); process.exit(1); });
