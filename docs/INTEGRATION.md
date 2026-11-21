# CuRmlGUI 下游模组使用说明

> 面向使用 CuRmlGUI 的模组开发者。内部实现见 [ARCHITECTURE.md](ARCHITECTURE.md)。

## 1. 引入

CuRmlGUI 是**库模组**：把它放在 `BepInEx/plugins/CuRmlGUI/`（含 `WebGUINative.dll`、
SkiaSharp、字体资源），你的模组只引用 `CuRmlGUI.dll`：

```xml
<Reference Include="CuRmlGUI">
  <HintPath>...\BepInEx\plugins\CuRmlGUI\CuRmlGUI.dll</HintPath>
  <Private>false</Private>
</Reference>
```

**不要**把 `CuRmlGUI.dll`/`WebGUINative.dll` 复制进自己的插件目录——原生库与字体
路径按插件目录解析，重复副本会导致两套原生运行时。

运行时依赖（`SkiaSharp.dll`/`libSkiaSharp.dll`/`System.Memory`/`System.Buffers`）
由 CuRmlGUI 目录提供；`NativeLoader.EnsureLoaded()` 会安装 **AppDomain 级
AssemblyResolve**，按简单名从 CuRmlGUI 插件目录解析这些依赖（含 SkiaSharp）。
因此：
- 你的模组**不要**复制 `WebGUINative.dll` / `libSkiaSharp.dll` / 字体；
- 如需自定义滤镜而引用 `SkiaSharp`，只加编译期 `<Reference>` 即可，运行时会由
  CuRmlGUI 的解析器提供；不要让两个插件各带一份 SkiaSharp。

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
        WebGuiFonts.LoadDefault(_view);              // Latin + CJK 回退
        _view.LoadDocument(
            "<rml><body><div id='panel'>Hello</div></body></rml>",
            "#panel { position:absolute; left:40px; top:40px; padding:12px; " +
            "background:rgba(20,24,40,0.85); color:#fff; border-radius:10px; }");
        _view.ElementEvent += (type, id) => Debug.Log($"{type} / {id}");
    }

    private void Update()
    {
        var mp = Input.mousePosition;
        float x = mp.x, y = Screen.height - mp.y;    // 左上原点
        _view.MouseMove(x, y);
        for (int b = 0; b <= 2; b++)
        {
            if (Input.GetMouseButtonDown(b)) _view.MouseDown(x, y, b);
            if (Input.GetMouseButtonUp(b))   _view.MouseUp(x, y, b);
        }
        _view.Update();                               // 有变化才重绘 + 上传纹理
    }

    private void OnGUI()
    {
        if (_view?.Texture == null) return;
        GUI.depth = -1000;
        GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), _view.Texture);
    }

    private void OnDestroy() => _view?.Dispose();
}
```

## 3. 生命周期与线程

- 所有 `WebGuiView` 调用都在 **Unity 主线程**。
- `new WebGuiView(w,h,dpi)` 创建原生上下文；`Dispose()` 释放（必须）。
- `SetSize(w,h,dpi)` 在分辨率变化时调用（会重建位图/图层池）。
- `Update()` 返回本帧是否真的重绘并上传了纹理；不必自己判断脏状态。
- 不要跨线程调用；不要在 `OnGUI` 里调用 `Update()`。

## 4. 呈现

```csharp
GUI.depth = -1000;  // 尽量靠上；IMGUI 在所有 UGUI 之后绘制
GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), view.Texture);
```

`Texture` 是 RGBA32、**bottom-up** 行序，宽高等于 `view.Width/Height`。
不要把纹理交给 Unity 再缩放——UI 已在 Skia 里按逻辑像素合成好。

## 5. 输入

坐标：**左上原点、逻辑像素**。

```csharp
view.MouseMove(x, y);                 // 内部对未变化坐标去重
view.MouseDown(x, y, button);         // 0=左 1=右 2=中
view.MouseUp(x, y, button);
view.MouseWheel(dx, dy);
view.KeyDown(keyCode, modifiers);     // UnityEngine.KeyCode
view.KeyUp(keyCode, modifiers);
view.TextInput(char);                 // 代理对请用 TextInputCodepoint
view.TextInputCodepoint(uint codePoint);
```

键盘/文本：只转发 `Input.inputString` 里的可打印字符；退格/回车等由 `KeyDown` 处理，
避免重复。修饰键用 `Event.current` 或 `Input.GetKey` 组合。

## 6. 按需渲染与帧率

- 无输入、无动画、无 backdrop 更新时**不重绘、不上传纹理**。
- `MaxRenderFps`（默认 **50**）同时约束输入/hover 与动画的重绘频率；
  设为 `0` 表示不限速（不推荐）。
- 重绘成本与内容复杂度相关；大面积玻璃时建议 20–30。
- `view.TakeRenderStats()` 返回分项耗时（draw/composite/mask/layers），用于诊断。

## 7. 毛玻璃（backdrop-filter）

### 7.1 注入 backdrop

`backdrop-filter` 的输入是**你自己抓的游戏画面**；它只作为模糊源，**不会**进入最终纹理。

```csharp
// rgba: top-down 行序、RGBA8888；destRect 是该图在视口中的逻辑区域
view.SetBackdrop(rgba, srcW, srcH, 0, 0, Screen.width, Screen.height);
view.ClearBackdrop();
```

抓屏建议：

- 用 `ScreenCapture.CaptureScreenshotAsTexture()` 在 **OnGUI 的 Repaint 阶段、
  自己 `GUI.DrawTexture` 之前**抓最终 framebuffer（与屏幕 1:1 对齐，且不含自己的 UI）。
- **不要**用 `Graphics.Blit(null, rt)` / `CaptureScreenshotIntoRenderTexture`：
  URP 下会读到中间渲染目标，导致背景错位/缩放。
- 节流：玻璃背景 10–20fps 足够；抓屏是同步读回，成本高于重绘。

### 7.2 Fake / Real

```csharp
view.BackdropBlurMode = BackdropBlurMode.Fake;   // 默认：不执行任何模糊
view.BackdropBlurMode = BackdropBlurMode.Real;   // Skia 高斯（重）
```

**Fake 的含义**：compositor 会**跳过** backdrop 链上的 `blur()`，只做双线性放大。
所以“模糊强度”完全由你给的 backdrop 分辨率/预处理决定。推荐配方：

1. 抓屏降采样 **1/2**，每个目标像素对源 cell 求**等权平均**（盒式降采样，不是点采样）；
2. 在 1/2 分辨率上做**可分离盒式平滑**（水平+垂直滑动窗口，半径可调，O(N)）；
3. 交给 `SetBackdrop`，compositor 双线性放大到元素区域。

参考实现：`src/Presentation/LiquidGlassDemo.cs` 的 `ReadBackdrop` / `BoxBlur`。
关键参数：降采样除数 2，平滑半径 6（等效观感半径 ≈ 12px）。

> Real 模式的成本正比于元素面积：400×260≈2.6ms、800×520≈14.9ms、全屏≈81ms（CPU），
> 大面积玻璃慎用。

## 8. 自定义滤镜

CSS：`filter: myfilter(p0 p1 ...)` 或 `backdrop-filter: ...`。
必须在 `LoadDocument` **之前**注册（RmlUi 解析 CSS 时需要 instancer）：

```csharp
view.RegisterFilter("myfilter", 3, args =>
{
    float sigma = args[0], tint = args[1], amount = args[2];
    // 返回 SKImageFilter + 向外溢出的像素
    return new CustomFilterResult(
        SKImageFilter.CreateColorFilter(SKColorFilter.CreateColorMatrix(...)),
        padPx: 0);
});
```

```css
.card { filter: myfilter(4 0.2 0.8); }
```

- `arity` = CSS 参数个数（0–16）；`args` 顺序与 `p0..pN` 一致。
- `PadPx` 用于扩大滤镜计算区域（模糊/位移会超出元素 bbox）。
- 滤镜按顺序应用；`backdrop-filter` 的链只作用于 backdrop（source=基座图层）。

## 9. 事件与 DOM

```csharp
view.ElementEvent += (type, id) =>
{
    // type: click / mouseover / mouseout / mousedown / mouseup / change / focus / blur
    if (type == "change" && id == "volume") { /* ... */ }
};

view.SetElementText("status", "已连接");   // 按 id 回写文本（HTML 转义）
```

RmlUi 没有 JS/DOM 查询 API；需要动态内容时用 `SetElementText` 或重新 `LoadDocument`。

## 10. 输入拦截（点击穿透）

我们的 UI 是 IMGUI，不参与 UGUI 射线检测，所以游戏自己的菜单/HUD 会收到同一批点击。
两种处理方式：

### 10.1 查询式（推荐）

用标准 CSS `pointer-events` 声明：**组件默认可交互（拦截）**，容器/背景声明
`pointer-events: none` 即可放行；子元素仍可显式改回 `auto`。

```css
#page    { pointer-events: none; }   /* 全屏背景放行 */
#widgets { pointer-events: none; }   /* 卡片间隙放行 */
.card    { pointer-events: auto; }   /* 组件拦截（默认值就是 auto） */
```

```csharp
bool block = view.ShouldBlockInput(x, y);   // 内部即 RmlUi GetElementAtPoint
```

命中规则完全由 RmlUi 的 CSS 语义决定：`pointer-events:none` 的元素不命中，
其子元素若为 `auto` 仍可命中；未命中任何元素 → 放行。

### 10.2 拦截层

在指针位于可拦截元素上时，启用一张全屏透明 UGUI `Image`（`raycastTarget=true`）
+ `GraphicRaycaster`，Canvas `sortingOrder` 高于游戏 HUD，让 EventSystem 的射线命中它，
从而吞掉游戏 UGUI 的点击。指针移到放行区域时**立即关闭**，点击照常穿透。

演示页实现见 `LiquidGlassDemo.EnsureInputBlocker` / `UpdateInputBlocker`。
注意：直接读 `Input.GetMouseButton` 的逻辑（世界交互/相机拖拽）无法被 UGUI 拦截。

## 11. 字体

```csharp
WebGuiFonts.LoadDefault(view);   // 发行字体：Liberation Sans + Unifont CJK 子集
// 或：
view.LoadFont(@"C:\Windows\Fonts\msyh.ttc");
```

CSS 里必须写 `font-family`（例如 `font-family: "Liberation Sans";`），
否则 RmlUi 可能不画字形。CJK 子集只含常用字；缺字时请自行加载完整字体。

## 12. 常见坑

1. **CSS 尺寸是 content-box**：`width` 不含 padding/border；写死宽度时记得算上，
   否则 flex 换行会与预期不同。
2. **`font-family` 必写**，否则文字可能不显示。
3. **`backdrop-filter` 需要 backdrop**：没调用 `SetBackdrop` 时玻璃后面是透明的。
4. **抓屏时机**：必须在自己的 `GUI.DrawTexture` 之前，否则会抓到自己上一帧的 UI。
5. **不要在非主线程调用**任何 `WebGuiView` 方法。
6. **`RegisterFilter` 必须在 `LoadDocument` 前**。
7. **`Update()` 之外的绘制不会发生**；不要在 `OnGUI` 里调用 `Update`。
8. **`MaxRenderFps=0`** 会每帧全量重绘 + 上传，CPU 占用显著上升。
9. **拦截层不要常开**：常开会挡住整个屏幕的点击；按 `ShouldBlockInput` 动态开关。
10. **多实例**：每个 `WebGuiView` 是独立原生上下文；数量多时注意内存与重绘成本。

## 13. 参考

- 演示页：`src/Presentation/LiquidGlassDemo.cs`（iOS 控制台风格、伪模糊、输入拦截）。
- 最小消费方：`samples/CuRmlGuiSample/`。
- 能力边界：`docs/MISSING-FEATURES.md`。
- 架构与渲染语义：`docs/ARCHITECTURE.md`。
