# CuRmlGUI 架构说明

[English](ARCHITECTURE.md) | **简体中文**

本文说明 CuRmlGUI 的分层、边界、渲染语义与设计取舍。接口用法见
[INTEGRATION.zh-CN.md](INTEGRATION.zh-CN.md)。

## 1. 范围与硬性约束

CuRmlGUI 是面向《Casualties: Unknown》的 HTML/CSS 界面库，不属于游戏本体。
下列约束由宿主环境决定，而非设计偏好，并且主导了整体架构。

1. **不执行 JavaScript。** RmlUi 不含脚本引擎，交互一律通过 C# 事件回调完成。
2. **Unity Mono 与 .NET Framework 4.7.2。** 无法加载 .NET 8 程序集。SkiaSharp
   固定为 1.68.3，因为 2.x 的 net462 版本引用
   `System.Runtime.InteropServices.RuntimeInformation`，会引入 Unity Mono 既未提供、
   也无法重定向的 netstandard 1.x 外观程序集。
3. **以 Skia 软件光栅化作为渲染后端。** RmlUi 只负责解析与布局，输出抽象绘制指令；
   `SkiaCompositor` 完成光栅化与合成，产出一张 RGBA8888 位图，经 `Texture2D`
   交给 IMGUI 呈现。
4. **单线程。** RmlUi、Skia 与纹理上传都必须在 Unity 主线程执行。

## 2. 分层结构

```
RML / RCSS（HTML/CSS 方言）
        │
        ▼
RmlUi 6.3（重新编译，静态 CRT）
  · 解析、样式、布局、事件、动画与计时器
  · Render() 输出顶点、索引、纹理句柄、裁剪区域、图层、滤镜、裁剪遮罩与着色器
        │  RenderInterface（C++ 抽象接口）
        ▼
webgui_shim.cpp（纯 C ABI，本仓库）
  · 逐项实现 RenderInterface，并把每次调用转发到 C 函数指针
  · 不跨越 DLL 边界传递 STL 或 C++ 类型；字符串用 malloc 分配，由 wg_free_string 释放
        │  裸函数指针（cdecl）
        ▼
C# / SkiaCompositor（SkiaSharp 1.68.3）
  · 状态机：裁剪区域、变换、图层栈、裁剪遮罩、滤镜、着色器
  · 区域快照、半分辨率滤镜、表面池、位图翻转
        │  RGBA8888（Unity 行序，bottom-up）
        ▼
Unity Texture2D ──► IMGUI（GUI.DrawTexture / WebGuiView.PresentOnGui）
```

RmlUi 自身不绘制任何像素。`RenderInterface` 的实现相当于它的显卡驱动，
适配层属于渲染后端的一部分，而非额外的转换层。

## 3. 渲染接口语义

实现以官方 `RmlUi_Renderer_GL3.cpp` 为参照逐函数对照，并用等价的 Skia 能力表达。
那些看似可用、但偏离官方语义的实现，会在圆角、图层与滤镜上产生缺陷。

### 3.1 裁剪区域

- 后端保证 `EnableScissorRegion(true)` 之后必定紧跟 `SetScissorRegion()`，
  因此可以忽略启用标志；`enable=false` 则立即取消裁剪。
- `SetScissorRegion()` 使用窗口坐标，与当前变换无关。
- Skia 侧在每次绘制前调用 `canvas.ClipRect(scissor, Intersect)`。

### 3.2 裁剪遮罩（圆角裁剪）

RmlUi 的 `overflow: hidden`、`border-radius` 与 `clip` 并不通过裁剪区域实现，
而是通过 `EnableClipMask` 与 `RenderToClipMask(geometry, op)` 传递几何遮罩。
官方 GL3 后端使用模板缓冲。

| 操作 | GL3 模板语义 | Skia 等价实现（alpha 遮罩） |
| --- | --- | --- |
| `Set` | 清零后写入 1 | 清空包围盒，再以 alpha 1 绘制几何 |
| `SetInverse` | 置 1 后写入 0 | 铺满包围盒，再擦除几何 |
| `Intersect` | 自增并提高参考值 | 以 `DstIn` 乘以几何覆盖率 |

- 遮罩是与视口等大的 alpha 位图，通过 `FromPixels` 与 Skia 共享。
- 启用遮罩期间，每次 `DrawGeometry` 与 `CompositeLayers` 都执行
  `SaveLayer()`、绘制内容、`DrawImage(mask, DstIn)`、`Restore()`。
- 清空与铺满只作用于几何包围盒与裁剪区域的交集，而非整个视口。
- 这两个回调是圆角裁剪能够工作的前提：缺少它们时，RmlUi 会静默丢弃遮罩，
  所有玻璃面板都会退化为矩形。

### 3.3 图层与滤镜

- `PushLayer` 分配或复用与视口等大的离屏 `SKSurface`，并且只清除当前裁剪区域，
  因为官方语义只要求新图层在该区域内透明。
- `PopLayer` 将表面归还到按尺寸分桶的表面池。
- `CompileFilter` 映射为 `SKImageFilter`（模糊、投影、颜色矩阵或自定义滤镜），
  同时保留原始参数，以便在低分辨率下重建。
- `CompositeLayers(source, dest, blend, filters)` 的规则如下：
  - 无滤镜时，对源图层取快照（写时复制）并按裁剪区域直接绘制到目标，不创建中间表面；
  - 有滤镜时，只对裁剪区域取快照并应用滤镜；
  - 区域较大且含模糊或阴影时，快照与滤镜在半分辨率下完成，再拉伸到目标区域。
- 不要在裁剪区域之外追加 `sigma * 3` 的内边距。`ApplyScissorRegionForBackdrop`
  与 `ApplyClippingRegion` 已经计入 ink overflow；额外扩张会把区域放大约 1.5 倍
  （实测由 662×210 变为 878×426），而 Skia 的 CPU 模糊成本与面积成正比。
- `SaveLayerAsTexture` 按裁剪区域截取当前图层，因此纹理尺寸等于裁剪区域尺寸。
- `SaveLayerAsMaskImage` 将当前图层保存为遮罩滤镜（`DstIn`）。

### 3.4 着色器（渐变）

官方 GL3 后端通过 `CompileShader` 与 `RenderShader` 实现 `linear-gradient`、
`radial-gradient` 与 `conic-gradient`，`decorator` 与 `mask-image` 都依赖它们。
本项目映射如下：

- 几何的 `tex_coord` 即渐变坐标系（`DecoratorGradient` 中
  `vertex.tex_coord = position - render_offset`），因此 Skia 直接以 UV 采样渐变。
- `linear`：`CreateLinearGradient(p0, p1)`。
- `radial`：半径为 1 的单位圆配合局部矩阵 `diag(1 / radius)`，同时支持椭圆。
- `conic`：`CreateSweepGradient`，起始角通过 `atan2` 对齐，
  因为 RmlUi 将 `t = 0` 定义在十二点方向。
- `repeating` 映射为 `SKShaderTileMode.Repeat`。
- 其他着色器类型（例如 `shader: creation`）暂未实现。

### 3.5 变换

- 官方 `SetTransform(const Matrix4f*)` 接收十六个列主序浮点数，`nullptr` 表示单位矩阵。
- 它只作用于携带几何句柄的绘制（`RenderGeometry`、`RenderToClipMask`），
  不影响裁剪区域与 `CompositeLayers`。
- Skia 矩阵按 `ScaleX=m00, SkewX=m01, TransX=m03, SkewY=m10, ScaleY=m11, TransY=m13`
  组装。目前只支持二维仿射变换，因此 CSS 的 `translate`、`rotate`、`scale` 与
  `skew` 可用，透视项被忽略。

### 3.6 纹理

- `GenerateTexture` 将 RmlUi 提供的预乘 RGBA 像素登记为 `SKImage`。
- `LoadTexture` 交由 Skia 解码（PNG、JPEG、TGA、WebP），并剥离内存文档伪协议前缀。
- `ReleaseTexture` 销毁图像与缓存的纹理着色器。

## 4. 坐标系与像素格式

- **逻辑坐标**采用左上原点、Y 轴向下，与 RmlUi 和 Skia 一致。
- **基座位图**在内存中按 bottom-up 存储，以匹配 Unity `Texture2D` 的行序。
  画布变换 `Translate(0, h); Scale(1, -1)` 完成翻转，因此顶点、裁剪与 UV
  自动保持一致。
- **离屏图层**使用单位变换与 top-down 存储，图层之间的合成方向必须一致。
- **基座快照**从 bottom-up 转 top-down 时，既需要偏移源矩形的上下位置，
  也需要在区域内部再做一次翻转；只做前者会倒置区域内容。
- **顶点颜色**以预乘形式 `0xRRGGBBAA` 传入，而 Skia 期望非预乘顶点色，
  因此逐顶点反预乘。
- **`DrawVertices` 的第二个参数**表示纹理与顶点色的组合模式，必须使用
  `SKBlendMode.Modulate`；`SrcOver` 会把透明像素绘制成实心白块。

## 5. 性能模型

CPU 渲染的成本主要由像素面积决定，因此所有优化都围绕「不触碰整个视口」展开。

1. **区域快照。** 裁剪区域有效时只对该区域取快照，无滤镜的合成不分配中间表面。
2. **不重复扩张内边距。** 裁剪区域已包含 ink overflow（见 3.3）。
3. **半分辨率滤镜。** 大范围模糊与阴影的像素量为四分之一，观感差异几乎不可见。
4. **按裁剪区域清图层。** 不再每次清空 8.7 MB 的完整表面。
5. **按尺寸分桶的表面池。** 尺寸交替时不再反复销毁与分配。
6. **纹理着色器缓存。** 字形图集每帧复用同一个 `SKShader`。
7. **重绘节流。** `MaxRenderFps`（默认 20）限制由输入、悬停与动画触发的重绘；
   背景更新只安排一帧。
8. **GPU 液态玻璃。** 玻璃不经过 CPU，详见 5.1。

在 1920×1080、全部玻璃面板可见的条件下实测，渲染平均每帧 2–4 ms，
游戏帧率接近隐藏覆盖层时的数值。未做区域快照与半分辨率滤镜之前，
同一场景每帧需要 70–125 ms，约 10 fps。

默认不使用 Skia 高斯模糊，是因为它在 CPU 上对 400×260 区域约需 2.6 ms、
800×520 约需 14.9 ms、全屏约需 81 ms，大面积玻璃无法承受。

### 5.1 GPU 液态玻璃管线

`WebGuiView.EnableGpuGlass(ids)` 与 `PresentOnGui()` 建立的顺序是固定的，
每一步都依赖前一步的结果。

1. 以 `alpha = 0` 绘制一次界面，促使 IMGUI 合并该帧，使随后的抓屏包含游戏自身的
   UGUI，而不包含本覆盖层。
2. 调用 `ScreenCapture.CaptureScreenshotIntoRenderTexture(rt)`，拷贝过程停留在 GPU。
3. 在 `Update` 中把抓屏依次降采样到 1/2、1/4 与 1/8。在 1/8 分辨率上执行可分离的
   八抽头滤波，采样点间距为一个像素，以保证核的密度并避免重影。抽头通过两两取
   50% 平均的归约树合成，因此每一步的 alpha 都保持为 1，结果不会变暗。
   随后按 1/8 → 1/4 → 1/2 → 全屏逐级放大。
4. `Draw` 使用程序化圆角矩形网格绘制（三角扇，边框 UV 向外偏移以产生边缘折射），
   并设置 `GL.sRGBWrite = true`，因为自定义 GL 绘制不会像 `GUI.DrawTexture`
   那样自动执行 sRGB 写入转换。
5. 最后绘制界面纹理。

必须使用密集采样，原因是两种直观做法都会失败：

- 用双线性插值降采样再放大，只会把每个低分辨率单元变成斜坡，
  单元边界上的斜率突变仍然存在，表现为方块纹理；
- 递归 `±d` 卷积得到的核是稀疏的，采样点间距为 `2d`，
  表现为若干错位的图像副本。

等间距采样配合等权合成可以近似连续的盒式核，同时避免上述两种缺陷。

该管线每帧约需 24 次八分之一分辨率的拷贝，以及三次降采样与三次升采样，
且没有 CPU 回读与纹理上传。代价是内存：每个启用的视图持有若干张全屏渲染纹理，
1080p 下约 25 MB，因此建议每个屏幕只启用一个视图。

## 6. 输入与命中测试

- 宿主轮询 Unity `Input`，将事件转换为左上原点坐标后转发给 RmlUi。
- 覆盖层使用 IMGUI 绘制，不参与 UGUI 射线检测，因此游戏自身的 UGUI
  会收到同一批点击，除非这些点击被拦截。
- `WebGuiView.ShouldBlockInput(x, y)` 调用 `Context::GetElementAtPoint`，
  完整遵循 CSS 的 `pointer-events` 语义：
  - 元素或其祖先声明 `pointer-events: none` 时不算命中，方法返回 false；
  - 后代元素显式声明 `pointer-events: auto` 时仍可命中；
  - 命中时返回 true，这也是可交互元素的默认行为。
- 因此每个组件默认拦截，可在 CSS 中单独声明放行，无需自定义属性：

  ```css
  #page    { pointer-events: none; }   /* 背景放行 */
  #widgets { pointer-events: none; }   /* 卡片间隙放行 */
  .card    { pointer-events: auto; }   /* 可交互组件 */
  ```

- 宿主在此基础上实现拦截层。演示页使用一张全屏透明 UGUI `Image` 与
  `GraphicRaycaster`，`sortingOrder` 设为 32760，并且只在指针位于可交互元素上时启用。
- 限制：直接读取 `Input.GetMouseButton` 的逻辑（例如世界交互与相机拖拽）
  无法通过 UGUI 拦截，需要 Harmony 或由模组自行处理。

## 7. 生命周期与所有权

- 回调委托必须保存在字段中，且生命周期不短于视图；否则垃圾回收可能在原生层
  仍持有函数指针时回收委托。
- `NativeLoader.EnsureLoaded()` 必须在 `wg_context_create` 之前执行。它会安装
  AppDomain 级的 `AssemblyResolve`，从插件目录解析 `System.Memory`、`System.Buffers`
  与 SkiaSharp，并且必须发生在任何 SkiaSharp 类型被 JIT 编译之前。
- 覆盖层组件挂在插件自身的 `GameObject` 上。在 BepInEx `Awake` 阶段创建新的根对象
  并调用 `DontDestroyOnLoad`，会导致该对象在首个场景加载时被销毁。
- `Dispose` 依次释放图层、滤镜与遮罩、纹理与着色器、画布与位图，最后释放原生上下文。

## 8. 已知限制

- 不支持 JavaScript，也没有 DOM 查询接口，只能按元素 id 更新文本。
- 变换仅支持二维仿射矩阵，`perspective` 被忽略。
- 未实现 `shader: creation` 等自定义 GLSL 着色器。
- `LoadTexture` 支持 Skia 可解码的格式，其中包括 TGA。
- `SaveLayerAsTexture` 与 `mask-image` 已实现，但演示页未使用。
- 输入拦截仅覆盖 UGUI，直接轮询 `Input` 的代码无法被拦截。
- GPU 玻璃依赖 IMGUI 的 `OnGUI` 合帧机制，建议每个屏幕只启用一个视图。

## 9. 构建与离屏自检

```powershell
powershell -ExecutionPolicy Bypass -File scripts\fetch-rmlui.ps1
powershell -ExecutionPolicy Bypass -File scripts\build-native.ps1
powershell -ExecutionPolicy Bypass -File scripts\deploy.ps1 -Build
```

`tools/render-test` 是离屏自检工具。它逐像素断言坐标、颜色顺序、预乘、
字形绘制、裁剪、`Modulate` 混合、内外阴影、文字效果、背景朝向以及
CSS 兼容重写，使渲染缺陷在游戏启动前暴露。运行前需将新生成的 `WebGUINative.dll`
复制到测试输出目录，否则 `DllImport` 会解析到旧副本。
