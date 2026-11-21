# CuRmlGUI

[English](README.md) | **简体中文**

CuRmlGUI 是面向《Casualties: Unknown》模组的界面库。界面使用 RML/RCSS（RmlUi 实现的
HTML/CSS 方言）描述，最终通过 IMGUI 呈现。其中，RmlUi 负责解析、样式计算、布局、事件与动画，
SkiaSharp 负责将绘制指令光栅化并合成为位图，再由一张 Unity 纹理送入游戏。

本库不执行 JavaScript。RmlUi 不含脚本引擎，公共接口也不提供 DOM 查询能力，
因此所有行为均由模组的 C# 代码承担。

## 项目状态与 AI 使用声明

本项目处于早期测试阶段，行为不稳定，功能尚不完整，并且可能存在尚未定位的缺陷。
接口可能随时变更，不建议用于生产环境。

- **文档。** 本仓库的全部文档，包括两个 README 与 `docs/` 下的所有文件，
  均由 AI 编写。
- **代码。** 约四分之一的源代码在 AI 辅助下完成，其余部分由维护者编写与审查。

## 特性

- **完整沿用 HTML/CSS 语义。** 元素、`position`、`margin` 与 `padding`、`border-radius`、
  `:hover` 与 `:active`、`rgba()`、`overflow: hidden`、flex 布局（含 `flex-wrap`）、
  渐变、变换、过渡与关键帧动画均由 RmlUi 原生实现。
- **与官方 RmlUi 后端保持一致的渲染接口。** 裁剪区域、用于圆角裁剪的遮罩、离屏图层、
  `filter` 与 `backdrop-filter`、`mask-image`、渐变着色器以及二维变换，均通过把
  `RmlUi_Renderer_GL3` 的每个回调映射到等价的 Skia 操作来实现。
- **GPU 液态玻璃。** 调用 `EnableGpuGlass` 即可为指定元素添加圆角、模糊且带边缘折射的
  玻璃面板，全部在 GPU 上完成：画面先被捕获到渲染纹理，再在八分之一分辨率上经过
  八抽头可分离模糊，最后由程序化圆角矩形网格采样。全过程无需 CPU 回读，也无需自定义着色器。
- **按需渲染。** 只有当输入、文档变更或动画确实需要时才重新渲染并上传纹理，
  `MaxRenderFps` 用于限制重绘频率。
- **内置字体。** 随插件发行 Liberation Sans 与 Unifont CJK 子集，
  无需额外配置即可显示拉丁字母与常用中文。

## 环境要求

- 《Casualties: Unknown》与 BepInEx 5（Unity Mono，.NET Framework 4.7.2）。
- RmlUi 6.3 与 SkiaSharp 1.68.3 为固定版本，由本仓库构建或分发。
- 从源码构建需要 MSVC、CMake、Windows SDK 与 .NET SDK。

## 快速开始

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

        // 可选：为面板添加圆角模糊玻璃背景。
        _view.EnableGpuGlass(new[] { "panel" });

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

## 文档

| 文档 | 读者 | 内容 |
| --- | --- | --- |
| [docs/INTEGRATION.zh-CN.md](docs/INTEGRATION.zh-CN.md) | 模组开发者 | 引入方式、输入转发、玻璃、自定义滤镜、命中测试与常见问题 |
| [docs/ARCHITECTURE.zh-CN.md](docs/ARCHITECTURE.zh-CN.md) | 维护者与研究者 | 分层结构、渲染接口语义、坐标系与性能模型 |
| [docs/MISSING-FEATURES.zh-CN.md](docs/MISSING-FEATURES.zh-CN.md) | 研究者 | RmlUi 6.3 尚未提供的 CSS 能力 |

英文版本：[README.md](README.md)、[docs/INTEGRATION.md](docs/INTEGRATION.md)、
[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)、[docs/MISSING-FEATURES.md](docs/MISSING-FEATURES.md)。

## 目录结构

| 路径 | 用途 |
| --- | --- |
| `src/` | 托管库：`WebGuiView`、`SkiaCompositor`、GPU 玻璃与原生绑定 |
| `native/` | C ABI 适配层，以及构建 `WebGUINative.dll` 的 CMake 工程 |
| `third_party/` | RmlUi 源码，由 `scripts/fetch-rmlui.ps1` 拉取，不纳入版本库 |
| `assets/` | 随插件分发的字体 |
| `tools/render-test/` | 离屏渲染自检，包含逐像素断言 |
| `tools/perf/` | 阴影与模糊路径的微基准 |
| `samples/` | 最小消费方示例 |
| `scripts/` | 拉取、构建、部署与打包脚本 |

## 构建

```powershell
powershell -ExecutionPolicy Bypass -File scripts\fetch-rmlui.ps1    # 拉取 RmlUi 6.3 并应用本地补丁
powershell -ExecutionPolicy Bypass -File scripts\build-native.ps1   # native/out/Release/WebGUINative.dll
powershell -ExecutionPolicy Bypass -File scripts\deploy.ps1 -Build  # 构建托管层并部署
```

`build-native.ps1` 可能因 FreeType 的弃用警告提前退出。此时可直接构建原生工程：

```powershell
cmake --build native\build --config Release
```

离屏自检无需启动游戏，可验证坐标、颜色顺序、预乘、字形绘制、裁剪与背景朝向：

```powershell
dotnet run --project tools\render-test -c Release
```

运行前需将新生成的 `WebGUINative.dll` 复制到 `render-test` 的输出目录，
否则测试会加载旧副本。

## 演示页与配置

`src/Presentation/LiquidGlassDemo.cs` 是一个可选的演示页，风格接近 iOS 控制中心：
两列玻璃卡片、渐变高光、GPU 液态玻璃、圆角裁剪与表单控件。相关配置位于
`BepInEx/config/com.cu.curmlgui.cfg` 的 `[Spike]` 段。

| 键 | 默认值 | 说明 |
| --- | --- | --- |
| `Enabled` | `false` | 是否加载演示页 |
| `AutoShow` | `false` | 启动后直接显示，无需按键 |
| `ToggleKey` | `F1` | 显示或隐藏演示页的按键 |
| `DpiScale` | `1` | dp 到像素的比例，仅影响 `dp` 单位；`px` 为绝对像素 |
| `Glass` | `true` | 是否启用 GPU 液态玻璃 |

## 性能

以下数据在 1920×1080、全部玻璃面板可见的条件下测得：

| 项目 | 早期 CPU 玻璃 | 当前 GPU 玻璃 |
| --- | --- | --- |
| 画面捕获 | CPU 回读，8–17 ms，约 15 fps | GPU 拷入渲染纹理，无回读 |
| 模糊 | CPU 可分离盒式平滑，每次约 21 ms | 八分之一分辨率上的八抽头可分离模糊，每帧约 24 次小尺寸拷贝 |
| 纹理上传 | 每帧 8 MB | 无，玻璃不经过 CPU |
| 覆盖层重绘 | 上限 20 fps | 上限 20 fps，玻璃模糊每帧刷新 |

上述指标对应的设计见 [docs/ARCHITECTURE.zh-CN.md](docs/ARCHITECTURE.zh-CN.md#5-性能模型)。

## 已知限制

- 不支持 JavaScript，也不提供 DOM 查询接口；动态内容通过 `SetElementText` 写入。
- 变换仅支持二维仿射矩阵，`perspective` 会被忽略。
- GPU 玻璃依赖 IMGUI 的 `OnGUI` 合帧机制，建议每个屏幕只启用一个视图。
- 输入拦截仅覆盖 UGUI。直接读取 `Input.GetMouseButton` 的逻辑需要由模组自行处理。

## 许可

- CuRmlGUI：MIT，见 [LICENSE](LICENSE)。
- RmlUi 6.3：MIT，以 RCDATA 资源内嵌于 `WebGUINative.dll`，运行时可通过
  `wg_license()` 读取声明。
- FreeType 2.13.3：FTL/GPLv2 双许可，静态链接。
- Liberation Sans 与 Unifont：分别遵循各自的开源许可。
