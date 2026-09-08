# 翻译任务被取消 —— 根因分析

> 代码基准:分支 `feature/personalized`,HEAD `28f6a19`
> 复现环境:Bazarr(Translator = Lingarr,endpoint `http://lingarr:9876`)→ Lingarr(`:main` 镜像,`DB_CONNECTION=sqlite`,`JOB_TIMEOUT_MINUTES=120`),直连 9876 无反向代理
> 实测:03:24:20 发起 → 03:54:19 取消(29 分 59 秒),1 秒后从头重跑

> 📌 **2026-09-07 修订**:本文原为纯 Lingarr 侧排查。此后从 **Bazarr 侧**复查了整条链路并回到 Lingarr `main` 源码交叉验证,**§2.4 的三条结论被推翻**(已就地修订并保留原文备查),**§1 与 §4 的建议被修正**,§12 新增三行。Bazarr 侧的完整调研另见 [`bazarr_lingarr_integration.md`](./bazarr_lingarr_integration.md)。
>
> 未受影响的部分:§5(字幕校验)与 §6-§11 全部属路径 B,本轮未复查,原文保持不变。

---

## 0. 诊断速查表:四种故障,三种都叫「已取消」

排查本类问题的**最大陷阱**:三种毫不相干的故障在 UI 上全部显示为 `Cancelled`,错误信息也几乎一样。必须靠日志分流。

**两步定位法 —— 先看耗时,再看 logger 名:**

| 症状 | 日志特征 | 真实原因 | 处置 |
| --- | --- | --- | --- |
| 约 30 分钟后取消 | logger = `…Services.TranslationRequestService` | **Bazarr 硬编码 `timeout=1800`** | 改用 Lingarr 自身入口(第 1 节) |
| 约 30 分钟后取消 | logger = `…Jobs.TranslationJob` | **Hangfire 租约**(即 #505) | `JOB_TIMEOUT_MINUTES` + `:main` 镜像 |
| **秒级取消** | `Subtitle is not valid according to configured preferences` | **字幕校验失败** | 关闭 Settings → **Subtitle**(第 5 节) |
| **跑到中途失败** | `JsonException … is an invalid end of a number` / `Failed to parse JSON response` | **模型输出被上下文窗口截断** | 调小 `max_batch_size`(第 6 节) |

前三者的共同点仅仅是「都落成 `Cancelled` 状态」—— 详见 12.4,这本身是个应当修复的可用性缺陷。第四种表现为 `Failed`,是本表中唯一能从 UI 看出端倪的。

### 两条执行路径

前两种原因分属两条**完全独立**的执行路径,超时机制毫无交集:

| | **路径 A:外部 API 同步调用** | **路径 B:Lingarr 自身任务** |
| --- | --- | --- |
| 入口 | `POST /api/translate/content` | `/api/translate/file`、`/bulk`、自动化任务 |
| 谁在用 | **Bazarr 的 Lingarr translator** | Lingarr 网页界面、定时自动翻译 |
| 执行方式 | **HTTP 请求内同步跑完** | Hangfire 后台任务,立即返回 jobId |
| 30 分钟上限来自 | **Bazarr 硬编码 `timeout=1800`** | Hangfire `InvisibilityTimeout` |
| `JOB_TIMEOUT_MINUTES` 是否有效 | **完全无效** | 有效(需 `:main` 镜像) |
| 断点续传 | **无,每次从 0 重来** | 有 |
| 进度可见 | 仅服务端日志 | UI 实时进度 + SignalR |
| 对应 discussion #505 | 否 | **是** |

**本次故障命中的是路径 A。** 而 discussion #505、`JOB_TIMEOUT_MINUTES`、Hangfire 租约等等全部属于路径 B —— 两者的唯一共同点只是「都有一个 30 分钟的常量」。

---

## 1. 你现在该怎么做

### 方案 1:改用 Lingarr 自身的翻译入口(推荐)

在 Lingarr 界面里对该剧集发起翻译。这条路走 `/api/translate/file` → `TranslationRequestService.CreateRequest` → `_backgroundJobClient.Enqueue<TranslationJob>`(`TranslationRequestService.cs:180`),**立即返回 jobId,不占用 HTTP 连接**。

得到的好处:

- 不再受任何客户端超时约束
- `JOB_TIMEOUT_MINUTES=120` 在这条路上**才真正生效**(需 `:main` 镜像,见附录 A)

> ℹ️ **换入口不会改变批量与否。** `/content` 与 `/file` 受**同一个** `use_batch_translation` 设置支配(判定条件几乎一致,详见
> [`prompt_and_batch_behavior.md` 第 5 节](./prompt_and_batch_behavior.md))。因此第 6 节所述的截断问题在两条入口上同样可能发生,
> `max_batch_size` 仍需按那里的公式设置。
>
> 两条入口真正的差异在**上下文行**:`/file` 的单条模式会构建前后各 N 行的上下文,而 `/content` 在任何模式下都拿不到 —— 同上文档第 6 节。
- 有实时进度、可续传(`TranslationJob.cs:188-208` 会复用已翻译的行)

**代价:不再由 Bazarr 自动触发,需要手动或用 Lingarr 自己的自动化。**

> ⚠️ **该方案只对「人工在 Lingarr 界面操作」成立,Bazarr 无法自动走这条路。**
> `/api/translate/file` 与 `/api/translate/bulk` 要的都是 **Lingarr 内部自增主键**(`TranslateAbleSubtitle.MediaId` / `BulkTranslateRequest.MediaIds`,后者的实现直接查 `Movies.Id` / `Shows.Id`,见 `TranslationRequestService.cs:201`、`:224`),而 `MediaController` 只提供分页列表,**没有 arr id → 内部 id 的转换接口**。
> 只有 `/api/translate/content` 内置了这层转换(`GetMediaId:968` → `MediaService.cs:154-160` 查 `Episodes.SonarrId`),而它恰恰是同步的。
> 详见 [`bazarr_lingarr_integration.md`](./bazarr_lingarr_integration.md) §1。

### 方案 2:让单次翻译压进 30 分钟内

换更小 / 更高量化的模型,或启用 GPU。**这是唯一能继续使用 Bazarr 集成的办法**,因为 Bazarr 侧的超时不可配置。

### 方案 3:本地打补丁改掉 Bazarr 的 1800

bind-mount 覆盖 Bazarr 容器内的 `bazarr/subtitles/tools/translate/services/lingarr_translator.py`。代价:Bazarr 升级后会被覆盖,需要重新维护。

### 方案 4:向 Bazarr 提 issue / PR

把该 `timeout` 做成可配置项。根治,但周期长。

### 关于你已经做过的两处改动

`JOB_TIMEOUT_MINUTES=120` 和换用 `:main` 镜像 —— **对当前用法无害,但也无用**,因为它们只作用于路径 B。若采纳方案 1,这两项会立刻开始起作用,**建议保留**。

---

## 2. 证据链

### 2.1 该路径根本不经过 Hangfire

- 日志行 `Processing batch translation request with 1891 lines` 出自 **`TranslationRequestService.cs:826`**,位于 `TranslateContentAsync` 内。
- `TranslateContentAsync` 的**唯一**调用者是 **`TranslateController.cs:115`**,端点 `POST /api/translate/content`。
- 传入的 `cancellationToken` 是 ASP.NET Core 动作绑定的 **`HttpContext.RequestAborted`** —— 即「HTTP 连接断开」信号。`TranslationRequestService.cs:764-766` 把它与自身 CTS 链接后,一路传到 Ollama 的 `PostAsync`:

  ```csharp
  // Link cancel token with new source to be able to cancel the async translation
  var asyncTranslationCancellationTokenSource = new CancellationTokenSource();
  var cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(
      parentCancellationToken, asyncTranslationCancellationTokenSource.Token);
  ```

- **Lingarr 前端从不调用该端点** —— `Lingarr.Client/src/services/translateService.ts` 全文只用到 `/api/translate/file`、`/bulk`、`/languages`、`/proofread*`。该端点是纯粹的外部集成接口。
- 全仓库**没有** `CancelAfter`、`CancellationTokenSource(TimeSpan)` 或显式 `RequestAborted` 的定时取消。

**结论:整个翻译在一次 HTTP 请求内同步跑完,取消源只能是调用方断开连接。`JOB_TIMEOUT_MINUTES` 只影响 Hangfire 存储的队列租约,对这条路径结构上不可能有任何作用。**

### 2.2 30 分钟是 Bazarr 硬编码的

`bazarr/subtitles/tools/translate/services/lingarr_translator.py`:

```python
response = requests.post(
    f"{settings.translator.lingarr_url}/api/translate/content",
    json=payload,
    headers=headers,
    timeout=1800
)
```

`timeout=1800` 秒 = **整 30 分钟,硬编码,不读任何设置**。与实测的 29 分 59 秒完全吻合。

### 2.3 如何从异常形态判断「外部取消」

这段判据在本次排查中把方向从「HTTP 超时」拨向了「外部取消」,值得保留:

```
TaskCanceledException: The operation was canceled.
 ---> IOException: Unable to read data from the transport connection: Operation canceled.
 ---> SocketException (125): Operation canceled
```

`HttpClient.Timeout` 到期时,.NET 抛出的消息会明确写成 *"The request was canceled due to the configured HttpClient.Timeout of X elapsing."*,内层是 `TimeoutException`。而这里是通用的 *"The operation was canceled."* + `SocketException(125)`(Linux `ECANCELED`),是**外部 CancellationToken 掐断 socket 读取**的特征。

⚠️ 注意:这只能证明「取消来自外部令牌」,**不能证明来自 Hangfire** —— 前两轮正是在这里多跨了一步。

### 2.4 有界的重试循环:每次点击 3 次,约 90 分钟后硬失败

> 🔄 **本节已于 2026-09-07 修订。** 原文三条结论(「进度全部作废」「无限重试」「1 秒后重启属推断」)在从 Bazarr 侧复查后全部需要修正,详见 [`bazarr_lingarr_integration.md`](./bazarr_lingarr_integration.md)。原判断保留在下方引用块中以备追溯。

03:54:20(取消后 1 秒)日志再次出现 `Processing batch translation request with 1891 lines`,行数相同 → 同一文件重跑。

**修正一:重试来源已定性,不再是推断。**

重试来自 Bazarr 自己 —— `bazarr/subtitles/tools/translate/services/lingarr_translator.py:113`:

```python
@retry(exceptions=(TooManyRequests, RequestError, requests.exceptions.RequestException), tries=3, delay=1,
       backoff=2, jitter=(0, 1))
def _translate_content(self, lines_list, job_id):
```

`requests` 抛 `Timeout` → `:185-187` 转成 `RequestError` → 命中装饰器的 `exceptions` 元组 → `libs/retry/api.py:30-45` 睡 `delay` 后重调。`delay=1, backoff=2, jitter=(0,1)` 意味着**首次重试在失败后 1-2 秒发出**,与实测的「1 秒后重跑」完全吻合。

「也可能是 Bazarr 的下一轮调度」这个备选可以排除:`_is_an_existing_job`(`bazarr/app/jobs_queue.py:622-623`)会剥掉 `job_id` 后比对 module/func/args/kwargs,把重复的翻译请求**静默丢弃**,不可能产生第二次执行。

**修正二:进度没有作废,作废的是「关联」。**

原文说「首轮已完成的 24%(450/1891)全部作废」—— **不成立**。Lingarr 的译文是**逐批落库**的:

| 事实 | 位置 |
| --- | --- |
| 每个内部批次(按 `max_batch_size` 切)翻完立刻写 `TranslationRequestLines` 表 | `SubtitleTranslationService.cs:326-341`(在 `for` 循环体内)→ `ProgressService.cs:78-95` `EmitLines` |
| 取消时只改 `TranslationRequest.Status`,**不删已落库的行** | `TranslationRequestService.cs:918-936` |
| `GET /api/translationrequest/{id}` 会把这些行全量返回 | `TranslationRequestDetail.Lines` |
| `POST /api/translationrequest/resume` 可把 Cancelled 请求重新入队成 Hangfire 任务 | `TranslationRequestService.cs:409-446` |
| 恢复时按 `Position` 复用已落库的行,只翻剩下的 | `TranslationJob.cs:188-205` |

**那 450 行一直好好地躺在 Lingarr 的数据库里。** 丢掉它的是 Bazarr —— `@retry` 重发的是一份全新 payload,Lingarr 于是 `new TranslationRequest` 建**新的一行**,从零开始。三次尝试产生三行互不相干的 TranslationRequest,各自攒了一部分译文,谁也不认识谁。

这个区别很重要:它意味着「让重试不白干」在协议上是**可行的**(见新文档 §7.2 的 resume 方案),而不是像原文推论的那样结构性无解。

**修正三:重试有界,不是无限。**

`tries=3` → 尝试 1(1800s)→ 睡约 1s → 尝试 2(1800s)→ 睡约 2s → 尝试 3(1800s)→ 抛出。**一次点击约 90 分钟后彻底失败,共烧掉 3 倍全文件算力。**

此后**不会自动重排**:`_run_job`(`bazarr/app/jobs_queue.py:581-586`)把 job 标 `failed` 并移入 `jobs_failed_queue`,`bazarr/api/system/jobs.py` 只提供 `force_start`(仅限 pending)、`move_top`、`move_bottom`、`empty_jobs_queue`、`delete`,**没有 retry-failed 接口**。所以循环终止于第 3 次,除非用户再点一次。

> **原文结论(已推翻,保留备查)**
> ~~而 `TranslateContentAsync` 每次调用都 `new TranslationRequest`,该路径**没有断点续传**(续传逻辑在 `TranslationJob.cs:188-208`,属路径 B)。因此每次都从 0% 重来,首轮已完成的 24%(450/1891)全部作废。~~
> ~~**只要单次翻译超过 30 分钟,就会无限重试、无限消耗算力,永远不可能成功。**~~
> ~~「1 秒后重启 = Bazarr 自动重试」属合理推断,非直接实测 —— 也可能是 Bazarr 的下一轮调度。~~

**仍然成立的部分**:单次翻译只要超过 30 分钟,这次点击就注定失败,且三次尝试的算力全部浪费。结论的**紧迫性不变**,变的是「无限」→「有界但仍然全废」,以及「数据丢了」→「数据还在,只是没人去取」。

**新增的连带后果(原文未覆盖)**:这 90 分钟里,翻译线程占着 Bazarr 全局任务队列 `concurrent_jobs`(默认 4)中的一个名额。前端 `TranslationForm.tsx:203-214` 是 `Promise.all` 提交的,选 4 集一起翻会**冻结整个 Bazarr 的任务队列** —— Sonarr/Radarr 同步、字幕搜索、健康检查、备份全部排队等着。详见新文档 §4。

---

## 3. 诊断方法论(本次三轮误判的教训)

- **「30 分钟」这个数字在三个不同层都存在**:Bazarr 的 `timeout=1800`、Hangfire SQLite 的 `InvisibilityTimeout`、以及 Lingarr 1.3.0 中写死的同一个值。仅凭数字巧合无法定位,必须找到执行路径。

- **日志的 logger 名称是最快的分流依据**:
  - `Lingarr.Server.Services.TranslationRequestService` → 路径 A(API 同步)
  - `Lingarr.Server.Jobs.TranslationJob` → 路径 B(Hangfire)

  第一轮若注意到这一点,可以少走两轮弯路。

- **`LocalAiService.cs:331` 的无过滤 `catch` 进一步掩盖了方向**:

  ```csharp
  catch (Exception ex)
  {
      _logger.LogWarning(ex, "Structured output failed, falling back to JSON parsing");
      return await TranslateBatchWithJsonParsing(subtitleBatch, cancellationToken);
  }
  ```

  它把取消异常伪装成「结构化输出失败」,并拿已取消的令牌再发一次请求。排查者第一眼会以为是模型返回格式问题。**建议加过滤条件 `when (!cancellationToken.IsCancellationRequested)`。**

---

## 4. `/api/translate/content` 的设计缺陷

把长耗时任务放在 HTTP 请求内**同步**执行,是该端点的结构性问题:

- 客户端超时、网络抖动、反代重启,都会让本次调用的**成果无法交付**(数据仍在库里,但调用方拿不到,也无从引用 —— 见 2.4 修正二)
- 调用方拿不到任何 id,因此无法轮询、无法 resume、无法引用上一次的部分成果
- 调用方必须维持一个数十分钟的空闲连接

对照同一控制器里 `/api/translate/file` 已有的正确做法:入队 → 返回 id → 调用方轮询或走 SignalR。

### 4.1 修正:所需的改造比原先设想的小得多

原文建议「该端点改为同样入队并返回 jobId」。从 Bazarr 侧复查后,这个判断需要两点修正:

**(a) `/file` 返回的就是可轮询的那个 id。** `CreateRequest` 的签名是 `Task<int>`(`TranslationRequestService.cs:121`),返回的是 **TranslationRequest 的自增主键**,包进 `TranslationJobDto { JobId }`(该字段类型也是 `int`)。它与 `GET /api/translationrequest/{id}` 用的是同一个 id。**协议上轮询是通的**,不存在「返回 Hangfire jobId 无法查询」的问题。

**(b) 缺的零件只有一个。** `/content` 路径所需的其余能力 Lingarr **已经全部具备**:

| 能力 | 现状 | 位置 |
| --- | --- | --- |
| 逐批持久化译文 | ✅ 已有 | `SubtitleTranslationService.cs:326-341` |
| 按 id 查状态 / 进度 / 全部译文 | ✅ 已有 | `GET /api/translationrequest/{id}` → `TranslationRequestDetail` |
| 从已落库的行恢复,只翻剩余 | ✅ 已有 | `TranslationJob.cs:188-205` |
| resume / cancel / retry | ✅ 已有 | `TranslationRequestService.cs:334/394/409` |
| **提交内容后立即返回 id** | ❌ **缺** | —— |

因此**最小改造不是「把 `/content` 改成入队」**(那会破坏现有调用方的契约),而是**新增一个 `POST /api/translate/content/async`,收同样的 `TranslateAbleSubtitleContent`,建好 TranslationRequest 后立即返回其 `Id`**。调用方随后用现成的 `GET /{id}` 轮询、从 `Lines` 取回译文。

**为什么这对 Bazarr 是刚需**:Bazarr 只能用 `/content`(它是唯一接受 `lines`、也是唯一接受 arr id 的入口 —— 见 §1 的补充说明与新文档 §1)。在没有这个异步变体之前,Bazarr 侧**只能**在客户端做分批把单次请求压短,无法真正做到断点续传。

> 另一条不改上游的迂回路径(POST 超时后靠 title 反查 id → `resume` → 轮询)在技术上可行,但需要共享文件系统、会触发 §5 的字幕校验、并会多写一个 Lingarr 命名的 SRT。评估见新文档 §7.2。

---

## 5. 秒级取消:字幕校验失败

与前面两节的超时**毫无关系**。特征是任务在**零点几秒内**结束,日志形如:

```
[Warning] SubtitleService: Subtitle at position 12 overlaps with previous subtitle.
                          Current start: 166170, Previous end: 168910
[Warning] TranslationJob: Subtitle is not valid according to configured preferences.
[Information] TranslationJob: Translation cancelled for subtitle: …
```

### 触发链

| 位置 | 内容 |
| --- | --- |
| `TranslationJob.cs:118` | `var validateSubtitles = settings[…ValidateSubtitles] != "false";` |
| `TranslationJob.cs:131` | `if (validateSubtitles)` —— 校验入口,在**翻译之前** |
| `SubtitleService.cs:329-336` | 重叠检查:`previousItem != null && item.StartTime < previousItem.EndTime` → `return false` |
| `TranslationJob.cs:171-172` | 记 Warning 并 `throw new TaskCanceledException(…)` |

### 立即处置:关闭字幕校验

⚠️ **不存在名为 "Validation" 的独立标签页。** 正确位置是:

> **Settings → Subtitle** → 页面上标题为 **"Subtitle Validation"** 的卡片 → 顶部开关 **"Enable validation:"** → 切到 Disabled

该卡片与 "Subtitle Settings" 卡片**并排**在同一页的双卡片栅格中(`SubtitlePage.vue:5`),不是独立入口,容易被忽略。菜单项定义见 `SettingPage.vue:86`(`label: 'Subtitle'`)。

卡片自身的说明文字其实已经写明了后果:*"If a validation fails, the translation will be canceled."*

**另外两条途径**(该设置**没有环境变量** —— `StartupService.ApplySettingsFromEnvironment` 的映射表 `:222-268` 不含它,改 compose 无效):

```bash
# 途径 2:HTTP API(SettingController.cs:56)
curl -X POST http://<lingarr>:9876/api/setting \
  -H 'Content-Type: application/json' \
  -H 'X-Api-Key: <key>' \
  -d '{"key":"subtitle_validation_enabled","value":"false"}'

# 途径 3:直接改应用库
sqlite3 /app/config/local.db \
  "UPDATE settings SET value='false' WHERE key='subtitle_validation_enabled';"
```

⚠️ **别改错库**:应用库是 `/app/config/local.db`(`DatabaseConfiguration.cs:94-103`:`SQLITE_DB_PATH` 未设时取 `local.db`,相对路径统一解析到 `/app/config/`),与 `DB_HANGFIRE_SQLITE_PATH` 指向的 `Hangfire.db`(任务队列库)是**两个不同文件**。

**代价**:关闭后会同时失去下表全部 10 项检查。对 Remux / 正规发布组来源的 SRT 风险很低。

**另注**:该开关的**播种默认值本就是 `"false"`**(`M0002_SeedSettings.cs:47`),即出厂关闭。若你撞上此问题,说明它在你的实例上被打开过。

### 调整数值能否代替关闭?不能

`ValidateSubtitle`(`SubtitleService.cs`)共有 **10 个**失败条件,其中**只有 4 个可配置**:

| # | 检查 | 行号 | 可配置? |
| --- | --- | --- | --- |
| 1 | 文件大小 > 上限 | `:246` | ✅ `MaxFileSizeBytes` |
| 2 | 字幕条数 < 2 | `:258` | ❌ 硬编码 |
| 3 | 某条内容为空 | `:272` | ❌ 硬编码 |
| 4 | 长度 < 下限 | `:283` | ✅ `MinSubtitleLength` |
| 5 | 序号不连续 | `:292` | ❌ 硬编码 |
| 6 | `StartTime >= EndTime` | `:302` | ❌ 硬编码 |
| 7 | 长度 > 上限 | `:311` | ✅ `MaxSubtitleLength` |
| 8 | 时长超出范围 | `:321` | ✅ `MinDurationMs` / `MaxDurationSecs` |
| 9 | **与前一条重叠** | `:330` | ❌ **硬编码** |
| 10 | 含控制字符 | `:339` | ❌ 硬编码 |

**本类故障命中第 9 条,而它没有任何对应选项** —— `SubtitleValidationOptions` 的 6 个字段中没有一个与重叠有关。因此无论怎么调数值都绕不过去。

**即便绕过第 9 条,以下两项仍极可能拦下你**(播种默认值见 `M0002_SeedSettings.cs:48-52`):

| 设置 | 播种默认 | 风险 |
| --- | --- | --- |
| `MaxSubtitleLength` | **500** | 若被调低(如 50),一条普通字幕就有 40–80 字符,多行合并后极易超限,正片中几乎必然触发第 7 条 |
| `MaxDurationSecs` | **10** | 对动画偏紧:招牌注释、片头片尾歌词常超 10 秒,触发第 8 条 |

**结论:应当关闭校验,而非调值。** 若确需保留校验,唯一办法是修复字幕文件本身(用 Subtitle Edit 等工具消除重叠),并把 `MaxSubtitleLength` 调回 500 —— 但这意味着每个文件都要人工过一遍,不具备可操作性。

### 为什么不能只关掉重叠检查

`Lingarr.Server/Models/SubtitleValidationOptions.cs` 只有六个字段:`MaxFileSizeBytes`、`MaxSubtitleLength`、`MinSubtitleLength`、`MinDurationMs`、`MaxDurationSecs`、`StripSubtitleFormatting` —— **没有任何与重叠相关的选项**,该检查无条件执行,无法单独豁免。

### 为什么开启 `fix_overlapping_subtitles` 也没用

这是个执行顺序 bug,详见 12.3:该修复在 `TranslationJob.cs:251` 执行,即**翻译完成之后**、作用于 `translatedSubtitles`;而校验在 `:131` 就已经把文件拒掉了。**它永远救不了被校验拒绝的源文件。**

### 评价

字幕重叠在真实世界的 SRT 中**极其常见**(字幕组、特效/双语字幕、OCR 转换结果尤甚)。因单条重叠就拒绝整个文件,且拒绝方式是不可区分的「已取消」,策略过于严苛。考虑到该功能默认关闭,建议保持关闭,除非确有需要。


## 6. 中途失败:模型输出被上下文窗口截断

与前面几节都无关。特征是翻译**已经正常跑了一段**,然后在某个批次上失败:

```
JsonException: '8' is an invalid end of a number. Expected a delimiter.
Path: $[445].position | BytePositionInLine: 26641
  at LocalAiService.TranslateBatchWithJsonParsing … LocalAiService.cs:line 486
```

### Lingarr 没有截断任何东西

必须先澄清这一点,因为报错位置极具误导性:

- `TranslateBatchWithJsonParsing` 用 `ReadAsStringAsync`(`LocalAiService.cs:467`)读取**完整** HTTP body,不存在缓冲区截断
- 随后的 `IndexOf('[')` / `LastIndexOf(']')` 裁剪(`:477-482`)带有 `jsonEnd != -1` 条件 —— 模型输出没有闭合 `]` 时该裁剪**整体跳过**,原始内容原样送进解析器
- 因此 `:486` 解析到的,就是模型产出的原样

**被截断的是模型输出本身。**

### 决定性证据:算术闭合

模型响应的 `usage` 字段:

| 字段 | 值 |
| --- | --- |
| `prompt_tokens` | 9225 |
| `completion_tokens` | 7159 |
| `total_tokens` | **16384** |

**9225 + 7159 = 16384 = 2¹⁴** —— 分毫不差。这是上下文窗口(`num_ctx = 16384`)被填满后的硬切断,不是格式错误,也不是网络问题。

### 批次容量测算

由上述 usage 反推(该批次约 450 行):

| 项 | 计算 | 结果 |
| --- | --- | --- |
| 输入 | 9225 / 450 | ≈ **20 token/行** |
| 输出 | 7159 / 445 | ≈ **16 token/条** |
| 合计 | — | ≈ **36 token/行** |

故 `num_ctx = 16384` 时,450 行需要 ≈ 16200 token —— **正好卡在天花板上,差一点点失败**。

安全批次应控制在窗口的一半以内:`16384 / 36 / 2 ≈ 200` 行,**保守取 50–100**。

> 若 `num_ctx` 不是 16384,按同样方法算:`安全批次 ≈ num_ctx / 36 / 2`。

### 处置

**1. 调小 `max_batch_size` 至 50–100(首选,无副作用)**

UI 位于 **Settings → Services** → Translation 卡片
(`SettingPage.vue:80` 菜单项 `label: 'Services'` → `ServicesPage.vue:5` 挂载 `TranslationSettings.vue`,字段读写在 `:131-134`)

**2. 提高 Ollama 的 `num_ctx` 至 32768(备选)**

通过 Modelfile 或 `OLLAMA_CONTEXT_LENGTH`。代价是内存占用与 CPU 推理耗时同步上升,**不如调小批次划算**。

**3. ⚠️ 遇到此错请立即停掉任务再改设置**

截断抛出的是 `TranslationParseException`,会被 `TranslateBatchAsync` 的重试循环接住(`LocalAiService.cs:288`),**用同样超限的批次重跑 `max_retries`(默认 5)次**,每次消耗一整个上下文窗口的推理,且必然以完全相同的方式失败。CPU 推理下这是数小时的纯浪费。详见 12.6。

### 与既有结论的联动

调小批次会**增加**批次数量,从而更容易触发 **12.1**(取消发生在循环边界时,写出残缺字幕并标记为「已完成」)。两者应一并关注。

---

# ══════════════════════════════════════
# 以下全部针对**路径 B**(Lingarr 自身任务)
# 对应 discussion #505
# ══════════════════════════════════════

## 7. 三种数据库的现状:仓库 vs 已发布

配置方法均位于 `Lingarr.Server/Extensions/ServiceCollectionExtensions.cs`。

| 数据库 | 行号 | `main` 分支现状 | **已发布的 1.3.0** | 长任务安全? |
| --- | --- | --- | --- | --- |
| MySQL | 284 | 仅 `TablesPrefix` | 同 | **否**(且无环境变量可救) |
| PostgreSQL | 309 | `UseSlidingInvisibilityTimeout = true` | **无此行** | main 安全 / 1.3.0 否 |
| SQLite | 335 | `InvisibilityTimeout` 可配置 | **写死 30 分钟** | main 安全 / 1.3.0 否 |

MySQL 分支至今仍是:

```csharp
configuration.UseStorage(new MySqlStorage(connectionString, new MySqlStorageOptions
{
    TablesPrefix = tablePrefix
}));
```

其余全走库默认值,`InvisibilityTimeout` 固定 30 分钟,**没有任何环境变量能改变它** —— 而 MySQL 正是 README 推荐、dev compose 默认使用的数据库。

## 8. 批次大小与请求超时

### `max_batch_size`

出厂播种值为 `"0"`(`M0002_SeedSettings.cs:60`),而 `SubtitleTranslationService.cs:297-300` 对 `<= 0` 的处理是:

```csharp
if (batchSize <= 0)
{
    batchSize = subtitles.Count;
}
```

即**整个字幕文件打包进一次请求**。注意 `TranslationJob.cs:214-217` 的 `10000` 回退只在值无法解析时生效,`"0"` 能正常解析。

⚠️ **本次实例并非该默认值。** 用户日志显示 `Progress: 24% (Subtitle 450 of 1891)`,存在中间进度,说明其 `max_batch_size` 已被改为约 450。因此不能断言用户命中了出厂默认;但对未改过设置的实例,上述行为成立。

对慢速本地模型,建议设为 50–100:单次请求更短、进度可见、失败时损失更小。

### `request_timeout`

定义于 `SettingKeys.cs:103`,默认 `"5"`(`M0002_SeedSettings.cs:65`),单位**分钟**,作用于 `LocalAiService.cs:105` 的 `_httpClient.Timeout`。它管**单次 HTTP 请求**,与 Hangfire 租约正交。

⚠️ 早期版本的本文曾推断「用户多半已调大 `request_timeout`」。**该推断已作废** —— 现已查明取消源是 Bazarr 断开连接,而非 `HttpClient.Timeout` 到期,`request_timeout` 与本次故障无关。

需留意的乘积效应(仅对路径 B):`request_timeout × max_retries × 指数退避`(默认 `5 × 5`,退避 1s→2s→4s→8s)可能在单个批次上消耗远超 30 分钟的墙钟时间,而这段时间对 Hangfire 完全不可见。

## 9. 上游库行为核实

均已比对 `Directory.Packages.props` 锁定版本的源码,非推断。

### Hangfire.Storage.SQLite 0.4.3

- `SQLiteJobQueue.Dequeue` **确实使用** `InvisibilityTimeout`:
  `var dateCondition = DateTime.UtcNow.AddSeconds(_storageOptions.InvisibilityTimeout.Negate().TotalSeconds);`
  随后 `FirstOrDefault(_ => _.Queue == queue && _.FetchedAt < dateCondition)`。
- `SQLiteFetchedJob` **没有任何心跳**:方法只有 `RemoveFromQueue` / `Requeue` / `Dispose`,`FetchedAt` 只在出队时记一次。
- **无滑动超时选项。**

### Hangfire.MySqlStorage 2.0.3

- `InvisibilityTimeout` 默认 30 分钟,`MySqlJobQueue.Dequeue` 仍在读取它:
  `timeout = _options.InvisibilityTimeout.Negate().TotalSeconds`
- `MySqlFetchedJob` 同样**没有心跳**。
- ⚠️ 该属性带 `[Obsolete("Does not make sense anymore. Background jobs re-queued instantly even after ungraceful shutdown now. Will be removed in 2.0.0.")]`。**文案与 2.0.3 实际行为不符** —— 抄自另一个已支持心跳的 provider,而 `MySqlJobQueue` 从未获得心跳。属性仍然生效。

  赋值会产生 **CS0618 警告**(`Lingarr.Server.csproj:13` 只设了 `NoWarn 1591`),需窄范围 `#pragma` 抑制并**必须配注释**,否则后人读到 obsolete 文案会误删而把 bug 改回来。

### Hangfire.PostgreSql 1.21.1

- `InvisibilityTimeout` **未标记 obsolete**,默认 30 分钟。
- `UseSlidingInvisibilityTimeout = true` 会在运行期间刷新 `fetchedat`,窗口从最后一次心跳起算 → 长任务安全。
- ⚠️ setter 调用 `ThrowIfValueIsNotPositive`,传 0 或负数是**启动崩溃**,任何写入必须先保证取值为正。

## 10. 仓库侧仍需完成的工作

1. **新增 `Lingarr.Core/Configuration/JobConfiguration.cs`** —— 与 `DatabaseConfiguration` 同风格的静态类。因 Core 不引用 Hangfire,只返回 `int` / `TimeSpan`。暴露 `DefaultJobTimeoutMinutes`、`MaximumJobTimeoutMinutes = 7 * 24 * 60`、`GetJobTimeoutMinutes()`、`GetJobTimeout()`(**保证恒为正**)。需显式 `using System.Globalization;`(Core 隐式 using 不含它)。
   - 上限理由:`JOB_TIMEOUT_MINUTES=2147483647` 会让 MySQL 生成越界 `INTERVAL`,**整个出队逻辑静默瘫痪**。
2. **`ConfigureHangfire` 中解析一次**,把 `TimeSpan` 传给三个存储方法,使其退化为纯函数。
3. **三个存储方法改造** —— MySQL 补 `InvisibilityTimeout`(含只包一条语句的 `#pragma CS0618` + 注释);Postgres 显式设值并删除从未使用的 `tablePrefix` 死参数;SQLite 改用传入参数。
4. **`LocalAiService.cs:331` 的 `catch` 加过滤** `when (!cancellationToken.IsCancellationRequested)`(见第 3 节)。
5. **新增 `Lingarr.Server.Tests/Configuration/JobConfigurationTests.cs`** —— 采用 `StartupServiceTests` 的强隔离模式(`[Collection]` + `IDisposable` + `DisableParallelization`,集合名不得与 `"StartupServiceEnv"` 撞名)。覆盖未设置 / 有效值 / 非正数 / 非整数 / 超上限钳制 / `GetJobTimeout()` 恒正。
6. **三处文档去掉「sqlite only」**(`Readme.MD:154`、`Settings.MD:33`、`Lingarr.Docs/getting-started/configuration.md:12`),保持既有列对齐(分别为每行 143 / 174 字符)。

## 11. 默认值建议:不应保持 30 分钟

- `ScheduleService.cs:104-114` 在**每次启动时**就会清理孤儿 processing 任务(`monitor.ProcessingJobs` → `BackgroundJob.Delete`)并调用 `ResumeTranslationRequests` 恢复未完成请求。
- Lingarr 本就不重试(`AutomaticRetry(Attempts = 0)`)。
- 因此 `InvisibilityTimeout` 的「崩溃恢复」价值与启动恢复**大量重叠**,唯一独占场景是「进程不重启但 worker 卡死」,相当罕见。
- 代价却不对称:窗口偏小会**误杀正在正常工作的长任务**;窗口偏大只是让真正卡死的任务多占一会儿位置,而 `MAX_CONCURRENT_JOBS` 默认为 1 时用户能立刻察觉。

**建议提高到数小时量级**,保留 7 天上限。

## 12. 排查中发现的其他缺陷

按严重性排序。均已核实,建议各自单独开 issue。

### 12.1 🔴 取消发生在循环边界时,会写出残缺字幕并标记为「已完成」

`SubtitleTranslationService.cs:80-84` 与 `:307-311`:

```csharp
if (cancellationToken.IsCancellationRequested)
{
    _lastProgression = -1;
    break;
}
```

`break` 而非 `throw`,随后 `return subtitles;` 返回**部分翻译**结果。`TranslationJob.Execute` 因此收不到 `OperationCanceledException`,继续执行 `WriteSubtitles` 与 `HandleCompletion` —— **残缺字幕被写入磁盘,请求标记为 `Completed` 100%**,用户看不到任何错误。

设置较小的 `max_batch_size` 后,这条路径更容易被触发。

### 12.2 🔴 租约过期后两个 worker 会并发处理同一请求

`TranslationJob` / `ProofreadJob` 都没有 `[DisableConcurrentExecution]`,而 `TranslationJob.cs:75` 的可运行性检查只跳过 `Completed` / `Cancelled`。仍处于 `InProgress` 的请求,其重新入队的副本会**通过**这层检查。`ProofreadJob` 连这层检查都没有。

且存在竞态:重跑是否被拦下,取决于第一个 worker 的 `HandleCancellation` 是否已把 `Cancelled` 写入数据库。

### 12.3 🔴 `fix_overlapping_subtitles` 与校验的执行顺序颠倒

- 校验在 `TranslationJob.cs:131-173`,**翻译之前**,对源字幕执行
- `FixOverlappingSubtitles` 在 `TranslationJob.cs:251-253`,**翻译之后**,作用于 `translatedSubtitles`

```csharp
// :251
if (settings[SettingKeys.Translation.FixOverlappingSubtitles] == "true")
{
    translatedSubtitles = _subtitleService.FixOverlappingSubtitles(translatedSubtitles);
}
```

于是产生一个荒谬的组合:Lingarr 自带「修复重叠字幕」功能,却会因为源字幕重叠而在该功能运行之前拒绝整个文件。**用户开启该设置对这类失败完全无效**,且从设置名称完全看不出这一点。

修法二选一:把 `FixOverlappingSubtitles` 提前到校验之前、对源字幕执行;或在该设置开启时让校验跳过重叠检查。

### 12.4 🔴 校验失败复用 `TaskCanceledException`,状态与原因双重丢失

`TranslationJob.cs:171-172`:

```csharp
_logger.LogWarning("Subtitle is not valid according to configured preferences.");
throw new TaskCanceledException("Subtitle is not valid according to configured preferences.");
```

该异常被 `:286` 的 `catch (OperationCanceledException)` 捕获 → `HandleCancellation` 写入
`Status = TranslationStatus.Cancelled`、`ErrorMessage = "Translation was cancelled"`。

两处信息同时丢失:

- **状态错**:校验失败是 `Failed`,不是用户取消的 `Cancelled`
- **原因错**:异常自带的说明被硬编码文案覆盖,UI 上只剩「Translation was cancelled」

结果是用户无法从界面得知任何有效信息,必须去翻服务端日志才能定位 —— 本文第三轮排查正是因此而起。

修法:引入独立的校验异常类型,写入 `Failed` 并保留原始原因。

### 12.5 🔴 `ChatResponse` 不解析 `finish_reason` / `usage`,截断无法被识别

`Lingarr.Server/Models/Integrations/Translation/LocalAiResponse.cs:17-31` 的 `ChatResponse` 只有:

```csharp
public class ChatResponse
{
    [JsonPropertyName("choices")]
    public List<ChatChoice> Choices { get; set; } = new();
}
```

`choices[].finish_reason` 与顶层 `usage` **都没有映射**(仓库中另有一个带 `FinishReason` 的 `ChatCompletionResponse.cs:67-68`,但 `LocalAiService` 不用它)。

后果:模型因触及 token 上限而截断时,`finish_reason` 明明等于 `"length"`,Lingarr 却完全看不见,只能把半截 JSON 丢给解析器,抛出指向 `$[445].position` 的 `JsonException` —— 排查者从错误信息中**完全无法推断真实原因**,只能靠比对模型侧的 `usage` 才能定位(见第 6 节)。

修法:补上 `finish_reason` 与 `usage` 两个字段,在 `finish_reason == "length"` 时抛出携带明确原因的异常(例如「输出被 token 上限截断,请调小 max_batch_size」)。

### 12.6 🔴 截断后仍重试 5 次,每次烧掉一整个上下文窗口

截断抛出的 `TranslationParseException` 会被 `TranslateBatchAsync` 的重试循环接住(`LocalAiService.cs:288-300`),**用完全相同的超限批次重跑 `_maxRetries`(默认 5)次**。

由于批次大小未变,每次都必然以同样方式失败,而每次都要完整消耗一次上下文窗口的推理。在 CPU-only 的本地模型上,这意味着数小时的纯浪费。

修法:识别 `length` 截断后**不重试**(它不是瞬时故障);或实现自适应拆批 —— 截断时把批次对半拆开重试,这样既能自愈又不必让用户手工调参。

### 12.7 🟠 `LocalAiService.TranslateAsync` 没有传递取消令牌

`LocalAiService.cs:146-154`:

```csharp
using var retry = new CancellationTokenSource();
using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, retry.Token);
…
return await CompleteWithLocalAiApi(replacements, retry.Token);   // ← 应为 linked.Token
```

`retry` 从未被取消,所以在**单行翻译路径**上取消令牌传不到 HTTP 调用。同文件的 `ProofreadAsync`(`:204`)与 `TranslateBatchAsync`(`:271`)都正确传了 `linked.Token` —— 基本可判定为笔误。

### 12.8 🟡 被取消的 Proofread 显示为「已完成」

`ProofreadJob.cs:206-224` 的 `HandleCancellation` 写入 `TranslationStatus.Completed`、`ErrorMessage = null`、进度 100,只有事件日志记了 `Cancelled`。或许有意(底层翻译确实完好),但与 `TranslationJob` 行为不一致。

### 12.9 🟡 `Interrupted` 状态名不副实

`Interrupted` 只在进程启动时由 `ResumeTranslationRequests`(`TranslationRequestService.cs:636-643`)写入。运行中被抢走租约的任务最终落为 `Cancelled`,与用户手动取消无法区分。

### 12.10 🟡 文档描述了尚未发布的环境变量,且无版本标注

`Readme.MD:154`、`Settings.MD:33`、`Lingarr.Docs/getting-started/configuration.md:12` 三处都把 `JOB_TIMEOUT_MINUTES` 写成可用配置,但**没有任何已发布版本包含它**,也没有「自 X 版本起可用」的标注。

建议:在配置表格中标注最低可用版本;或在功能发版前不合入面向用户的配置文档。

### 12.11 🟡 其他

- `DatabaseConfigurationTests` 修改进程级 `SQLITE_DB_PATH` 却无 `[Collection]`,存在并行竞争隐患。
- `ConfigureMySqlStorage` / `ConfigurePostgresStorage` 各自手搓连接串,而 `DatabaseConfiguration` 已有带校验的等价方法。
- **默认模板不设任何 token 上限**:`LocalAiChatTemplate` 只有 `model` + `messages`,无 `max_tokens` / `num_predict` / `options.num_ctx`,一切听凭后端默认值。
- **`stream` 设置不一致**:`OllamaGenerateTemplate` 显式设了 `stream: false`,而 `LocalAiChatTemplate` 未设。
- **端点类型判定过于脆弱**:`_isChatEndpoint` 仅凭 endpoint 是否以 `completions` 结尾判定(`LocalAiService.cs:99`),指向 Ollama 原生 `/api/chat` 会被误路由到 generate 模板。
- **校验规则可调性不足**:10 条失败条件中 **6 条硬编码**,UI 只暴露 4 项。用户无法针对性放宽单条规则(例如只豁免重叠检查),只能全开或全关。叠加 12.4(失败一律显示为不可区分的 `Cancelled`)与开关位置不直观(藏在 Subtitle 页第二张卡片),使该类问题几乎无法自助定位。
- **`subtitle_validation_enabled` 的默认值不自洽**:`TranslationJob.cs:118` 用 `!= "false"` 判断,意味着设置行**缺失**时校验为**开启**;而 `M0002_SeedSettings.cs:47` 的播种值是 `"false"`(关闭)。二者相反。

## 13. 待决问题

| # | 问题 | 建议 |
| --- | --- | --- |
| 1 | `/api/translate/content` 是否改为异步入队(见第 4 节) | **值得** —— 当前设计使任何慢速后端 + 任何客户端超时的组合都必然失败 |
| 2 | 是否为 #505 单发一个 patch(1.3.1) | 值得 —— 稳定版用户拿不到修复,且文档已把该变量描述为可用(见 12.10) |
| 3 | 修复范围:三库全覆盖 / 只补 MySQL | 三库全覆盖 |
| 4 | 默认值:30 / 提高 | **提高到数小时**(见第 9 节) |
| 5 | 是否保留 7 天上限 | 保留 |
| 6 | `max_batch_size` 出厂默认是否该从 `0` 改掉 | 建议改 —— 对本地模型场景不友好 |
| 7 | 12.1 / 12.2 / 12.3 / 12.4 是否本次一并修 | 单独开 issue,但 12.1 优先级应高于超时本身 |
| 8 | 三种故障全部呈现为 `Cancelled` 且不可区分,是否引入更精确的状态/错误信息 | **值得** —— 与 12.4、12.8(Proofread 显示 Completed)、12.9(`Interrupted` 名不副实)属同一类问题,建议合并为一个「翻译请求状态语义」议题 |
| 9 | 是否实现自适应拆批(截断时对半重试)| **值得** —— 见 12.6。可让 `max_batch_size` 不再需要用户按模型窗口手工估算 |
| 10 | `.doc/` 是否进版本库 | 该目录不在 `.gitignore` 中,会出现在 PR 里 |

---

## 附录 A:`latest` 与 `main` 镜像的区别

仅在采纳方案 1(路径 B)时才有意义 —— `JOB_TIMEOUT_MINUTES` 需要 `:main` 才被读取。

### 版本时间线

| 事实 | 依据 |
| --- | --- |
| 最新稳定版 **1.3.0,2026-08-12 发布** | GitHub releases 页 |
| `JOB_TIMEOUT_MINUTES` 由 `28f6a19` 引入,**2026-09-05** | `git log` |
| `latest` 标签**仅在打 semver tag 时**发布 | `.github/workflows/docker-build.yml:51` |
| 1.3.0 代码中该值**写死** | `git show 00abdc8:…ServiceCollectionExtensions.cs` → 第 365 行 |
| GHCR 上 `main` 为 **1 天前**,`latest`/`1.3.0` 为 **25 天前** | [GHCR 包页面](https://github.com/lingarr-translate/lingarr/pkgs/container/lingarr),观测于 2026-09-06 |

三条时间线互相印证:25 天前 = 2026-08-12(1.3.0 发布日);1 天前 = 2026-09-05(`28f6a19` 提交日)。

### 出处

**a) `Readme.MD:34-42`**,标题 `### Docker Image Tags`
→ <https://github.com/lingarr-translate/lingarr#docker-image-tags>

```
| `latest` | Latest stable release | `amd64` `arm64` |
| `main` | ⚠️ Development build from main branch | `amd64` `arm64` |
```

**b) `Lingarr.Docs/getting-started/installation.md:7-13`** —— 同一张表,逐字相同。

> 此处刻意不给 lingarr.com 的具体网址:`.vitepress/config.mts` 未配置 `base`,`docs.yml` 也未注入,仓库内无 CNAME;直接抓取返回 403(Cloudflare),既不能证实也不能证伪。

**c) `.github/workflows/docker-build.yml:48-51`** —— 文档会漂移,这里才是事实来源。

⚠️ README 那句 "Latest stable release" 措辞没错,但读者**无法从中推断出**「`latest` 可能落后 `main` 一个月,因而缺少已写进文档的环境变量」—— 见 12.10。

### registry 不是标签

`ghcr.io/lingarr-translate/lingarr` 与 `lingarr/lingarr` **是同一个镜像**,只是发布到两个 registry。`docker-build.yml` 中只有一个 metadata 步骤和一个 build-push 步骤,两个地址共用同一份 `tags:`,一次构建同时推送 —— 同一个 digest。

差异仅在 registry 层面:Docker Hub 有匿名拉取限流,GHCR 没有。**换 registry 不改变任何行为,要改的是标签。**

---

## 附录 B:关键文件

| 文件 | 角色 |
| --- | --- |
| `Lingarr.Server/Controllers/TranslateController.cs` | 路径 A 入口(115)、路径 B 入口(47) |
| `Lingarr.Server/Services/TranslationRequestService.cs` | `TranslateContentAsync`(736)、日志行(826)、入队(180) |
| `Lingarr.Server/Services/Translation/LocalAiService.cs` | 吞掉取消的 catch(331)、令牌笔误(154)、HTTP 超时(105)、堆栈落点(396) |
| `Lingarr.Server/Services/SubtitleTranslationService.cs` | 批次切分(297)、静默 break(80 / 307)、**逐批落库 `EmitLines`(340)**、进度日志(537) |
| `Lingarr.Server/Extensions/ServiceCollectionExtensions.cs` | 三个存储配置方法(284 / 309 / 335) |
| `Lingarr.Client/src/services/translateService.ts` | 证明前端不使用路径 A |
| `Lingarr.Server/Services/SubtitleService.cs` | 字幕校验、重叠检查(329-336) |
| `Lingarr.Server/Models/SubtitleValidationOptions.cs` | 校验选项(无重叠开关) |
| `Lingarr.Client/…/settings/ValidationSettings.vue` | 校验开关 UI(卡片标题 2,开关 13-19,读写 134-141) |
| `Lingarr.Client/src/pages/settings/SubtitlePage.vue` | 校验卡片的挂载点(5) |
| `Lingarr.Client/src/pages/SettingPage.vue` | 设置菜单项定义(86,`label: 'Subtitle'`) |
| `Lingarr.Server/Controllers/SettingController.cs` | 设置写接口(56),兜底途径 2 |
| `Lingarr.Server/Services/ProgressService.cs` | `EmitLine`(42)、`EmitLines`(78)—— 译文写入 `TranslationRequestLines` 表 |
| `Lingarr.Server/Services/MediaService.cs` | arr id 解析(154-160)、404 兜底**全量重同步**(186-204) |
| `Lingarr.Server/Jobs/TranslationJob.cs` | 复用已落库行(188-205)、字幕校验(169)、读盘(184)、写盘(308-325,**无视 `TranslatedSubtitlePath`**) |

### Bazarr 侧(本次新增,详见 [`bazarr_lingarr_integration.md`](./bazarr_lingarr_integration.md))

| 文件 | 角色 |
| --- | --- |
| `bazarr/subtitles/tools/translate/services/lingarr_translator.py` | **`timeout=1800`(160)—— 本次故障的真正源头**;整文件级重试(113);假进度(50);空数组当成功(166-171);`arrMediaId` 传错(134-137) |
| `bazarr/subtitles/tools/translate/main.py` | 入队即返回(20-24)、输出路径(35-52)、失败也写过去式任务名(88-91) |
| `bazarr/subtitles/tools/translate/core/translator_utils.py` | `default_score` 换算(56-63) |
| `bazarr/subtitles/tools/translate/services/gemini_translator.py` | 对照组:分批(508-528)、批级重试(343)、落盘续传(185-202) |
| `bazarr/app/jobs_queue.py` | 全局并发闸门(519)、任务去重(622-623)、失败处理(581-586) |
| `bazarr/app/config.py` | translator 段(204-211)、`concurrent_jobs` 默认值(174) |
| `frontend/src/pages/Settings/General/index.tsx` | Concurrent Jobs 下拉,**上限硬钉在 CPU 核数**(156-161) |
