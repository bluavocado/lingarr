# NAS 部署与端到端测试流程:已译上下文 + `/content` 上下文能力

> 代码基准:分支 `feature/personalized`,代码 commit 见第 1 节
> 状态:**待执行**
> 对应设计:[`design_context_improvements.md`](./design_context_improvements.md);实现记录见该文档第 10 节

---

## 0. 目的、前提与注意事项

### 0.1 要验证什么

| # | 改动 | 观察面 |
| --- | --- | --- |
| 8.5 | `{contextBefore}` 里已翻译的行以 `[SOURCE]` / `[TRANSLATION]` 配对出现 | LocalAI 收到的 user message |
| 8.3 | `/api/translate/content` 单条模式也带上下文 | lingarr 日志 + LocalAI 请求体 |
| 8.4 / 6 | `/content` 复用 `TranslateSubtitles`:进度日志、重复行不合并、取消不污染统计 | lingarr 日志、Translations 页、`/api/statistics` |

### 0.2 已知环境(以你贴出的 compose 为准)

| 项 | 值 |
| --- | --- |
| 部署主机 | NAS,`docker compose`,compose 目录 `/srv/docker/apps/media-stack/` |
| 当前镜像 | `ghcr.io/lingarr-translate/lingarr:main`,容器名 `lingarr`,`user: "1000:1000"` |
| 数据库 | SQLite;配置目录 `/srv/docker/apps/media-stack/config/lingarr` ↔ `/app/config`(含 `local.db`、`Hangfire.db`、`keys/`) |
| 模型后端 | LocalAI(Lingarr 服务名 `localai`),同在 `ai_stack_external_net` |
| 调用方 | Bazarr → `/api/translate/content` |

### 0.3 三条必须先知道的事

1. **批量翻译现在是开着的**(日志里有 `Processing batch translation request with 1891 lines`)。新开关只在**单条模式**下可见且生效,测试前必须先关掉批量(第 6 节)。
2. **单条模式 = 每行一次请求**。本地模型下 1891 行远超 Bazarr 硬编码的 1800 秒超时,**所有测试都用短字幕(几十行)**。这是 Bazarr 侧限制,与本次改动无关。
3. **开关只在 user prompt 含 `{contextBefore}` 时生效**。默认 user prompt 是 `{lineToTranslate}`,不含占位符,不改 prompt 开关就是静默无效的。

---

## 1. 本机:提交与推送

分支已推到 fork:`https://github.com/bluavocado/lingarr/tree/feature/personalized`

| commit | 内容 |
| --- | --- |
| `b50f79f` | `feat: pair translated lines into contextBefore and give /content context` —— 代码、迁移、测试、`ai-services.md` |
| (本文件所在) | `docs: add CLAUDE.md and design notes for translated context and /content refactor` —— `.doc/` 与 `CLAUDE.md` |

NAS 侧拉到代码后用 `git log --oneline -2` 核对第一个 commit 哈希一致。

---

## 2. NAS:拉代码并构建镜像

### 2.1 拉代码

首次:

```bash
# fork 是 public:
git clone --branch feature/personalized --single-branch \
  https://github.com/bluavocado/lingarr.git /srv/docker/src/lingarr

# fork 是 private:二选一
#   a) NAS 上生成 ssh key,加到 GitHub → Settings → SSH keys(或仓库 Deploy keys),然后
git clone --branch feature/personalized --single-branch \
  git@github.com:bluavocado/lingarr.git /srv/docker/src/lingarr
#   b) 用 PAT(只需 repo 读权限)
git clone --branch feature/personalized --single-branch \
  https://<PAT>@github.com/bluavocado/lingarr.git /srv/docker/src/lingarr
```

之后每次更新:

```bash
cd /srv/docker/src/lingarr && git pull && git log --oneline -2
```

### 2.2 构建

```bash
cd /srv/docker/src/lingarr
docker build -f Lingarr.Server/Dockerfile \
  --build-arg VERSION=1.3.0-context.1 \
  -t lingarr:context-test .
```

说明:

- Dockerfile 是多阶段构建:`node:24-slim` 编译前端 → `dotnet/sdk:10.0` 编译后端 → `dotnet/aspnet:10.0` 运行。**NAS 不需要装 node 或 dotnet**。
- `TARGETARCH` 由 BuildKit 按 NAS 自身架构注入,amd64 / arm64 都会自动正确。
- `VERSION` 会进 `/p:Version=`,必须是合法 SemVer。不传则为 `0.0.0-dev`,UI 可能提示「有新版本」,无害。
- 首次构建约 5~10 分钟,峰值约 4 GB 内存,需要能访问 `mcr.microsoft.com`、`docker.io`、NuGet、npm。

自检:

```bash
docker image ls lingarr:context-test
docker run --rm --entrypoint ls lingarr:context-test /app/wwwroot | head   # 应看到 index.html 与 assets/
```

(镜像的 ENTRYPOINT 是 `dotnet Lingarr.Server.dll`,不加 `--entrypoint` 的话 `ls` 会被当成应用参数,应用因缺少 `DB_CONNECTION` 直接退出。)

### 2.3 备选:NAS 太弱时本机构建再传过去

```bash
# 本机 WSL,仓库根目录
docker build --platform linux/amd64 -f Lingarr.Server/Dockerfile \
  --build-arg VERSION=1.3.0-context.1 -t lingarr:context-test .
docker save lingarr:context-test | gzip | ssh <user>@<nas> 'gunzip | docker load'
```

不需要上传 GitHub / GHCR。fork 上的 `docker-build.yml` 只在 push `main` 或发 release 时触发,且依赖 Docker Hub secrets,**不会自动替这个分支出镜像**。

### 2.4 可选:让 compose 直接构建(日常迭代手册)

2.2 是「手动 `docker build`,compose 只认镜像名」。如果之后还要反复改代码、反复部署,可以把构建交给 compose:`git pull` 之后一条命令完成构建和重建容器。本节按这条路线写,和 2.2 二选一。

#### 2.4.1 compose 文件怎么改

只改 `image:` 那一行,换成 `build:` 块加一个本地标签名。其余环境变量、卷、网络、日志、`restart` 全部不动:

```diff
   lingarr:
-    image: ghcr.io/lingarr-translate/lingarr:main
+    build:
+      context: /srv/docker/src/lingarr          # NAS 上 git clone 的目录(2.1)
+      dockerfile: Lingarr.Server/Dockerfile      # 相对 context 的路径
+      args:
+        VERSION: 1.3.0-context.1                 # 会显示在 UI 侧边栏,每次迭代可递增
+    image: lingarr:context-test                  # 构建产物打的标签,不是去拉取
+    # image: ghcr.io/lingarr-translate/lingarr:main   # 原行留着,回滚时换回来
     container_name: lingarr
     user: "1000:1000"
     environment:
       - ASPNETCORE_URLS=http://+:9876
       ...(以下全部不变)
```

改完的完整 service:

```yaml
  lingarr:
    build:
      context: /srv/docker/src/lingarr
      dockerfile: Lingarr.Server/Dockerfile
      args:
        VERSION: 1.3.0-context.1
    image: lingarr:context-test
    # image: ghcr.io/lingarr-translate/lingarr:main
    container_name: lingarr
    user: "1000:1000"
    environment:
      - ASPNETCORE_URLS=http://+:9876
      - DB_CONNECTION=sqlite
      - DB_HANGFIRE_SQLITE_PATH=/app/config/Hangfire.db
      - JOB_TIMEOUT_MINUTES=120
      - MAX_CONCURRENT_JOBS=1
      - TZ=America/Los_Angeles
    ports:
      - "9876:9876"
    volumes:
      - /srv/docker/apps/media-stack/config/lingarr:/app/config
      - /mnt/media-share:/media
    networks:
      - media-stack-net
      - ai_stack_external_net
    logging:
      driver: json-file
      options:
        max-size: "10m"
        max-file: "5"
    restart: unless-stopped
```

几点说明:

- **`image:` 和 `build:` 同时存在**的含义:compose 用 `build:` 构建,然后把结果打成 `image:` 写的标签 `lingarr:context-test`。这里 `image:` 是给产物起名,compose 不会去 Docker Hub 找它。
- **有了 `build:`,`image:` 可以不写**。不写时 compose 自动起名 `<项目名>-<服务名>`,项目名默认取 compose 文件所在目录名,即 `media-stack-lingarr:latest`(已用同名目录 `docker compose config --images` 核实)。建议还是写上:名字固定、不随目录名变,本文档所有用到 `lingarr:context-test` 的命令(2.2、2.4.5、第 13 节)才能直接照抄;不写的话要自行替换成 `media-stack-lingarr`。
- `context` 写绝对路径最省心;也可以写相对 compose 文件的路径,例如 compose 在 `/srv/docker/apps/media-stack/` 时写 `../../src/lingarr`。
- `args.VERSION` 会进 `/p:Version=`,UI 侧边栏显示的「当前版本」就是它。每次迭代递增(`1.3.0-context.2`、`.3`…)能一眼分辨容器跑的是哪次构建;不改也不影响功能。
- 仓库根目录的 `.dockerignore` 会生效,`.git`、`node_modules`、`bin`、`obj` 不会进 build context。
- **为什么不能像别的服务那样写 `build: <目录>` 一行**:短写法等价于 `context: <目录>` + `dockerfile: Dockerfile`,要求目录里直接有 `Dockerfile`。Lingarr 的 Dockerfile 在 `Lingarr.Server/Dockerfile`,仓库根目录没有;而它的 `COPY` 又全是相对仓库根的路径(`Lingarr.Client/`、`Directory.Packages.props`、`Lingarr.Core/…`、最后 `COPY . .`),上下文必须是仓库根。`build: /srv/docker/src/lingarr` 找不到 Dockerfile,`build: /srv/docker/src/lingarr/Lingarr.Server` 找得到但 `COPY` 会失败,只有长写法能同时满足。三个键里 `context`、`dockerfile` 必需,`args.VERSION` 可选(不写时 Dockerfile 默认 `0.0.0-dev`,只影响 UI 显示的版本号)。

#### 2.4.2 三条命令的区别

| 命令 | 做什么 | 什么时候用 |
| --- | --- | --- |
| `docker compose build lingarr` | 只构建、打标签,**不动容器** | 想先确认构建能过 |
| `docker compose up -d --build lingarr` | 构建;镜像有变化就重建容器,没变化就什么都不做 | **日常迭代,一步到位** |
| `docker compose up -d lingarr` | **不构建**。本地有 `lingarr:context-test` 就直接用 | 只是想把容器拉起来 |

两个偶尔用到的开关:

- `docker compose build --no-cache lingarr`:不用任何缓存,从头全量构建。怀疑缓存有问题时用。
- `docker compose build --pull lingarr`:顺便去拉更新的 `node:24-slim` / `dotnet/sdk:10.0` / `dotnet/aspnet:10.0` 基础镜像。平时不需要。

#### 2.4.3 日常迭代完整序列

```bash
# 1. 拉代码,记下 commit
cd /srv/docker/src/lingarr && git pull && git log --oneline -1

# 2. 构建并重建容器
cd /srv/docker/apps/media-stack
docker compose up -d --build lingarr

# 3. 看日志,出现 Now listening 即可 Ctrl-C
docker compose logs -f lingarr
```

第 3、5 节的备份与迁移验证照做,与构建方式无关。

#### 2.4.4 构建要多久

时长由 Dockerfile 的层缓存决定,层的顺序是:`npm ci` → `dotnet restore` → `COPY . .` → 编译/发布。

| 情形 | 哪些层重跑 | 大约耗时 |
| --- | --- | --- |
| 首次 | 全部(含拉基础镜像) | 5~10 分钟 |
| 只改了 C# / Vue 源码 | `COPY . .` 之后的编译层;`npm ci`、`dotnet restore` 命中缓存 | 2~4 分钟 |
| 改了 `package-lock.json` | `npm ci` 起全部重跑 | 接近首次 |
| 改了 `*.csproj` / `Directory.Packages.props` / `global.json` | `dotnet restore` 起全部重跑 | 接近首次 |
| 源码没变就 `up -d --build` | 全部命中缓存 | 几十秒,且容器不会被重建 |

#### 2.4.5 确认容器跑的是刚构建的镜像

```bash
docker compose images lingarr                                  # 镜像 id 与 created 时间
docker inspect lingarr --format '{{.Image}}'                   # 容器实际用的镜像 id
docker image inspect lingarr:context-test --format '{{.Id}}'   # 标签指向的镜像 id
```

后两个 id 应一致。再看 UI 侧边栏的版本号(或 `curl -s http://<nas>:9876/api/version`)是不是这次 `VERSION` 的值。

#### 2.4.6 两个坑

1. **整栈 `docker compose pull` 会报错。** 它会去 Docker Hub 找 `lingarr:context-test`,找不到就失败。更新其他服务时改用:

   ```bash
   docker compose pull --ignore-buildable     # 跳过所有带 build: 的服务
   ```

   或者只 pull 指定服务:`docker compose pull radarr sonarr …`。

2. **悬空镜像堆积。** 每次重建后,旧的 `lingarr:context-test` 会变成 `<none>` 悬空层,一次几百 MB。定期清理:

   ```bash
   docker image prune -f
   ```

#### 2.4.7 可选:`pull_policy: build`

`pull_policy` 是 compose「启动时怎么拿镜像」的策略。默认是 `missing`:本地有 `lingarr:context-test` 就直接用,没有才构建。改成 `build` 后,**每次 `docker compose up` 都先构建一遍,哪怕本地已经有这个镜像**,效果等于永远自带 `--build`。

写法是在 service 里多加一行,放 `image:` 后面最直观:

```yaml
  lingarr:
    build:
      context: /srv/docker/src/lingarr
      dockerfile: Lingarr.Server/Dockerfile
      args:
        VERSION: 1.3.0-context.1
    image: lingarr:context-test
    pull_policy: build            # ← 每次 up 都重新构建
    container_name: lingarr
    # …以下全部不变
```

加与不加的区别:

| | 不加(默认 `missing`) | 加 `pull_policy: build` |
| --- | --- | --- |
| 日常迭代 | `git pull` 后必须 `up -d --build lingarr`;忘了 `--build` 就还是旧镜像 | `git pull` 后 `up -d lingarr` 即可,不会跑到旧代码 |
| 源码没变时 `up` | 秒起 | 也很快,全部命中缓存,几十秒 |
| 整栈 `docker compose up -d` | lingarr 不重建 | lingarr 每次都走一遍构建 |
| 只想重启 | `up -d` 或 `restart` 都行 | 用 `docker compose restart lingarr`,它不触发构建 |

**建议先不加**,用 `--build` 手动控制,构建成功与否、耗时多少都在眼前;流程跑顺了、想省事再加。两种写法二选一,不要一边加了 `pull_policy: build` 一边还习惯性带 `--build`。

#### 2.4.8 与第 4 节的关系

第 4 节按 2.2 的「手动 `docker build` + `image:`」写。若采用本节写法,第 4 节的 compose 片段以 2.4.1 为准,启动命令改为 `docker compose up -d --build lingarr`;备份、迁移验证、后面所有测试步骤不变。

---

## 3. 部署前备份

```bash
cd /srv/docker/apps/media-stack
docker compose stop lingarr
tar czf ~/lingarr-config-$(date +%F).tgz -C /srv/docker/apps/media-stack/config lingarr
```

迁移 M0021 只往 `settings` 表插一行 `ai_context_use_translated = false`,没有 schema 变更;回滚到旧镜像时这行是惰性的(设计文档 4.1)。备份只是保险。

---

## 4. 修改 compose 并启动

本节对应 2.2 的手动构建路线。若走 2.4 的 compose 构建路线,compose 片段以 2.4.1 为准,启动命令用 `docker compose up -d --build lingarr`,其余不变。

只改 `image:` 一行,其余环境变量、卷、网络全部不动:

```yaml
  lingarr:
    image: lingarr:context-test                       # ← 原来是 ghcr.io/lingarr-translate/lingarr:main
    container_name: lingarr
    user: "1000:1000"
    environment:
      - ASPNETCORE_URLS=http://+:9876
      - DB_CONNECTION=sqlite
      - DB_HANGFIRE_SQLITE_PATH=/app/config/Hangfire.db
      - JOB_TIMEOUT_MINUTES=120
      - MAX_CONCURRENT_JOBS=1
      - TZ=America/Los_Angeles
    ports:
      - "9876:9876"
    volumes:
      - /srv/docker/apps/media-stack/config/lingarr:/app/config
      - /mnt/media-share:/media
    networks:
      - media-stack-net
      - ai_stack_external_net
    logging:
      driver: json-file
      options:
        max-size: "10m"
        max-file: "5"
    restart: unless-stopped
```

```bash
docker compose up -d lingarr
docker compose logs -f lingarr
```

启动成功判据:出现 `Now listening on: http://[::]:9876`(或稍后的 `Retrieved latest version`),且没有异常堆栈。

---

## 5. 验证迁移已应用

下文 `<nas>` 指 NAS 地址。若 `auth_enabled` 为 true,所有 `curl` 要加 `-H "X-Api-Key: <key>"`,key 在 Settings → Authentication → **API Key** 卡片。

方式一(推荐,不进容器):

```bash
curl -s http://<nas>:9876/api/setting/ai_context_use_translated
# 期望: "false"
```

(全新空库会先返回 `{"message":"Onboarding required","onboardingRequired":true}`,那是尚未完成初始化向导;你的实例已完成,不会遇到。)

方式二(直接看库):

```bash
docker run --rm -v /srv/docker/apps/media-stack/config/lingarr:/data:ro alpine sh -c \
  "apk add -q sqlite && sqlite3 /data/local.db \
   \"select key, value from settings where key like 'ai_context%'; select max(version) from version_info;\""
# 期望:
#   ai_context_after|0   (你的实际值)
#   ai_context_before|0  (你的实际值)
#   ai_context_use_translated|false
#   21
```

拿不到这行说明容器跑的不是新镜像(`docker inspect lingarr --format '{{.Config.Image}}'` 核对)。

---

## 6. UI 配置

1. **Settings → Services**:`Use batch translation` → **Disabled**。
2. 同页 `localai` 服务卡片 → **Open Request Settings** → **Translation Prompt** 卡片:
   - `Translation user prompt` 改成含占位符的模板,推荐:

     ```
     Previous lines:
     {contextBefore}

     Translate this line:
     {lineToTranslate}
     ```

   - `Context before` = `2`
   - `Context after` = `0`(第一轮先设 0,让 user message 里只有配对块;第 12 节再设 1 验证 after 恒原文)
   - **Use translated lines as context before** → **Enabled**(新开关,只在批量关闭时显示)
3. 持久化验证:刷新页面,开关仍是 Enabled;`curl -s http://<nas>:9876/api/setting/ai_context_use_translated` 返回 `"true"`。

如果拨开关时页面报错 `Setting not found or could not be updated`,就是第 5 节没过(种子行不存在)。

---

## 7. 让 LocalAI 打印收到的完整请求

上下文最终长什么样,只有模型那一侧能看到。LocalAI 容器加环境变量 `DEBUG=true`(等价于启动参数 `--debug`)并重启:

```bash
docker logs -f <localai 容器名> 2>&1 | grep -A 30 'chat/completions'
```

之后每次请求的 JSON 体(含 `messages`)都会打印出来。

备选(不想动 AI 栈):把 Lingarr 的 `local_ai_endpoint` 临时指向一个回显容器,只看请求不看结果,测完改回:

```bash
docker run -d --name echo --network ai_stack_external_net mendhak/http-https-echo:latest
# Lingarr 里把 endpoint 改成 http://echo:8080/v1/chat/completions,翻译会失败,但 docker logs echo 能看到完整请求
```

### 观察面对照表(整份文档的核心)

| 验证点 | 在哪里看 | 期望 |
| --- | --- | --- |
| `/content` 走了单条 + 上下文 | lingarr 日志 | `Using individual line translation for N lines from en to zh-CN (context before: 2, after: 0, translated context: True)` |
| `/content` 有进度日志(改前没有) | lingarr 日志 | `Progress: N% (Subtitle X of Y)`,类别仍是 `TranslationRequestService` |
| 配对格式 | LocalAI 请求体 `messages` 里 role=user 的 content | 第 2 行起出现 `[SOURCE] …\n[TRANSLATION] …`,一行一组、按顺序、最多 2 组 |
| 重复文本不被合并 | LocalAI 收到的请求数 | 三个 `Yeah.` 产生**三次**请求,后两次的上下文里带前一次的译文 |
| after 恒原文 | 第 12 节把 `Context after` 设 1 后 | after 部分是纯原文,没有标签 |
| `/file` 路径 | lingarr 日志 | `TranslateJob started` → `Using individual translation with context (before: 2, after: 0, translated context: True)` → `Progress: …` |
| 开关关闭回归 | LocalAI 请求体 | 上下文变回纯原文,没有 `[SOURCE]` |

---

## 8. 测试 A:curl 模拟 Bazarr 调 `/content`

### 8.1 取一个 Radarr 电影 id

```bash
curl -s 'http://<nas>:9876/api/media/movies?pageSize=3' | jq '.items[] | {id, radarrId, title}'
```

`arrMediaId` 填 `radarrId`(字段名以实际返回为准;若返回结构不是 `items`,直接 `| jq .` 看一眼)。

### 8.2 请求体

`body.json`(枚举按字符串序列化,`mediaType` 写 `"Movie"`):

```json
{
  "arrMediaId": 123,
  "sourceLanguage": "en",
  "targetLanguage": "zh-CN",
  "mediaType": "Movie",
  "lines": [
    {"position": 1, "line": "Detective Mouri is on the case."},
    {"position": 2, "line": "But he is drunk again."},
    {"position": 3, "line": "Yeah."},
    {"position": 4, "line": "Yeah."},
    {"position": 5, "line": "Yeah."},
    {"position": 6, "line": ""},
    {"position": 7, "line": "Let's ask Conan."}
  ]
}
```

三个 `Yeah.` 是故意的:它们就是设计文档 8bis 发现 1 要防的场景。

### 8.3 发请求

```bash
curl -sS -X POST http://<nas>:9876/api/translate/content \
  -H 'Content-Type: application/json' \
  -d @body.json | jq
```

### 8.4 期望

- 返回 7 个元素,`position` 顺序一致,第 6 个 `line` 为空。
- lingarr 日志:`Using individual line translation for 7 lines from en to zh-CN (context before: 2, after: 0, translated context: True)`,随后若干条 `Progress: N% (Subtitle X of 7)`,最后 `Individual line translation completed. Processed 7 lines`。
- LocalAI 请求体:
  - 第 1 行的 user message 里 `{contextBefore}` 位置为空;
  - 第 2 行起出现 `[SOURCE] Detective Mouri is on the case.\n[TRANSLATION] <第 1 行译文>`;
  - 第 4、5 行(后两个 `Yeah.`)各自是独立请求,且上下文里带着前一个 `Yeah.` 的译文;
  - 第 7 行的上下文是第 5 行(`Yeah.`)和第 6 行(空)的配对。
- Translations 页多一条 `Completed` 请求,详情页能看到逐行译文。

### 8.5 坑

**同一媒体 + 语言对已有 `Pending` / `InProgress` 请求时,`/content` 直接返回 `[]`**(日志 `Duplicate content-translation request skipped`)。重跑前先在 Translations 页把上一条取消或等它完成。

---

## 9. 测试 B:取消语义(设计文档 8bis 发现 3)

1. 先记下统计基线:

   ```bash
   curl -s http://<nas>:9876/api/statistics | jq '{totalLinesTranslated, totalFilesTranslated}'
   ```

2. 把 `body.json` 扩到 30~50 行(内容随意,`position` 递增),发请求。
3. 翻到一半时取消。两种方式任选:
   - UI:Translations 页对应行点 **Cancel**;
   - API:`TranslationRequest` 的字段是 `required`,取消接口要整个对象,先取再回传:

     ```bash
     ID=<Translations 页看到的 id>
     curl -s http://<nas>:9876/api/translationrequest/$ID > req.json
     curl -sS -X POST http://<nas>:9876/api/translationrequest/cancel \
       -H 'Content-Type: application/json' -d @req.json
     ```

4. 期望:
   - `curl` 那边收到 HTTP 500,body 形如 `{"error": "The operation was canceled."}`;
   - Translations 页该请求状态 `Cancelled`;
   - **`totalLinesTranslated` 与基线相同**(部分结果没有进统计);
   - lingarr 日志里**没有** `Individual line translation completed` 这行。

---

## 10. 测试 C:UI 发起 `/file` 翻译

1. **Movies** 页选一部有**短**英文字幕的电影,发起到 zh-CN 的翻译。
2. 观察:
   - 日志三连:`TranslateJob started for subtitle: …` → `Using individual translation with context (before: 2, after: 0, translated context: True) for subtitle: …` → `Progress: N% (Subtitle X of Y)`;
   - Translations → 该请求详情页,译文逐行实时出现;
   - LocalAI 请求体第 2 行起带配对块;
   - 完成后目标语言字幕文件出现在媒体目录。
3. 可选,验证续传预填也能配对(设计文档 4.6 第二行):翻到一半 **Cancel**,再点 **Resume**。日志出现 `Resuming translation for request N: X of Y lines already translated`,之后第一条 LocalAI 请求的配对块里,译文来自数据库里已存的行。

---

## 11. 测试 D:真实 Bazarr 联调

1. 按你现有的 Bazarr → Lingarr 用法,对**一部短字幕**触发一次翻译。
2. 观察:
   - lingarr 日志出现与第 8 节相同的 `Using individual line translation for N lines … translated context: True`(说明 Bazarr 路径也拿到了上下文);
   - Bazarr 侧收到完整译文文件,行数与源文件一致。
3. 再次提醒:单条模式下长字幕会先撞上 Bazarr 的 1800 秒超时(表现为 lingarr 日志里 `TaskCanceledException` + 请求 `Cancelled`),这是 Bazarr 侧限制。真要跑长字幕,回到批量模式。

---

## 12. 回归对照

| 操作 | 期望 |
| --- | --- |
| 开关改 **Disabled**,重跑测试 A | LocalAI 请求体里上下文是纯原文,没有 `[SOURCE]`;日志 `translated context: False` |
| `Context after` 设 1,开关 Enabled,重跑测试 A | before 部分是配对块,after 部分是纯原文 |
| `Use batch translation` 改 **Enabled** | 开关从 UI 消失;`/content` 日志回到 `Processing batch translation request with N lines`;行为与改动前一致 |

---

## 13. 回滚

```bash
cd /srv/docker/apps/media-stack
# compose 里 image 改回 ghcr.io/lingarr-translate/lingarr:main
docker compose up -d lingarr
```

- 多出的 `ai_context_use_translated` 行对旧版本是惰性的(全链路按显式键名读取),不需要清理。
- 真要恢复数据:`docker compose stop lingarr && tar xzf ~/lingarr-config-<日期>.tgz -C /srv/docker/apps/media-stack/config`。
- LocalAI 去掉 `DEBUG=true`;若用了回显容器,把 `local_ai_endpoint` 改回并 `docker rm -f echo`。
- 记得把批量翻译开关和 user prompt 改回你日常用的配置。

---

## 14. 常见问题

| 现象 | 原因 | 处理 |
| --- | --- | --- |
| `/content` 返回 `[]` | 同媒体 + 语言对有活动请求 | 取消上一条再发(8.5) |
| 拨新开关报 `Setting not found or could not be updated` | 种子行不存在,容器不是新镜像 | 第 5 节核对镜像与 `version_info` |
| Translation Prompt 卡片里看不到新开关 | 批量翻译还开着 | 第 6 节第 1 步 |
| LocalAI 请求体里没有上下文 | user prompt 没写 `{contextBefore}`,或 `Context before` 为 0 | 第 6 节第 2 步 |
| 有上下文但没有 `[SOURCE]` 标签 | 开关未开,或看的是第 1 行(前面没有已译行) | 从第 2 行起看 |
| `/content` 500 且 `All configured translation services failed` | LocalAI 不可达 / 模型未加载 | 看 LocalAI 日志 |

---

## 15. 结果记录

| 测试 | 期望 | 实际 | 通过 |
| --- | --- | --- | --- |
| 第 5 节 迁移 | 设置行存在,`version_info` = 21 | | |
| A `/content` 上下文 + 配对 | 日志 `translated context: True`;LocalAI 见配对块;`Yeah.` 三次请求 | | |
| B 取消 | 500 + `Cancelled` + 统计不变 | | |
| C `/file` | 日志三连 + 配对块 + 文件生成 | | |
| D Bazarr | 日志同 A;Bazarr 收到完整文件 | | |
| 回归:开关关闭 | 纯原文上下文 | | |
| 回归:after = 1 | after 纯原文 | | |
| 回归:批量打开 | 开关消失,行为同改前 | | |
