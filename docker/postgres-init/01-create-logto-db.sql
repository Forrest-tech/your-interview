-- 票241:Logto 自有库。只在 postgres 数据卷首次初始化时执行;
-- 已有 pgdata 的旧卷不会重跑,需手动补一次: CREATE DATABASE logto;
-- (或删卷重建,仅开发环境)。
SELECT 'CREATE DATABASE logto'
WHERE NOT EXISTS (SELECT FROM pg_database WHERE datname = 'logto')\gexec
