// 给已存在的角色补上新加的功能权限(权限点常量新增后,旧库需要补齐)。
// 用法: node tools/grant-new-perms.js
const { Client } = require('/home/node/pg/node_modules/pg');

(async () => {
  const c = new Client({
    host: '127.0.0.1', port: 5433, user: 'postgres',
    password: 'postgres', database: 'yourinterview',
  });
  await c.connect();

  const roles = await c.query('select "Id","Name" from identity.roles');
  const byName = {};
  roles.rows.forEach((r) => { byName[r.Name] = r.Id; });

  // 与 SharedContracts/Security/Permissions.cs 的 DefaultPermissions 保持一致
  const plan = {
    Admin: ['analytics.read', 'analytics.write'],
    PowerUser: ['analytics.read', 'analytics.write'],
    User: ['analytics.read'],
    Viewer: ['analytics.read'],
  };

  let added = 0;
  for (const [role, perms] of Object.entries(plan)) {
    if (!byName[role]) continue;
    for (const p of perms) {
      // 该表没有 (RoleId,Permission) 唯一约束,所以用"先查再插"保证幂等
      const exists = await c.query(
        'select 1 from identity.role_permissions where "RoleId"=$1 and "Permission"=$2 limit 1',
        [byName[role], p],
      );
      if (exists.rowCount > 0) continue;

      await c.query(
        'insert into identity.role_permissions ("RoleId","Permission") values ($1,$2)',
        [byName[role], p],
      );
      added++;
    }
  }

  const granted = await c.query(
    'select r."Name" rn, rp."Permission" p from identity.role_permissions rp ' +
    'join identity.roles r on r."Id"=rp."RoleId" where rp."Permission" like $1 order by 1',
    ['analytics%'],
  );
  console.log('新增 ' + added + ' 条权限绑定');
  console.log('analytics 权限现状: ' + granted.rows.map((x) => x.rn + ':' + x.p).join(', '));

  await c.end();
})().catch((e) => { console.error('ERR', e.message); process.exit(1); });
