# CuRmlGUI

给 CU（Casualties: Unknown）模组开发者用的 **HTML/CSS UI 组件库**。

用 RML/RCSS（RmlUi 的 HTML/CSS 方言）定义组件与样式，由重编译的 **RmlUi 6.3**
原生运行时解析与布局，**SkiaSharp** 光栅化/合成，再经 Unity 纹理桥接到
IMGUI 或 uGUI 呈现。**只做渲染，绝不执行 JavaScript。**

本插件是「库模组」：默认不注入任何 UI，只负责初始化原生运行时与字体，
供其他模组通过 `WebGuiView` 使用。

---

## 特性

- **HTML/CSS 语义**：`<div>`/`<button>`/`<input>`、`position: absolute`、
  `margin/padding/border-radius`、`:hover/:active`、`rgba()`、`overflow: hidden`
  裁剪等，全部由 RmlUi 原生实现。
- **零 JS**：RmlUi 不包含脚本引擎；交互通过 C# 侧的事件回调转发。
- **精确渲染**：RmlUi 每帧 `Render()` 吐出几何，Skia `DrawVertices` 合成，
  预乘/反预乘与 Y 轴（flipY）在离屏测试中逐像素断言过。
- **按需渲染**：RmlUi 6.3 的 `Context::Update()` 无条件返回 `true`，因此脏状态由
  `WebGuiView` 自行维护；无输入、无动画、无 backdrop 更新时**不重绘也不上传纹理**。
- **毛玻璃（`backdrop-filter`）**：CSS `backdrop-filter: blur(Npx)` 对注入的游戏画面
  做模糊，且模糊只作用于元素区域（+3σ 上下文），成本 O(元素) 而非 O(视口)。
- **中文开箱即用**：随插件发行 `Liberation Sans`（Latin）与 Unifont CJK 子集
  回退字体，字形按需回退，无需用户安装字体。

## 用法（给模组开发者）

```csharp
using CuRmlGUI;

var view = new WebGuiView(1280, 720);   // 创建 RmlUi 上下文 + 离屏合成器
WebGuiFonts.LoadDefault(view);          // 加载随插件发行的字体（Latin + CJK 回退）
view.LoadDocument("<rml><body><div id='hello'>你好</div></body></rml>", css);

view.ElementEvent += (type, id) => Debug.Log($"event {type} / {id}");
view.SetElementText("hello", "更新后的文本");   // 通过 RmlUi DOM 回写元素内容

// 每帧（主线程）：
view.Update();                              // 有变化才重绘并上传纹理
Graphics.DrawTexture(rect, view.Texture);   // 或交给 uGUI RawImage
```

### 输入（坐标：左上原点）

```csharp
view.MouseMove(x, y);
view.MouseDown(x, y, button);
view.MouseUp(x, y, button);
view.MouseWheel(dx, dy);
view.KeyDown(keyCode, modifiers);   // KeyCode → RmlUi 键标识
view.KeyUp(keyCode, modifiers);
view.TextInputCodepoint(cp);        // Unicode 码点
```

### 毛玻璃 / `backdrop-filter`

CSS 侧照常写：

```css
#glass {
  background: rgba(255, 255, 255, 0.14);
  backdrop-filter: blur(14px);
}
```

背景侧需要把「玻璃后面的游戏画面」注入合成器——它是 `backdrop-filter` 的**模糊源**，
**不会被绘进最终输出**（backdrop 永不进入呈现纹理，因此不会以低分辨率副本覆盖
Unity 自己的渲染）：

```csharp
// rgba 为 top-down 行序的 RGBA 字节；destW/destH 是该图在视口中铺满的逻辑区域
view.SetBackdrop(rgba, srcW, srcH, 0, 0, screenW, screenH);
view.ClearBackdrop();              // 不需要时清除
```

实现要点（性能）：

- **区域受限模糊**：RmlUi 对 `backdrop-filter` 会设置 `scissor = 元素包围盒 + 溢出`，
  scissor 之外的合成结果随后被裁剪丢弃，因此快照与滤镜只作用于「scissor + Σ滤镜
  外扩」，成本从 O(视口) 降到 O(元素)。σ=14 时 500×450 区域模糊约 **1.7 ms**。
- 无 scissor、或滤镜链含 `mask`（需全图对齐）时退回全视口路径。
- 抓屏建议节流 + 降采样（spike 用 15 fps、1/4 分辨率）。

## Spike overlay 配置

开发用 spike 覆盖层（默认关闭）用于联调全链路，配置文件
`BepInEx/config/com.cu.curmlgui.cfg` 的 `[Spike]` 段：

| 键 | 默认 | 说明 |
| --- | --- | --- |
| `Enabled` | `false` | 启用 spike 覆盖层 |
| `AutoShow` | `false` | 启动即显示（免按键，供自动化截图） |
| `ToggleKey` | `F1` | 显隐按键 |
| `Presentation` | `ImGui` | `ImGui` / `UGui` |
| `DpiScale` | `1` | dp→px 比例 |
| `SortingOrder` | `30000` | uGUI Canvas 层序 |
| `Backdrop` | `false` | 抓屏注入 backdrop（毛玻璃模糊源） |

启用毛玻璃演示：`Enabled = true`、`AutoShow = true`、`Backdrop = true`，
玻璃面板 `#glass` 会显示背后游戏画面的模糊效果。

## 架构

```
RML/RCSS ──► RmlUi 6.3（重编译, /MT, C ABI shim）
                │ 每帧 Render() 吐出三角形/索引/纹理/裁剪
                ▼
           SkiaSharp 1.68.3 合成（SkiaCompositor）
                │ RGBA32 像素
                ▼
           Unity Texture2D（整张 Apply）
                ▼
        IMGUI（GUI.DrawTexture）或 uGUI（Canvas + RawImage）
```

- **原生 shim**（`native/shim/webgui_shim.cpp`）：纯 C ABI（`const char*`/`void*`/
  基础类型）。字符串用 C++ `malloc`，托管侧 `wg_free_string` 释放；事件指针仅在
  回调内有效，托管侧立即拷贝。`/MT` 静态 CRT，不跨 DLL 传 STL。
- **合成器**（`SkiaCompositor`）：`DrawVertices` 的第二参数是「纹理 × 顶点色」
  组合模式，必须用 `SKBlendMode.Modulate`（`SrcOver` 会把透明像素画成实心白块）；
  UV 乘纹理尺寸得到像素坐标；顶点色做反预乘（RmlUi 给的是预乘 0xRRGGBBAA）。
  Y 轴：Skia top-down 输出、Unity 纹理 bottom-up → 合成器 `flipY` CTM。
- **呈现层**：`PresentationMode.ImGui` / `PresentationMode.UGui`，配置切换。
  uGUI 用 ScreenSpaceOverlay + 全屏 RawImage + `CanvasScaler.ConstantPixelSize`。
- **backdrop**（毛玻璃模糊源）：由 C# 抓屏注入，`SkiaCompositor` 只把它当作
  `backdrop-filter` 的输入，**永不绘入最终输出**。方向约定：基座保持 flipY
  （bottom-up），离屏图层用 identity（top-down），`GetSnapshot` 仅在 `_flipY` 时
  行翻转。
- **字体**：`wg_load_font` 以 `fallback_face=true` 同时注册族名与回退 → Latin 主族
  + Unifont CJK 回退（RmlUi 6.3 不支持 `font-family` 列表，整串当单一族名）。

## 构建

前置：VS Community 2026（MSVC 14.51）+ CMake 4.3.1 + Windows SDK 10.0.26100，
dotnet SDK 8+，Python（字体子集化，可选）。

```powershell
# 1) 拉取 pinned 依赖（RmlUi 6.3 / FreeType 2.13.3）
powershell -ExecutionPolicy Bypass -File scripts\fetch-rmlui.ps1

# 2) 编译原生运行时（native/out/Release/WebGUINative.dll）
powershell -ExecutionPolicy Bypass -File scripts\build-native.ps1

# 3) 编译托管 + 部署到游戏
powershell -ExecutionPolicy Bypass -File scripts\deploy.ps1 -Build
```

### 离屏自检（进游戏前验证渲染）

```powershell
dotnet build tools\render-test\render-test.csproj -c Release
# 必须先把新原生 DLL 拷进 render-test 的输出目录，否则 DllImport 命中旧副本：
Copy-Item native\out\Release\WebGUINative.dll tools\render-test\bin\Release\net472\ -Force
cd tools\render-test\bin\Release\net472
.\render-test.exe WebGUINative.dll out.png C:\Windows\Fonts\segoeui.ttf --family="Segoe UI"
.\render-test.exe WebGUINative.dll glass.png C:\Windows\Fonts\segoeui.ttf --family="Segoe UI" --flip
```

- 第一行输出 `ALL CHECKS PASSED`：坐标/颜色/预乘/字形/裁剪/`Modulate` 全部正确。
- `--flip` 额外跑 `ALL BACKDROP CHECKS PASSED`：backdrop 模糊源方向、边界上下文 clamp、
  非占位像素不被模糊覆盖（含 flipY 双模式，基座 bottom-up / 离屏图层 top-down 的约定）。
- `glass.png` 写在**工作目录**，跑之前先 `cd`。

## 示例 mod

`samples/CuRmlGuiSample/` 是一个最小可运行的消费方模组，演示完整的接入流程：
创建 `WebGuiView` → `WebGuiFonts.LoadDefault` → 加载文档 → 每帧输入转发 +
`Update()` → `OnGUI` 绘制；按钮点击经 `ElementEvent` 回 C#，再 `SetElementText`
回写计数。构建后把 `CuRmlGuiSample.dll` 放到 `BepInEx\plugins\CuRmlGuiSample\`
（CuRmlGUI 已装在 `BepInEx\plugins\CuRmlGUI\`），进游戏即见左上角示例窗口。

## 核心 API

| 成员 | 说明 |
| --- | --- |
| `WebGuiView(w, h, dpi=1)` | 一个 RmlUi 上下文 + 离屏 Skia 合成器（`IDisposable`） |
| `LoadDocument(rml, rcss)` | 载入 RML/RCSS 文档 |
| `UnloadDocument()` | 卸载当前文档 |
| `LoadFont(path)` | 加载字体（ttf/otf，`fallback_face=true` 同时注册族名与回退） |
| `SetElementText(id, text)` | 按元素 id 回写文本内容（HTML 自动转义） |
| `SetSize(w, h, dpi)` | 更新上下文尺寸/DPI |
| `Update()` → bool | 推进一帧；按需重绘并上传纹理（见下「按需渲染」） |
| `SetBackdrop(rgba, srcW, srcH, destX, destY, destW, destH)` | 注入 backdrop（`backdrop-filter` 的模糊源，不进最终输出） |
| `ClearBackdrop()` | 清除 backdrop |
| `HasBackdrop` | 是否有 backdrop 注入 |
| `Texture` / `Width` / `Height` | 结果纹理（RGBA32）与尺寸 |
| `ElementEvent` | 事件：(eventType, elementId)，如 `click`/`mouseover`/`change` |
| `MouseMove/Down/Up/Wheel` | 鼠标输入（左上原点逻辑像素） |
| `KeyDown/KeyUp` | 键盘（`KeyCode` → RmlUi 键标识） |
| `TextInput(char)` / `TextInputCodepoint(uint)` | 文本输入（代理对走码点） |
| `WebGuiFonts.LatinPath` / `.CjkPath` / `.LoadDefault(view)` | CuRmlGUI 发行字体路径与加载 |
| `PresentationMode.ImGui` / `.UGui` | 呈现层选择（OnGUI 或 Canvas+RawImage） |

### 按需渲染

RmlUi 6.3 的 `Context::Update()` / `Render()` **无条件返回 `true`**，不再报告脏状态，
因此 `wg_update != 0` 恒真、会导致每帧都重渲染 + 整张纹理上传。

`WebGuiView` 自行维护脏状态：

- `RequestRender()`（linger 2 帧）在输入、文档/尺寸/文本变更、backdrop 更新时调用；
- 动画与计时器（滑块箭头、输入光标闪烁、滚动箭头）由 `wg_update_delay`
  （`Context::GetNextUpdateDelay()`）报告：`<=0` 立即渲染、`>0` 可等待、`INF` 空闲。

**注意**：hover / 布局变更**不**会请求更新（RmlUi 只在动画/滚动/输入计时器路径调
`RequestNextUpdate`），所以输入方法内部必须自行标记脏——`MouseMove` 还会对未变化的
坐标去重，避免调用方每帧转发导致每帧重绘。

实测效果（1920×1080，毛玻璃开启）：重绘帧数从 **195/195（100%）** 降到
**84/275（≈31%，等于 15 fps 抓屏节流的比例）**，渲染均摊耗时
10.5 ms/帧 → 3.0 ms/帧。

## 关键约束（设计决策）

1. **不依赖 CCL / 原生构建**：重编译 RmlUi（MIT），自写 C# 绑定，绕开
   net8-only 的 RmlUi.Net。
2. **SkiaSharp 必须用 1.68.3**：2.x 的 net462 资产引用
   `System.Runtime.InteropServices.RuntimeInformation`，其依赖整套 netstandard 1.x
   门面（`System.Runtime 4.0.0.0` 等），Unity 的 Mono 一个都没有且无绑定重定向 →
   无解。1.68.3 仅依赖 `System.Memory`/`System.Buffers`（随插件发行）。
3. **插件目录依赖解析**：BepInEx 以字节数组载入插件，CLR 只在 Unity `Managed`
   目录找依赖；`NativeLoader` 首行安装 `AssemblyProbe`（AssemblyResolve）在插件目录
   解析 `System.Memory`/`System.Buffers`，必须在任何 SkiaSharp 类型被 JIT 前。
4. **覆盖层挂到插件自己的 GameObject**（`BepInEx_Manager`）：BepInEx Awake 阶段
   `new GameObject + DontDestroyOnLoad` 会被游戏首个场景加载销毁。
5. **字体子集化**：CJK 用 `pyftsubset` 子集到 7917 字形（815 KB），Latin 254 字形
   （44 KB），避免携带 5.3MB 的 Unifont。

## 许可

- CuRmlGUI 代码：MIT（见 `LICENSE`）。
- RmlUi 6.3：MIT（以 RCDATA 资源 id 1000 内嵌在 `WebGUINative.dll`，运行时
  `wg_license()` 可读）。
- FreeType 2.13.3：FTL/GPLv2 双许可，静态链接。
- Liberation Sans、Unifont：各自开源许可，随插件发行。
