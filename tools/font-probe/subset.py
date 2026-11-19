"""把游戏字体子集化，生成体积可控的发行字体。

默认对 Unifont（CFF/OTF）做「GB2312 汉字 + Latin + 标点/符号」子集化；
也可对 Liberation Sans 做 Latin 子集化。

用法:
  python subset.py [--src <font>] [--out <file>] [--mode cjk|latin]

依赖: fonttools
"""
import argparse
import os
import sys

from fontTools import subset


def gb2312_chars():
    """GB2312 全部可解码字符（6763 汉字 + 682 符号）。"""
    chars = set()
    for hi in range(0xA1, 0xFF):
        for lo in range(0xA1, 0xFF):
            try:
                ch = bytes([hi, lo]).decode("gb2312")
            except UnicodeDecodeError:
                continue
            if ch:
                chars.add(ch)
    return chars


def base_chars():
    chars = set()
    # 基本拉丁 + Latin-1
    chars.update(chr(c) for c in range(0x20, 0x7F))
    chars.update(chr(c) for c in range(0xA0, 0x100))
    # 常用标点 / 通用标点
    chars.update(chr(c) for c in range(0x2000, 0x2070))
    # CJK 标点、全角字符
    chars.update(chr(c) for c in range(0x3000, 0x3040))
    chars.update(chr(c) for c in range(0xFF00, 0xFFF0))
    # 常见符号
    for c in "©®™℃€£¥§¶•…‰′″←↑→↓↔⇒⇔★☆✓✔✕✖":
        chars.add(c)
    return chars


def build_text(mode):
    chars = base_chars()
    if mode == "cjk":
        chars |= gb2312_chars()
    return "".join(sorted(chars))


def main():
    here = os.path.dirname(os.path.abspath(__file__))
    default_src = os.path.join(here, "out", "unifont-17.0.04.otf")
    default_out = os.path.join(here, "..", "..", "assets", "fonts", "curmlgui-cjk.otf")

    ap = argparse.ArgumentParser()
    ap.add_argument("--src", default=default_src)
    ap.add_argument("--out", default=default_out)
    ap.add_argument("--mode", choices=["cjk", "latin"], default="cjk")
    args = ap.parse_args()

    if not os.path.exists(args.src):
        sys.exit(f"source font not found: {args.src}")

    text = build_text(args.mode)
    uniq = len(set(text))
    print(f"mode={args.mode} chars={uniq} src={args.src} ({os.path.getsize(args.src)} bytes)")

    options = subset.Options()
    options.layout_features = ["*"]
    options.name_IDs = ["*"]
    options.name_legacy = True
    options.notdef_outline = True
    options.recalc_bounds = True
    options.recalc_timestamp = False
    options.drop_tables = ["DSIG"]
    options.flavor = None  # 不压缩：RmlUi/FreeType 直接读取

    font = subset.load_font(args.src, options)
    subsetter = subset.Subsetter(options=options)
    subsetter.populate(text=text)
    subsetter.subset(font)

    os.makedirs(os.path.dirname(os.path.abspath(args.out)), exist_ok=True)
    subset.save_font(font, args.out, options)

    print(f"-> {args.out} ({os.path.getsize(args.out)} bytes, "
          f"{os.path.getsize(args.out)/1024/1024:.2f} MB)")

    # 报告子集后的 cmap 覆盖
    from fontTools.ttLib import TTFont
    t = TTFont(args.out)
    cmap = t.getBestCmap()
    print(f"cmap entries: {len(cmap)}")
    for ch in "天地玄黄宇宙洪荒ABC你好":
        print(f"  U+{ord(ch):04X} {ch}: {'yes' if ord(ch) in cmap else 'NO'}")


if __name__ == "__main__":
    main()
