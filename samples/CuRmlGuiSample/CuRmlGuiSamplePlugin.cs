using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using BepInEx.Logging;
using CuRmlGUI;
using UnityEngine;

namespace CuRmlGuiSample
{
    /// <summary>
    /// 示例：如何用 CuRmlGUI 在游戏里显示一个 HTML/CSS(RML/RCSS) 界面。
    ///
    /// 步骤：
    ///   1) 创建 WebGuiView（一个 RmlUi 上下文 + 离屏 Skia 合成器）。
    ///   2) 用 WebGuiFonts.LoadDefault 加载 CuRmlGUI 随插件发行的字体
    ///      （Latin 主字体 + Unifont CJK 回退）。
    ///   3) 加载 RML/RCSS 文档。
    ///   4) 每帧：转发鼠标/键盘输入 → view.Update()（有变化才重绘并上传纹理）。
    ///   5) OnGUI：把 view.Texture 画到屏幕上（IMGUI 呈现，零场景依赖）。
    /// 交互：按钮点击会累加计数并回写界面（SetElementText）；输入框支持中文输入。
    /// </summary>
    [BepInPlugin(Guid, Name, Version)]
    [BepInDependency("com.cu.curmlgui")]   // 保证 CuRmlGUI 先加载
    public sealed class CuRmlGuiSamplePlugin : BaseUnityPlugin
    {
        public const string Guid = "com.cu.curmlgui.sample";
        public const string Name = "CuRmlGuiSample";
        public const string Version = "0.1.0";

        private static new ManualLogSource Logger;

        private WebGuiView _view;
        private bool _failed;
        private int _clicks;

        // 输入转发用的上一帧鼠标位置（避免重复发 move）。
        private float _lastMx = float.NaN, _lastMy = float.NaN;

        // 只轮询会用到的按键；逐个枚举 KeyCode 每帧会产生数百次无效查询。
        private static readonly KeyCode[] TrackedKeys = BuildKeyList();

        private static KeyCode[] BuildKeyList()
        {
            var list = new List<KeyCode>
            {
                KeyCode.Backspace, KeyCode.Tab, KeyCode.Return, KeyCode.KeypadEnter,
                KeyCode.Escape, KeyCode.Delete, KeyCode.Insert, KeyCode.Space,
                KeyCode.LeftArrow, KeyCode.RightArrow, KeyCode.UpArrow, KeyCode.DownArrow,
                KeyCode.Home, KeyCode.End, KeyCode.PageUp, KeyCode.PageDown,
                KeyCode.LeftShift, KeyCode.RightShift,
                KeyCode.LeftControl, KeyCode.RightControl,
                KeyCode.LeftAlt, KeyCode.RightAlt,
            };

            for (var k = KeyCode.A; k <= KeyCode.Z; k++) list.Add(k);
            for (var k = KeyCode.Alpha0; k <= KeyCode.Alpha9; k++) list.Add(k);
            return list.ToArray();
        }

        private void Awake()
        {
            Logger = base.Logger;
            Logger.LogInfo($"{Name} v{Version} loaded.");

            int w = Mathf.Max(1, Screen.width);
            int h = Mathf.Max(1, Screen.height);

            try
            {
                _view = new WebGuiView(w, h, 1f);

                // 字体：直接用 CuRmlGUI 发行的（必须先 Latin，再 CJK 回退）。
                WebGuiFonts.LoadDefault(_view);

                // 事件：点击/悬停/change 等（eventType, elementId）。
                _view.ElementEvent += OnElementEvent;

                if (!_view.LoadDocument(Rml, Css))
                {
                    Logger.LogError("Failed to load document.");
                    _failed = true;
                    return;
                }

                Logger.LogInfo("Sample UI loaded (window: " + w + "x" + h + ").");
            }
            catch (Exception e)
            {
                _failed = true;
                Logger.LogError("Init failed: " + e);
            }
        }

        private void OnElementEvent(string type, string id)
        {
            Logger.LogInfo($"event: {type} / {id}");

            // 按钮点击 → 累加计数，回写界面文本（通过 RmlUi DOM）。
            if (type == "click" && id == "btn")
            {
                _clicks++;
                _view.SetElementText("count", $"点击了 {_clicks} 次");
            }
        }

        private void Update()
        {
            if (_failed || _view == null) return;

            try
            {
                // --- 输入转发（RmlUi 坐标：左上原点） ---
                Vector3 mp = Input.mousePosition;
                float x = mp.x;
                float y = Screen.height - mp.y;   // Unity 左下原点 → 左上

                if (x != _lastMx || y != _lastMy)
                {
                    _lastMx = x;
                    _lastMy = y;
                    _view.MouseMove(x, y);
                }
                for (int b = 0; b <= 2; b++)
                {
                    if (Input.GetMouseButtonDown(b)) _view.MouseDown(x, y, b);
                    if (Input.GetMouseButtonUp(b)) _view.MouseUp(x, y, b);
                }

                // 键盘 + 文本（只轮询实际需要的按键；代理对合并为一个码点后提交）。
                int mods = KeyMap.CurrentModifiers();
                for (int i = 0; i < TrackedKeys.Length; i++)
                {
                    KeyCode k = TrackedKeys[i];
                    if (Input.GetKeyDown(k)) _view.KeyDown(k, mods);
                    if (Input.GetKeyUp(k)) _view.KeyUp(k, mods);
                }

                string typed = Input.inputString;
                for (int i = 0; i < typed.Length; i++)
                {
                    char c = typed[i];
                    if (c == '\b' || c == '\n' || c == '\r' || c == 127) continue;

                    if (char.IsHighSurrogate(c) && i + 1 < typed.Length && char.IsLowSurrogate(typed[i + 1]))
                    {
                        _view.TextInputCodepoint((uint)char.ConvertToUtf32(c, typed[i + 1]));
                        i++;
                        continue;
                    }
                    if (char.IsSurrogate(c)) continue;

                    _view.TextInput(c);
                }

                // --- 推进一帧，有变化才重绘并上传纹理 ---
                _view.Update();
            }
            catch (Exception e)
            {
                _failed = true;
                Logger.LogError("Update failed: " + e);
            }
        }

        private void OnGUI()
        {
            if (_failed || _view == null) return;
            if (_view.Texture == null) return;

            // 库统一呈现：含 GPU 玻璃合成与 GUI.depth 处理。
            _view.PresentOnGui();
        }

        private void OnDestroy()
        {
            _view?.Dispose();
            _view = null;
        }

        // ------------------------------------------------------------------ UI

        private const string Rml = @"
<rml>
<head>
</head>
<body>
  <div id=""window"">
    <div id=""titlebar"">CuRmlGUI 示例<span id=""hint"">库 API 演示</span></div>
    <div id=""content"">
      <p class=""lead"">这是一个用 HTML/CSS（RML/RCSS）写的界面。</p>
      <p>中文排版：天地玄黄，宇宙洪荒。字形应为真实字形。</p>

      <div class=""row"">
        <button id=""btn"">点我 +1</button>
        <span id=""count"">点击了 0 次</span>
      </div>

      <div class=""row"">
        <span class=""label"">文本输入：</span>
        <input type=""text"" id=""name"" placeholder=""在这里输入...""/>
      </div>

      <div class=""row"">
        <span class=""label"">只读文本（C# 回写）：</span>
        <span id=""static"">由 SetElementText 更新</span>
      </div>
    </div>
  </div>
</body>
</rml>";

        private const string Css = @"
body {
  width: 100%; height: 100%;
  background: transparent;
  font-family: Liberation Sans;
  font-size: 15px;
  color: #e8e8f0;
}

#window {
  position: absolute;
  left: 200px; top: 140px;
  width: 520px;
  background: #1c1c28;
  border-width: 1px;
  border-color: #4a4a6a;
  border-radius: 8px;
}

#titlebar {
  height: 32px;
  padding-left: 12px;
  line-height: 32px;
  font-size: 15px;
  color: #ffffff;
  background: #2c2c44;
  border-top-left-radius: 8px;
  border-top-right-radius: 8px;
}

#hint { float: right; margin-right: 12px; font-size: 12px; color: #9090b0; }

#content { padding: 12px; }
.lead { font-size: 16px; color: #ffffff; margin-bottom: 8px; }
.row { margin-top: 10px; height: 30px; }
.label { color: #b0b0c8; }

button {
  width: 110px; height: 28px;
  background: #3a5bd0;
  color: #ffffff;
  border-width: 0px;
  border-radius: 5px;
}
button:hover { background: #4a6be0; }
button:active { background: #2a4bb0; }

input {
  width: 220px; height: 24px;
  padding-left: 6px;
  background: #101018;
  color: #ffffff;
  border-width: 1px;
  border-color: #4a4a6a;
  border-radius: 4px;
}

#count { margin-left: 12px; color: #9ad0ff; }
#static { color: #c8d8ff; }
";
    }
}
