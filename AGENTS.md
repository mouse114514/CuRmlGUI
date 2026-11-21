# Casualties Unknown 模组开发核心要领

本仓库(workspace root)下所有 CU 模组项目的通用开发手册。

## 1. 关键路径

| 用途 | 路径 |
|---|---|
| 游戏本体 | `C:\Program Files (x86)\Steam\steamapps\common\Casualties Unknown\` |
| 游戏主程序 DLL(反编译对象) | `C:\Program Files (x86)\Steam\steamapps\common\Casualties Unknown\CasualtiesUnknown_Data\Managed\Assembly-CSharp.dll` |
| 游戏语言文本 | `C:\Program Files (x86)\Steam\steamapps\common\Casualties Unknown\CasualtiesUnknown_Data\Lang\` |
| BepInEx 插件目录 | `C:\Program Files (x86)\Steam\steamapps\common\Casualties Unknown\BepInEx\plugins\`(DLL 直放或放子目录均可) |
| 编译引用 DLL 副本 | `E:\work\Cu-SaveManager-main\references\`(0Harmony/BepInEx/各 UnityEngine 模块副本; Assembly-CSharp 与 URP 现直接引用游戏正式版 Managed) |
| 反编译源码(常用) | `E:\work\temp-decompile\CasualtiesUnknown-release\`（正式版全量反编译，302 类型；含 `Assembly-CSharp.csproj`） |
| 反编译工程(完整) | `E:\work\temp-decompile\CasualtiesUnknown-release\`（正式版全量反编译；旧 `E:\work\temp-decompile\Decompiler\` 为按类型名筛选的 dump 工具） |
| 发行版目录 | `C:\Users\Administrator\Desktop\IBuild\`(命名 `{ModName}-v{版本}.zip`) |

## 2. 开发工作流

```
dotnet build -c Debug --nologo -v q        # 构建(0 错误才算过)
Get-Process 'Casualties*' | Stop-Process   # 先杀游戏进程,否则 DLL 被占用复制失败
Copy-Item bin\Debug\Xxx.dll -> plugins\    # 部署
启动游戏测试
```

发布流程:csproj `<Version>` 与 `[BepInPlugin("com.cu.xxx", "名字", "版本")]` 保持一致 → `dotnet build -c Release` → 临时目录放 DLL(**发行包只放 DLL,不需要 README,也禁止新写任何 README/说明文档**) → 打 zip 命名 `{ModName}-v{版本}.zip` 放 IBuild → **用完删除临时目录**(用户要求工作区干净)。

## 3. 模组构建方式

本仓库下所有 CU 模组有两种构建方式，**新建模组必须先确定用哪种，不能混用**（要么全程原生，要么全程走 CCL）。

### 3.1 原生构建（不依赖 CCL）

- 直接引用游戏 Managed DLL（Assembly-CSharp + UnityEngine 各模块），用 Harmony 直接 patch。
- 不引用 `CUCoreLib.dll`，不加 `[BepInDependency("net.cucorelib")]`。
- 适用：纯逻辑、轻量、不需要 CCL 基础设施的模组。

### 3.2 CCL 依赖构建

- csproj 引用 `CUCoreLib.dll`（`C:\Program Files (x86)\Steam\steamapps\common\Casualties Unknown\BepInEx\plugins\CUCoreLib.dll`），插件类加 `[BepInDependency("net.cucorelib")]`。
- 适用：需要 CCL 基础设施的模组（物品注册、音频、精灵/文本/动画/AssetBundle 加载与缓存、`CUCoreUtils` 工具类等）。

### 3.3 选型规则（关键）

- **创建新模组时，必须先主动询问用户是否需要依赖 CCL**。不得默认、不得自行假设，等用户明确表态后再按选型走。
- 若选择**依赖 CCL**：
  - **只要 CCL 提供了对应能力，就必须用 CCL 的 API 实现**，禁止自己用原生方式重复实现同一功能。
  - **只有 CCL 没有提供的能力**，才允许自己用原生 Unity API / 手写实现兜底。
  - **CCL 的能力边界由 AI 自己找，不要手动枚举/硬编码**。实现时由 AI 主动读取 CCL 源码（`D:\BepInEx\CUCoreLib\`）判断当前需求对应的能力是否存在：存在则走 CCL API，不存在才原生兜底。以当时 CCL 源码为准，不预设能力清单。
  - **「引用原版」≠「依赖原版 API」**：Harmony patch 必须拿到原版类型定义（`Assembly-CSharp` 里的 `Body`/`WorldGeneration`/`Item`/`Locale` 等），因此 csproj 引用 `Assembly-CSharp.dll` 是**允许且必要**的——这只是**编译期类型引用**，不是业务逻辑依赖。原版没有设计为对外 API 面（这些类型是游戏内部实现），不存在「依赖原版 API」一说；真正的 API 依赖是 CCL。**业务逻辑实现一律按本规则走 CCL**，原版类型只用于 patch 签名与必要的类型交互，不得用原版 API 重复实现 CCL 已有的能力。
- 若选择**不依赖 CCL**：全部用原生方式实现，不得引入 `CUCoreLib` 引用，否则视为违反本规则。

## 4. 架构套路

- Harmony:`Harmony.CreateAndPatchAll(typeof(Plugin).Assembly, Info.Metadata.GUID)`;patch 用嵌套 `static class Patch_XXX` + `Prefix/Postfix`
- 编辑器/容器 UI:全手绘 IMGUI(UiPanel/UiButton/UiText/UiTextField,仿 Cu-NoitaMod),**不用 GUILayout**,每帧记录控件矩形用于失焦判断
- 存档:自定义文本格式存插件目录(CommandBlock\commands.txt),多套存档(State/RsState/Containers);新世界(`WorldGeneration.world != _containerWorld`)要重载并清状态
- 玩家可见文本用 `I18n.T(中文, English)`(**直接读游戏本地化字段** `Locale.currentLang` / `Locale.currentLangName`,含 CJK 即中文,不使用外部语言标记文件);**日志 `Logger.*` 和代码注释不翻译**

## 5. 踩坑经验(按血泪程度排序)

- **绝不用 PowerShell 读写源码文件**:PS 5.1 的 Get-Content/Set-Content 按系统 ANSI(GBK)解码 UTF-8 源码,写回后中文全部损坏且**不可逆**,只能 git checkout 重做。所有文件编辑用编辑器工具,PS 只做构建/复制/杀进程。
- **`BlockToWorldPos` 返回格子中心**(`(pos.x-halfWidth+0.5, pos.y-halfHeight+0.5)`),不要再 +0.5,否则实体跑到格子右上角/下一格。
- **`SetBlock` 后 tilemap 碰撞体可能未当帧更新**:紧接着 Raycast 会落穿到下方格子(陷阱跑到角落/下一格、刚放就被炸)。放实体类陷阱(地雷/捕兽夹/尖刺)要:垫岩石(`SetBlock(cur, 1)`,lightrock id=1)且岩石放**放置格本格**、固定在格子中心、不依赖 Raycast 找地面。
- **坐姿动画重播会抽搐**:ExperimentSit 有站→坐过渡,每帧重播会抽搐。稳定坐姿 = `Body.Update` Prefix + `PlayerCamera.HandleInput` Postfix 把 moveDir 清零,原版 idleTime 分支自然保持坐姿不切走。
- **`Item.SetupItems()` 每次进世界重建 GlobalItems**,mod 物品注册必须幂等(`ContainsKey` 缺啥补啥),并用 SetupItems Postfix 在第二局后自动补注册。
- **缩放放子对象**:矿车精灵父对象 scale=1、精灵子对象承担全部缩放(scale=3),避免父级缩放传递被破坏;collider 按世界单位重算(原 0.3×0.24 @ scale3 → 0.9×0.72)。
- **BepInEx 5 目标 .NET Framework**:`string.Contains(string, StringComparison)` 不存在,用 `IndexOf(s, StringComparison)`;static 类里没有 `BepInEx.Logging.Logger`(它是 BasePlugin 实例成员),static 工具类要么传实例要么 catch 静默。
- **mod 物品无 Resources prefab**:合成产出需 patch `RecipeResult.SpawnResult` 改用 CreateItemEntity;配方名/图标/描述 getter 走 Locale,需 patch `get_fullName`/`get_resultSprite`/`get_description` + `Locale.GetItem`,否则显示裸 id。
- **验证完必须清掉自己启动的所有进程**:为了验证而起的任何后台进程（本地服务、命令派生的宿主与子进程等），收尾必须全部强制结束，并用进程列表 + 端口监听检查确认无残留。**禁止为了让用户"方便直接打开测试"而留着进程**——进程存亡由用户自己掌控，残留进程会让他无法判断服务是否真的存在。同理清掉本次验证自己生成的临时产物（测试工程目录、临时脚本）。
- **部署前必须杀游戏进程**,否则 DLL 复制报"文件被占用"。
- **git 提交只 add 项目源码目录**,bin/obj 不提交;commit 前先看 git status/diff,不得混入无关文件。
- **AI 必须勤奋地提交**:每完成一段可验证的改动(构建通过 + 自检通过),就做一次带清晰信息的常规 commit 把工作保存下来,不要攒着一大坨;提交信息据 `git status`/`git diff` 如实概括。
- **M提交与I提交** 这是两种里程碑提交，I提交必须覆盖在M提交上面，M提交代表可运行版本，I提交代表具有重大意义的可运行版本。**除非用户主动要求，禁止擅自M提交或I提交**。M提交或I提交的标记方式为在一次提交的描述前端打上[M]前缀或[I]前缀。
- **push / tag / Release / 升版本必须经用户明确同意**:常规 commit 由 AI 主动做(见上条),但 **`git push`、打 tag、创建 GitHub Release、上传构建包、升 `<Version>` 一律不得擅自执行**,本地提交完就停,不要反问"要不要 push / 要不要发版"甩选项让用户选——直接停在已提交状态等用户发话。

## 6. 发布打包规则（NexusMods 审核）

NexusMods 自动扫描会标记 `.exe`、`.cmd` 等可执行文件，导致审核不通过。**必须打两种包**：

| 包类型 | 用途 | 包含内容 | 排除内容 |
|---|---|---|---|
| 完整包 `CuBlocky-v{ver}.zip` | GitHub Releases | exe + wwwroot + Launch.cmd + README | — |
| 源码包 `SCuBlocky-v{ver}.zip` | NexusMods 上传 | 纯源码（web/src、server/*.cs、*.csproj、*.sln、package.json、tsconfig.json、vite.config.ts、README） | .exe、.cmd、bin/、obj/、node_modules/、publish/ |

> 启动器是 `Launch.cmd`（不是 `.ps1`，避免 PowerShell 执行策略问题）。它以前台方式跑服务器，关窗口/Ctrl+C 即停，不会产生残留进程。端口可用环境变量覆盖：`CUBLOCKY_URL=http://localhost:6000 Launch.cmd`。

**打包流程**：
1. 先杀进程 `Get-Process 'CuBlocky*' | Stop-Process -Force`
2. `dotnet publish server -c Release --nologo -v q` 构建（含 wwwroot）
3. 完整包：`Compress-Archive` 打包 `server\bin\Release\net9.0\win-x64\publish\*` + `Launch.cmd` + `README.md`
4. 源码包：复制源码到临时目录（排除 exe/cmd/bin/obj/node_modules），`Compress-Archive` 打包为 `SCuBlocky-v{ver}.zip`，完成后删除临时目录
5. 两个包都放 `C:\Users\Administrator\Desktop\IBuild\`
6. **源码包只上传到 NexusMods，禁止上传到 GitHub Releases**
7. **GitHub Releases 只上传完整包**

## 7. 标签规范

### Tag 与 Release 的区别（重要）

- **Git tag**: 只是源代码版本标记（指向某次 commit），**不能携带构建产物**。`git tag` + `git push` 后，源码头/下载页只有一个源码 zipball/tarball，用户**无法下载可执行程序**。
- **GitHub Release**: 挂在一个 tag 上，是**唯一能上传构建产物（zip 资产）**的方式。发布 = `git tag` + `创建 GitHub Release` + `上传 zip`。

**发布任何版本（包括 Night Build）都必须创建 GitHub Release 并上传构建 zip，绝不能只打 git tag**（否则就是无产物的"裸 tag"，会造成困惑）。

### 正式发行版（`vX.Y.Z`）

- 命名 `v{版本}`（如 `v0.1.5`），对应 csproj `<Version>`。
- GitHub Release 创建时 `prerelease: false`，显示在正式发行列表中。
- 流程:构建产物(Release) → 打 zip → 创建 Release(prerelease:false) → 上传 zip 资产。

### Night Build 标签（`N-<hash>`）

Night Build 用于非正式发布的测试版本。

- **命名规则**: `N-{最新提交的前7位hash}`（如 `N-a5c4d95`）
- **描述固定为**: `This is a night build version, which may be somewhat unstable and has not been fully tested. You can try out the latest exciting features.`
- **用途**: 与正式发行版（`vX.Y.Z`）区分，方便用户快速获取最新功能但不保证稳定性
- **必须标为 prerelease**: GitHub Release 创建时 `prerelease: true`，这样在 Releases 页面不会显示在正式发行列表中
- **发布流程**: 先构建产物 → 打 zip → 创建 GitHub Release（带 `prerelease: true`）→ 上传 zip 资产。**不要只打 git tag，必须创建 GitHub Release 才能上传构建产物**

### 预发布版本（`T-<hash>`）

当用户明确要求发布**预发布版本**时，使用 `T-` 前缀。

- **命名规则**: `T-{最新提交的前7位hash}`（如 `T-a5c4d95`）
- **与 Night Build 的区别**: 仅前缀不同（`T-` vs `N-`），其余流程（prerelease: true、上传 zip）完全一致
- **描述**: 不用固定文案，应根据本次提交内容撰写，列出主要变更、修复或新功能
- **触发条件**: 用户主动说"发预发布版本"或类似表述时使用此前缀，**不要**自动使用

## 8. GitHub Issue 回复规范

回复 GitHub Issue 时，必须明确标注是 agent 自动回复，而非维护者手动回复。例如在回复开头或末尾加上 `> 此回复由 opencode agent 自动生成`，避免让用户误以为是维护者本人操作。

**必须用 Issue 原文相同的语言回复**（Issue 提交者和读者主要是英文用户），默认用英文；如果 Issue 本身是中文或俄文等，用对应语言回复。不要用中文回复英文 Issue。

**跟进时效**：如果 Issue 报告者反馈问题未修复或提出后续问题，应在 24 小时内回复；超过 24 小时无回复则关闭该 Issue。

## 9. GitHub 发布

- **仓库**: `mouse114514/CuBlocky`
- **Token**: `REDACTED`
- **发布命令**: 在 PowerShell 里 `curl` 是 `Invoke-WebRequest` 别名（会报错）。用 `curl.exe`(真正的 curl) 或 `Invoke-RestMethod`。

正式发行（`prerelease: false`）：
```powershell
$token = "REDACTED"
$ver = "0.1.5"
$zip = "C:\Users\Administrator\Desktop\IBuild\CuBlocky-v$ver.zip"

# 1. 创建 release
$body = '{"tag_name":"v' + $ver + '","name":"v' + $ver + '","body":"Release v' + $ver + '","prerelease":false}'
$r = Invoke-RestMethod -Uri "https://api.github.com/repos/mouse114514/CuBlocky/releases" `
  -Method Post -Headers @{"Authorization"="Bearer $token"} -ContentType "application/json" -Body $body

# 2. 上传 zip（用返回的 upload_url，去掉 {?name,label} 后缀换成 ?name=）
$uploadUrl = $r.upload_url -replace "\{\?name,label\}", "?name=CuBlocky-v$ver.zip"
Invoke-RestMethod -Uri $uploadUrl -Method Post `
  -Headers @{"Authorization"="Bearer $token"} -ContentType "application/zip" `
  -Body ([System.IO.File]::ReadAllBytes($zip))
```

Night Build（`prerelease: true`，`N-<hash>` 命名）：
```powershell
$token = "REDACTED"
$hash = "a5c4d95"   # git rev-parse --short HEAD
$tag  = "N-$hash"
$zip  = "C:\Users\Administrator\Desktop\IBuild\CuBlocky-$tag.zip"

# 1. 创建 release（prerelease:true，固定描述）
$body = '{"tag_name":"' + $tag + '","name":"' + $tag + '","prerelease":true,"body":"This is a night build version, which may be somewhat unstable and has not been fully tested. You can try out the latest exciting features."}'
$r = Invoke-RestMethod -Uri "https://api.github.com/repos/mouse114514/CuBlocky/releases" `
  -Method Post -Headers @{"Authorization"="Bearer $token"} -ContentType "application/json" -Body $body

# 2. 上传 zip
$uploadUrl = $r.upload_url -replace "\{\?name,label\}", "?name=CuBlocky-$tag.zip"
Invoke-RestMethod -Uri $uploadUrl -Method Post `
  -Headers @{"Authorization"="Bearer $token"} -ContentType "application/zip" `
  -Body ([System.IO.File]::ReadAllBytes($zip))
```