# 设计:已译上下文 + `/content` 复用单条翻译路径

> 代码基准:分支 `feature/personalized`,HEAD `28f6a19`
> 状态:**已实现,第二轮改版已实现**(2026-09-08,见第 10、11 节)
> 出处:[`prompt_and_batch_behavior.md`](./prompt_and_batch_behavior.md) 第 8 节改进项 8.3 / 8.4 / 8.5

---

## 1. 背景与目标

| 改进项 | 现状问题 | 目标 |
| --- | --- | --- |
| **8.5** | `BuildContext` 只读 `Lines` / `PlaintextLines`(原文),**从不读 `TranslatedLines`**。模型看不到自己此前的译法,人名、称谓、专有名词的跨行一致性没有任何强化机制 | `contextBefore` 可携带已翻译内容,且**句对句一一对应** |
| **8.3** | `/content` 单条分支构造 `TranslateAbleSubtitleLine` 时未给 `ContextLinesBefore` / `ContextLinesAfter` 赋值,该分支从不调用 `BuildContext` | `/content` 与 `/file` 上下文能力对齐 |
| **8.4** | `/content` 单条分支构造 `SubtitleTranslationService` 时未传 `_progressService`(`:872`),与批量分支(`:829`)不一致 | 消除不一致 |

**明确要求**:已译上下文必须是**句对句配对**,而不是「一坨原文 + 另一坨译文」—— 后者要求模型自行对齐两个列表,容易错位。

---

## 2. 🔴 硬约束:不能动插件契约

这条约束决定了整个方案形态,必须先讲。

`ITranslationService.TranslateAsync` 位于 **`Lingarr.Contracts`** —— 这是**插件面向**的公共契约:

```csharp
Task<string> TranslateAsync(
    string text,
    string sourceLanguage,
    string targetLanguage,
    List<string>? contextLinesBefore,
    List<string>? contextLinesAfter,
    CancellationToken cancellationToken);
```

而 `PluginLoader` 强制校验主版本号:

```csharp
private const int HostMajorVersion = 1;              // PluginLoader.cs:15
...
if (versionAttribute.Major != HostMajorVersion)      // PluginLoader.cs:88
{
    // 跳过该插件
}
```

外部插件编译时依赖 `Lingarr.Contracts`。**给 `TranslateAsync` 增加参数,会让所有已发布插件不再满足新接口,加载即失败** —— 这是大版本破坏性变更,不能为一个可选功能付出。

### 推论

已译上下文**必须搭载在现有的 `contextLinesBefore` 列表内**。由 `SubtitleTranslationService` 决定往这个列表里放什么内容,provider 侧零改动、插件零影响。

---

## 3. 方案选型

| 方案 | 结论 | 理由 |
| --- | --- | --- |
| 新增 `{translatedContextBefore}` 占位符 | ❌ **否决** | 该占位符的值同样要送达 provider,而唯一通道是 `TranslateAsync` 的参数 —— 撞上第 2 节约束 |
| 新增设置开关,切换 `{contextBefore}` 的**内容** | ✅ **采纳** | 零契约改动;provider、插件、`BaseLanguageService` 全部不动 |

**采纳方案的代价**:同一个占位符 `{contextBefore}` 在开关不同状态下含义不同(纯原文 vs 原文+译文配对)。这是隐式行为,**必须靠 UI 帮助文案弥补**,否则用户无从知晓。设计上这是为兼容性付出的可接受代价。

---

## 4. 已译上下文的设计

### 4.1 设置

| 项 | 值 |
| --- | --- |
| 键名 | `ai_context_use_translated` |
| 类型 | 布尔字符串(`"true"` / `"false"`,与仓库其余布尔设置一致) |
| 默认 | **`"false"`** —— 不改变任何存量用户的行为 |
| 条数 | 复用现有的 `ai_context_before`,不新增 |

#### 迁移做什么、影响谁

**迁移的全部内容就是往 `settings` 表插一行**,没有 `ALTER TABLE`、不建表、不转数据(照抄 `M0011_SeedPreserveLineBreaks.cs`,全文 17 行):

```csharp
[Migration(21)]
public class M0021_SeedAiContextUseTranslated : Migration
{
    public override void Up()
    {
        Insert.IntoTable("settings").Row(new { key = "ai_context_use_translated", value = "false" });
    }

    public override void Down()
    {
        Delete.FromTable("settings").Row(new { key = "ai_context_use_translated" });
    }
}
```

Lingarr 每个配置项都以此方式注册(`preserve_line_breaks`、`use_batch_translation`、`max_batch_size` 皆然)。执行时机为应用启动时自动运行(`ApplicationBuilderExtensions.cs:59` → `MigrationConfiguration.RunMigrations` → `runner.MigrateUp()`)。

**影响面 —— 三种情形均安全:**

| 情形 | 影响 |
| --- | --- |
| 升级到含该迁移的版本 | 启动时插入一行 `value = "false"`。**`false` 即现有行为,零行为变更**;用户不主动去 UI 打开开关就毫无感知 |
| 不升级 | 迁移不会执行,完全不受影响 |
| 升级后回滚到旧镜像 | **安全**。`MigrateUp()` 只执行 VersionInfo 中未记录的迁移,旧代码不含 21 号类,既不会触碰也不会报错;多出的那行对旧代码是**惰性**的 |

回滚安全的依据:全链路的设置读取都按**显式键名**过滤 —— `FirstOrDefaultAsync(s => s.Key == key)`(`SettingService.cs:60`、`:133`)、`Where(s => keysToFetch.Contains(s.Key))`(`:96-97`);前端同样发送明确的 key 列表(`store/setting.ts:95`)。**不存在任何 `SELECT *` 式读取**,因此一行未知配置不会干扰任何逻辑。

#### 🔴 迁移不可省的两个独立理由

1. **读取会抛异常** —— `SettingService.GetSettings` 只为命中的键写入 `result[key]`,缺失的键不进字典,`TranslationJob` 中的 `settings[新键]` 抛 `KeyNotFoundException`。
2. **UI 根本存不了这个设置(决定性)** —— `SettingService.SetSetting`(`:131-142`)在行不存在时直接失败,**不会创建**:

   ```csharp
   var setting = await _dbContext.Settings.FirstOrDefaultAsync(s => s.Key == key);
   if (setting == null)
   {
       return false;
   }
   ```

   `SettingController` 收到 `false` 会返回 `BadRequest("Setting not found or could not be updated.")`。**没有种子行,用户在 UI 里拨动新开关会直接报错**,功能完全不可用。

第 2 条比第 1 条更硬:即便把 `settings[key]` 改成 `TryGetValue` 带默认值以绕开异常,UI 依然无法保存。**因此迁移必须与读取代码同版本发布**,不可拆开。

> **实现约束:开关经由构造函数传入,不要给 `TranslateSubtitles` 加参数。**
> 在 `SubtitleTranslationService` 构造函数尾部追加可选参数 `bool useTranslatedContext = false`,`TranslateSubtitles` 的签名保持不动。
> 给方法加参数会踩到编译期陷阱(11 处测试调用的 `CancellationToken` 是位置实参),**详见 8bis 发现 2 —— 动手前务必先读**。

### 4.2 渲染格式

> ⚠️ **本节及 4.3、4.4、4.5 已被第 11 节取代**(实测后改为 JSON 行格式)。保留原文以记录当时的推理。

**已定稿:采用 `[SOURCE]` / `[TRANSLATION]` 标签配对。**

```
[SOURCE] Detective Mouri is on the case.
[TRANSLATION] 毛利侦探正在查这个案子。
[SOURCE] But he is drunk again.
[TRANSLATION] 但他又喝醉了。
```

`GetReplacements` 本就以 `"\n"` 连接上下文列表:

```csharp
["contextBefore"] = string.Join("\n", contextLinesBefore ?? [])
```

所以只要让 `BuildContext` 的每个列表元素是一组 `[SOURCE]…\n[TRANSLATION]…`,上述形态天然成立 —— **`BaseLanguageService` 一行都不用改**。

**多行字幕的拼接规则**:原文侧沿用现有做法 `string.Join(" ", stripSubtitleFormatting ? PlaintextLines : Lines)`;译文侧对应地用 `string.Join(" ", TranslatedLines)`。`preserveLineBreaks` 开启时 `TranslatedLines` 可能含多个元素,**必须一并合并为单行**,否则配对块的行结构会错乱(模型会看到一个 `[SOURCE]` 对应多行无标签文本)。

#### 为何不用更紧凑的箭头单行式

曾考虑 `原文 → 译文` 的单行写法(每组省约 6 tokens),**已否决**。理由按重要性排序:

1. **避免模型续写示范模式(决定性理由)** —— 箭头式本质是一组「转换示范」。连续出现两三组 `原文 → 译文` 之后,紧接着要求模型翻译新的一行,模型有相当概率把 `原文 → 译文` 这个形态一并续写进输出。而 `ai_prompt` 明确要求「只输出译文,不要任何额外内容」,两者直接冲突。
   `[SOURCE]` / `[TRANSLATION]` 是方括号结构标记,读起来像**格式框架**而非待续写的模式,被带进输出的概率明显更低。
2. **角色显式,不依赖位置推断** —— 英译中时「左边是原文」容易猜对;但葡译西、简中转繁中这类**相近语言对**,箭头两侧谁是原文全靠模型自行判断,并不可靠。标签把角色写死,不留推断空间。
3. **边界不受正文内容影响** —— 字幕正文本身可能出现箭头(路牌、方向指示、特效字幕),此时箭头式的分隔符产生歧义。标签形式不存在该问题。
4. **仓库既有实证** —— 校对功能的默认 `proofread_user_prompt` 用的就是 `[SOURCE] {sourceLine}\n[TRANSLATION] {translatedLine}`,已在生产运行,格式一致亦降低用户认知成本。

**代价**:每组约多 6 tokens,`ai_context_before = 2` 时每次请求约多 15 tokens。依 9.3 的决定,不就此向用户作任何提示。

### 4.3 行为矩阵

| 模式 | 侧 | 该行状态 | 开关关闭 | 开关开启 |
| --- | --- | --- | --- | --- |
| 单条 | before | 已翻译 | 纯原文 | **`[SOURCE]` + `[TRANSLATION]` 配对** |
| 单条 | before | 未翻译 | 纯原文 | **纯原文(回退)** |
| 单条 | after | 恒未翻译 | 纯原文 | **纯原文(恒定)** |
| 批量 | — | — | 空串 | **空串(不受影响)** |

两处需重点标注:

- **未译行自动回退为纯原文** —— 无需额外分支,判据就是 `TranslatedLines.Count > 0`
- **`contextAfter` 恒为原文** —— 那些行按定义尚未翻译,不存在可配对的译文

### 4.4 混合语言的后果

开启后,模型在同一次请求中会看到:

- `contextBefore` —— **目标语言**(配对中含原文,但译文部分是目标语言)
- `contextAfter` —— **源语言**

这是设计上无法避免的不对称(after 侧根本没有译文)。配对格式本身带 `[SOURCE]` / `[TRANSLATION]` 标签,已在很大程度上帮模型消除歧义 —— 这也是选择配对而非两个独立块的额外好处。

### 4.5 开关帮助文案(实现轮直接照此撰写)

新开关的说明文字**必须**覆盖以下三点,缺一不可:

| # | 要点 | 原因 |
| --- | --- | --- |
| 1 | 开启后 `{contextBefore}` 输出的是**原文 + 译文配对**,而非纯原文 | 同一占位符含义随开关变化,是本方案为兼容插件契约付出的代价(第 3 节),必须显式告知 |
| 2 | `contextAfter` **仍为原文**(那些行尚未翻译),模型会看到混合语言 | 避免用户困惑于前后语言不一致(本节) |
| 3 | **必须在 user prompt 中写入 `{contextBefore}` 才生效**,否则开关静默无效 | 上下文本身是 opt-in(第 5 节);不加此说明,多数用户会以为开关坏了 |

### 4.6 时序正确性

无需任何额外同步,两条数据来源都天然就绪:

| 来源 | 位置 | 说明 |
| --- | --- | --- |
| 本轮循环中已翻译的行 | `SubtitleTranslationService.cs:144` 赋值 `subtitle.TranslatedLines` | 该赋值位于循环体末尾,而下一轮的 `BuildContext`(`:104`)在其之后执行 —— 读到的必然是已写入的值 |
| 断点续传恢复的行 | `TranslationJob.cs:188` 起从 `TranslationRequestLines` 预填 | 循环开始前就已就位 |

---

## 5. 前提:上下文本身是 opt-in 的

**这一点必须写在实现之前,否则新功能会「看起来没生效」。**

上下文抵达模型的唯一途径,是 prompt 中的 `{contextBefore}` / `{contextAfter}` 占位符。而两个默认 prompt **都没有引用它们**:

- `ai_prompt` 默认值:`Translate from {sourceLanguage} to {targetLanguage}, …` —— 无 context 占位符
- `ai_user_prompt` 默认值:`{lineToTranslate}` —— 无 context 占位符

所以**开箱状态下 `ai_context_before` / `ai_context_after` 不产生任何效果**:上下文被算出来、拼好、放进字典,然后因为没人引用而被丢弃。

这是**有意设计**,不是缺陷 —— `TranslationPrompt.vue` 的 user prompt 编辑器已把两者列为可插入占位符,并附有说明文字("Use the context placeholders to include lines before and after the current subtitle…")。

### 决定:本次不改默认 prompt

理由:修改 `ai_prompt` / `ai_user_prompt` 的播种值会改变**所有存量用户**的翻译结果,风险与收益不匹配。用户须自行把 `{contextBefore}` 写入 user prompt,新开关才可见 —— 此点需在开关的帮助文案中直接点明。

### 处置定稿:仅以文案告知

曾考虑降低使用门槛(开关旁提供一键插入 `{contextBefore}` 的按钮、或在开启开关时校验 user prompt 并给出提示),**均不实现**。

本次只在开关说明文案中把这一前提讲清楚(见 4.5 第 3 点),不引入任何额外的 UI 交互或校验逻辑。

---

## 6. `/content` 复用 `TranslateSubtitles`(8.3 + 8.4 合并解决)

### 6.1 现状

`TranslationRequestService.TranslateContentAsync` 的 else 分支(`:867-911`)自写了一个逐行循环,与 `SubtitleTranslationService.TranslateSubtitles` 功能重复,但缺少后者的上下文构建、重复时间轴去重缓存与进度日志。

### 6.2 改法

1. 把批量分支已有的 `subtitleItems` 构造(`Lines` → `List<SubtitleItem>`,`:840-844`)**提取到 if/else 之前**共用
2. ⚠️ **在该构造中补 `StartTime = EndTime = item.Position`** —— 见 8bis 发现 1,**这两行不可省略,否则去重缓存会静默合并全文重复行并绕过上下文**
3. else 分支改为调用 `TranslateSubtitles(...)`,删除现有逐行循环
4. 构造 `SubtitleTranslationService` 时**传入 `_progressService`** —— 这是**必需项**,`TranslateSubtitles` 在 `:61` 对 null 直接抛异常(顺带即解决 8.4)
5. ⚠️ **在 `TranslateSubtitles` 返回后立刻插入 `cancellationToken.ThrowIfCancellationRequested();`** —— 见 8bis 发现 3,**不可省**
6. 结果映射复用批量分支的 `subtitleItems.Select(...)` 形态
7. `:772` 的 `GetSettings([...])` 补入 `AiContextBefore`、`AiContextAfter` 与新键

**白拿的收益**:上下文能力、`Progress: N%` 日志、以及少维护一套循环逻辑。

> 注意:去重缓存**不**属于收益。它是为 fansub `.ass` 同时间轴堆叠行设计的,在 `/content` 场景下必须靠第 2 步压制 —— 详见 8bis。

### 6.3 进度上报的差异(专项说明)

改用 `TranslateSubtitles` 后,进度上报行为会变。逐项拆开:

| | 现状(`:906-907` 自写循环) | 改后(`EmitProgress` `:531`) |
| --- | --- | --- |
| `EmitLine`(逐行译文数据) | 每行一次 | 每行一次 —— **完全不变** |
| `Emit`(百分比进度) | 每行**无条件**发一次 | 仅当整数百分比**变化时**才发 |
| 1891 行文件的 `Emit` 条数 | **1891 条** | **≤ 101 条**(0…100) |

节流逻辑:

```csharp
// SubtitleTranslationService.cs:531
var progress = (int)Math.Round((double)iteration * 100 / total);
if (progress != _lastProgression)
{
    _logger.LogInformation($"Progress: {progress}% (Subtitle {iteration} of {total})");
    await _progressService!.Emit(request, progress);
    _lastProgression = progress;
}
```

**结论:零信息损失,是净收益而非代价。**

`Emit` 的载荷只有一个整数百分比加请求状态(`ProgressService.cs:27-39`),而百分比只有 101 种可能取值 —— 现状那 1891 条广播里约 **1790 条载荷完全重复**。节流后前端进度条表现一致,SignalR 广播量减少约 95%。

附带好处:`EmitProgress` 会输出 `Progress: N% (Subtitle X of Y)` 日志,而当前 `/content` 单条分支没有这行 —— 排查时终于能从日志看到进度。

> 📝 **自我更正**:设计讨论初期我曾把这一项列为「代价」。经核对 `EmitLine` 未受影响、且 `Emit` 载荷本身只有 101 种取值,该判断有误,此处更正。

---

## 7. 改动面清单

### 需要改动

| 文件 | 改动 |
| --- | --- |
| `Lingarr.Migrations/Migrations/M0021_SeedAiContextUseTranslated.cs` | **新建**,`[Migration(21)]`(当前最大为 20),照抄 `M0011_SeedPreserveLineBreaks.cs` 形态 |
| `Lingarr.Core/Configuration/SettingKeys.cs` | 在 `AiContextAfter`(`:92`)之后新增常量 |
| `Lingarr.Server/Services/SubtitleTranslationService.cs` | 构造函数尾部加可选参数 `bool useTranslatedContext = false` + 同名字段;`BuildContext`(`:498`)由 `private static` 改为 `private` 并读该字段,before 侧生效、after 侧恒为原文。**`TranslateSubtitles`(`:52`)签名保持不动** —— 见 8bis 发现 2 |
| `Lingarr.Server/Jobs/TranslationJob.cs` | `:91` 的设置数组补键,按 `== "true"` 解析,传入 **`:183` 的构造函数**(而非 `:240` 的方法调用) |
| `Lingarr.Server/Services/TranslationRequestService.cs` | `:772` 设置数组补 **3 个**键(`AiContextBefore`、`AiContextAfter`、新键 —— 现仅 5 个);`:840-844` 的 `SubtitleItem` 构造补 **`StartTime = EndTime = item.Position`**(见 8bis 发现 1,**不可省**);`:867-911` 按第 6 节重构,构造服务时传入 `_progressService` 与新开关 |
| `Lingarr.Client/src/ts/setting.ts` | `SETTINGS` 常量(`:46` 附近)+ `ISettings` 字段(`:116` 附近)各加一项(类型均为 `string`)。已核实前端仅此两处,store 对缺失键**无兜底默认值**,进一步印证种子行必需 |
| `Lingarr.Client/…/settings/TranslationPrompt.vue` | 在两个 context 数字框之后加 `ToggleButton`(需新增 import),置于 `v-if="useBatchTranslation !== 'true'"` 块内 |

⚠️ **迁移是必需的,且必须与读取代码同版本发布**,两个独立理由:

1. `SettingService.GetSettings` 只回填已存在的行,缺行会让 `TranslationJob` 的 `settings[key]` 抛 `KeyNotFoundException`
2. `SettingService.SetSetting` 对缺失行**直接 `return false` 而不创建**,导致 `SettingController` 返回 `BadRequest` —— **用户在 UI 里拨动开关会直接报错**

详见 4.1「迁移做什么、影响谁」。该迁移仅插入一行配置,无 schema 变更,升级零行为变更、回滚亦安全。

### 不需要改动

| 位置 | 原因 |
| --- | --- |
| `StartupService.ApplySettingsFromEnvironment` 环境变量映射 | 参照 `use_batch_translation`、`ai_context_before` —— 它们都不在映射表中,属纯 UI 设置 |
| `SettingChangedListener` | 该设置仅在任务启动时读取,无需触发任何副作用 |
| `Lingarr.Migrations.Tests` | 其断言针对固定 key 与固定 `MigrateDown` 目标,不受新增迁移影响 |
| `BaseLanguageService` | 配对字符串由 `BuildContext` 产出,现有 `string.Join("\n", …)` 已足够 |
| provider 实现与外部插件 | 契约未变(第 2 节) |
| `SubtitleTranslationServiceTests` 的 **11 处**既有 `TranslateSubtitles` 调用 | 方法签名不变 ⇒ 零改动(8bis 发现 2)。**实现时不要顺手去改它们** |
| `SubtitleTranslationServiceTests` 的 **6 处**既有构造点(`:94/130/537/562/590/623`) | 新参数可选且在尾部 ⇒ 零改动 |
| `Lingarr.Server/Controllers/TranslateController.cs:90` | 2 参构造,可选参数在尾部 ⇒ 零改动 |
| `Lingarr.Server/Jobs/ProofreadJob.cs` | 已核实**不使用** `SubtitleTranslationService`,与本次改动无关 |
| `TranslateSubtitlesBatch` 路径 | 已核实**不读** `StartTime` / `EndTime`,不受时间戳赋值影响 |

---

## 8. 测试计划

在既有的 `Lingarr.Server.Tests/Services/SubtitleTranslationServiceTests.cs` 中补充(沿用 xunit.v3 + `NullLogger<T>.Instance` + `Mock<T>` 约定)。

⚠️ **三个前置条件 —— 现有测试基建无法直接支撑新用例**:

| # | 现状 | 需要做什么 |
| --- | --- | --- |
| 1 | `CreatePerLineHarness` 的 `TranslateAsync` mock **丢弃**了上下文实参(签名里是 `List<string>? _, List<string>? _`),无法观测 | 加一个能捕获 `contextLinesBefore` 的 harness 变体,或用 `Mock.Verify(… It.Is<List<string>>(c => …))` |
| 2 | 现有用例**全部只有一条字幕**(`Subtitle(1, …)`),contextBefore/After 恒为空 —— **当前对上下文零覆盖** | 新用例必须构造多条字幕的列表 |
| 3 | `Subtitle()` 辅助方法**不设** `StartTime` / `EndTime`(恒为 0) | 发现 1 的回归用例需要显式时间戳,应加一个带时间戳的重载 |

用例:

| 用例 | 期望 |
| --- | --- |
| 开关关闭 | 行为与现状完全一致,输出纯原文 |
| 开关开启,前序行**已翻译** | 输出 `[SOURCE]` / `[TRANSLATION]` 配对 |
| 开关开启,前序行**未翻译** | 回退为纯原文 |
| 开关开启,`contextAfter` | 恒为纯原文,不受开关影响 |
| **`/content` 场景:多行同文本、时间戳均由 Position 赋值** | **每行都独立调用翻译,不被去重缓存合并** —— 这是 8bis 发现 1 的回归防线,必须有 |

## 8bis. 设计复核

对本方案做整体复核(可行性 + 最小改动面)的结论。主体成立,但有两项必须回填的修正。

### 🔴 发现 1:去重缓存会过度合并,直接抵消本次要加的上下文能力

`TranslateSubtitles` 内有一个翻译去重缓存,键为 `$"{StartTime}|{EndTime}|{text}"`(`SubtitleTranslationService.cs:94`、`:123`)。其设计意图见代码注释:应对 fansub `.ass` 文件在**同一时间轴**上堆叠多条同文本 Dialogue 行(阴影/描边/主层)。SRT/VTT 几乎不共享时间轴,故对 `/file` 路径基本是空操作。

**问题**:`/content` 构造 `SubtitleItem` 时只设 `Position` / `Lines` / `PlaintextLines`(`TranslationRequestService.cs:840-844`),`StartTime` 与 `EndTime` 保持 `int` 默认值 **0**。一旦 else 分支改调 `TranslateSubtitles`,所有行的缓存键退化为 `0|0|<文本>`。

完整因果链:

```
SubtitleItem 未设时间戳
  → StartTime = EndTime = 0
  → 缓存键退化为 0|0|<文本>
  → 全文件范围内任意重复文本键碰撞
  → 命中缓存后直接 continue,跳过 TranslateSubtitleLine
  → 该行根本没走翻译,自然也用不上上下文
```

两层后果:

1. **行为回归** —— `/content` 现状是逐行独立翻译。改后重复行(「Yeah.」「What?」这类短句)会被静默合并,只翻一次
2. **自相矛盾** —— 缓存命中发生在调用翻译**之前**,同一文本在不同上下文中本就应当允许译法不同。这恰好抵消了 8.5 要加的能力

**修正**:构造 `SubtitleItem` 时补 `StartTime = EndTime = item.Position`(共 2 行,见 6.2 第 2 步)。

已核实该路径中 `StartTime` / `EndTime` **仅**用于上述两处缓存键,再无其他读取点,故此改动安全且充分。批量分支不使用该缓存,不受影响。

> ⚠️ 给实现者:这两行看起来像是多余的样板赋值,**不要删**。删掉即触发上述回归,且失败是静默的(译文看起来正常,只是丢了上下文与差异化译法)。

### 🟢 发现 2:开关走**构造函数**而非方法参数 —— 全部调用点零改动

初版设计打算给 `TranslateSubtitles` 加参数。复核发现该方案有一个**编译期陷阱**,改走构造函数可完全规避。

#### 陷阱:11 处测试调用的 `CancellationToken` 是位置实参

`SubtitleTranslationServiceTests.cs` 中的 11 处调用(`:147/174/203/229/259/283/316/342/367/394/604`)形态统一为:

```csharp
await harness.Service.TranslateSubtitles(subtitles, NewRequest(),
    stripSubtitleFormatting: false,
    preserveLineBreaks: false,
    contextBefore: 0,
    contextAfter: 0,
    CancellationToken.None);        // ← 位置实参,不是具名
```

前面几个是具名实参,**最后的 `CancellationToken.None` 是位置实参**。因此:

| 新参数插入位置 | 后果 |
| --- | --- |
| `contextAfter` 与 `cancellationToken` **之间**(即 C# 惯例的「CancellationToken 放最后」) | `CancellationToken.None` 会落到 `bool` 形参上 → **11 处全部编译失败** |
| `cancellationToken` **之后** | 能编译,但把 `bool` 排在 `CancellationToken` 后面违反惯例,后人「顺手修正」即触发上面那一行 |

也就是说,方法参数方案要么当场炸,要么埋一颗雷。

#### 采用方案:加到构造函数

现有构造函数:

```csharp
public SubtitleTranslationService(
    IReadOnlyList<TranslationServiceEntry> services,
    ILogger logger,
    IProgressService? progressService = null)
```

追加第 4 个可选参数 `bool useTranslatedContext = false`,全部收益:

| 项 | 结果 |
| --- | --- |
| `TranslateSubtitles` 签名 | **完全不动** → 11 处方法调用零改动 |
| 生产构造点 4 处(`TranslateController.cs:90` 2 参、`TranslationJob.cs:183` 3 参、`TranslationRequestService.cs:829` 3 参、`:872` 3 参) | 全部不受影响,可选参数在尾部 |
| 测试构造点 6 处(`:94/130/537/562/590/623`) | 全部不受影响 |
| 签名惯例 | 可选参数在尾部,不涉及 `CancellationToken`,无陷阱 |

语义上也更贴切:该开关来自设置、每个翻译任务读一次,属于**实例级配置**,与已有的 `_progressService` 同性质。

**连带调整**:`BuildContext` 由 `private static` 改为 `private`(实例方法)以读取该字段。它仅被 `:104-105` 调用,无任何外部波及。

### 🔴 发现 3:取消语义不等价,重构会污染统计数据

两条循环对取消的处理**不同**,重构时必须补偿。

| | 现有 `/content` 循环(`:876-908`) | `TranslateSubtitles`(`:79-84`) |
| --- | --- | --- |
| 循环内取消检查 | **无** | `if (cancellationToken.IsCancellationRequested) { _lastProgression = -1; break; }` |
| 取消如何浮现 | 靠 `TranslateSubtitleLine` 的 `ThrowIfCancellationRequested()`(`:194`)**抛出** | 边界处静默 `break`,返回**部分结果** |

现状下取消一定以异常形式浮现,被 `:919` 的 `catch (OperationCanceledException)` 接住 → 置 `Cancelled` → 重新抛出。重构后,若取消恰好落在两行之间的**循环边界**(而非某次 HTTP 调用进行中),`TranslateSubtitles` 会静默返回部分结果,流程继续往下走。

#### 实际后果:真实但有限

后续 `HandleAsyncTranslationCompletion` 内部把 `cancellationToken` 传给了 `ExecuteUpdateAsync`,令牌既已取消,该调用会抛 `OperationCanceledException`,最终仍落到 `:919` 置 `Cancelled` 并重新抛出。**因此最坏情况(Bazarr 收到 200 OK 并写入半截字幕文件)不会发生。**

但仍有两处实际损害:

1. **统计数据被污染** —— `HandleAsyncTranslationCompletion` 的第一步 `_statisticsService.UpdateTranslationStatisticsFromLines(...)` **不接收 CancellationToken**(已核实其签名),会先于上述抛出点执行完毕,把**部分结果**计入翻译统计
2. **正确性依赖巧合** —— 取消得以浮现,靠的是下游一个恰好接收令牌的数据库调用,而非设计意图。日后若有人给该调用去掉令牌,或调整两步顺序,静默部分成功就会真的发生

#### 修正:一行

在 `TranslateSubtitles` 返回后立即:

```csharp
cancellationToken.ThrowIfCancellationRequested();
```

即可精确还原现状语义:取消必定抛出、统计不被触及、意图显式。

> 注:`TranslateSubtitles` 内部这个 `break` 本身是既有缺陷(见 `prompt_and_batch_behavior.md` 姊妹文档 `fix_long_running_timeout.md` 的 12.1 —— 会导致 `/file` 路径写出残缺字幕并标记为「已完成」)。**本次不修它**,只在 `/content` 侧做补偿,避免把该缺陷扩散到新路径。彻底修复属独立议题。

### ✅ 迁移确实必需,不可省

已核对 `SettingService.GetSettings` 实现:它只为**在缓存或数据库中命中的**键写入 `result[key]`,缺失的键根本不进字典。因此 `TranslationJob` 中的 `settings[新键]` 会抛 `KeyNotFoundException` 而非返回 null。M0021 是硬性前提。

### ✅ 等价性核对(四项均通过)

| 项 | 结论 |
| --- | --- |
| 空白行处理 | 现有 `/content` 循环跳过空白行返回 `""`;`TranslateSubtitles` 的 `if (string.IsNullOrWhiteSpace(subtitleLine))` 同样原样返回 —— 等价 |
| `ToSubtitleLines` 单行输入 | `originalLineCount = 1` 时返回 `[translated]`,与现状一致 |
| 结果映射 | 改后由 `subtitleItems.Select(...)` 产出;空白行 `TranslatedLines = [""]` → join 为 `""`,与现状一致 |
| 早退分支 | `TranslateSubtitles` 跳过 `TranslatedLines.Count > 0` 的项;`/content` 初始均为空,不触发 |

### 取舍:是否值得重构 `/content`

重构会改写约 45 行可用的生产代码,确有风险。但替代方案(保留自写循环、仅就地补上下文)需要在该循环内**同时**实现上下文构建与译文回写 —— 等于把 `TranslateSubtitles` 再实现一遍,且 8.5 的逻辑要维护两份。

**结论:维持重构。** 净代码量为负(删多于增),风险由发现 1 / 发现 2 的两项修正压制。

---

## 9. 已确认的设计决定

以下三项曾为待定项,现已定稿。保留理由以备日后回溯。

### 9.1 配对标签:`[SOURCE]` / `[TRANSLATION]`

否决了更省 token 的箭头单行式。核心理由是**箭头式构成「转换示范」,模型可能续写该模式而破坏「只输出译文」的要求**;另有角色显式、边界不受正文影响、与既有校对 prompt 一致三条支撑。完整论证见 4.2。

### 9.2 不降低使用门槛,仅以文案告知

不提供一键插入按钮,不做 user prompt 校验。新开关对未写入 `{contextBefore}` 的用户静默无效这一事实,只在开关说明文案中讲明(4.5 第 3 点)。理由:保持改动面最小,避免为一个可选功能引入额外 UI 交互与校验逻辑。

### 9.3 不提示 token 开销

配对格式使 `contextBefore` 的 token 数约翻倍(`ai_context_before = 2` 时每次请求约多 15 tokens)。**不**在文案中提示用户相应下调条数 —— 该量级相对整体 prompt 可忽略,提示反而增加认知负担。

---

## 10. 实现记录(2026-09-07)

按第 7 节清单逐项落地,另有三处与计划的出入,记录如下:

| # | 出入 | 说明 |
| --- | --- | --- |
| 1 | 第 8 节用例「开关开启,前序行未翻译 → 回退纯原文」**未写** | 该分支经 `TranslateSubtitles` **不可达**:循环按序处理,到达第 N 行时所有前序行要么已被预填(续传)、要么刚翻译完,`TranslatedLines.Count ≥ 1` 恒成立(`ToSubtitleLines` 至少返回一个元素)。代码里保留该回退作为防御,但无法通过公共 API 触发,直接测需要放开 `BuildContext` 可见性,不值得 |
| 2 | 补了一个计划外用例:续传预填行的配对 | `TranslatedLines` 由 `TranslationJob` 从数据库预填的场景(4.6 第二行),用例名 `…ResumedLinesArePairedFromPersistedTranslation` |
| 3 | `/content` 对**纯空白行**的返回值有细微变化 | 旧循环对空白行返回 `""`;`TranslateSubtitles` 原样透传空白(如 `"  "`)。对 Bazarr 等调用方无实际影响,与 `/file` 路径行为一致,未做归一化 |

另:`Lingarr.Docs/translation-services/ai-services.md` 的 `{contextBefore}` 占位符说明补了一句开关行为,这是第 7 节清单之外的唯一改动。

验证:`dotnet test --filter "Category!=Integration"` 228 通过(含新增 6 个);SQLite 迁移升/降/重放通过;`oxlint` 与 `vue-tsc --noEmit` 零错误。

---

## 11. 第二轮:实测反馈与改版(2026-09-08)

第一轮部署到 NAS(LocalAI + Qwen3.6-27B)后,用真实字幕测出三个问题:

| # | 现象 | 根因 |
| --- | --- | --- |
| 1 | 配对块没有编号,模型看不出行与行的对应 | 4.2 的格式只有标签没有 position |
| 2 | `/content` 的一条「行」内含 `\n`(Bazarr 把多行字幕整体当一行发来),`[SOURCE]` 块被撑成两行 | 标签格式对换行没有任何转义 |
| 3 | 模型把 `[TRANSLATION]` 当输出返回,该译文被存回后又进入后续行的上下文,形成 `[TRANSLATION] [TRANSLATION] …` 污染循环 | 4.2 对「标签不会被续写」的判断在这个模型上不成立;且没有任何输出清洗 |

### 11.1 决策(经多轮讨论,含被否决的方案)

| 议题 | 结论 | 理由 |
| --- | --- | --- |
| 开关 vs 新增 `{previousTranslations}` 占位符 | **保留开关,不新增占位符** | 新占位符和 `{contextBefore}` 取同一组前文、共用同一行数设置,是同一个槽位;两者同时写进 prompt 无意义,只能靠说明去禁止。开关改变占位符渲染方式在仓库里有先例:`language_code_format` 之于 `{sourceLanguage}` |
| 结构化格式 | **JSON 行**,与批量模式的 `{position,line}` 同源 | 换行、引号由 JSON 转义;目标句仍是裸文本,不构成「续写标签」的诱导 |
| 后文 | 开关开时同样 JSON 行,只有 position / source | 同一 prompt 内格式统一 |
| 指导文字放哪 | **写进 `ai_user_prompt` 新安装默认值末尾**,不做运行时注入、不做新设置项 | system prompt 在批量模式也用,放那里会把「只翻 [Target-to-Translate]」带进没有该段落的批量请求;user prompt 只在单条模式出现。目标句在指导段之前、上下文之后 |
| 指导放前还是放后 | 放 user message **最后** | 离模型回答最近 |
| 老用户 | **一律不动**:prompt 仍是 `{lineToTranslate}`、行数仍是 2 的也不改 | 用户决定;文档给出新默认全文供手动粘贴 |
| 新安装默认 | `ai_user_prompt` = 分段布局 + 指导段;`ai_context_before` / `after` = 0 | 行为中性:上下文段为空直到用户调高行数 |
| 自定义 JSON key(如 `previous_translation`) | **否决** | 只有 `messages[].content` 会进模型;LocalAI 静默丢弃未知 key(用 `prompt_tokens` 230 vs 376 证实),线上 API 直接 400 |
| few-shot 对话展开 | **暂不做** | 效果最好但改动面大一倍以上;需要时再加 |

上游先例:M0014 曾把独立的 `ai_context_prompt`(`[TARGET]` / `[CONTEXT]` 分段)合并进 `ai_user_prompt`,本次回到同一思路。

### 11.2 新格式

开关开时 `BuildContext` 每条上下文渲染为一个 JSON 对象(`Lingarr.Server/Models/ContextLine.cs`),`string.Join("\n")` 后即 JSON 行:

```
{"position":12,"source":"Hold it!","translation":"等一下！"}
{"position":14,"source":"Hey! What are you doing?\nGet away from here!","translation":"嘿！你在干什么？\n离这儿远点！"}
{"position":18,"source":"Who called us?"}                  ← after 侧或尚未翻译的行:无 translation
```

序列化用 `JavaScriptEncoder.UnsafeRelaxedJsonEscaping`(否则中文变 `\uXXXX`),`WhenWritingNull` 省略 translation。开关关时两侧维持纯原文,一个字不改。

### 11.3 输出清洗(始终生效)

`SubtitleTranslationService.CleanTranslationOutput`,在 `TranslateSubtitleLine` 拿到 provider 返回后调用,覆盖 `/file`、`/content`、`/line` 与所有 provider:

1. `Trim()`;
2. 循环剥掉开头的 `[TRANSLATION]` / `[SOURCE]` / `[TARGET…]` 标签(忽略大小写,允许 `#12` 编号与冒号),处理叠加的 `[TRANSLATION] [TRANSLATION] …`;
3. 若整段是 JSON 对象且含 `translation` 或 `line` 字符串字段,取该字段;
4. 只匹配这三个词,`[MUSIC]`、`[LAUGHS]` 等 SDH 标记不受影响;发生清洗时记一条 Information 日志;
5. 清洗后为空(模型只回了一个标签,或原始输出就是空)→ 记 Warning 并**回退为原文行**,与批量路径对缺译文的处理一致。不报错:报错会让一行抽风使整个文件 Failed,且 Retry 大概率复现;
6. **校对路径同样清洗**:`ProofreadJob` 与单行校对 `TranslationRequestService.ProofreadLine` 拿到 `ProofreadAsync` 结果后先经 `CleanTranslationOutput`。校对默认 prompt 用的正是 `[SOURCE]/[TRANSLATION]` 标签,不清洗会用带标签的文本覆盖已正确的译文。批量路径**不**清洗:批量请求里没有任何标签,模型无从回声。

它切断了污染循环:存回的译文永远不含标签,后续行的 `translation` 字段也就干净。

### 11.4 改动面

| 文件 | 改动 |
| --- | --- |
| `Lingarr.Server/Models/ContextLine.cs` | 新建 |
| `Lingarr.Server/Services/SubtitleTranslationService.cs` | `BuildContext` 改 JSON 渲染;新增 `CleanTranslationOutput`;`TranslateSubtitleLine` 调用之,清洗后为空回退原文 |
| `Lingarr.Server/Jobs/ProofreadJob.cs`、`Lingarr.Server/Services/TranslationRequestService.cs`(`ProofreadLine`) | 校对结果套 `CleanTranslationOutput` |
| `Lingarr.Migrations/Migrations/M0014_SeedAiUserPrompt.cs` | 种子值改为新布局(只影响新安装) |
| `Lingarr.Migrations/Migrations/M0002_SeedSettings.cs` | `ai_context_before` / `after` 种子 2 → 0(只影响新安装) |
| `Lingarr.Client/.../TranslationPrompt.vue` | 开关说明改写 |
| `Lingarr.Docs/translation-services/ai-services.md` | 默认 prompt、占位符表、清洗说明 |
| `Lingarr.Server.Tests/.../SubtitleTranslationServiceTests.cs` | 6 个上下文用例改 JSON 期望;新增换行转义 / 中文不转义 / 标签回声端到端 / `CleanTranslationOutput` 14 组 Theory |

不改:`ITranslationService` 契约、provider、`BaseLanguageService`、`ai_prompt` 默认值、老用户任何设置行、`TranslationJob` / `TranslationRequestService`。

---

## 附:关键位置索引

| 位置 | 作用 |
| --- | --- |
| `Lingarr.Contracts/Translation/ITranslationService.cs` | 插件契约,**不可改** |
| `Lingarr.Server/Services/Plugins/PluginLoader.cs:15,88` | 主版本号强制校验 |
| `Lingarr.Server/Services/SubtitleTranslationService.cs:52` | `TranslateSubtitles` |
| ` ` `:61` | `_progressService` 空值检查(抛异常) |
| ` ` `:104-105` | `BuildContext` 调用点 |
| ` ` `:144` | `TranslatedLines` 赋值点(时序论证依据) |
| ` ` `:498` | `BuildContext` 定义 |
| ` ` `:531` | `EmitProgress` 节流 |
| `Lingarr.Server/Jobs/TranslationJob.cs:91` | 设置读取数组 |
| ` ` `:188` | 续传行预填 |
| ` ` `:240` | `TranslateSubtitles` 调用 |
| `Lingarr.Server/Services/TranslationRequestService.cs:772` | `/content` 设置读取 |
| ` ` `:829` | 批量分支(已传 `_progressService`) |
| ` ` `:867-911` | 单条分支(待重构) |
| `Lingarr.Server/Services/ProgressService.cs:27-39` | `Emit` 载荷定义 |
| `Lingarr.Core/Configuration/SettingKeys.cs:91-92` | 上下文设置常量 |
