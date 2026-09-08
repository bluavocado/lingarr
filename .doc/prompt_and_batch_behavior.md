# Prompt 构造与批量/单条行为 —— 调研

> 代码基准:分支 `feature/personalized`,HEAD `28f6a19`
> 姊妹文档:[`fix_long_running_timeout.md`](./fix_long_running_timeout.md) —— 翻译任务被取消的四种故障

---

## 0. 速查:一句话回答

**Prompt 的差异不由端点决定,而由「批量 vs 单条」决定。**

三个端点(`/api/translate/content`、`/file`、`/bulk`)最终都汇入 `SubtitleTranslationService`,再由同一套 `BaseLanguageService` 构造 prompt。端点之间真正的差异只有两处:**批量判定的附加条件**,以及**是否构建上下文**。

| 维度 | 由什么决定 |
| --- | --- |
| system prompt / user prompt 的内容与占位符 | **批量 vs 单条**(见第 2 节) |
| 是否走批量 | `use_batch_translation` 设置,三端点语义一致(见第 4 节) |
| 是否有上下文行 | **端点**:`/file`、`/bulk` 有,`/content` 没有(见第 5 节) |

---

## 1. 两阶段占位符替换

理解 prompt 构造的关键:**占位符替换分两个阶段,作用域互不相通**。

```
阶段一:渲染用户的 prompt 文本
  ai_prompt / ai_user_prompt
    ← {sourceLanguage} {targetLanguage} {model}
      {lineToTranslate} {contextBefore} {contextAfter}
  产出 → systemPrompt、userMessage

阶段二:渲染 HTTP 请求体
  请求模板(如 LocalAiChatTemplate)
    ← {model} {systemPrompt} {userMessage}
  产出 → 实际 POST 出去的 JSON
```

- 阶段一由 `BaseLanguageService` 的 `GetReplacements` / `GetBatchReplacements` / `GetProofreadReplacements` 完成
- 阶段二由 `RequestTemplateService.BuildRequestBody(template, replacements)` 完成

**实际含义**:`{lineToTranslate}`、`{contextBefore}` 这类占位符只能写在**你的 prompt 设置里**;`{systemPrompt}`、`{userMessage}` 只能写在**请求模板里**。写错地方不会报错,只会原样保留(见 1.2)。

### 1.1 `GetReplacements` / `GetBatchReplacements` 到底做了什么

两者都返回**同一个字典**,既作为阶段一的输入,又携带阶段一的产出:

1. 复制基础字典 `_replacements` —— 其中只有 `sourceLanguage` 与 `targetLanguage`,由
   `SetLanguageReplacements`(`BaseLanguageService.cs:202`)写入,取值受 `language_code_format` 影响
   (`"true"` 用语言代码如 `zh-CN`,否则用完整名称如 `Chinese (Simplified)`)
2. 补入本次调用相关的键(`model`、`lineToTranslate`、`contextBefore`、`contextAfter`)
3. 用这个字典**渲染 prompt 设置**,得到 `systemPrompt` 与 `userMessage`
4. 把两个渲染结果**塞回同一个字典**,供阶段二使用

`GetReplacements`(`BaseLanguageService.cs:43-63`,单条):

```csharp
var replacements = new Dictionary<string, string>(_replacements)
{
    ["model"] = model,
    ["lineToTranslate"] = lineToTranslate,
    ["contextBefore"] = string.Join("\n", contextLinesBefore ?? []),
    ["contextAfter"] = string.Join("\n", contextLinesAfter ?? [])
};
var systemPrompt = ReplacePlaceholders(_prompt, replacements);      // ai_prompt
var userMessage = string.IsNullOrEmpty(_userPrompt)
    ? lineToTranslate                                                // 回退:直接用原文行
    : ReplacePlaceholders(_userPrompt, replacements);                // ai_user_prompt
replacements["systemPrompt"] = systemPrompt;
replacements["userMessage"] = userMessage;
```

`GetBatchReplacements`(`BaseLanguageService.cs:65-77`,批量):

```csharp
var replacements = new Dictionary<string, string>(_replacements)
{
    ["model"] = model,
    ["lineToTranslate"] = string.Empty,
    ["contextBefore"] = string.Empty,
    ["contextAfter"] = string.Empty
};
replacements["systemPrompt"] = ReplacePlaceholders(_prompt, replacements);
replacements["userMessage"] = serializedBatch;                       // 绕过 ai_user_prompt
```

**只有两处实质差异**:`userMessage` 直接就是序列化后的 JSON 批次(`ai_user_prompt` 被完全绕过);三个单条相关的占位符被显式置为空串。

### 1.2 为什么要把占位符置空,而不是不放进字典

因为 `ReplacePlaceholders` 对认不出的占位符是**原样保留**:

```csharp
return PlaceholderPattern.Replace(template, match =>
    replacements.TryGetValue(match.Groups[1].Value, out var value) ? value : match.Value);
```

若不置空,用户 `ai_prompt` 里写的 `{lineToTranslate}` 在批量模式下会以**字面量** `{lineToTranslate}` 泄漏进 system prompt。置空是为了让它们静默消失。

---

## 2. 占位符全表

| 占位符 | 单条 `GetReplacements` | 批量 `GetBatchReplacements` | 校对 `GetProofreadReplacements` |
| --- | --- | --- | --- |
| `sourceLanguage` / `targetLanguage` | ✅ | ✅ | ✅ |
| `model` | ✅ | ✅ | ✅ |
| `lineToTranslate` | 待译行 | **空串** | **空串** |
| `contextBefore` / `contextAfter` | 前后各 N 行 | **空串** | **空串** |
| `sourceLine` / `translatedLine` | — | — | ✅ 校对专用 |
| `systemPrompt` ← 渲染自 | `ai_prompt` | **同一个 `ai_prompt`** | `proofread_prompt` |
| `userMessage` ← 渲染自 | `ai_user_prompt`(空则回退原文行) | **序列化 JSON 批次,忽略 `ai_user_prompt`** | `proofread_user_prompt`(空则回退译文行) |

三点结论:

- **没有独立的批量 system prompt** —— 批量与单条共用 `ai_prompt`
- **`ai_user_prompt` 在批量模式下形同虚设** —— 自定义它对批量翻译零影响
- **上下文行仅单条模式可能生效** —— 批量模式被显式置空

---

## 3. 各 prompt 的播种默认值

| 设置键 | 默认值 | 来源 |
| --- | --- | --- |
| `ai_prompt` | 见下 | `M0002_SeedSettings.cs:54` |
| `ai_user_prompt` | `{lineToTranslate}` | `M0014_SeedAiUserPrompt.cs:10` |
| `proofread_prompt` | 见下 | `M0015_SeedProofreadPrompts.cs:12` |
| `proofread_user_prompt` | `[SOURCE] {sourceLine}\n[TRANSLATION] {translatedLine}` | `M0015_SeedProofreadPrompts.cs:17` |
| `ai_context_before` / `ai_context_after` | `2` / `2` | `M0002_SeedSettings.cs:57-58` |
| `use_batch_translation` | **`false`** | `M0002_SeedSettings.cs:59` |
| `language_code_format` | `false`(用完整语言名) | `M0007_SeedLanguageCodeFormat.cs:10` |

`ai_prompt` 原文:

> Translate from {sourceLanguage} to {targetLanguage}, preserving the tone and meaning without censoring the content. Adjust punctuation as needed to make the translation sound natural. **Provide only the translated text as output, with no additional comments.**

`proofread_prompt` 原文:

> You are proofreading a subtitle translated from {sourceLanguage} to {targetLanguage}. Compare the translation against the source and correct mistranslations, wrong names, grammar and punctuation, preserving the tone and meaning without censoring the content. Keep the length close to the original so it still fits on screen. If the translation is already correct, repeat it unchanged. Provide only the corrected translation as output, with no additional comments.

---

## 4. 🔴 默认 system prompt 与批量输出契约相冲突

`ai_prompt` 是**为单条翻译写的**,末句要求「只输出译文,不要任何额外内容」。

但批量模式把**同一句话**发给模型,却期待返回:

```json
[{"position": 1, "line": "译文"}, {"position": 2, "line": "译文"}]
```

**system prompt 里没有任何一个字**说明要返回 JSON、要保留 `position`、要保持条数与输入一致。那句「Provide only the translated text」实际上与所需的 JSON 输出**互相矛盾**。

唯一让批量能工作的机制:

| 路径 | 约束来源 | 可靠性 |
| --- | --- | --- |
| 结构化输出 | `response_format` 的 `json_schema`(`LocalAiService.cs:342-386`),由服务端强制 | 较可靠 |
| JSON 解析回退 | **无任何约束** | 完全依赖模型从输入形状自行推断 |

这解释了为何回退路径如此脆弱 —— 参见姊妹文档
[`fix_long_running_timeout.md` 第 6 节](./fix_long_running_timeout.md)(输出被上下文窗口截断)
与其 12.5(`finish_reason` 未被解析,截断无法识别)。

**若要自行改善**:在 `ai_prompt` 中补上 JSON 契约说明。代价是这段说明在单条模式下也会被发送,属于无谓开销 —— 这正是「应当有独立批量 prompt」的论据。

---

## 5. 端点的批量判定对照

> ⚠️ **勘误**:本调研初版曾断言「`/content` 强制批量、无视 `use_batch_translation`」。**该结论错误,已撤回。**
> 成因:只读了 `TranslationRequestService.cs:826` 起的片段(那正处于 `if` 体内部),未回溯到 `:822` 的判定条件即下结论。经用户实测反驳后复核更正。

| 端点 | 批量条件 | 依据 |
| --- | --- | --- |
| `/file`、`/bulk` | `use_batch_translation == "true"` **且** 存在批量能力服务 | `TranslationJob.cs:211` |
| `/content` | `use_batch_translation == "true"` **且** `Lines.Count > 1` **且** 存在批量能力服务 | `TranslationRequestService.cs:822` |

两者**语义一致**,`/content` 仅多一个 `Lines.Count > 1` 的保护(单行请求恒走单条)。条件不满足时,`/content` 进入 else 分支逐行翻译(`:867-915`),**不会抛异常**。

`use_batch_translation` 播种默认为 **`false`**,即批量默认关闭。

具备批量能力的服务仅 6 个(实现 `IBatchTranslationService`):
`AnthropicService`、`OpenAiService`、`GoogleGeminiService`、`LocalAiService`、`MistralService`、`XAiService`。
Google / Bing / Microsoft / Yandex / DeepL / LibreTranslate **均不具备**,配置这些服务时无论设置如何都只能走单条。

**实际含义**:换入口(Bazarr → Lingarr 界面)**不会改变**批量与否 —— 两条入口受同一个 `use_batch_translation` 支配。

---

## 6. 上下文行的行为

### 6.1 哪些端点能拿到上下文

`BuildContext` **只在** `TranslateSubtitles`(单条路径)内被调用(`SubtitleTranslationService.cs:104-105`)。

| 端点 | 模式 | 上下文 | 原因 |
| --- | --- | --- | --- |
| `/file`、`/bulk` | 单条 | ✅ | `TranslationJob.cs:240` 传入 `ai_context_before/after`,内部调用 `BuildContext` |
| `/file`、`/bulk` | 批量 | ❌ | `GetBatchReplacements` 置空 |
| `/content` | 单条 | ❌ | `TranslationRequestService.cs:879` 构造 `TranslateAbleSubtitleLine` 时**未给 `ContextLinesBefore` / `ContextLinesAfter` 赋值**,保持 `null`;`GetReplacements` 中 `?? []` 渲染为空串。该分支**从不调用 `BuildContext`** |
| `/content` | 批量 | ❌ | 同上批量 |
| `/line` | 单条 | ✅ 由调用方提供 | `TranslateAbleSubtitleLine` 自带该两字段,从请求体传入,非自动构建 |

**即:经 `/content` 的调用方(如 Bazarr)在任何模式下都享受不到上下文功能**,`ai_context_before/after` 设成多少都无效。这看起来像是该分支的疏漏而非有意设计 —— 同一个 `TranslateAbleSubtitleLine` 模型明明支持这两个字段。

附带:`/content` 单条分支创建 `SubtitleTranslationService` 时**未传入 `_progressService`**(`:872`),与批量分支(`:829` 传入)不一致,进度改由该分支手工 `EmitLine` + `Emit` 补偿。

### 6.2 🔴 上下文只含原文,不含已翻译内容

`BuildContext`(`SubtitleTranslationService.cs:498`)核心一行:

```csharp
context.Add(string.Join(" ",
    stripSubtitleFormatting ? contextSubtitle.PlaintextLines : contextSubtitle.Lines));
```

只读 `PlaintextLines` / `Lines`(**原文**),**从不读 `TranslatedLines`**。

因此即便前 N 行在本次循环中早已翻译完成(或从断点续传中恢复),送给模型的 `contextBefore` 仍是**源语言原文**。

**平心而论**:`contextAfter` 天然只能是原文(那些行尚未翻译),所以「两侧都用原文」在设计上是自洽、对称的。

**但代价是**:模型看不到自己此前的译法,人名、称谓、专有名词的跨行一致性缺少任何强化机制 —— 而这恰是字幕翻译中携带已译上下文的主要动机。

改为携带译文并非简单换个字段:会引入前后不对称,且与断点续传(恢复的行已有译文)、批次边界(批量模式无上下文)的交互都需要设计。

---

## 7. 实践建议

| 想做的事 | 该怎么做 |
| --- | --- |
| 在 Lingarr 界面用批量 | 显式打开 `use_batch_translation`(默认关闭),否则走单条 |
| 自定义 user prompt | 注意**它对批量模式无效**;只在单条与校对生效 |
| 改善批量输出稳定性 | 在 `ai_prompt` 中补上 JSON 契约说明(见第 4 节) |
| 用上下文提升翻译质量 | 必须走 `/file` 或 `/bulk` 的**单条**模式;经 Bazarr 无论如何都拿不到 |
| 配置非 AI 服务(Google/DeepL 等) | 批量设置无意义,恒走单条 |

**注意批量与上下文互斥**:批量模式没有上下文行,但批次内相邻字幕天然互为上下文;单条模式有显式上下文,但每行一次请求、开销高。二者不可兼得。

---

## 8. 建议的仓库改进

| # | 改进 | 理由 |
| --- | --- | --- |
| 1 | **独立的批量 system prompt**,或在批量模式下自动追加 JSON 契约段落 | 当前 `ai_prompt` 的指令与批量输出契约互相矛盾(第 4 节) |
| 2 | 批量模式尊重 `ai_user_prompt`(例如提供 `{batchJson}` 占位符) | 目前该设置被静默忽略,用户无从察觉 |
| 3 | `/content` 单条分支补齐上下文构建,与 `/file` 对齐 | 模型已支持该字段,仅调用方未填(6.1) |
| 4 | `/content` 单条分支传入 `_progressService`,消除与批量分支的不一致 | `:872` vs `:829` |
| 5 | 可选的「已译上下文」模式 | 提升术语与人名的跨行一致性(6.2);需处理前后不对称与续传交互 |
| 6 | UI 中标注哪些设置只对特定模式生效 | `ai_user_prompt`、`ai_context_before/after` 均存在「设了但不生效」的场景 |

---

## 附:关键文件

| 文件 | 角色 |
| --- | --- |
| `Lingarr.Server/Services/Translation/Base/BaseLanguageService.cs` | 阶段一渲染:`GetReplacements`(43)、`GetBatchReplacements`(65)、`GetProofreadReplacements`(79)、`SetLanguageReplacements`(202) |
| `Lingarr.Server/Services/Translation/RequestTemplateService.cs` | 阶段二渲染:`BuildRequestBody`、默认模板工厂 |
| `Lingarr.Server/Models/RequestTemplates/LocalAiChatTemplate.cs` | 请求模板,仅含 `model` + `messages` |
| `Lingarr.Server/Services/SubtitleTranslationService.cs` | 单条/批量调度、`BuildContext`(498) |
| `Lingarr.Server/Jobs/TranslationJob.cs` | `/file`、`/bulk` 的批量判定(211)与单条调用(240) |
| `Lingarr.Server/Services/TranslationRequestService.cs` | `/content` 的批量判定(822)、单条分支(867-915) |
| `Lingarr.Migrations/Migrations/M0002_SeedSettings.cs` | `ai_prompt`(54)、上下文行数(57-58)、`use_batch_translation`(59) |
| `Lingarr.Migrations/Migrations/M0014_SeedAiUserPrompt.cs` | `ai_user_prompt`(10) |
| `Lingarr.Migrations/Migrations/M0015_SeedProofreadPrompts.cs` | 校对 prompt(12-18) |
