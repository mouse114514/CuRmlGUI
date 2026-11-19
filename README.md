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
- **脏矩形只在 Skia 层**：RmlUi 报变化才重绘，变化时整张 `Texture2D.Apply()`。
- **中文开箱即用**：随插件发行 `Liberation Sans`（Latin）与 Unifont CJK 子集
  回退字体，字形按需回退，无需用户安装字体。

## 用法（给模组开发者）

```csharp
using CuRmlGUI;

var view = new WebGuiView(1280, 720);   // 创建 RmlUi 上下文 + 离屏合成器
view.LoadFont(Path.Combine(dir, "assets", "fonts", "curmlgui-latin.ttf"));
view.LoadFont(Path.Combine(dir, "assets", "fonts", "curmlgui-cjk.otf")); // CJK 回退
view.LoadDocument("<rml><body><div id='hello'>你好</div></body></rml>", css);

view.ElementEvent += (type, id) => Debug.Log($"event {type} / {id}");

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
cd tools\render-test\bin\Release\net472
.\render-test.exe WebGUINative.dll out.png ..\..\..\assets\fonts\curmlgui-latin.ttf `
  --family="Liberation Sans" --extra=..\..\..\assets\fonts\curmlgui-cjk.otf
```

输出 `ALL CHECKS PASSED` 表示坐标/颜色/预乘/字形/裁剪/`Modulate` 全部正确。

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
