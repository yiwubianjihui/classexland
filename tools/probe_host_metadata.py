"""只读解析宿主 ClassIsland.Core.dll 的元数据，查某个属性/方法是否存在、可见性如何。

用途：插件编译期引用的是 PluginSdk 2.0.0.2，宿主实际跑的是 2.1.x，
两者 API 有差异（例如 AttachedTimePointTimeType 只在宿主里有）。
靠反射调宿主 API 时，必须先确认「到底有没有、是 public 还是 protected」，
否则反射失败会被 catch 掉、静默退回默认行为，很难发现。

不加载程序集、不执行任何 IL，纯字节解析（dnfile + pefile）。

用法：
    pip install dnfile
    python tools/probe_host_metadata.py <ClassIsland.Core.dll> [名字片段]

默认在常见的 ClassIsland 安装位置里找 Core.dll，默认查 AttachedTimePointTimeType。
"""
import glob
import os
import sys

import dnfile

DEFAULT_NAME = "AttachedTimePointTimeType"

SEARCH_GLOBS = [
    r"D:\我的文档\教学喵喵包\ClassIsland-*\app-*\ClassIsland.Core.dll",
    r"C:\Program Files\ClassIsland\app-*\ClassIsland.Core.dll",
    os.path.expandvars(r"%LOCALAPPDATA%\ClassIsland\app-*\ClassIsland.Core.dll"),
]


def find_dll() -> str | None:
    for pattern in SEARCH_GLOBS:
        hits = sorted(glob.glob(pattern))
        if hits:
            return hits[-1]
    return None


def access_of(flags) -> str:
    """把 ClrMethodAttr / ClrFieldAttr 的成员访问位翻译成人话。"""
    for bit, label in (
        ("mdPublic", "public"),
        ("mdFamily", "protected"),
        ("mdFamORAssem", "protected internal"),
        ("mdAssem", "internal"),
        ("mdFamANDAssem", "private protected"),
        ("mdPrivate", "private"),
        ("mdPrivateScope", "compilercontrolled"),
    ):
        if getattr(flags, bit, False):
            return label
    return "?"


def main() -> int:
    dll = sys.argv[1] if len(sys.argv) > 1 else find_dll()
    needle = sys.argv[2] if len(sys.argv) > 2 else DEFAULT_NAME

    if not dll or not os.path.isfile(dll):
        print(f"找不到 Core.dll，请把路径作为第一个参数传入（当前：{dll}）")
        return 2

    pe = dnfile.dnPE(dll)
    if pe.net is None:
        print(f"{dll} 不是 .NET 程序集")
        return 2

    md = pe.net.mdtables
    print(f"程序集：{dll}")
    print(f"查找：{needle}\n")

    found = 0

    # 方法（属性的 get_/set_ 也在这里）
    for type_row in md.TypeDef:
        owner = str(type_row.TypeName)
        ns = str(type_row.TypeNamespace) or ""
        full = f"{ns}.{owner}" if ns else owner

        for index in type_row.MethodList:
            method = getattr(index, "row", None)
            if method is None:
                continue
            name = str(method.Name)
            if needle not in name:
                continue
            found += 1
            print(f"[method] {full}.{name}")
            print(f"         访问性：{access_of(method.Flags)}  static={bool(getattr(method.Flags, 'mdStatic', False))}")

        for index in getattr(type_row, "FieldList", []) or []:
            field = getattr(index, "row", None)
            if field is None:
                continue
            name = str(field.Name)
            if needle not in name:
                continue
            found += 1
            print(f"[field ] {full}.{name}")

    print(f"\n命中 {found} 处。" if found else "\n没有命中——宿主这个版本没有该成员。")
    return 0 if found else 1


if __name__ == "__main__":
    sys.exit(main())
