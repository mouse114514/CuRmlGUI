"""扫描游戏资源中的字体对象（Font / TMP_FontAsset / TextAsset）。

用法: python scan.py <CasualtiesUnknown_Data 目录>
"""
import sys
import os
import collections

import UnityPy


def main():
    data_dir = sys.argv[1] if len(sys.argv) > 1 else r"C:\Program Files (x86)\Steam\steamapps\common\Casualties Unknown\CasualtiesUnknown_Data"
    files = [f for f in os.listdir(data_dir) if f.endswith((".assets", ".resource"))]
    files.sort()

    for fname in files:
        path = os.path.join(data_dir, fname)
        try:
            env = UnityPy.load(path)
        except Exception as e:
            print(f"[skip] {fname}: {e}")
            continue

        counts = collections.Counter()
        fonts = []
        for obj in env.objects:
            t = obj.type.name
            counts[t] += 1
            if t == "Font":
                try:
                    d = obj.read()
                    fonts.append((obj.path_id, "Font", getattr(d, "m_Name", "?"), getattr(d, "m_FontSize", "?")))
                except Exception as e:
                    fonts.append((obj.path_id, "Font", f"<err {e}>", "?"))
            elif t == "MonoBehaviour":
                # TMP_FontAsset 也以 MonoBehaviour 形式存在，名称里常含字体名
                try:
                    tree = obj.read_typetree()
                    name = tree.get("m_Name", "")
                    script = tree.get("m_Script", {})
                    # 只挑名称像字体的
                    if name and any(k in name.lower() for k in ("font", "arial", "noto", "yahei", "simhei", "simsun", "roboto", "liberation")):
                        fonts.append((obj.path_id, "MonoBehaviour", name, tree.get("m_FontSize", "")))
                except Exception:
                    pass

        if fonts:
            print(f"\n=== {fname} ===")
            for pid, typ, name, size in fonts:
                print(f"  path_id={pid} type={typ} name={name!r} size={size}")

    print("\nDone.")


if __name__ == "__main__":
    main()
