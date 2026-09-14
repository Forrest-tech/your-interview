# Your Interview — 本地运行指南(Mac,全容器版)

给 Forrest 的机器:MacBook Pro 13" Mid 2014 / macOS Big Sur。

整套后端(数据库 + 消息队列 + 7 个微服务 + 网关)全部跑在 Docker 里,
一条命令起全套。前端在宿主机跑(改代码热重载更顺手)。

## 一、需要装什么

1. Docker Desktop for Mac
   https://www.docker.com/products/docker-desktop
   装完打开,确认状态栏图标是运行中。验证:`docker info` 能返回信息。

2. Node.js 18 或 20(只为跑前端)
   `brew install node@20`
   验证:`node -v`

就这么两个。不需要装 .NET SDK,不需要装 PostgreSQL 或 RabbitMQ——全在容器里。

注意:Docker Desktop 在 2014 款双核 i5 上启动会慢,耐心等一两分钟。
首次构建镜像会更久(要下 SDK 镜像 + 还原 NuGet 包 + 构建 8 个项目),
大概 10-20 分钟,取决于网速。之后就快了。

## 二、把代码拿到本地

```bash
cd ~
git clone <仓库地址> your-interview
cd your-interview
```

## 三、配置本地密钥(必经步骤)

仓库里不含任何真实密钥(密钥都在 `.env` 和你本地的 `appsettings.Development.json` 里,
已被 gitignore,不会提交)。所以 clone 之后第一件事是配自己的密钥:

```bash
bash tools/setup-secrets.sh
```

这个脚本会在仓库根创建 `.env`,并在 `src/Analysis.Worker/` 创建
`appsettings.Development.json`(都是从 `.example` 模板复制的)。
然后编辑这两个文件,把 `REPLACE_WITH_*` 换成真值:

`.env`(Docker 路线用,compose 会自动读取):

```
AZURE_SPEECH_KEY=<Azure 门户 → Speech 资源 → 密钥1>
AZURE_SPEECH_REGION=canadacentral
SERVICE_ACCOUNT_EMAIL=service.analysis@your-interview.local
SERVICE_ACCOUNT_PASSWORD=<自定义一个强密码>
JWT_SIGNING_KEY=<openssl rand -base64 48 生成>
ADMIN_PASSWORD=<自定义管理员密码>
```

`src/Analysis.Worker/appsettings.Development.json`(不开 Docker、直接 dotnet run 时用):

```
AzureSpeech.Key            = 同上 Azure key
ServiceAccount.Password    = 同上服务账号密码
Storage.RootDirectory      = 本机仓库绝对路径
```

两个文件里的 Azure key 与服务账号密码要保持一致(一个是容器用,一个是本机用)。

检查有没有漏填:

```bash
grep -rn REPLACE_WITH .env src/Analysis.Worker/appsettings.Development.json
```

输出为空就说明配好了。

## 四、起后端(Docker 路线,推荐)

```bash
cd ~/your-interview
docker compose up -d --build
```

这条命令会构建 9 个镜像并启动全部容器(数据库 + 消息队列 + 7 微服务 + 网关
+ 分析 Worker)。等它返回后看状态:

```bash
docker compose ps
```

期望 10 个容器全 running / healthy。首次启动时各服务会自动建表播种,
头一两分钟日志有重连属正常。

验证后端通了:

```bash
curl http://localhost:5200/api/gateway/info
```

## 五、起前端

另开一个终端:

```bash
cd ~/your-interview/web
npm install     # 首次需要
npm start
```

浏览器打开 http://localhost:4200

登录用 `.env` 里 `ADMIN_PASSWORD` 配合 `admin@your-interview.local`。

## 六、常用命令

```bash
docker compose up -d --build     # 起(改了代码要重新 build)
docker compose ps                # 看状态
docker compose logs -f gateway   # 实时看某个服务日志
docker compose logs -f identity  # 换服务名即可
docker compose restart jobs      # 重启单个服务
docker compose down              # 停全部(数据保留)
docker compose down -v           # 停并清空数据(彻底重来)
bash tools/setup-secrets.sh      # 生成/重建本地密钥文件
```

改后端代码后要重新构建对应服务:

```bash
docker compose up -d --build jobs
```

## 七、出问题怎么办

先看日志,九成问题都在日志里:

```bash
docker compose logs --tail 100 <服务名>
```

容器起不来 / unhealthy —— 看是不是数据库还没好。`docker compose ps` 里
postgres 和 rabbitmq 必须是 healthy,其他服务才等得到。

端口被占 —— 5200 / 5433 / 5672 / 15672 有一个被别的程序占了。

`lsof -i :5200` 查是谁,关掉它或者改 docker-compose.yml 里的映射端口。

数据乱了想重来 —— `docker compose down -v` 然后重新 up(会清空数据)。

构建时内存不够 / 卡死 —— 这台机器 16GB,Docker Desktop 默认可能只给 2GB。
打开 Docker Desktop → Settings → Resources,把 Memory 调到 6-8GB。
这是全容器方案在 2014 机器上最可能踩的坑。

想省资源,可以只起需要的部分:

```bash
docker compose up -d postgres rabbitmq identity jobs gateway
```

## 八、方案说明

全容器:数据库、消息队列、微服务都在 Docker 里,环境完全一致,
你 Mac 上不需要装 .NET、PG、RabbitMQ 任何东西。

前端不在容器里。开发时前端热重载很重要,放宿主机跑体验最好,
前端通过 localhost:5200 访问容器里的网关(web/proxy.conf.json 已配好)。

服务之间走 Docker 内部网络(用服务名当主机名),不经过宿主机端口;
只有网关把 5200 暴露出来给前端用,5433 / 5672 / 15672 也暴露了方便你用
本机客户端连进去排查。
