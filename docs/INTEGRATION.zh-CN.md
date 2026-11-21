# CuRmlGUI 接入指南

[English](INTEGRATION.md) | **简体中文**

本文面向使用 CuRmlGUI 的模组开发者。内部实现细节见
[ARCHITECTURE.zh-CN.md](ARCHITECTURE.zh-CN.md)。

## 1. 引入库

CuRmlGUI 是一个库模组。将它连同 `WebGUINative.dll`、SkiaSharp 与字体资源放在
`BepInEx/plugins/CuRmlGUI/` 下，你的模组只引用 `CuRmlGUI.dll`：

```xml
<Reference Include="CuRmlGUI">
  <HintPath>...\BepInEx\plugins\CuRmlGUI\CuRmlGUI.dll</HintPath>
  <Private>false</Private>
</Reference>
```

不要把 `CuRmlGUI.dll` 或 `WebGUINative.dll` 复制进自己的插件目录。原生库与字体路径
都相对于插件目录解析，重复的副本会导致两套原生运行时。

`SkiaSharp.dll`、`libSkiaSharp.dll`、`System.Memory` 与 `System.Buffers` 等运行时依赖
由 CuRmlGUI 目录提供。`NativeLoader.EnsureLoaded()` 会安装 AppDomain 级的
`AssemblyResolve`，按简单名解析这些依赖（包括 SkiaSharp）。因此：

- 你的模组不应分发 `WebGUINative.dll`、`libSkiaSharp.dll` 或字体；
- 若需要引用 SkiaSharp 来构建自定义滤镜，只添加编译期 `<Reference>`，
  运行时由上述解析器提供该程序集。

## 2. 最小示例

```csharp
using CuRmlGUI;
using UnityEngine;

public sealed class MyOverlay : MonoBehaviour
{
    private WebGuiView _view;

    private void Awake()
    {
        _view = new WebGuiView(Screen.width, Screen.height);
        WebGuiFonts.LoadDefault(_view);
        _view.LoadDocument(
            "<rml><body><div id='panel'>Hello</div></body></rml>",
            "#panel { position: absolute; left: 40px; top: 40px; padding: 12px;" +
            "         background: rgba(20, 24, 40, 0.85); color: #fff; border-radius: 10px; }");
        _view.ElementEvent += (type, id) => Debug.Log($"{type} / {id}");
    }

    private void Update()
    {
        Vector3 p = Input.mousePosition;
        float x = p.x;
        float y = Screen.height - p.y;          // RmlUi 采用左上原点

        _view.MouseMove(x, y);
        for (int b = 0; b <= 2; b++)
        {
            if (Input.GetMouseButtonDown(b)) _view.MouseDown(x, y, b);
            if (Input.GetMouseButtonUp(b)) _view.MouseUp(x, y, b);
        }

        _view.Update();
    }

    private void OnGUI()
    {
        if (_view?.Texture == null) return;
        _view.PresentOnGui();
    }

    private void OnDestroy() => _view?.Dispose();
}
```

若需要液态玻璃，在载入文档后调用 `_view.EnableGpuGlass(new[] { "panel" });`，
详见 7.1 节。

## 3. 生命周期与线程

- `new WebGuiView(w, h, dpi)` 创建原生上下文，必须与 `Dispose()` 成对使用。
- 分辨率变化时调用 `SetSize(w, h, dpi)`，它会重建位图与图层池。
- `Update()` 返回本帧是否真正重绘并上传了纹理，因此无需自行维护脏状态。
- 不要跨线程调用，也不要在 `OnGUI` 中调用 `Update()`。

## 4. 呈现

```csharp
private void OnGUI()
{
    if (_view?.Texture == null) return;
    _view.PresentOnGui();          // 含 GPU 玻璃合成与 GUI.depth 处理
}
```

`PresentOnGui` 将覆盖层绘制在游戏画面之上。`Texture` 为 RGBA32、bottom-up 行序，
尺寸等于 `view.Width` 与 `view.Height`。不要让 Unity 再次缩放该纹理：
界面已经在 Skia 中按逻辑像素完成合成。

## 5. 输入

坐标采用左上原点与逻辑像素。

```csharp
view.MouseMove(x, y);                 // 坐标未变化时内部会去重
view.MouseDown(x, y, button);         // 0 = 左键，1 = 右键，2 = 中键
view.MouseUp(x, y, button);
view.MouseWheel(dx, dy);
view.KeyDown(keyCode, modifiers);     // UnityEngine.KeyCode
view.KeyUp(keyCode, modifiers);
view.TextInput(c);                    // 代理对请改用 TextInputCodepoint
view.TextInputCodepoint(codePoint);
```

只转发 `Input.inputString` 中的可打印字符。退格、回车等按键已由 `KeyDown` 处理，
重复转发会造成输入重复。修饰键可通过 `Event.current` 或 `Input.GetKey` 组合。

## 6. 按需渲染与帧率

- 没有输入、动画或背景更新时，视图既不重绘也不上传纹理。
- `MaxRenderFps`（默认 20）限制由输入、悬停与动画触发的重绘。设为 0 表示取消限制，
  不推荐使用。
- 重绘成本随内容复杂度增长；大面积玻璃可见时，建议将上限设为 20–30。
- `view.TakeRenderStats()` 返回各阶段耗时（`draw`、`composite`、`mask`、`layers`），
  用于诊断。

## 7. 液态玻璃与背景模糊

### 7.1 GPU 液态玻璃（推荐）

库内置的玻璃可生成带真实模糊与边缘折射的圆角面板，全部在 GPU 上完成。
它既不需要 CPU 回读，也不依赖 `backdrop-filter`，并且不使用自定义着色器。

```csharp
var glass = view.EnableGpuGlass(new[] { "card-weather", "card-music" });
glass.CornerRadius     = 30;   // 圆角半径，单位为屏幕像素
glass.BlurStepPixels   = 1f;   // 采样间距，单位为 1/8 分辨率像素；1 表示无重影
glass.RefractionPixels = 5f;   // 边缘折射强度，单位为屏幕像素
```

`Update()` 每帧把面板对齐到对应元素并执行模糊，`PresentOnGui()` 负责呈现。
它固定的顺序是：以 `alpha = 0` 绘制一次界面、把画面捕获到渲染纹理、绘制玻璃网格、
再绘制界面纹理。由于捕获发生在玻璃绘制之前，玻璃不会把自身捕获进去。

补充说明：

- 每个启用的视图会持有若干张全屏渲染纹理，1080p 下约 25 MB，
  因此建议每个屏幕只启用一个视图。
- 卡片应保持透明或极淡的背景，只绘制文字、边框与高光；模糊背景由玻璃网格提供。
- 覆盖层隐藏期间可将 `glass.Visible` 设为 false，以跳过捕获、模糊与绘制。
- 该路径依赖 IMGUI 的 `OnGUI` 合帧机制。若界面不使用 IMGUI 呈现，
  请改用 7.2 节的 CPU 路径。

### 7.2 注入背景（CPU 备用方案）

`backdrop-filter` 模糊的是由调用方提供的像素。背景仅作为模糊来源，
不会进入最终纹理。

```csharp
// rgba：top-down 行序，RGBA8888；目标矩形使用视口坐标
view.SetBackdrop(rgba, srcW, srcH, 0, 0, Screen.width, Screen.height);
view.ClearBackdrop();
```

抓屏建议：

- 在 `OnGUI` 的 Repaint 阶段、且在自己的 `GUI.DrawTexture` 之前调用
  `ScreenCapture.CaptureScreenshotAsTexture()`，这样图像与屏幕一致，
  并且不包含本覆盖层。
- 不要从相机目标抓取：在 URP 下，UGUI 绘制的世界与 HUD 不属于相机目标，
  抓取结果会是黑屏或错位。
- 将抓屏频率限制在 10–20 fps。回读是同步操作，成本高于重绘。

### 7.3 Fake 与 Real 模式

```csharp
view.BackdropBlurMode = BackdropBlurMode.Fake;   // 默认：完全不执行模糊滤镜
view.BackdropBlurMode = BackdropBlurMode.Real;   // Skia 高斯模糊，开销较大
```

在 `Fake` 模式下，合成器会跳过背景链上的 `blur()`，只做双线性放大，
因此观感完全取决于所提供背景的分辨率与预处理。常见做法是把抓屏降采样至二分之一，
在该分辨率上执行可分离盒式滤波，再由合成器缩放到元素区域。

`Real` 模式的耗时与元素面积成正比：在 CPU 上，400×260 约需 2.6 ms，
800×520 约需 14.9 ms，全屏约需 81 ms。

### 7.4 CSS 兼容层

RmlUi 6.3 没有注册若干浏览器 CSS 属性。CuRmlGUI 内置一个文本级兼容层（默认开启），
在样式表解析之前把它们重写为 RmlUi 等价写法，使浏览器风格的标记依然可用：

| 浏览器 CSS | RmlUi 等价写法 | 说明 |
| --- | --- | --- |
| `text-shadow: x y [blur] color` | `font-effect: shadow(x y color)` | 模糊半径会被丢弃（RmlUi 的 font-effect 无模糊），并给出一次告警。`text-shadow: none` 不处理。 |
| `outline: w style color` | `box-shadow: 0 0 0 (w + offset) color` | 非 `solid` 线型会退化为实线并告警。`outline-offset` 会并入扩散值。`outline: none` 不处理。 |
| `background: linear-gradient(...)` | `background-color: transparent; decorator: linear-gradient(...)` | 仅当整条声明恰好是单个 `linear-gradient` / `radial-gradient` 函数时转换。与颜色或多层背景混合的渐变保持原样。 |

该层是纯声明重写器（`src/Render/CssCompat.cs`）。它解析完整 RCSS，包括注释、字符串、
括号以及 `@media` / `@keyframes` / `@font-face` / `@decorator` 块，对无法安全转换的
声明**逐字节保留**，绝不静默改变语义；不支持的转换只记录一次告警。如需关闭（例如排查
某个样式表）：

```csharp
view.CssCompatEnabled = false;   // 默认 true
```

原生手写 RmlUi 写法依然可用，且在不需浏览器兼容时是推荐形式：

```css
.card-title { font-effect: shadow(0px 1px rgba(0, 0, 0, 0.55)), blur(1px); }
.card-label { font-effect: outline(1px rgba(255, 255, 255, 0.35)); }
```

`box-shadow` 支持 `inset`、模糊、扩散与逗号分隔的多重阴影。外阴影需要有非零偏移
或扩散才可见，因为 RmlUi 会把它裁剪到边框盒之外。

### 7.5 Liquid Glass 主题预设

`LiquidGlassTheme.Css` 是一套现成的 Apple「液态玻璃」样式，针对暗色、有内容的背景
（如游戏画面）调校。直接拼进 `LoadDocument` 即可用：

```csharp
view.EnableGpuGlass(new[] { "card-1" });          // 卡片透出背后的游戏画面并模糊
view.LoadDocument(rml, LiquidGlassTheme.Css + myCss);
```

它覆盖以下选择器（与内置 `DefaultControlsCss` 同一批选择器，因此附加后会覆盖默认控件外观）：

| 选择器 | 外观 |
| --- | --- |
| `.card` | 半透明玻璃面板：边缘一圈内高光（顶部最亮）、135° 对角光泽、24px 外阴影。配合 GPU 玻璃可透出模糊背景。 |
| `.lg-button` | 暗玻璃按钮：白字、1px 亮边、顶部内高光、外阴影；`:active` 按下凹陷。 |
| `input[type=checkbox]` | 暗玻璃方块；未选中变暗（关），选中略亮；`:active` 凹陷。 |
| `input[type=range]` | 透明凹槽 + 略亮的进度 + 与进度同色的玻璃圆钮；`:active` 圆钮变暗并凹陷。 |

该预设面向**暗色背景**。若用在浅色背景，需自行覆盖 tint（`background-color`）与文字颜色。
它是纯 CSS，有无 GPU 玻璃都能用（GPU 玻璃只是给 `.card` 加上背后的模糊）。

## 8. 自定义滤镜

滤镜必须在载入文档之前注册，因为 RmlUi 在解析 CSS 时就需要对应的实例化器：

```csharp
view.RegisterFilter("myfilter", 3, args =>
{
    float sigma = args[0], tint = args[1], amount = args[2];
    return new CustomFilterResult(
        SKImageFilter.CreateColorFilter(SKColorFilter.CreateColorMatrix(...)),
        padPx: 0);
});
```

```css
.card { filter: myfilter(4 0.2 0.8); }
```

- `arity` 是 CSS 参数个数（0–16），`args` 的顺序与 `p0..pN` 一致。
- `PadPx` 用于扩大滤镜计算区域；模糊与位移会超出元素盒，因此需要额外空间。
- 滤镜按顺序应用。在 `backdrop-filter` 链上，它们只作用于背景，即基座图层。

## 9. 事件与 DOM

```csharp
view.ElementEvent += (type, id) =>
{
    // type：click / mouseover / mouseout / mousedown / mouseup / change / focus / blur
    if (type == "change" && id == "volume") { /* ... */ }
};

view.SetElementText("status", "已连接");   // 替换文本内容，HTML 自动转义
```

RmlUi 不提供 JavaScript 或 DOM 查询接口。需要动态内容时，使用 `SetElementText`
或重新载入文档。

## 10. 输入拦截

覆盖层使用 IMGUI 绘制，不参与 UGUI 射线检测，因此游戏的菜单与 HUD 会收到同一批点击。
以下两种机制可以配合使用。

### 10.1 查询式拦截（推荐）

用标准 CSS 的 `pointer-events` 声明意图。组件默认可交互，因此默认拦截；
容器与背景通过 `pointer-events: none` 放行，子元素可用 `auto` 重新启用。

```css
#page    { pointer-events: none; }   /* 全屏背景放行 */
#widgets { pointer-events: none; }   /* 卡片间隙放行 */
.card    { pointer-events: auto; }   /* 可交互组件 */
```

```csharp
bool block = view.ShouldBlockInput(x, y);   // 内部即 Context::GetElementAtPoint
```

返回值严格遵循 RmlUi 的 CSS 语义：声明 `pointer-events: none` 的元素不算命中，
其后代声明 `auto` 时仍可命中；若该位置没有命中任何元素，点击照常穿透。

### 10.2 拦截层

当指针位于可交互元素上时，启用一张全屏透明 UGUI `Image`（`raycastTarget = true`）
与 `GraphicRaycaster`，并把所在 Canvas 的 `sortingOrder` 设得高于游戏 HUD。
这样 EventSystem 会优先命中该图像，游戏便收不到这次点击。指针离开可交互元素后
应立即关闭该层。

演示页在 `LiquidGlassDemo.EnsureInputBlocker` 与 `UpdateInputBlocker` 中实现了这一机制。
需要注意，直接读取 `Input.GetMouseButton` 的逻辑（例如世界交互与相机拖拽）
无法通过 UGUI 拦截。

## 11. 字体

```csharp
WebGuiFonts.LoadDefault(view);   // Liberation Sans + Unifont CJK 子集
// 或者：
view.LoadFont(@"C:\Windows\Fonts\msyh.ttc");
```

CSS 中必须指定 `font-family`，例如 `font-family: "Liberation Sans";`，
否则 RmlUi 可能不绘制任何字形。内置的 CJK 子集只包含常用字；
若需要更大覆盖范围，请自行加载完整字体。

## 12. 常见问题

1. **CSS 尺寸按 content-box 计算。** `width` 不包含内边距与边框，
   因此固定宽度下的 flex 换行结果与 border-box 假设不同。
2. **必须声明 `font-family`。** 缺少该属性时文字可能不显示。
3. **`backdrop-filter` 需要背景。** 未调用 `SetBackdrop` 时玻璃后面是透明的。
4. **在自己的绘制之前抓屏。** 若在 `GUI.DrawTexture` 之后抓屏，
   下一次捕获会包含本覆盖层。
5. **只在主线程调用。** 不要从其他线程调用任何 `WebGuiView` 方法。
6. **在 `LoadDocument` 之前注册滤镜。** CSS 解析阶段就需要实例化器。
7. **渲染发生在 `Update()` 中。** 不要在 `OnGUI` 中调用 `Update()`。
8. **`MaxRenderFps = 0` 会显著增加 CPU 占用。** 它会每帧重绘并上传纹理。
9. **不要让拦截层常开。** 常开会挡住整个屏幕的点击，
   应根据 `ShouldBlockInput` 动态开关。
10. **多个视图彼此独立。** 每个 `WebGuiView` 拥有独立的原生上下文，
    实例越多，内存与重绘成本越高。

## 13. 参考

- 演示页：`src/Presentation/LiquidGlassDemo.cs`。
- 最小消费方：`samples/CuRmlGuiSample/`。
- 能力缺口：`docs/MISSING-FEATURES.zh-CN.md`。
- 架构与渲染语义：`docs/ARCHITECTURE.zh-CN.md`。
