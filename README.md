# CuRmlGUI

给 CU（Casualties: Unknown）模组用的 **HTML/CSS UI 库**。

用 RML/RCSS（RmlUi 的 HTML/CSS 方言）写界面，由重编译的 **RmlUi 6.3** 解析与布局，
**SkiaSharp** 光栅化/合成，再经 Unity 纹理桥接到 IMGUI 呈现。
**只做渲染，绝不执行 JavaScript。**

本插件是「库模组」：默认不注入任何 UI，只负责初始化原生运行时与字体，
供其他模组通过 `WebGuiView` 使用。仓库内的 `LiquidGlassDemo` 是一个可选的
iOS 风格「液态玻璃」演示页（配置段仍沿用旧的 `[Spike]` 名称以兼容旧配置）。

| 文档 | 读者 | 内容 |
| --- | --- | --- |
| [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) | 研究者 / 维护者 | 架构声明：分层、ABI、渲染语义、坐标系、性能模型、设计取舍 |
| [docs/INTEGRATION.md](docs/INTEGRATION.md) | 下游模组开发者 | 接口用法：生命周期、输入、毛玻璃、自定义滤镜、事件、坑 |
| [docs/MISSING-FEATURES.md](docs/MISSING-FEATURES.md) | 研究者 | 与 RmlUi 官方后端相比仍缺的能力清单 |

---

## 特性

- **HTML/CSS 语义**：`<div>`/`<button>`/`<input>`、`position:absolute`、
  `margin/padding/border-radius`、`:hover/:active`、`rgba()`、`overflow:hidden`、
  `display:flex`（含 `flex-wrap`）等，由 RmlUi 原生实现。
- **零 JS**：RmlUi 不含脚本引擎；交互经 C# 事件回调转发。
- **按 RmlUi 官方后端语义实现渲染接口**：scissor、clip mask（圆角裁剪）、
  离屏图层、`filter`/`backdrop-filter`、`mask-image`、渐变 shader、变换矩阵，
  逐条对齐 `RmlUi_Renderer_GL3` 的语义（用 Skia 的等价能力实现）。
- **真毛玻璃**：`backdrop-filter` 对注入的游戏画面做模糊，并按 `border-radius` 裁剪；
  默认使用**伪模糊**（1/2 降采样 + 盒式加权平均 + 双线性放大，不调用高斯），
  成本极低；可切换为 `Real`（Skia 高斯）。
- **按需渲染**：无输入、无动画、无 backdrop 更新时不重绘、不上传纹理；
  `MaxRenderFps` 同时约束输入/hover 与动画的重绘频率。
- **中文开箱即用**：随插件发行 Liberation Sans（Latin）与 Unifont CJK 子集回退字体。

## 快速开始

```csharp
using CuRmlGUI;

var view = new WebGuiView(1280, 720);      // RmlUi 上下文 + 离屏 Skia 合成器
WebGuiFonts.LoadDefault(view);             // 加载随插件发行的字体
view.LoadDocument(
    "<rml><body><div id='hello'>你好</div></body></rml>",
    "#hello { color: #fff; font-size: 20px; }");

view.ElementEvent += (type, id) => Debug.Log($"event {type} / {id}");

// 每帧（主线程）
view.Update();                             // 有变化才重绘并上传纹理
GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), view.Texture);
```

输入转发、毛玻璃抓屏、自定义滤镜、输入拦截等见
[docs/INTEGRATION.md](docs/INTEGRATION.md)。

## 构建

前置：VS Community（MSVC）+ CMake + Windows SDK，.NET SDK，Python（字体子集化，可选）。

```powershell
powershell -ExecutionPolicy Bypass -File scripts\fetch-rmlui.ps1   # 拉取 pinned 依赖
powershell -ExecutionPolicy Bypass -File scripts\build-native.ps1  # native/out/Release/WebGUINative.dll
powershell -ExecutionPolicy Bypass -File scripts\deploy.ps1 -Build # 托管 + 部署到游戏
```

> `build-native.ps1` 在 freetype 弃用警告下可能误退出；可直接调用
> `cmake --build native\build --config Release`。

离屏自检（进游戏前验证渲染）：见 [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md#离屏自检)。

## 演示页与配置

`LiquidGlassDemo`（`src/Presentation/LiquidGlassDemo.cs`）是可选的演示页：
iOS 控制中心式两列玻璃卡片、渐变高光、真毛玻璃、圆角裁剪、表单控件。
配置位于 `BepInEx/config/com.cu.curmlgui.cfg` 的 `[Spike]` 段（历史名称）：

| 键 | 默认 | 说明 |
| --- | --- | --- |
| `Enabled` | `false` | 启用演示页 |
| `AutoShow` | `false` | 启动即显示（免按键，供自动化截图） |
| `ToggleKey` | `F1` | 显隐按键 |
| `DpiScale` | `1` | dp→px 比例 |
| `Backdrop` | `false` | 抓屏注入 backdrop（毛玻璃模糊源） |
| `BackdropBlur` | `Fake` | `Fake` 伪模糊（默认）/ `Real` Skia 高斯 |
| `FakeBlurScale` | `2` | Fake 的抓屏降采样除数（盒式平均） |
| `FakeBlurRadius` | `6` | Fake 在降采样分辨率上的盒式平滑半径 |

## 性能

实测（游戏内 1920×1080、演示页玻璃全开、伪模糊）：

| 指标 | 早期实现 | 当前 |
| --- | --- | --- |
| 游戏 FPS | ~10 | 50–80（随场景波动） |
| overlay render（摊销） | 70–125 ms/帧 | 2–4 ms/帧 |
| capture | 8–17 ms | 1–2 ms |
| overlay 重绘 | 每帧 | 10–20 fps（与抓屏同频）+ 输入上限 50 fps |

性能设计（区域快照、半分辨率滤镜、图层只清 scissor、surface 分桶等）见
[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md#性能模型)。

## 许可

- CuRmlGUI 代码：MIT（见 `LICENSE`）。
- RmlUi 6.3：MIT（以 RCDATA 资源内嵌在 `WebGUINative.dll`，运行时 `wg_license()` 可读）。
- FreeType 2.13.3：FTL/GPLv2 双许可，静态链接。
- Liberation Sans、Unifont：各自开源许可，随插件发行。
