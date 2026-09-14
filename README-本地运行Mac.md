# Your Interview — 本地运行指南(Mac)

给 Forrest 的机器:MacBook Pro 13" Mid 2014 / macOS Big Sur。

## 一、需要装什么

1. .NET 8 SDK
   https://dotnet.microsoft.com/download/dotnet/8.0
   装完验证:`dotnet --version` 应显示 8.x

2. Node.js 18 或 20(LTS)
   `brew install node@20`
   验证:`node -v`

3. Docker Desktop for Mac
   https://www.docker.com/products/docker-desktop
   装完打开,确认状态栏图标是运行中。验证:`docker info` 能返回信息。

注意:Docker Desktop 在 2014 款双核 i5 上启动会慢,耐心等一两分钟。

## 二、把代码拿到本地

```bash
cd ~
git clone <仓库地址> your-interview
cd your-interview
```

(仓库还没推 GitHub,见文末"待办"。)

## 三、起后端

```bash
cd ~/your-interview
bash tools/dev.macos.sh up
```

这一步会:用 Docker 起 PostgreSQL(5433)和 RabbitMQ(5672 / 15672),然后依次构建并启动 7 个微服务 + 网关。首次会慢(dotnet 要还原包 + 构建),大概 3-5 分钟。

看到下面这样就是好了:

```
  ✓ PostgreSQL :5433
  ✓ RabbitMQ :5672 / :15672
  ✓ identity 就绪 :5262
  ...
  ✓ gateway 就绪 :5200
▸ 全部就绪
```

## 四、起前端

另开一个终端:

```bash
cd ~/your-interview/web
npm install     # 首次需要
npm start
```

然后浏览器打开 http://localhost:4200

登录:admin@your-interview.local / Admin!Passw0rd2026

## 五、常用命令

```bash
bash tools/dev.macos.sh up             # 起全部
bash tools/dev.macos.sh status         # 看状态
bash tools/dev.macos.sh down           # 停服务(基础设施容器保留)
bash tools/dev.macos.sh down-all       # 全停,含 Docker 容器
bash tools/dev.macos.sh restart jobs   # 重启某个服务
bash tools/dev.macos.sh logs gateway   # 看日志
bash tools/dev.macos.sh urls           # 打印所有地址
```

## 六、出问题怎么办

服务起不来,先看日志:

```bash
bash tools/dev.macos.sh logs <服务名>
```

常见情况:

端口被占 —— 5433 或 5672 被别的程序占了,`lsof -i :5433` 查是谁。

Docker 没运行 —— 报 "Docker 没在运行 —— 请先启动 Docker Desktop",打开 Docker Desktop 再重试。

数据库连不上 —— 确认 `docker compose ps` 里 postgres 是 healthy。数据不对想重来:`docker compose down -v` 然后重新 up(会清空数据)。

构建内存不够 —— 这台机器 16GB,同时跑 7 个 dotnet 进程 + Docker + Angular 会比较紧。如果某个服务构建失败,先 `bash tools/dev.macos.sh down`,单独构建它,再 up。

## 七、为什么是这套方案

基础设施用 Docker(不用在 Mac 上折腾装 PG 和 RabbitMQ,一条命令搞定),微服务用宿主机 dotnet run(改代码即时生效,断点调试正常,也不用把 7 个服务塞容器里吃内存)。

原来容器里用的 embedded PostgreSQL 二进制和手工解包的 RabbitMQ 都是 Linux 专用,在 macOS 上跑不了,所以这个 Mac 版把这两块换成了 Docker 容器。
