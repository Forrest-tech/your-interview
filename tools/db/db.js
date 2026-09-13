#!/usr/bin/env node
/**
 * 开发用数据库管理器(无 Docker 环境的替代方案)。
 * 用 embedded-postgres 自带的 PostgreSQL 17 二进制,起一个真实 PG 实例。
 *
 *   node db.js start|stop|status|psql|reset|create-dbs
 */
const { execFileSync, spawn } = require('child_process');
const fs = require('fs');
const path = require('path');
const os = require('os');

const ROOT = __dirname;
const DATA = path.join(ROOT, 'data');
const LOG = path.join(ROOT, 'postgres.log');
const PORT = process.env.PG_PORT || '5433';
const PGBIN = getPgBin();
const PG_CTL = path.join(PGBIN, 'pg_ctl');
const INITDB = path.join(PGBIN, 'initdb');

const DATABASES = [
  'yourinterview',        // 单库多 schema 方案:每服务一个 PG schema
];

/** 优先用本目录 node_modules,否则回退到 ~/pg 已有的安装 */
function getPgBin() {
  const candidates = [
    path.join(ROOT, 'node_modules', '@embedded-postgres', 'linux-x64', 'native', 'bin'),
    path.join(os.homedir(), 'pg', 'node_modules', '@embedded-postgres', 'linux-x64', 'native', 'bin')
  ];
  for (const c of candidates) if (fs.existsSync(path.join(c, 'pg_ctl'))) return c;
  return candidates[0];
}

function sh(cmd, args, opts) {
  return execFileSync(cmd, args, Object.assign({ stdio: 'inherit', maxBuffer: 64 * 1024 * 1024 }, opts || {}));
}

function capture(cmd, args) {
  try {
    return execFileSync(cmd, args, { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] }).trim();
  } catch (e) { return ''; }
}

function isRunning() {
  const out = capture(PG_CTL, ['-D', DATA, 'status']);
  return out.includes('server is running');
}

function initIfNeeded() {
  if (fs.existsSync(path.join(DATA, 'PG_VERSION'))) return;
  fs.mkdirSync(DATA, { recursive: true });
  console.log('[db] 首次初始化数据库集群 …');
  sh(INITDB, ['-D', DATA, '-U', 'postgres', '--auth=trust', '-E', 'UTF8']);
}

function start() {
  initIfNeeded();
  if (isRunning()) { console.log('[db] 已在运行 (port ' + PORT + ')'); return; }
  console.log('[db] 启动 PostgreSQL 17 …');
  sh(PG_CTL, ['-D', DATA, '-l', LOG, '-o',
    `-p ${PORT} -k /tmp -c listen_addresses=127.0.0.1 -c max_connections=200 -c shared_buffers=256MB -c work_mem=16MB -c maintenance_work_mem=128MB`, 'start']);
  console.log('[db] 已启动 → 127.0.0.1:' + PORT);
  createDatabases();
}

function stop() {
  if (!isRunning()) { console.log('[db] 未在运行'); return; }
  sh(PG_CTL, ['-D', DATA, 'stop', '-m', 'fast']);
  console.log('[db] 已停止');
}

function createDatabases() {
  const pgPaths = [
    path.join(ROOT, 'node_modules', 'pg'),
    path.join(os.homedir(), 'pg', 'node_modules', 'pg')
  ];
  const pgPath = pgPaths.find(p => fs.existsSync(p)) || pgPaths[0];
  const { Client } = require(pgPath);
  (async () => {
    const c = new Client({ host: '127.0.0.1', port: Number(PORT), user: 'postgres', database: 'postgres' });
    await c.connect();
    for (const db of DATABASES) {
      const r = await c.query('select 1 from pg_database where datname=$1', [db]);
      if (r.rowCount === 0) {
        await c.query(`create database "${db}"`);
        console.log('[db] 已创建数据库 ' + db);
      }
    }
    await c.end();
  })().catch(e => console.error('[db] 建库失败: ' + e.message));
}

function status() {
  console.log(isRunning() ? `[db] running on 127.0.0.1:${PORT}` : '[db] stopped');
}

function reset() {
  stop();
  fs.rmSync(DATA, { recursive: true, force: true });
  console.log('[db] 数据目录已清空');
  start();
}

const cmd = process.argv[2] || 'status';
({ start, stop, status, reset, 'create-dbs': createDatabases }[cmd] || status)();
