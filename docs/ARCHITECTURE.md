# CuRmlGUI 架构声明（研究者文档）

> 面向维护者/研究者：说明本项目的**分层、边界、渲染语义与设计取舍**。
> 下游模组怎么用见 [INTEGRATION.md](INTEGRATION.md)。

## 1. 目标与硬约束

CuRmlGUI 是 CU（Casualties: Unknown）的 **HTML/CSS UI 库**，不是游戏本体的一部分。

硬约束（全部由环境决定，不是偏好）：

1. **不执行 JavaScript**。RmlUi 本身不含脚本引擎，交互只走 C# 事件回调。
2. **不依赖 CCL / 官方 C# 绑定**。RmlUi 官方只有 C++ GPU 后端（GL3/Vulkan/DX11/12/Metal）；
   唯一的 C# 绑定 `Chorizite/RmlUi.Net` 是 RmlUi 4.x 时代产物，缺 filter/layer/clip-mask。
   因此我们**重编译 RmlUi 6.3（MIT）+ 自写纯 C ABI shim**。
3. **Unity Mono / net472**。不能引入 net8 资产；SkiaSharp 只能用 **1.68.3**
   （2.x 的 net462 资产引用 `System.Runtime.InteropServices.RuntimeInformation`，
   会拖入整套 netstandard 1.x 门面，Unity 的 Mono 没有且无绑定重定向）。
4. **渲染后端是 Skia（CPU）**。RmlUi 只负责解析/布局，产出抽象绘制指令；
   光栅化/合成由 `SkiaCompositor` 完成，结果是一张 RGBA8888 位图，
   经 `Texture2D` 交给 IMGUI 呈现。
5. **主线程**。RmlUi、Skia、Unity 纹理上传全部必须在 Unity 主线程调用。

## 2. 分层

```
RML / RCSS（HTML/CSS 方言）
        │
        ▼
RmlUi 6.3（重编译，/MT 静态 CRT）
  · 解析、样式、布局、事件、动画/计时器
  · 每帧 Render() 产出：顶点/索引/纹理句柄/scissor/图层/滤镜/clip mask/shader
        │  RenderInterface（C++ 抽象接口）
        ▼
webgui_shim.cpp（纯 C ABI，本仓库）
  · 逐条实现 RenderInterface，把指令翻译成 C 回调
  · 不跨 DLL 传 STL/C++ 类型；字符串 malloc + wg_free_string
        │  裸函数指针（cdecl）
        ▼
C# / SkiaCompositor（SkiaSharp 1.68.3）
  · 状态机：scissor / transform / 图层栈 / clip mask / 滤镜 / shader
  · 区域快照、半分辨率滤镜、surface 池、位图翻转
        │  RGBA8888（Unity 行序 bottom-up）
        ▼
Unity Texture2D ──► IMGUI（GUI.DrawTexture）
```

**关键点：RmlUi 自身不画一个像素。** 我们实现的 `RenderInterface` 就是它的“显卡驱动”。
shim 是渲染后端的一部分，不是额外适配层。

## 3. RenderInterface 实现语义

实现以官方 `RmlUi_Renderer_GL3.cpp` 为参照（逐函数对照），用 Skia 的等价能力落地。
任何“看起来能跑”但偏离官方语义的实现都会在圆角、图层、滤镜上出问题。

### 3.1 Scissor

- 官方约定：`EnableScissorRegion(true)` 后**一定会**紧跟 `SetScissorRegion()`，
  所以 `enable=true` 可忽略；`enable=false` 立即取消裁剪。
- `SetScissorRegion()` 用**窗口坐标**，与 transform 无关。
- Skia：`canvas.ClipRect(scissor, Intersect)`，每次绘制前套用。

### 3.2 Clip mask（圆角裁剪）

RmlUi 的 `overflow:hidden`/`border-radius`/`clip` 裁剪**不是** scissor，而是
`EnableClipMask` + `RenderToClipMask(geometry, op)` 的几何遮罩。官方 GL3 用 stencil：

| 操作 | GL3 stencil 语义 | Skia 等价（alpha 遮罩） |
| --- | --- | --- |
| `Set` | 清 0 后写 1 | 清空包围盒 → 画几何（alpha=1） |
| `SetInverse` | 清 1 后写 0 | 铺满包围盒 → 擦除几何 |
| `Intersect` | INCR 后 ref+1 | `DstIn` 乘几何覆盖度 |

- 遮罩是一张与视口同尺寸的 alpha 位图（`SKBitmap`），用 `FromPixels` 共享给 Skia。
- 启用遮罩时，每个 `DrawGeometry` 与 `CompositeLayers` 都：`SaveLayer()` → 画内容 →
  `DrawImage(mask, DstIn)` → `Restore()`。
- 清空/铺满只做**几何包围盒 ∩ scissor**，不是整张视口。
- 这是圆角裁剪缺失的根因修复：没有这两个回调时，RmlUi 的遮罩被静默丢弃，玻璃全是方的。

### 3.3 图层与滤镜

- `PushLayer`：分配/复用一个离屏 `SKSurface`（视口大小），只清当前 scissor 区域
  （官方语义：新图层只需在当前 scissor 内为透明）。
- `PopLayer`：归还 surface 池（按尺寸分桶）。
- `CompileFilter`：映射到 `SKImageFilter`（blur / drop-shadow / 颜色矩阵 / 自定义）。
  同时记录原始参数（sigma/offset/color），以便低分辨率重建。
- `CompositeLayers(source, dest, blend, filters)`：
  - 无滤镜：源图层 `Snapshot()`（copy-on-write）直接按 scissor 画到目标，不建中间表面；
  - 有滤镜：只对 **scissor 区域**取快照与滤镜；
  - 大区域 + 模糊/阴影：**直接在半分辨率上取快照并计算**，最后拉伸到目标区域。
- 注意：**不要**在 scissor 之外再加 `sigma*3` padding。RmlUi 的
  `ApplyScissorRegionForBackdrop` / `ApplyClippingRegion` 已经把 ink overflow 算进
  scissor；再加一圈会把区域放大约 1.5 倍（实测 662×210 → 878×426），
  而 Skia CPU 模糊成本正比于面积。
- `SaveLayerAsTexture`：按当前 scissor 截取当前图层（官方语义；纹理尺寸=scissor 尺寸）。
- `SaveLayerAsMaskImage`：把当前图层存为 mask 滤镜（`DstIn`）。

### 3.4 Shader（渐变）

官方 GL3 的 `CompileShader/RenderShader` 承载 `linear/radial/conic-gradient`
（RmlUi 的 `decorator`/`mask-image` 都靠它）。我们的映射：

- 几何的 `tex_coord` 就是渐变坐标系（`DecoratorGradient` 里
  `vertex.tex_coord = position - render_offset`），因此 Skia 用 UV 直接采样渐变。
- `linear`: `CreateLinearGradient(p0, p1)`
- `radial`: 半径 1 的单位圆 + 局部矩阵 `diag(1/radius)`，支持椭圆
- `conic`: `CreateSweepGradient`，起始角按 `atan2` 对齐（RmlUi 的 t=0 在 12 点方向）
- `repeating` → `SKShaderTileMode.Repeat`
- 其他 shader（如 `shader: creation`）暂不支持。

### 3.5 变换

- 官方 `SetTransform(const Matrix4f*)`：column-major 16 float；`nullptr` = identity。
- 只作用于**带几何句柄的绘制**（`RenderGeometry` / `RenderToClipMask`），
  不影响 scissor 与 `CompositeLayers`。
- 映射到 Skia：`ScaleX=m00, SkewX=m01, TransX=m03, SkewY=m10, ScaleY=m11, TransY=m13`；
  目前只支持 2D 仿射（CSS translate/rotate/scale/skew），透视项忽略。

### 3.6 纹理

- `GenerateTexture`：RmlUi 提供预乘 RGBA 像素，登记为 `SKImage`。
- `LoadTexture`：交给 Skia 解码（PNG/JPG/TGA/WebP），剥掉内存文档伪协议前缀。
- `ReleaseTexture`：销毁图像与缓存的 texture shader。

## 4. 坐标系与像素格式

- **逻辑坐标**：左上原点、Y 向下（RmlUi 与 Skia 一致）。
- **基座位图**：内存按 bottom-up（Unity `Texture2D` 行序）存储；通过画布 CTM
  `Translate(0,h); Scale(1,-1)` 完成翻转，因此顶点/裁剪/UV 全部自动一致。
- **离屏图层**：identity CTM、top-down 存储；层间合成方向必须一致。
- **基座快照**：bottom-up → top-down 需要“源矩形上下偏移 + 区域内部再翻转”；
  只做前者会把区域内容倒置。
- **顶点色**：RmlUi 给预乘 `0xRRGGBBAA`；Skia 顶点色接收非预乘，逐顶点反预乘。
- **DrawVertices 第二参数**：是“纹理 × 顶点色”的组合模式，必须用
  `SKBlendMode.Modulate`；`SrcOver` 会把透明像素画成实心白块。

## 5. 性能模型

CPU 渲染的成本由**像素面积**主导，所有优化都围绕“别碰整张视口”：

1. **区域快照**：只要 scissor 有效就只取 scissor 区域；无滤镜的合成也不建中间表面。
2. **去掉重复 padding**：scissor 已含 ink overflow（见 3.3）。
3. **半分辨率滤镜**：大区域模糊/阴影面积 /4（观感几乎无差别）。
4. **图层只清 scissor**：不再每次清 8.7MB。
5. **surface 池按尺寸分桶**：避免尺寸交替时反复销毁/重建。
6. **texture shader 缓存**：文字图集每帧复用同一个 `SKShader`。
7. **抓屏与重绘节流**：`MaxRenderFps`（默认 50）同时约束输入/hover 与动画的重绘；
   backdrop 更新只 linger 1 帧。
8. **GPU 液态玻璃（默认档位）**：玻璃不经 CPU —— 详见 §5.1。

实测（1920×1080、玻璃全开）：render 均摊 2–4ms/帧，游戏 FPS 与不开 UI 基本一致。
早期未做区域快照/半分辨率时是 70–125ms/帧、~10fps。

> 为什么不用 Skia 高斯做默认：实测 400×260 ≈ 2.6ms、800×520 ≈ 14.9ms、
> 全屏 ≈ 81ms（CPU），大面积玻璃不可接受。

### 5.1 GPU 液态玻璃链

`WebGuiView.EnableGpuGlass(ids)` + `PresentOnGui()` 的固定顺序（缺一不可）：

1. `alpha = 0` 画一次 UI —— 触发 IMGUI 合帧，让后续抓屏拿到「含游戏 UGUI、不含我们 UI」的最终屏幕；
2. `ScreenCapture.CaptureScreenshotIntoRenderTexture(rt)` —— GPU 拷贝，**不读回 CPU**；
3. `Update` 里做模糊：全屏 → 1/2 → 1/4 → 1/8；在 1/8 上**可分离密集 8 tap**，
   采样点间距 1 个 1/8 像素（相邻采样 → 无重影），用两两 50% 平均的归约树**等权**合成
   （每步 alpha 恒为 1 → **不变暗**，无需亮度补偿）；再 1/8 → 1/4 → 1/2 → 全屏逐级放大；
4. `Draw` 用程序化圆角矩形网格（三角扇 + 边缘 UV 偏移＝折射）采样模糊图，
   `GL.sRGBWrite = true`（自定义 GL 绘制不会像 `GUI.DrawTexture` 自动处理 sRGB 写入）；
5. 最后画 UI 纹理。

为什么是「密集 8 tap」而不是「缩小再放大」或「±d 递归」：

- 缩小再放大（双线性）只把低分辨率格子拉成斜坡，**格子边界仍是斜率突变** → 可见方块；
- `±d` 递归卷积得到的核是**稀疏格点**（间距 2d）→ 多个错开的副本 → **重影**；
- 密集等间距采样 + 等权归约，等效一块连续盒式核，既无方块也无重影。

成本：约 24 次 1/8 分辨率 blit + 3 次降/升采样/帧；无 CPU 读回、无纹理上传。
**代价**：每个启用的视图占用数张全屏 RT（1080p 约 25MB），一个屏幕建议只启用一个。

## 6. 输入与命中测试

- 输入由宿主（模组）轮询 Unity `Input` 后转发给 RmlUi（坐标换成左上原点）。
- 我们的 UI 用 IMGUI 绘制，不参与 UGUI 射线检测 → 游戏自己的 UGUI 会收到“穿透”点击。
- 库提供 `WebGuiView.ShouldBlockInput(x, y)`：内部调用 RmlUi 的
  `Context::GetElementAtPoint`，**完整遵循 CSS `pointer-events`**：
  - 元素（或继承链）为 `pointer-events: none` → 不命中，返回 false（放行）；
  - 子元素显式 `pointer-events: auto` 时仍可命中（标准 CSS 语义）；
  - 命中 → true（默认拦截）。
- 因此“每个组件默认拦截、可单独声明放行”直接用 CSS 表达，无需自定义属性：
  ```css
  #page    { pointer-events: none; }   /* 背景放行 */
  #widgets { pointer-events: none; }   /* 间隙放行 */
  .card    { pointer-events: auto; }   /* 组件拦截（默认值就是 auto） */
  ```
- 宿主据此实现拦截层（演示页用一张全屏透明 UGUI `Image` + `GraphicRaycaster`，
  `sortingOrder=32760`），**只在指针命中可交互元素时启用**；移到放行区域立即关闭。
- 限制：直接读 `Input.GetMouseButton` 的逻辑（世界交互/相机拖拽）无法被 UGUI 拦截，
  需要 Harmony 或宿主自行处理。

## 7. 生命周期与所有权

- 回调委托必须作为字段长期持有（否则 GC 回收后原生层持有悬空函数指针）。
- `wg_context_create` 前必须 `NativeLoader.EnsureLoaded()`（安装 AssemblyResolve，
  在插件目录解析 `System.Memory`/`System.Buffers`，必须发生在任何 SkiaSharp 类型 JIT 前）。
- 覆盖层组件挂在**插件自己的 GameObject** 上：BepInEx Awake 阶段新建根对象
  + `DontDestroyOnLoad` 会被游戏首个场景加载销毁。
- `Dispose` 顺序：图层 → 滤镜/遮罩 → 纹理/shader → 画布/位图 → 原生上下文。

## 8. 已知边界

- 无 JS、无 DOM 查询 API（只有按 id 回写文本）。
- 变换只支持 2D 仿射；`perspective` 等忽略。
- `shader: creation` 等自定义 GLSL shader 未实现。
- `LoadTexture` 依赖 Skia 可解码格式；TGA 也支持（Skia 解码）。
- `SaveLayerAsTexture`/mask-image 已实现，但演示页未使用。
- 输入拦截只覆盖 UGUI；直接 `Input` 轮询无法拦截。
- GPU 液态玻璃依赖 IMGUI 的 `OnGUI` 合帧行为（`alpha=0` 触发 + `PresentOnGui`）；
  一个屏幕建议只让一个视图启用（每个实例占用数张全屏 RT）。

## 9. 构建与离屏自检

```powershell
powershell -ExecutionPolicy Bypass -File scripts\fetch-rmlui.ps1
powershell -ExecutionPolicy Bypass -File scripts\build-native.ps1
powershell -ExecutionPolicy Bypass -File scripts\deploy.ps1 -Build
```

`tools/render-test` 是离屏自检：坐标/颜色/预乘/字形/裁剪/`Modulate`/backdrop 方向
逐像素断言，进游戏前先跑它。构建后必须把新的 `WebGUINative.dll` 拷进 render-test
输出目录，否则 `DllImport` 会命中旧副本。
