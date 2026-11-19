"""导出 resources.assets 中的 Font 对象，尝试取出内嵌 TTF 字节。

用法: python extract.py <data 目录> <输出目录>
"""
import sys
import os

import UnityPy


def main():
    data_dir = sys.argv[1] if len(sys.argv) > 1 else r"C:\Program Files (x86)\Steam\steamapps\common\Casualties Unknown\CasualtiesUnknown_Data"
    out_dir = sys.argv[2] if len(sys.argv) > 2 else os.path.join(os.path.dirname(__file__), "out")
    os.makedirs(out_dir, exist_ok=True)

    path = os.path.join(data_dir, "resources.assets")
    env = UnityPy.load(path)

    for obj in env.objects:
        if obj.type.name != "Font":
            continue
        d = obj.read()
        name = getattr(d, "m_Name", f"font_{obj.path_id}")
        size = getattr(d, "m_FontSize", "?")
        font_names = getattr(d, "m_FontNames", None)
        font_data = getattr(d, "m_FontData", None)

        n = len(font_data) if font_data is not None else 0
        print(f"\n[{name}] path_id={obj.path_id} size={size} embedded_bytes={n} fontNames={font_names}")

        if font_data:
            # 判断是否为 TTF/OTF: TTF 以 0x00010000 或 'true'/'ttcf' 开头
            head = bytes(font_data[:4])
            print("   magic:", head.hex())
            ext = ".ttf"
            if head[:4] == b"OTTO":
                ext = ".otf"
            elif head[:4] == b"ttcf":
                ext = ".ttc"
            safe = "".join(c if c.isalnum() or c in "-_." else "_" for c in name)
            out = os.path.join(out_dir, safe + ext)
            with open(out, "wb") as f:
                f.write(bytes(font_data))
            print("   -> wrote", out, os.path.getsize(out), "bytes")

        # 列出所有可用属性，便于后续判断
        try:
            keys = [k for k in vars(d).keys() if not k.startswith("__")]
            interesting = [k for k in keys if any(s in k.lower() for s in ("font", "data", "name", "texture", "size", "char"))]
            print("   attrs:", interesting[:20])
        except Exception:
            pass

    print("\nDone. output:", out_dir)


if __name__ == "__main__":
    main()
