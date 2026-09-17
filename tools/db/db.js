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

/**
 * 按**当前运行平台**挑对应的 embedded-postgres 二进制包。
 *
 * ⚠️ 历史坑(2026-09-15):这里原先写死 linux-x64 两条候选路径,
 * 结果 Mac 上永远找不到 pg_ctl,`db.js start` 直接失败 →
 * 上层只显示"PG 启动异常",排查困难。embedded-postgres 的二进制是
 * **按平台分包**的可选依赖(darwin-x64 / darwin-arm64 / linux-x64 / ...),
 * npm 只会装当前平台那个,所以必须动态拼包名。
 *
 * 优先级:本仓库 tools/db/node_modules > ~/pg/node_modules(历史安装位置)。
 */
function getPgBin() {
  const platform = process.platform;                      // darwin | linux | win32
  const arch = process.arch;                              // x64 | arm64 | arm | ia32 | ppc64
  const pkg = `${platform}-${arch}`;

  const bases = [
    path.join(ROOT, 'node_modules', '@embedded-postgres'),
    path.join(os.homedir(), 'pg', 'node_modules', '@embedded-postgres')
  ];

  const candidates = [];
  for (const base of bases) {
    candidates.push(path.join(base, pkg, 'native', 'bin'));
  }
  for (const c of candidates) if (fs.existsSync(path.join(c, 'pg_ctl'))) return c;

  // 全都没命中 → 给出可执行的修复指引,而不是让上层只看到"启动异常"
  const tried = candidates.map(c => '    ' + c).join('\n');
  console.error('[db] ✗ 找不到 PostgreSQL 二进制(pg_ctl)。');
  console.error('[db]   当前平台需要 npm 包:' + ' @embedded-postgres/' + pkg);
  console.error('[db]   已尝试的路径:');
  console.error(tried);
  console.error('[db]   修复:cd ' + ROOT + ' && npm install');
  process.exit(1);
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

/**
 * 用 embedded-postgres 自带的 psql 执行一段 SQL。
 *
 * 为什么需要它:开发机上通常**没装全局 psql**,但 embedded-postgres 包里
 * 就带着一个完整可用的 psql 二进制。以前这里只实现 start/stop/status,
 * 用户想手动查表/清表就得自己找二进制,体验断档。
 *
 * 用法:
 *   node db.js psql "select 1"                # 执行 SQL
 *   node db.js psql -f some.sql               # 执行 SQL 文件
 *   node db.js psql                            # 进交互式 psql
 */
/**
 * 执行任意 SQL —— 用 node 的 pg 驱动直连,不依赖 psql 可执行文件。
 *
 * ⚠️ 为什么不用 psql:
 *   embedded-postgres 包**只带 initdb / pg_ctl / postgres 三个二进制**,
 *   没有 psql。之前这里按"包内有 psql"实现,在真机上直接
 *   "找不到 psql: .../native/bin/psql" 报错 —— 属于假设未实测。
 *   项目本来就依赖 `pg` 驱动(createDatabases 也在用),直接复用它最稳。
 *
 * 目标库可用环境变量覆盖 —— 因为 .env 的连接串可能指向
 * **宿主机自带的 PostgreSQL**(如 127.0.0.1:5432),而不是本 embedded 实例
 * (127.0.0.1:5433)。两边是不同的库,打错地方会"看起来成功"却毫无效果。
 *   PGPSQL_HOST / PGPSQL_PORT / PGPSQL_DB / PGPSQL_USER / PGPSQL_PASSWORD
 *
 * 用法:
 *   node db.js psql "select 1"              # 执行 SQL
 *   node db.js psql -c "select 1"           # 同上(-c 可省略)
 *   node db.js psql -f some.sql             # 执行 SQL 文件
 */
/**
 * 从仓库根的 .env 里读某个 ConnectionStrings__* 的连接串并解析成对象。
 *
 * 目的:让手动查库的**目标**和服务实际使用的**目标**永远一致。
 * 之前吃过大亏:.env 指向宿主机 PG(5432),而 db.js 默认连 embedded(5433),
 * 清表打错库还"看起来成功"。这里直接复用 .env,消除这个不一致。
 *
 * 解析形如:Host=127.0.0.1;Port=5432;Database=x;Username=y;Password=z;Path=assessment
 */
function readEnvConnString(key) {
  try {
    const envPath = path.join(ROOT, '..', '..', '.env');   // tools/db → 仓库根
    if (!fs.existsSync(envPath)) return null;
    const txt = fs.readFileSync(envPath, 'utf8');
    const m = txt.match(new RegExp('^' + key + '\\s*=\\s*"?([^"\\n]*)"?', 'm'));
    if (!m) return null;
    const kv = {};
    for (const part of m[1].split(';')) {
      const i = part.indexOf('=');
      if (i > 0) kv[part.slice(0, i).trim().toLowerCase()] = part.slice(i + 1).trim();
    }
    return kv;
  } catch { return null; }
}

function psql() {
  // 优先级:显式环境变量 > .env 里的 AssessmentDb 连接串 > embedded 默认
  const fromEnv = readEnvConnString('ConnectionStrings__AssessmentDb');
  const host = process.env.PGPSQL_HOST || (fromEnv && fromEnv.host) || '127.0.0.1';
  const port = Number(process.env.PGPSQL_PORT || (fromEnv && fromEnv.port) || PORT);
  const database = process.env.PGPSQL_DB || (fromEnv && fromEnv.database) || 'yourinterview';
  const user = process.env.PGPSQL_USER || (fromEnv && fromEnv.username) || 'postgres';
  const password = process.env.PGPSQL_PASSWORD || (fromEnv && fromEnv.password) || '';

  // 找 pg 驱动(与 createDatabases 同一套查找逻辑)
  const pgPaths = [
    path.join(ROOT, 'node_modules', 'pg'),
    path.join(os.homedir(), 'pg', 'node_modules', 'pg')
  ];
  const pgPath = pgPaths.find(p => fs.existsSync(p)) || pgPaths[0];

  const rest = process.argv.slice(3);
  let sql = '';
  const fileIdx = rest.indexOf('-f');
  if (fileIdx >= 0 && rest[fileIdx + 1]) {
    sql = fs.readFileSync(rest[fileIdx + 1], 'utf8');
  } else {
    // 去掉可选的 -c,剩下的拼成 SQL(支持带空格的语句)
    const parts = rest.filter(a => a !== '-c');
    sql = parts.join(' ');
  }
  if (!sql.trim()) {
    console.error('[db] 用法: node db.js psql "SQL" | -c "SQL" | -f file.sql');
    process.exit(2);
  }

  const { Client } = require(pgPath);
  const cfg = { host, port, user, database };
  if (password) cfg.password = password;

  (async () => {
    const c = new Client(cfg);
    try {
      await c.connect();
      const r = await c.query(sql);
      if (process.env.DB_SHOW_TARGET === '1') {
        console.error('[db] 目标: ' + user + '@' + host + ':' + port + '/' + database);
      }
      if (r.rows && r.rows.length) {
        // 简单表格输出
        const cols = Object.keys(r.rows[0]);
        console.log(cols.join(' | '));
        console.log(cols.map(() => '---').join(' | '));
        for (const row of r.rows) {
          console.log(cols.map(k => row[k] === null ? 'NULL' : String(row[k])).join(' | '));
        }
        console.log('(' + r.rows.length + ' row' + (r.rows.length === 1 ? '' : 's') + ')');
      } else {
        console.log('[db] OK — ' + (typeof r.rowCount === 'number' ? r.rowCount + ' row(s) affected' : 'done'));
      }
    } catch (e) {
      console.error('[db] SQL 失败: ' + e.message);
      console.error('[db]   目标: ' + user + '@' + host + ':' + port + '/' + database);
      process.exitCode = 1;
    } finally {
      await c.end().catch(() => {});
    }
  })();
}

const cmd = process.argv[2] || 'status';
({ start, stop, status, reset, 'create-dbs': createDatabases, psql }[cmd] || status)();
