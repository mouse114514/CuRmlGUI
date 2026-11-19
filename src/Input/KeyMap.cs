using UnityEngine;

namespace CuRmlGUI
{
    /// <summary>
    /// Unity <see cref="KeyCode"/> → RmlUi <c>Input::KeyIdentifier</c> 映射，
    /// 以及 Unity 输入状态 → RmlUi <c>Input::KeyModifier</c> 位掩码。
    ///
    /// 枚举数值直接取自 RmlUi 6.3 <c>Include/RmlUi/Core/Input.h</c>，
    /// 与原生侧 <c>(Input::KeyIdentifier)key</c> 的强转一一对应。
    /// </summary>
    public static class KeyMap
    {
        // RmlUi Input::KeyIdentifier（节选，仅用到部分）
        public const int KI_UNKNOWN = 0;
        public const int KI_SPACE = 1;
        public const int KI_0 = 2;   // .. KI_9 = 11
        public const int KI_A = 12;  // .. KI_Z = 37
        public const int KI_OEM_1 = 38;
        public const int KI_OEM_PLUS = 39;
        public const int KI_OEM_COMMA = 40;
        public const int KI_OEM_MINUS = 41;
        public const int KI_OEM_PERIOD = 42;
        public const int KI_OEM_2 = 43;
        public const int KI_OEM_3 = 44;
        public const int KI_OEM_4 = 45;
        public const int KI_OEM_5 = 46;
        public const int KI_OEM_6 = 47;
        public const int KI_OEM_7 = 48;
        public const int KI_BACK = 69;
        public const int KI_TAB = 70;
        public const int KI_RETURN = 72;
        public const int KI_PAUSE = 73;
        public const int KI_CAPITAL = 74;
        public const int KI_ESCAPE = 81;
        public const int KI_PRIOR = 86;
        public const int KI_NEXT = 87;
        public const int KI_END = 88;
        public const int KI_HOME = 89;
        public const int KI_LEFT = 90;
        public const int KI_UP = 91;
        public const int KI_RIGHT = 92;
        public const int KI_DOWN = 93;
        public const int KI_INSERT = 98;
        public const int KI_DELETE = 99;
        public const int KI_LWIN = 101;
        public const int KI_RWIN = 102;
        public const int KI_APPS = 103;
        public const int KI_F1 = 107;   // .. KI_F12 = 118
        public const int KI_NUMLOCK = 131;
        public const int KI_SCROLL = 132;
        public const int KI_LSHIFT = 138;
        public const int KI_RSHIFT = 139;
        public const int KI_LCONTROL = 140;
        public const int KI_RCONTROL = 141;
        public const int KI_LMENU = 142;
        public const int KI_RMENU = 143;

        // RmlUi Input::KeyModifier
        public const int KM_CTRL = 1 << 0;
        public const int KM_SHIFT = 1 << 1;
        public const int KM_ALT = 1 << 2;
        public const int KM_META = 1 << 3;
        public const int KM_CAPSLOCK = 1 << 4;
        public const int KM_NUMLOCK = 1 << 5;
        public const int KM_SCROLLLOCK = 1 << 6;

        public static int ToRml(KeyCode key)
        {
            // 字母
            if (key >= KeyCode.A && key <= KeyCode.Z)
                return KI_A + (key - KeyCode.A);

            // 主键盘数字
            if (key >= KeyCode.Alpha0 && key <= KeyCode.Alpha9)
                return KI_0 + (key - KeyCode.Alpha0);

            // 小键盘数字
            if (key >= KeyCode.Keypad0 && key <= KeyCode.Keypad9)
                return 51 + (key - KeyCode.Keypad0); // KI_NUMPAD0 = 51

            // 功能键 F1..F12
            if (key >= KeyCode.F1 && key <= KeyCode.F12)
                return KI_F1 + (key - KeyCode.F1);

            switch (key)
            {
                case KeyCode.Space: return KI_SPACE;
                case KeyCode.Backspace: return KI_BACK;
                case KeyCode.Tab: return KI_TAB;
                case KeyCode.Return: return KI_RETURN;
                case KeyCode.KeypadEnter: return 61;
                case KeyCode.Escape: return KI_ESCAPE;
                case KeyCode.Pause: return KI_PAUSE;
                case KeyCode.CapsLock: return KI_CAPITAL;
                case KeyCode.Numlock: return KI_NUMLOCK;
                case KeyCode.ScrollLock: return KI_SCROLL;

                case KeyCode.PageUp: return KI_PRIOR;
                case KeyCode.PageDown: return KI_NEXT;
                case KeyCode.End: return KI_END;
                case KeyCode.Home: return KI_HOME;
                case KeyCode.LeftArrow: return KI_LEFT;
                case KeyCode.UpArrow: return KI_UP;
                case KeyCode.RightArrow: return KI_RIGHT;
                case KeyCode.DownArrow: return KI_DOWN;
                case KeyCode.Insert: return KI_INSERT;
                case KeyCode.Delete: return KI_DELETE;

                case KeyCode.LeftShift: return KI_LSHIFT;
                case KeyCode.RightShift: return KI_RSHIFT;
                case KeyCode.LeftControl: return KI_LCONTROL;
                case KeyCode.RightControl: return KI_RCONTROL;
                case KeyCode.LeftAlt: return KI_LMENU;
                case KeyCode.RightAlt: return KI_RMENU;
                case KeyCode.LeftWindows: return KI_LWIN;
                case KeyCode.RightWindows: return KI_RWIN;
                case KeyCode.Menu: return KI_APPS;

                // 标点
                case KeyCode.Semicolon: return KI_OEM_1;
                case KeyCode.Equals: return KI_OEM_PLUS;
                case KeyCode.Comma: return KI_OEM_COMMA;
                case KeyCode.Minus: return KI_OEM_MINUS;
                case KeyCode.Period: return KI_OEM_PERIOD;
                case KeyCode.Slash: return KI_OEM_2;
                case KeyCode.BackQuote: return KI_OEM_3;
                case KeyCode.LeftBracket: return KI_OEM_4;
                case KeyCode.Backslash: return KI_OEM_5;
                case KeyCode.RightBracket: return KI_OEM_6;
                case KeyCode.Quote: return KI_OEM_7;

                // 小键盘运算
                case KeyCode.KeypadMultiply: return 62;
                case KeyCode.KeypadPlus: return 63;
                case KeyCode.KeypadMinus: return 65;
                case KeyCode.KeypadPeriod: return 66;
                case KeyCode.KeypadDivide: return 67;
            }

            return KI_UNKNOWN;
        }

        /// <summary>根据 Unity 当前输入状态组装 RmlUi 修饰键位掩码。</summary>
        public static int CurrentModifiers()
        {
            int m = 0;
            if (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)) m |= KM_CTRL;
            if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)) m |= KM_SHIFT;
            if (Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt)) m |= KM_ALT;
            return m;
        }
    }
}
